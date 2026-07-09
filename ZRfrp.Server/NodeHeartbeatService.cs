using System.Net.Http.Json;

namespace ZRfrp.Server;

public sealed class NodeHeartbeatService : BackgroundService
{
    private readonly ServerOptions _options;
    private readonly FrpsManager _frps;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public NodeHeartbeatService(ServerOptions options, FrpsManager frps)
    {
        _options = options;
        _frps = frps;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.MasterUrl) || string.IsNullOrWhiteSpace(_options.MasterKey))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var heartbeat = new NodeHeartbeat(
                    string.IsNullOrWhiteSpace(_options.NodeId) ? Environment.MachineName : _options.NodeId,
                    string.IsNullOrWhiteSpace(_options.NodeName) ? "ZRfrp 节点" : _options.NodeName,
                    _options.PublicHost,
                    $"http://{_options.PublicHost}:7600",
                    _options.FrpsBindPort,
                    await _frps.IsReachableAsync(stoppingToken),
                    0,
                    0,
                    UpdateService.CurrentVersion);
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, _options.MasterUrl.TrimEnd('/') + "/api/peer/heartbeat");
                request.Headers.Add("X-ZRfrp-Peer-Key", _options.MasterKey);
                request.Content = JsonContent.Create(heartbeat);
                using var response = await _http.SendAsync(request, stoppingToken);
            }
            catch
            {
                // The next heartbeat retries automatically.
            }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
