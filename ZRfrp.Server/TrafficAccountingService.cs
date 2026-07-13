namespace ZRfrp.Server;

public sealed class TrafficAccountingService
{
    private readonly StateStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TrafficAccountingService(StateStore store)
    {
        _store = store;
    }

    public async Task<long> ApplyAsync(
        string nodeId,
        IReadOnlyCollection<TrafficSample> samples,
        CancellationToken cancellationToken = default)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            long applied = 0;
            foreach (var sample in samples)
            {
                if (string.IsNullOrWhiteSpace(sample.AccountId) || sample.TotalBytes < 0)
                {
                    continue;
                }

                var account = _store.State.Accounts.FirstOrDefault(item =>
                    item.Role == "customer" && item.Id == sample.AccountId);
                if (account is null)
                {
                    continue;
                }

                var key = SnapshotKey(nodeId, sample);
                var hasPrevious = _store.State.TrafficSnapshots.TryGetValue(key, out var previous);
                long increment;
                if (!hasPrevious)
                {
                    increment = sample.TotalBytes;
                }
                else if (previous == long.MaxValue)
                {
                    // An administrator reset the account. Establish a fresh baseline
                    // without restoring the bytes already visible in frps.
                    increment = 0;
                }
                else
                {
                    // frps counters are daily and also reset when the process restarts.
                    increment = sample.TotalBytes >= previous
                        ? sample.TotalBytes - previous
                        : sample.TotalBytes;
                }

                if (increment > 0 && account.TrafficUsedBytes <= long.MaxValue - increment)
                {
                    account.TrafficUsedBytes += increment;
                    applied += increment;
                }
                _store.State.TrafficSnapshots[key] = sample.TotalBytes;
            }

            await _store.SaveAsync();
            return applied;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static bool BelongsToAccount(string snapshotKey, string accountId) =>
        snapshotKey.Contains($":{accountId}:", StringComparison.Ordinal)
        || snapshotKey.Contains(accountId + ".", StringComparison.Ordinal);

    private static string SnapshotKey(string nodeId, TrafficSample sample) =>
        $"traffic-v2:{nodeId}:{sample.AccountId}:{sample.ProxyType}:{sample.ClientId}:{sample.ProxyName}";
}
