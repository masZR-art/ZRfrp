using System.Text.Json;

namespace ZRfrp.Server;

public sealed class StateStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public StateStore(ServerOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        _path = Path.Combine(options.DataDirectory, "state.json");
        State = Load();
    }

    public ServerState State { get; }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(State, _json));
            File.Move(temporary, _path, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AuditAsync(string action, string detail)
    {
        State.Audit.Insert(0, new AuditEntry(DateTimeOffset.UtcNow, action, detail));
        if (State.Audit.Count > 500)
        {
            State.Audit.RemoveRange(500, State.Audit.Count - 500);
        }
        await SaveAsync();
    }

    private ServerState Load()
    {
        if (!File.Exists(_path))
        {
            return new ServerState();
        }

        try
        {
            return Normalize(JsonSerializer.Deserialize<ServerState>(File.ReadAllText(_path), _json) ?? new());
        }
        catch
        {
            File.Copy(_path, _path + $".broken-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", true);
            return new ServerState();
        }
    }

    private static ServerState Normalize(ServerState state)
    {
        state.Allocations ??= [];
        state.Audit ??= [];
        state.Accounts ??= [];
        state.AccountSessions ??= [];
        state.SubscriptionPlans ??= [];
        state.SubscriptionOrders ??= [];
        state.Alipay ??= new();
        state.Announcements ??= [];
        state.Nodes ??= [];
        state.RevokedNodeIds ??= [];
        state.Clients ??= [];
        state.TrafficSnapshots ??= [];
        state.TrafficInSnapshots ??= [];
        state.TrafficOutSnapshots ??= [];
        state.TrafficHistory ??= [];
        state.TrafficHistoryInitializedKeys ??= [];
        state.Smtp ??= new();
        state.EmailVerificationChallenges ??= [];
        foreach (var account in state.Accounts) account.AllowedNodeIds ??= [];
        foreach (var plan in state.SubscriptionPlans) plan.AllowedNodeIds ??= [];
        foreach (var order in state.SubscriptionOrders) order.AllowedNodeIds ??= [];
        return state;
    }
}
