using System.Text.Json.Serialization;

namespace ZRfrp.Server;

public sealed class ServerOptions
{
    public string PublicHost { get; set; } = "";
    public string FrpsAddress { get; set; } = "127.0.0.1";
    public int FrpsBindPort { get; set; } = 7000;
    public string FrpsDashboardUrl { get; set; } = "http://127.0.0.1:7500";
    public string FrpsDashboardUser { get; set; } = "admin";
    public string FrpsDashboardPassword { get; set; } = "";
    public string FrpsBinaryPath { get; set; } = "/opt/zrfrp/frps";
    public string FrpsConfigPath { get; set; } = "/etc/zrfrp/frps.toml";
    public string FrpsServiceName { get; set; } = "zrfrp-frps";
    public string DataDirectory { get; set; } = "/var/lib/zrfrp";
    public int PortRangeStart { get; set; } = 20000;
    public int PortRangeEnd { get; set; } = 40000;
    public int SessionHours { get; set; } = 12;
}

public sealed class ServerState
{
    public string AdminPasswordHash { get; set; } = "";
    public string ClientApiKeyHash { get; set; } = "";
    public List<PortAllocation> Allocations { get; set; } = [];
    public List<AuditEntry> Audit { get; set; } = [];
}

public sealed class PortAllocation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClientId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string TunnelId { get; set; } = "";
    public string ProxyName { get; set; } = "";
    public string ProxyType { get; set; } = "tcp";
    public int RemotePort { get; set; }
    public string BandwidthLimit { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Active { get; set; } = true;
}

public sealed record AuditEntry(DateTimeOffset Time, string Action, string Detail);
public sealed record LoginRequest(string Password);
public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);
public sealed record AllocationRequest(
    string ClientId,
    string ProfileId,
    string TunnelId,
    string ProxyName,
    string ProxyType,
    string BandwidthLimit);
public sealed record AllocationResponse(
    string AllocationId,
    string NodeName,
    string ServerAddress,
    int ServerPort,
    int RemotePort,
    string BandwidthLimit,
    bool Locked);
public sealed record ConfigUpdateRequest(string Content, bool Restart);

public sealed class PluginRequest
{
    [JsonPropertyName("content")]
    public Dictionary<string, object?> Content { get; set; } = new();
}
