using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;

namespace FrpDesktop;

public sealed class ZRfrpControlClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private NetworkProxyOptions _proxyOptions = new("none", "HTTP", "", 0, "", "");
    private bool _useSystemProxyFallback;

    public void ConfigureProxy(NetworkProxyOptions options)
    {
        _proxyOptions = options;
        _useSystemProxyFallback = false;
    }

    public async Task<ClientAccountSession> LoginAsync(
        string platformUrl, string username, string password, string clientId,
        CancellationToken cancellationToken = default)
    {
        var baseAddress = CreatePlatformUri(platformUrl);
        var payload = JsonSerializer.Serialize(new { username, password, clientId }, JsonOptions);
        HttpResponseMessage response;
        try
        {
            response = await SendLoginAsync(baseAddress, payload, cancellationToken);
        }
        catch (HttpRequestException) when (_proxyOptions.Mode.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            _useSystemProxyFallback = true;
            response = await SendLoginAsync(baseAddress, payload, cancellationToken);
        }
        using (response)
        {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = JsonSerializer.Deserialize<ControlError>(text, JsonOptions);
            throw new InvalidOperationException(error?.Error ?? "账号登录失败。");
        }
        return JsonSerializer.Deserialize<ClientAccountSession>(text, JsonOptions)
            ?? throw new InvalidOperationException("控制平台返回了无效登录结果。");
        }
    }

    public async Task<NodeExportDocument> ExportNodesAsync(
        string platformUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        var baseAddress = CreatePlatformUri(platformUrl);
        using var client = CreateHttpClient(baseAddress);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.GetAsync("api/customer/nodes/export", cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = JsonSerializer.Deserialize<ControlError>(text, JsonOptions);
            throw new InvalidOperationException(error?.Error ?? "节点配置导出失败。");
        }
        return JsonSerializer.Deserialize<NodeExportDocument>(text, JsonOptions)
            ?? throw new InvalidOperationException("控制平台返回了无效节点配置。");
    }

    public async Task<ManagedAllocation> AllocateAsync(
        FrpProfile profile, FrpProxy proxy, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(profile);
        var payload = new
        {
            clientId = profile.Id,
            profileId = profile.Id,
            tunnelId = proxy.Id,
            proxyName = proxy.Name,
            proxyType = proxy.Type.ToLowerInvariant(),
            bandwidthLimit = "",
            nodeId = profile.ManagedNodeId
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("api/client/allocate", content, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var error = JsonSerializer.Deserialize<ControlError>(responseText, JsonOptions);
                throw new InvalidOperationException(error?.Error ?? $"服务端拒绝了分配请求 ({(int)response.StatusCode})。");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"服务端拒绝了分配请求 ({(int)response.StatusCode})。");
            }
        }
        return JsonSerializer.Deserialize<ManagedAllocation>(responseText, JsonOptions)
            ?? throw new InvalidOperationException("服务端返回了无效的分配结果。");
    }

    public async Task ReleaseAsync(FrpProfile profile, string allocationId)
    {
        if (string.IsNullOrWhiteSpace(allocationId))
        {
            return;
        }
        using var client = CreateClient(profile);
        var nodeId = Uri.EscapeDataString(profile.ManagedNodeId ?? "");
        using var response = await client.DeleteAsync(
            $"api/client/allocations/{Uri.EscapeDataString(allocationId)}?nodeId={nodeId}");
        response.EnsureSuccessStatusCode();
    }

    private HttpClient CreateClient(FrpProfile profile)
    {
        var baseAddress = CreatePlatformUri(profile.ControlApiUrl);
        if (string.IsNullOrWhiteSpace(profile.ControlApiKey)
            && string.IsNullOrWhiteSpace(profile.AccountAccessToken))
        {
            throw new InvalidOperationException("请填写客户端 API Key。");
        }
        var client = CreateHttpClient(baseAddress);
        if (!string.IsNullOrWhiteSpace(profile.AccountAccessToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", profile.AccountAccessToken);
        }
        else
        {
            client.DefaultRequestHeaders.Add("X-ZRfrp-Key", profile.ControlApiKey);
        }
        return client;
    }

    private async Task<HttpResponseMessage> SendLoginAsync(
        Uri baseAddress, string payload, CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient(baseAddress);
        return await client.PostAsync(
            "api/client/login", new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken);
    }

    private HttpClient CreateHttpClient(Uri baseAddress) =>
        new(CreateHttpHandler())
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(10)
        };

    private HttpClientHandler CreateHttpHandler()
    {
        var mode = _proxyOptions.Mode.Trim().ToLowerInvariant();
        var handler = new HttpClientHandler { UseProxy = false };
        if (mode == "system" || _useSystemProxyFallback)
        {
            handler.UseProxy = true;
            handler.UseDefaultCredentials = true;
        }
        else if (mode == "manual"
            && !string.IsNullOrWhiteSpace(_proxyOptions.Host)
            && _proxyOptions.Port > 0)
        {
            var scheme = _proxyOptions.Type.Trim().ToLowerInvariant() switch
            {
                "https" => "https",
                "socks4" => "socks4",
                "socks5" => "socks5",
                _ => "http"
            };
            handler.UseProxy = true;
            handler.Proxy = new WebProxy($"{scheme}://{_proxyOptions.Host.Trim()}:{_proxyOptions.Port}");
            if (!string.IsNullOrWhiteSpace(_proxyOptions.Username))
            {
                handler.Proxy.Credentials = new NetworkCredential(
                    _proxyOptions.Username, _proxyOptions.Password);
            }
        }
        return handler;
    }

    public static string NormalizePlatformUrl(string platformUrl) =>
        CreatePlatformUri(platformUrl).ToString().TrimEnd('/');

    private static Uri CreatePlatformUri(string platformUrl)
    {
        var value = platformUrl.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException("控制平台地址无效，请填写域名或 HTTP/HTTPS 地址。");
        }

        return uri;
    }

    private sealed record ControlError(string Error);
}

public sealed record ManagedAllocation(
    string AllocationId,
    string NodeName,
    string ServerAddress,
    int ServerPort,
    int RemotePort,
    string BandwidthLimit,
    bool Locked,
    string NodeId);

public sealed record ClientAccountSession(
    string AccountId,
    string Username,
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string ServerAddress,
    int ServerPort,
    string FrpToken,
    long TrafficQuotaBytes,
    long TrafficUsedBytes);
