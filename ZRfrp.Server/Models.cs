using System.Text.Json.Serialization;

namespace ZRfrp.Server;

public sealed class ServerOptions
{
    public string Mode { get; set; } = "master";
    public string NodeId { get; set; } = "";
    public string NodeName { get; set; } = "";
    public string MasterUrl { get; set; } = "";
    public string MasterKey { get; set; } = "";
    public string PeerKey { get; set; } = "";
    public string FrpAuthToken { get; set; } = "";
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
    public string LocalNodeName { get; set; } = "";
    public List<PortAllocation> Allocations { get; set; } = [];
    public List<AuditEntry> Audit { get; set; } = [];
    public List<UserAccount> Accounts { get; set; } = [];
    public List<AccountSession> AccountSessions { get; set; } = [];
    public List<ManagedNode> Nodes { get; set; } = [];
    public Dictionary<string, long> TrafficSnapshots { get; set; } = [];
}

public sealed class UserAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "customer";
    public bool Enabled { get; set; } = true;
    public long TrafficQuotaBytes { get; set; }
    public long TrafficUsedBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AccountSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ManagedNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string PublicHost { get; set; } = "";
    public string ControlUrl { get; set; } = "";
    public int FrpsPort { get; set; }
    public bool Online { get; set; }
    public int ActiveClients { get; set; }
    public int ActiveProxies { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public string Version { get; set; } = "";
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
    public string AccountId { get; set; } = "";
    public string NodeId { get; set; } = "";
}

public sealed record AuditEntry(DateTimeOffset Time, string Action, string Detail);
public sealed record LoginRequest(string Username, string Password);
public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);
public sealed record AccountRequest(string Username, string Password, string Role, long TrafficQuotaBytes, bool Enabled);
public sealed record ClientLoginRequest(string Username, string Password, string ClientId);
public sealed record ClientLoginResponse(
    string AccountId, string Username, string AccessToken, DateTimeOffset ExpiresAt,
    string ServerAddress, int ServerPort, string FrpToken, long TrafficQuotaBytes, long TrafficUsedBytes);
public sealed record NodeUpdateRequest(string Name);
public sealed record NodeExportDocument(
    string Kind,
    int Version,
    string PlatformUrl,
    DateTimeOffset ExportedAt,
    IReadOnlyList<NodeExportEntry> Nodes);
public sealed record NodeExportEntry(
    string Id,
    string Name,
    string ServerAddress,
    int ServerPort,
    string FrpToken,
    string ControlApiUrl);
public sealed record NodeHeartbeat(
    string Id, string Name, string PublicHost, string ControlUrl, int FrpsPort,
    bool Online, int ActiveClients, int ActiveProxies, string Version);
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
public sealed record FrpsInstallStatus(
    bool Installed,
    string Version,
    bool Reachable,
    string ServiceState,
    bool BinaryExists,
    bool ConfigExists,
    bool OptWritable,
    bool FileSystemReadOnly,
    string Message);
public sealed record FrpsConfigModel(
    string BindAddress,
    int BindPort,
    string AuthToken,
    string DashboardAddress,
    int DashboardPort,
    string DashboardUser,
    string DashboardPassword,
    int PortRangeStart,
    int PortRangeEnd,
    bool EnablePrometheus,
    string LogLevel,
    int LogMaxDays);

public sealed class PluginRequest
{
    [JsonPropertyName("content")]
    public Dictionary<string, object?> Content { get; set; } = new();
}
