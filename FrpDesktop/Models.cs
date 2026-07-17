using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

namespace FrpDesktop;

public sealed class AppState
{
    public string? LastProfileId { get; set; }

    public string ClientFrpcPath { get; set; } = "";

    public string NetworkProxyMode { get; set; } = "none";

    public string NetworkProxyType { get; set; } = "HTTP";

    public string NetworkProxyHost { get; set; } = "";

    public int NetworkProxyPort { get; set; }

    public string NetworkProxyUsername { get; set; } = "";

    public string NetworkProxyPassword { get; set; } = "";

    public bool ExitOnCloseWhenDisconnected { get; set; }

    public string PlatformUrl { get; set; } = "";

    public string AccountUsername { get; set; } = "";

    public string AccountId { get; set; } = "";

    public string AccountAccessToken { get; set; } = "";

    public DateTimeOffset AccountTokenExpiresAt { get; set; }

    public string AccountRefreshToken { get; set; } = "";

    public DateTimeOffset AccountRefreshExpiresAt { get; set; }

    public bool AccountLoginSkipped { get; set; }

    public ObservableCollection<FrpProfile> Profiles { get; set; } = new();
}

public sealed class FrpProfile : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "默认连接";
    private string _frpcPath = "";
    private string _serverAddr = "";
    private int _serverPort = 7000;
    private string _token = "";
    private bool _isLatencyTesting;
    private int? _latencyMs;
    private string _latencyStatus = "待测速";
    private bool _serverManaged;
    private string _controlApiUrl = "";
    private string _controlApiKey = "";
    private string _accountId = "";
    private string _accountAccessToken = "";
    private DateTimeOffset _accountTokenExpiresAt;
    private string _accountRefreshToken = "";
    private DateTimeOffset _accountRefreshExpiresAt;
    private string _managedNodeId = "";
    private int _adminPort;

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value))
            {
                OnPropertyChanged(nameof(NameWithoutFlag));
                OnPropertyChanged(nameof(FlagIconPath));
                OnPropertyChanged(nameof(HasFlagIcon));
            }
        }
    }

    [JsonIgnore]
    public string NameWithoutFlag => RemoveLeadingFlag(Name);

    [JsonIgnore]
    public string FlagIconPath
    {
        get
        {
            var code = GetLeadingFlagCode(Name).ToLowerInvariant();
            return code is "cn" or "jp" or "us" or "sg" or "hk" or "kr" or "de" or "gb" or "fr"
                ? $"Assets/Flags/{code}.png"
                : "";
        }
    }

    [JsonIgnore]
    public bool HasFlagIcon => !string.IsNullOrWhiteSpace(FlagIconPath);

    public string FrpcPath
    {
        get => _frpcPath;
        set => SetField(ref _frpcPath, value);
    }

    public string ServerAddr
    {
        get => _serverAddr;
        set => SetField(ref _serverAddr, value);
    }

    public int ServerPort
    {
        get => _serverPort;
        set => SetField(ref _serverPort, value);
    }

    public string Token
    {
        get => _token;
        set => SetField(ref _token, value);
    }

    public bool ServerManaged
    {
        get => _serverManaged;
        set => SetField(ref _serverManaged, value);
    }

    public string ControlApiUrl
    {
        get => _controlApiUrl;
        set => SetField(ref _controlApiUrl, value);
    }

    public string ControlApiKey
    {
        get => _controlApiKey;
        set => SetField(ref _controlApiKey, value);
    }

    public string AccountId
    {
        get => _accountId;
        set => SetField(ref _accountId, value);
    }

    public string ManagedNodeId
    {
        get => _managedNodeId;
        set => SetField(ref _managedNodeId, value);
    }

    public string AccountAccessToken
    {
        get => _accountAccessToken;
        set => SetField(ref _accountAccessToken, value);
    }

    public DateTimeOffset AccountTokenExpiresAt
    {
        get => _accountTokenExpiresAt;
        set => SetField(ref _accountTokenExpiresAt, value);
    }

    public string AccountRefreshToken
    {
        get => _accountRefreshToken;
        set => SetField(ref _accountRefreshToken, value);
    }

    public DateTimeOffset AccountRefreshExpiresAt
    {
        get => _accountRefreshExpiresAt;
        set => SetField(ref _accountRefreshExpiresAt, value);
    }

    public int AdminPort
    {
        get => _adminPort;
        set => SetField(ref _adminPort, value);
    }

    [JsonIgnore]
    public bool IsLatencyTesting
    {
        get => _isLatencyTesting;
        set
        {
            if (SetField(ref _isLatencyTesting, value))
            {
                OnPropertyChanged(nameof(LatencyText));
            }
        }
    }

    [JsonIgnore]
    public int? LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (SetField(ref _latencyMs, value))
            {
                OnPropertyChanged(nameof(LatencyText));
            }
        }
    }

    [JsonIgnore]
    public string LatencyStatus
    {
        get => _latencyStatus;
        set
        {
            if (SetField(ref _latencyStatus, value))
            {
                OnPropertyChanged(nameof(LatencyText));
            }
        }
    }

    [JsonIgnore]
    public string LatencyText
    {
        get
        {
            if (IsLatencyTesting)
            {
                return "测速中";
            }

            return LatencyMs is > 0 ? $"{LatencyMs.Value} ms" : LatencyStatus;
        }
    }

    public ObservableCollection<FrpProxy> Proxies { get; set; } = new();

    public FrpProfile Clone()
    {
        return new FrpProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"{Name} 副本",
            FrpcPath = FrpcPath,
            ServerAddr = ServerAddr,
            ServerPort = ServerPort,
            Token = Token,
            ServerManaged = ServerManaged,
            ControlApiUrl = ControlApiUrl,
            ControlApiKey = ControlApiKey,
            AccountId = AccountId,
            ManagedNodeId = ManagedNodeId,
            AccountAccessToken = AccountAccessToken,
            AccountTokenExpiresAt = AccountTokenExpiresAt,
            AccountRefreshToken = AccountRefreshToken,
            AccountRefreshExpiresAt = AccountRefreshExpiresAt,
            AdminPort = 0,
            Proxies = new ObservableCollection<FrpProxy>(Proxies.Select(proxy => proxy.Clone()))
        };
    }

    private static string RemoveLeadingFlag(string value)
    {
        var normalized = value.Replace("\ufe0f", "", StringComparison.Ordinal).TrimStart();
        var code = GetLeadingFlagCode(normalized);
        if (code.Length != 2)
        {
            return value;
        }

        var builder = new StringBuilder();
        foreach (var rune in normalized.EnumerateRunes().Skip(2))
        {
            builder.Append(rune.ToString());
        }
        return builder.ToString().TrimStart();
    }

    private static string GetLeadingFlagCode(string value)
    {
        var runes = value.Replace("\ufe0f", "", StringComparison.Ordinal).TrimStart().EnumerateRunes().Take(2).ToArray();
        if (runes.Length < 2)
        {
            return "";
        }

        Span<char> code = stackalloc char[2];
        for (var i = 0; i < 2; i++)
        {
            var number = runes[i].Value;
            if (number is < 0x1F1E6 or > 0x1F1FF)
            {
                return "";
            }
            code[i] = (char)('A' + number - 0x1F1E6);
        }
        return new string(code);
    }
}

public sealed class FrpProxy : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private bool _enabled = true;
    private string _name = "new-tunnel";
    private string _type = "tcp";
    private string _localIP = "127.0.0.1";
    private int _localPort = 80;
    private int _remotePort = 6000;
    private string _customDomains = "";
    private string _allocationId = "";
    private bool _remotePortLocked;
    private string _bandwidthLimit = "";

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Type
    {
        get => _type;
        set => SetField(ref _type, value);
    }

    public string LocalIP
    {
        get => _localIP;
        set => SetField(ref _localIP, value);
    }

    public int LocalPort
    {
        get => _localPort;
        set => SetField(ref _localPort, value);
    }

    public int RemotePort
    {
        get => _remotePort;
        set => SetField(ref _remotePort, value);
    }

    public string CustomDomains
    {
        get => _customDomains;
        set => SetField(ref _customDomains, value);
    }

    public string AllocationId
    {
        get => _allocationId;
        set => SetField(ref _allocationId, value);
    }

    public bool RemotePortLocked
    {
        get => _remotePortLocked;
        set => SetField(ref _remotePortLocked, value);
    }

    public string BandwidthLimit
    {
        get => _bandwidthLimit;
        set => SetField(ref _bandwidthLimit, value);
    }

    public FrpProxy Clone()
    {
        return new FrpProxy
        {
            Enabled = Enabled,
            Name = $"{Name}-copy",
            Type = Type,
            LocalIP = LocalIP,
            LocalPort = LocalPort,
            RemotePort = RemotePort,
            CustomDomains = CustomDomains,
            BandwidthLimit = BandwidthLimit
        };
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

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
