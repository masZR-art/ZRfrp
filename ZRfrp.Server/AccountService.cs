namespace ZRfrp.Server;

public sealed class AccountService
{
    private readonly StateStore _store;
    private readonly ServerOptions _options;

    public AccountService(StateStore store, ServerOptions options)
    {
        _store = store;
        _options = options;
    }

    public UserAccount? ValidatePassword(string username, string password) =>
        _store.State.Accounts.FirstOrDefault(account =>
            account.Enabled
            && account.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase)
            && Security.Verify(password, account.PasswordHash));

    public async Task<(UserAccount Account, string Token, DateTimeOffset ExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt)> CreateSessionAsync(
        UserAccount account)
    {
        var token = Security.CreateSecret(32);
        var configuredHours = _store.State.SessionHours > 0
            ? _store.State.SessionHours
            : _options.SessionHours;
        var expiresAt = DateTimeOffset.UtcNow.AddHours(Math.Clamp(configuredHours, 1, 8760));
        var refreshToken = Security.CreateSecret(48);
        var refreshExpiresAt = DateTimeOffset.UtcNow.AddDays(30);
        _store.State.AccountSessions.RemoveAll(session => session.RefreshExpiresAt <= DateTimeOffset.UtcNow);
        _store.State.AccountSessions.Add(new AccountSession
        {
            AccountId = account.Id,
            TokenHash = Security.HashToken(token),
            ExpiresAt = expiresAt,
            RefreshTokenHash = Security.HashToken(refreshToken),
            RefreshExpiresAt = refreshExpiresAt
        });
        await _store.SaveAsync();
        return (account, token, expiresAt, refreshToken, refreshExpiresAt);
    }

    public async Task<(UserAccount Account, string Token, DateTimeOffset ExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt)?> RefreshSessionAsync(string refreshToken)
    {
        var now = DateTimeOffset.UtcNow;
        var session = _store.State.AccountSessions.FirstOrDefault(item =>
            item.RefreshExpiresAt > now && !string.IsNullOrWhiteSpace(item.RefreshTokenHash)
            && Security.VerifyToken(refreshToken, item.RefreshTokenHash));
        if (session is null) return null;
        var account = Find(session.AccountId);
        if (account is null || !account.Enabled) return null;
        var configuredHours = _store.State.SessionHours > 0 ? _store.State.SessionHours : _options.SessionHours;
        var accessToken = Security.CreateSecret(32);
        var nextRefreshToken = Security.CreateSecret(48);
        session.TokenHash = Security.HashToken(accessToken);
        session.ExpiresAt = now.AddHours(Math.Clamp(configuredHours, 1, 8760));
        session.RefreshTokenHash = Security.HashToken(nextRefreshToken);
        session.RefreshExpiresAt = now.AddDays(30);
        await _store.SaveAsync();
        return (account, accessToken, session.ExpiresAt, nextRefreshToken, session.RefreshExpiresAt);
    }

    public UserAccount? ValidateAccessToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var session in _store.State.AccountSessions.Where(item => item.ExpiresAt > now))
        {
            if (Security.VerifyToken(token, session.TokenHash))
            {
                return _store.State.Accounts.FirstOrDefault(account => account.Id == session.AccountId && account.Enabled);
            }
        }
        return null;
    }

    public UserAccount? Find(string id) => _store.State.Accounts.FirstOrDefault(account => account.Id == id);

    public bool IsQuotaExceeded(UserAccount account) =>
        account.TrafficQuotaBytes > 0 && account.TrafficUsedBytes >= account.TrafficQuotaBytes;
}
