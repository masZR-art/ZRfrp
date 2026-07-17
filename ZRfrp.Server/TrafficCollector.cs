using System.Net.Http.Json;
using System.Text.Json;

namespace ZRfrp.Server;

public sealed class TrafficCollector : BackgroundService
{
    private static readonly string[] ProxyTypes = ["tcp", "udp", "http", "https"];

    private readonly FrpsManager _frps;
    private readonly StateStore _store;
    private readonly ServerOptions _options;
    private readonly TrafficAccountingService _accounting;
    private readonly ILogger<TrafficCollector> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public int LastDashboardProxyCount { get; private set; }
    public int LastMatchedSampleCount { get; private set; }
    public int LastUnmatchedProxyCount { get; private set; }
    public long LastAppliedBytes { get; private set; }
    public string LastError { get; private set; } = "尚未执行流量采集。";
    public string LastUnmatchedSummary { get; private set; } = "";

    public TrafficCollector(
        FrpsManager frps,
        StateStore store,
        ServerOptions options,
        TrafficAccountingService accounting,
        ILogger<TrafficCollector> logger)
    {
        _frps = frps;
        _store = store;
        _options = options;
        _accounting = accounting;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LastAttemptAt = DateTimeOffset.UtcNow;
                var samples = new List<TrafficSample>();
                var unmatched = new List<string>();
                var dashboardProxyCount = 0;
                foreach (var type in ProxyTypes)
                {
                    var json = await _frps.GetDashboardJsonAsync($"/api/proxy/{type}", stoppingToken);
                    if (json is not null)
                    {
                        var proxies = EnumerateProxies(json.Value).ToArray();
                        dashboardProxyCount += proxies.Length;
                        samples.AddRange(Collect(type, proxies, unmatched));
                    }
                }

                LastDashboardProxyCount = dashboardProxyCount;
                LastMatchedSampleCount = samples.Count;
                LastUnmatchedProxyCount = unmatched.Count;
                LastUnmatchedSummary = string.Join("；", unmatched.Take(5));
                if (dashboardProxyCount == 0 && !string.IsNullOrWhiteSpace(_frps.LastDashboardError))
                    throw new InvalidOperationException(_frps.LastDashboardError);

                if (_options.Mode.Equals("node", StringComparison.OrdinalIgnoreCase))
                {
                    await ReportToMasterAsync(samples, stoppingToken);
                    LastAppliedBytes = 0;
                }
                else
                {
                    LastAppliedBytes = await _accounting.ApplyAsync(LocalNodeId(), samples, stoppingToken);
                }
                LastSuccessAt = DateTimeOffset.UtcNow;
                LastError = "";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                _logger.LogWarning(exception, "采集 frps 流量失败，将在下一周期重试。");
            }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }

    private IEnumerable<TrafficSample> Collect(
        string type,
        IEnumerable<JsonElement> proxies,
        ICollection<string> unmatched)
    {
        foreach (var proxy in proxies)
        {
            var name = ReadString(proxy, "name", "proxy_name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var user = ReadString(proxy, "user");
            var clientId = ReadString(proxy, "clientID", "clientId", "client_id");
            var remotePort = ReadNestedLong(proxy, "conf", "remotePort", "remote_port");
            var accountId = ResolveAccountId(type, name, user, clientId, remotePort);
            if (string.IsNullOrWhiteSpace(accountId))
            {
                unmatched.Add($"{type}/{name} (user={ValueOrDash(user)}, client={ValueOrDash(clientId)}, port={remotePort})");
                continue;
            }

            var trafficIn = ReadLong(proxy, "today_traffic_in", "todayTrafficIn", "trafficIn", "traffic_in");
            var trafficOut = ReadLong(proxy, "today_traffic_out", "todayTrafficOut", "trafficOut", "traffic_out");
            var current = SaturatingAdd(trafficIn, trafficOut);
            yield return new TrafficSample(
                accountId, type, name, clientId, Math.Max(0, current),
                Math.Max(0, trafficIn), Math.Max(0, trafficOut), remotePort);
        }
    }

    private string ResolveAccountId(
        string type, string proxyName, string user, string clientId, int remotePort)
    {
        if (!string.IsNullOrWhiteSpace(user))
        {
            var account = _store.State.Accounts.FirstOrDefault(item =>
                item.Role == "customer"
                && (item.Id.Equals(user, StringComparison.Ordinal)
                    || item.Username.Equals(user, StringComparison.OrdinalIgnoreCase)));
            if (account is not null)
            {
                return account.Id;
            }
            if (_store.State.Allocations.Any(item => item.Active && item.AccountId == user))
            {
                return user;
            }
        }

        var allocations = _store.State.Allocations.Where(item =>
            item.Active
            && !string.IsNullOrWhiteSpace(item.AccountId)
            && item.ProxyType.Equals(type, StringComparison.OrdinalIgnoreCase)).ToArray();

        var allocation = allocations
            .Select(item => new
            {
                Item = item,
                Score = AllocationMatchScore(item, proxyName, clientId, remotePort)
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Item.UpdatedAt)
            .FirstOrDefault()?.Item;
        if (allocation is not null)
        {
            return allocation.AccountId;
        }

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            var trackedClient = _store.State.Clients.FirstOrDefault(item =>
                item.ClientId.Equals(clientId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(item.AccountId));
            if (trackedClient is not null)
            {
                return trackedClient.AccountId;
            }
        }

        allocation = _store.State.Allocations.FirstOrDefault(item => item.Active
            && !string.IsNullOrWhiteSpace(clientId)
            && item.ClientId.Equals(clientId, StringComparison.Ordinal));
        if (allocation is not null && !string.IsNullOrWhiteSpace(allocation.AccountId))
            return allocation.AccountId;

        return _store.State.Accounts
            .Where(item => item.Role == "customer")
            .Select(item => item.Id)
            .FirstOrDefault(id => proxyName.StartsWith(id + ".", StringComparison.Ordinal)) ?? "";
    }

    private static int AllocationMatchScore(
        PortAllocation allocation, string proxyName, string clientId, int remotePort)
    {
        var nameMatches = allocation.ProxyName.Equals(proxyName, StringComparison.Ordinal)
            || proxyName.Equals($"{allocation.AccountId}.{allocation.ProxyName}", StringComparison.Ordinal)
            || proxyName.EndsWith("." + allocation.ProxyName, StringComparison.Ordinal);
        var clientMatches = !string.IsNullOrWhiteSpace(clientId)
            && allocation.ClientId.Equals(clientId, StringComparison.Ordinal);
        var portMatches = remotePort > 0 && allocation.RemotePort == remotePort;
        if (!nameMatches && !clientMatches && !portMatches)
        {
            return 0;
        }
        return (nameMatches ? 4 : 0) + (clientMatches ? 3 : 0) + (portMatches ? 5 : 0);
    }

    private async Task ReportToMasterAsync(
        IReadOnlyCollection<TrafficSample> samples,
        CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_options.MasterUrl)
            || string.IsNullOrWhiteSpace(_options.MasterKey))
        {
            throw new InvalidOperationException("节点缺少主控地址或 Peer Key，无法上报流量。");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post, _options.MasterUrl.TrimEnd('/') + "/api/peer/traffic");
        request.Headers.Add("X-ZRfrp-Peer-Key", _options.MasterKey);
        request.Content = JsonContent.Create(new PeerTrafficReport(LocalNodeId(), samples.ToArray()));
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"主控拒绝流量上报：HTTP {(int)response.StatusCode} {detail}");
        }
    }

    private string LocalNodeId() =>
        string.IsNullOrWhiteSpace(_options.NodeId) ? "local" : _options.NodeId;

    private static IEnumerable<JsonElement> EnumerateProxies(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("proxies", out var proxies)
            && proxies.ValueKind == JsonValueKind.Array)
        {
            foreach (var proxy in proxies.EnumerateArray())
            {
                if (proxy.ValueKind == JsonValueKind.Object)
                {
                    yield return proxy;
                }
            }
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var proxy in element.EnumerateArray())
            {
                if (proxy.ValueKind == JsonValueKind.Object)
                {
                    yield return proxy;
                }
            }
        }
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }
        }
        return "";
    }

    private static long ReadLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                if (value.TryGetInt64(out var result))
                {
                    return result;
                }
                if (value.ValueKind == JsonValueKind.String
                    && long.TryParse(value.GetString(), out result))
                {
                    return result;
                }
            }
        }
        return 0;
    }

    private static int ReadNestedLong(
        JsonElement element, string objectName, params string[] names)
    {
        if (!element.TryGetProperty(objectName, out var nested)
            || nested.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }
        var value = ReadLong(nested, names);
        return value is > 0 and <= 65535 ? (int)value : 0;
    }

    private static string ValueOrDash(string value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
}
