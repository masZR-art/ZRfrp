using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

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
            Proxies = new ObservableCollection<FrpProxy>(Proxies.Select(proxy => proxy.Clone()))
        };
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
