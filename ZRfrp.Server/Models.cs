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
    public string LocalNodeFlagCode { get; set; } = "";
    public List<PortAllocation> Allocations { get; set; } = [];
    public List<AuditEntry> Audit { get; set; } = [];
    public List<UserAccount> Accounts { get; set; } = [];
    public List<AccountSession> AccountSessions { get; set; } = [];
    public List<ManagedNode> Nodes { get; set; } = [];
    public List<string> RevokedNodeIds { get; set; } = [];
    public List<ManagedClient> Clients { get; set; } = [];
    public Dictionary<string, long> TrafficSnapshots { get; set; } = [];
    public Dictionary<string, long> TrafficInSnapshots { get; set; } = [];
    public Dictionary<string, long> TrafficOutSnapshots { get; set; } = [];
    public List<TrafficHistoryBucket> TrafficHistory { get; set; } = [];
    public long TotalTrafficInBytes { get; set; }
    public long TotalTrafficOutBytes { get; set; }
    public SmtpSettings Smtp { get; set; } = new();
    public List<EmailVerificationChallenge> EmailVerificationChallenges { get; set; } = [];
    public bool RegistrationEnabled { get; set; } = true;
    public long RegistrationQuotaBytes { get; set; } = 1024L * 1024 * 1024;
    public int SessionHours { get; set; }
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
    public string Email { get; set; } = "";
}

public sealed class SmtpSettings
{
    public bool EmailVerificationEnabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "ZRfrp";
    public bool EnableSsl { get; set; } = true;
    public int VerificationMinutes { get; set; } = 15;
    public string SubjectTemplate { get; set; } = "[{{site_name}}] 邮箱验证码";
    public string HtmlTemplate { get; set; } = """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0;padding:0;background:#f4f4f5;font-family:Arial,'Microsoft YaHei',sans-serif;color:#18181b">
<table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="width:100%;background:#f4f4f5;table-layout:fixed"><tr><td align="center" style="padding:16px 12px">
<table role="presentation" width="600" cellspacing="0" cellpadding="0" style="width:100%;max-width:600px;margin:0 auto;background:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 8px 30px rgba(15,23,42,.10)">
<tr><td align="left" style="padding:24px;background:#4f46e5;color:#ffffff"><h1 style="margin:0;font-size:25px;line-height:1.25">邮箱验证码</h1></td></tr>
<tr><td align="left" style="padding:28px 24px;font-size:16px;line-height:1.5"><p style="margin:0 0 12px">{{recipient_name}}，您好：</p><p style="margin:0 0 12px">您的验证码是：</p>
<table role="presentation" width="100%" cellspacing="0" cellpadding="0"><tr><td align="center" style="padding:8px 0 20px;font-size:36px;line-height:1.2;font-weight:700;letter-spacing:7px;white-space:nowrap;color:#111827">{{code}}</td></tr></table>
<p style="margin:0 0 12px">验证码将在 <strong>{{expires_minutes}} 分钟</strong>后失效。</p><p style="margin:0">如果不是您本人操作，请忽略此邮件。</p></td></tr>
<tr><td align="left" style="padding:16px 24px;background:#fafafa;color:#64748b;font-size:13px;line-height:1.45">This email was sent by {{site_name}}. Please do not reply directly.</td></tr>
</table></td></tr></table></body></html>
""";
}

public sealed class EmailVerificationChallenge
{
    public string Email { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSentAt { get; set; }
    public int FailedAttempts { get; set; }
}

public sealed class AccountSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string RefreshTokenHash { get; set; } = "";
    public DateTimeOffset RefreshExpiresAt { get; set; }
}

public sealed class ManagedNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string FlagCode { get; set; } = "";
    public string PublicHost { get; set; } = "";
    public bool PublicHostLocked { get; set; }
    public string ControlUrl { get; set; } = "";
    public int FrpsPort { get; set; }
    // Control-plane heartbeat and frps health are deliberately independent.
    public bool Online { get; set; }
    public bool FrpsOnline { get; set; }
    public int ActiveClients { get; set; }
    public int ActiveProxies { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public string Version { get; set; } = "";
    public string FrpAuthToken { get; set; } = "";
    public string EnrollmentTokenHash { get; set; } = "";
    public DateTimeOffset EnrollmentExpiresAt { get; set; }
    public string EnrollmentMasterUrl { get; set; } = "";
}

public sealed class ManagedClient
{
    public string ClientId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Username { get; set; } = "";
    public string Address { get; set; } = "";
    public string Protocol { get; set; } = "frpc";
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
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
    public string NodeName { get; set; } = "";
}

public sealed record AuditEntry(DateTimeOffset Time, string Action, string Detail);
public sealed record LoginRequest(string Username, string Password);
public sealed record RegistrationRequest(string Username, string Password, string Email, string VerificationCode);
public sealed record EmailCodeRequest(string Email, string Username);
public sealed record SmtpSettingsRequest(
    bool EmailVerificationEnabled, string Host, int Port, string Username, string Password,
    string FromEmail, string FromName, bool EnableSsl, int VerificationMinutes,
    string SubjectTemplate, string HtmlTemplate);
public sealed record TestEmailRequest(string RecipientEmail);
public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);
public sealed record AccountRequest(string Username, string Password, string Role, long TrafficQuotaBytes, bool Enabled);
public sealed record RegistrationSettingsRequest(bool Enabled, long DefaultTrafficQuotaBytes);
public sealed record SessionSettingsRequest(int SessionHours);
public sealed record NodeEnrollmentRequest(
    string Name, string PublicHost, string MasterUrl, string? FlagCode, string? Architecture);
public sealed record NodeEnrollmentResponse(
    string Id,
    string Name,
    string Command,
    string OfflineScriptUrl,
    string ServerPackageUrl,
    string FrpPackageUrl,
    string ServerFileName,
    string FrpFileName);
public sealed record ClientLoginRequest(string Username, string Password, string ClientId);
public sealed record ClientLoginResponse(
    string AccountId, string Username, string AccessToken, DateTimeOffset ExpiresAt,
    string RefreshToken, DateTimeOffset RefreshExpiresAt,
    string ServerAddress, int ServerPort, string FrpToken, long TrafficQuotaBytes, long TrafficUsedBytes);
public sealed record ClientRefreshRequest(string RefreshToken, string ClientId);
public sealed record NodeUpdateRequest(string Name, string? FlagCode, string? PublicHost);
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
    bool Online, bool? FrpsOnline, int ActiveClients, int ActiveProxies,
    string Version, string FrpAuthToken);
public sealed record AllocationRequest(
    string ClientId,
    string ProfileId,
    string TunnelId,
    string ProxyName,
    string ProxyType,
    string BandwidthLimit,
    string NodeId);
public sealed record AllocationResponse(
    string AllocationId,
    string NodeName,
    string ServerAddress,
    int ServerPort,
    int RemotePort,
    string BandwidthLimit,
    bool Locked,
    string NodeId);
public sealed record PeerAllocationRequest(AllocationRequest Allocation, string AccountId);
public sealed record PeerAccountValidationRequest(string AccessToken);
public sealed record PeerAccountValidationResponse(
    string Id, string Username, long TrafficQuotaBytes, long TrafficUsedBytes);
public sealed record TrafficSample(
    string AccountId, string ProxyType, string ProxyName, string ClientId, long TotalBytes,
    long TrafficInBytes = 0, long TrafficOutBytes = 0, int RemotePort = 0);
public sealed record PeerTrafficReport(string NodeId, IReadOnlyList<TrafficSample> Samples);

public sealed class TrafficHistoryBucket
{
    public DateTimeOffset StartedAt { get; set; }
    public List<TrafficHistorySlice> Slices { get; set; } = [];
}

public sealed class TrafficHistorySlice
{
    public string AccountId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string ProxyType { get; set; } = "";
    public string ProxyName { get; set; } = "";
    public long TrafficInBytes { get; set; }
    public long TrafficOutBytes { get; set; }
}

public sealed record TrafficTimelinePoint(
    DateTimeOffset Time, long TrafficInBytes, long TrafficOutBytes);
public sealed record TrafficDimensionItem(
    string Key, string Label, long TrafficInBytes, long TrafficOutBytes, long TotalBytes);
public sealed record TrafficStatisticsResponse(
    string Range,
    DateTimeOffset From,
    DateTimeOffset To,
    long LifetimeBytes,
    long LifetimeTrafficInBytes,
    long LifetimeTrafficOutBytes,
    long PeriodTrafficInBytes,
    long PeriodTrafficOutBytes,
    bool HasHistory,
    IReadOnlyList<TrafficTimelinePoint> Timeline,
    IReadOnlyList<TrafficDimensionItem> Nodes,
    IReadOnlyList<TrafficDimensionItem> Accounts,
    IReadOnlyList<TrafficDimensionItem> Protocols,
    IReadOnlyList<TrafficDimensionItem> Tunnels);
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
