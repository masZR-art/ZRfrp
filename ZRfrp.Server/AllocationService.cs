using System.Net;
using System.Net.Sockets;

namespace ZRfrp.Server;

public sealed class AllocationService
{
    private readonly ServerOptions _options;
    private readonly StateStore _store;
    private readonly FrpsManager _frps;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AllocationService(ServerOptions options, StateStore store, FrpsManager frps)
    {
        _options = options;
        _store = store;
        _frps = frps;
    }

    public async Task<(AllocationResponse? Result, string? Error)> AllocateAsync(
        AllocationRequest request, CancellationToken cancellationToken, UserAccount? account = null)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId)
            || string.IsNullOrWhiteSpace(request.TunnelId)
            || string.IsNullOrWhiteSpace(request.ProxyName))
        {
            return (null, "客户端、隧道和代理名称不能为空。");
        }
        if (request.ProxyType is not ("tcp" or "udp"))
        {
            return (null, "自动端口分配当前适用于 TCP/UDP 隧道。");
        }
        if (!ValidateBandwidth(request.BandwidthLimit))
        {
            return (null, "带宽格式无效，请使用 512KB、2MB 这类格式。");
        }
        if (!await _frps.IsReachableAsync(cancellationToken))
        {
            return (null, "当前节点不可用，暂时无法分配。");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = _store.State.Allocations.FirstOrDefault(item =>
                item.ClientId == request.ClientId && item.TunnelId == request.TunnelId && item.Active
                && (account is null || item.AccountId == account.Id));
            if (existing is not null)
            {
                existing.BandwidthLimit = request.BandwidthLimit.Trim().ToUpperInvariant();
                existing.ProxyName = request.ProxyName;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await _store.SaveAsync();
                return (ToResponse(existing), null);
            }

            var reserved = _store.State.Allocations.Where(item => item.Active)
                .Select(item => item.RemotePort).ToHashSet();
            var port = Enumerable.Range(_options.PortRangeStart, _options.PortRangeEnd - _options.PortRangeStart + 1)
                .FirstOrDefault(candidate => !reserved.Contains(candidate) && IsPortAvailable(candidate, request.ProxyType));
            if (port == 0)
            {
                return (null, "可分配端口已耗尽。");
            }

            var allocation = new PortAllocation
            {
                ClientId = request.ClientId,
                ProfileId = request.ProfileId,
                TunnelId = request.TunnelId,
                ProxyName = request.ProxyName,
                ProxyType = request.ProxyType,
                RemotePort = port,
                BandwidthLimit = request.BandwidthLimit.Trim().ToUpperInvariant(),
                AccountId = account?.Id ?? ""
            };
            _store.State.Allocations.Add(allocation);
            await _store.AuditAsync("allocate", $"{request.ClientId}/{request.ProxyName} -> {port}");
            return (ToResponse(allocation), null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ReleaseAsync(string allocationId)
    {
        var allocation = _store.State.Allocations.FirstOrDefault(item => item.Id == allocationId);
        if (allocation is null)
        {
            return false;
        }
        allocation.Active = false;
        allocation.UpdatedAt = DateTimeOffset.UtcNow;
        await _store.AuditAsync("release", $"{allocation.ClientId}/{allocation.ProxyName} ({allocation.RemotePort})");
        return true;
    }

    public PortAllocation? FindForPlugin(string clientId, string proxyName, string tunnelId) =>
        _store.State.Allocations.FirstOrDefault(item =>
            item.Active
            && item.ClientId.Equals(clientId, StringComparison.Ordinal)
            && (!string.IsNullOrWhiteSpace(tunnelId)
                ? item.TunnelId.Equals(tunnelId, StringComparison.Ordinal)
                : item.ProxyName.Equals(proxyName, StringComparison.Ordinal)));

    private AllocationResponse ToResponse(PortAllocation allocation) => new(
        allocation.Id,
        Environment.MachineName,
        string.IsNullOrWhiteSpace(_options.PublicHost) ? _options.FrpsAddress : _options.PublicHost,
        _options.FrpsBindPort,
        allocation.RemotePort,
        allocation.BandwidthLimit,
        true);

    private static bool IsPortAvailable(int port, string type)
    {
        try
        {
            if (type == "udp")
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            }
            else
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateBandwidth(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0")
        {
            return true;
        }
        var normalized = value.Trim().ToUpperInvariant();
        var suffix = normalized.EndsWith("KB", StringComparison.Ordinal) ? "KB"
            : normalized.EndsWith("MB", StringComparison.Ordinal) ? "MB" : "";
        return suffix.Length > 0
            && int.TryParse(normalized[..^2], out var amount)
            && amount is > 0 and <= 10240;
    }
}
