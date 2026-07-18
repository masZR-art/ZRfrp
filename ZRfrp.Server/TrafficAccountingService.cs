namespace ZRfrp.Server;

public sealed class TrafficAccountingService
{
    public const string UnattributedAccountId = "__unattributed__";
    private static readonly TimeSpan HistoryBucketSize = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(31);
    private readonly StateStore _store;
    private readonly ServerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TrafficAccountingService(StateStore store, ServerOptions options)
    {
        _store = store;
        _options = options;
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
            var stateChanged = false;
            foreach (var sample in samples)
            {
                if (string.IsNullOrWhiteSpace(sample.AccountId) || sample.TotalBytes < 0)
                {
                    continue;
                }

                var account = _store.State.Accounts.FirstOrDefault(item =>
                    item.Id == sample.AccountId);
                var effectiveSample = sample;
                if (account is null && sample.AccountId != UnattributedAccountId)
                {
                    effectiveSample = sample with { AccountId = UnattributedAccountId };
                }

                var key = SnapshotKey(nodeId, effectiveSample);
                var historyInitialized = _store.State.TrafficHistoryInitializedKeys.Contains(key)
                    || HasHistoryForSample(nodeId, effectiveSample);
                var increment = ApplySnapshot(
                    _store.State.TrafficSnapshots, key, effectiveSample.TotalBytes);
                var trafficInIncrement = ApplySnapshot(
                    _store.State.TrafficInSnapshots, key, effectiveSample.TrafficInBytes);
                var trafficOutIncrement = ApplySnapshot(
                    _store.State.TrafficOutSnapshots, key, effectiveSample.TrafficOutBytes);
                (trafficInIncrement, trafficOutIncrement) = NormalizeDirectionalIncrements(
                    increment, trafficInIncrement, trafficOutIncrement);
                var historyIn = trafficInIncrement;
                var historyOut = trafficOutIncrement;
                if (!historyInitialized && effectiveSample.TotalBytes > 0)
                {
                    (historyIn, historyOut) = NormalizeDirectionalIncrements(
                        effectiveSample.TotalBytes,
                        effectiveSample.TrafficInBytes,
                        effectiveSample.TrafficOutBytes);
                }
                _store.State.TrafficHistoryInitializedKeys.Add(key);
                stateChanged = true;

                if (increment > 0)
                {
                    if (account is not null)
                    {
                        account.TrafficUsedBytes = SaturatingAdd(account.TrafficUsedBytes, increment);
                    }
                    applied = SaturatingAdd(applied, increment);
                    _store.State.TotalTrafficInBytes = SaturatingAdd(
                        _store.State.TotalTrafficInBytes, trafficInIncrement);
                    _store.State.TotalTrafficOutBytes = SaturatingAdd(
                        _store.State.TotalTrafficOutBytes, trafficOutIncrement);
                }
                if (historyIn > 0 || historyOut > 0)
                {
                    RecordHistory(
                        nodeId, effectiveSample, historyIn, historyOut, DateTimeOffset.UtcNow);
                }
            }

            PruneHistory(DateTimeOffset.UtcNow);
            if (stateChanged)
            {
                await _store.SaveAsync();
            }
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

    public async Task<bool> ResetAccountAsync(
        string accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var account = _store.State.Accounts.FirstOrDefault(item => item.Id == accountId);
            if (account is null)
            {
                return false;
            }

            account.TrafficUsedBytes = 0;
            foreach (var key in _store.State.TrafficSnapshots.Keys
                         .Where(key => BelongsToAccount(key, accountId))
                         .ToArray())
            {
                _store.State.TrafficSnapshots[key] = long.MaxValue;
                _store.State.TrafficInSnapshots[key] = long.MaxValue;
                _store.State.TrafficOutSnapshots[key] = long.MaxValue;
            }
            foreach (var bucket in _store.State.TrafficHistory)
            {
                bucket.Slices.RemoveAll(slice => slice.AccountId == accountId);
            }
            _store.State.TrafficHistory.RemoveAll(bucket => bucket.Slices.Count == 0);
            await _store.SaveAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAccountDataAsync(
        string accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var key in _store.State.TrafficSnapshots.Keys
                         .Where(key => BelongsToAccount(key, accountId))
                         .ToArray())
            {
                _store.State.TrafficSnapshots.Remove(key);
                _store.State.TrafficInSnapshots.Remove(key);
                _store.State.TrafficOutSnapshots.Remove(key);
                _store.State.TrafficHistoryInitializedKeys.Remove(key);
            }
            foreach (var bucket in _store.State.TrafficHistory)
            {
                bucket.Slices.RemoveAll(slice => slice.AccountId == accountId);
            }
            _store.State.TrafficHistory.RemoveAll(bucket => bucket.Slices.Count == 0);
            await _store.SaveAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TrafficStatisticsResponse> GetStatisticsAsync(
        string range,
        string? accountId,
        CancellationToken cancellationToken = default)
    {
        var (normalizedRange, duration, groupSize) = ParseRange(range);
        var now = DateTimeOffset.UtcNow;
        var from = now.Subtract(duration);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var buckets = _store.State.TrafficHistory
                .Where(bucket => bucket.StartedAt >= from)
                .OrderBy(bucket => bucket.StartedAt)
                .ToArray();
            var timeline = CreateTimeline(from, now, groupSize);
            var nodes = new Dictionary<string, TrafficTotals>(StringComparer.Ordinal);
            var accounts = new Dictionary<string, TrafficTotals>(StringComparer.Ordinal);
            var protocols = new Dictionary<string, TrafficTotals>(StringComparer.OrdinalIgnoreCase);
            var tunnels = new Dictionary<string, TrafficTotals>(StringComparer.Ordinal);
            long periodIn = 0;
            long periodOut = 0;

            foreach (var bucket in buckets)
            {
                var pointTime = FloorUtc(bucket.StartedAt, groupSize);
                if (!timeline.TryGetValue(pointTime, out var point))
                {
                    continue;
                }
                foreach (var slice in bucket.Slices)
                {
                    if (!string.IsNullOrWhiteSpace(accountId) && slice.AccountId != accountId)
                    {
                        continue;
                    }

                    point.In = SaturatingAdd(point.In, slice.TrafficInBytes);
                    point.Out = SaturatingAdd(point.Out, slice.TrafficOutBytes);
                    periodIn = SaturatingAdd(periodIn, slice.TrafficInBytes);
                    periodOut = SaturatingAdd(periodOut, slice.TrafficOutBytes);
                    AddDimension(nodes, slice.NodeId, slice.TrafficInBytes, slice.TrafficOutBytes);
                    AddDimension(accounts, slice.AccountId, slice.TrafficInBytes, slice.TrafficOutBytes);
                    AddDimension(protocols, slice.ProxyType, slice.TrafficInBytes, slice.TrafficOutBytes);
                    AddDimension(
                        tunnels,
                        $"{slice.NodeId}\u001f{slice.ProxyName}",
                        slice.TrafficInBytes,
                        slice.TrafficOutBytes);
                }
            }

            var account = string.IsNullOrWhiteSpace(accountId)
                ? null
                : _store.State.Accounts.FirstOrDefault(item => item.Id == accountId);
            var lifetimeIn = string.IsNullOrWhiteSpace(accountId)
                ? _store.State.TotalTrafficInBytes
                : SumAccountHistory(accountId, inbound: true);
            var lifetimeOut = string.IsNullOrWhiteSpace(accountId)
                ? _store.State.TotalTrafficOutBytes
                : SumAccountHistory(accountId, inbound: false);
            var lifetimeBytes = account?.TrafficUsedBytes
                ?? SaturatingAdd(lifetimeIn, lifetimeOut);

            return new TrafficStatisticsResponse(
                normalizedRange,
                from,
                now,
                lifetimeBytes,
                lifetimeIn,
                lifetimeOut,
                periodIn,
                periodOut,
                _store.State.TrafficHistory.Any(bucket => bucket.Slices.Any(slice =>
                    string.IsNullOrWhiteSpace(accountId) || slice.AccountId == accountId)),
                timeline.Select(pair => new TrafficTimelinePoint(pair.Key, pair.Value.In, pair.Value.Out)).ToArray(),
                ToDimensions(nodes, NodeLabel, 8, NodeFlagCode),
                string.IsNullOrWhiteSpace(accountId) ? ToDimensions(accounts, AccountLabel, 10) : [],
                ToDimensions(protocols, key => key.ToUpperInvariant(), 8),
                ToDimensions(tunnels, TunnelLabel, 10, TunnelFlagCode));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string SnapshotKey(string nodeId, TrafficSample sample) =>
        $"traffic-v2:{nodeId}:{sample.AccountId}:{sample.ProxyType}:{sample.ClientId}:{sample.ProxyName}";

    private static long ApplySnapshot(
        IDictionary<string, long> snapshots, string key, long current)
    {
        current = Math.Max(0, current);
        if (!snapshots.TryGetValue(key, out var previous))
        {
            snapshots[key] = current;
            return current;
        }
        if (previous == long.MaxValue)
        {
            snapshots[key] = current;
            return 0;
        }
        var increment = current >= previous ? current - previous : current;
        snapshots[key] = current;
        return increment;
    }

    private static (long In, long Out) NormalizeDirectionalIncrements(
        long total, long inbound, long outbound)
    {
        total = Math.Max(0, total);
        inbound = Math.Max(0, inbound);
        outbound = Math.Max(0, outbound);
        var directionalTotal = SaturatingAdd(inbound, outbound);
        if (directionalTotal == total)
        {
            return (inbound, outbound);
        }
        if (total == 0)
        {
            return (0, 0);
        }
        if (directionalTotal < total)
        {
            return (inbound, SaturatingAdd(outbound, total - directionalTotal));
        }

        var scaledIn = (long)Math.Round((decimal)inbound * total / directionalTotal);
        scaledIn = Math.Clamp(scaledIn, 0, total);
        return (scaledIn, total - scaledIn);
    }

    private bool HasHistoryForSample(string nodeId, TrafficSample sample)
    {
        var normalizedNodeId = string.IsNullOrWhiteSpace(nodeId) ? "local" : nodeId;
        var proxyName = FriendlyProxyName(sample.AccountId, sample.ProxyName);
        return _store.State.TrafficHistory.Any(bucket => bucket.Slices.Any(slice =>
            slice.AccountId == sample.AccountId
            && slice.NodeId == normalizedNodeId
            && slice.ProxyType.Equals(sample.ProxyType, StringComparison.OrdinalIgnoreCase)
            && slice.ProxyName == proxyName));
    }

    private void RecordHistory(
        string nodeId,
        TrafficSample sample,
        long inbound,
        long outbound,
        DateTimeOffset now)
    {
        if (inbound <= 0 && outbound <= 0)
        {
            return;
        }
        var startedAt = FloorUtc(now, HistoryBucketSize);
        var bucket = _store.State.TrafficHistory.LastOrDefault(item => item.StartedAt == startedAt);
        if (bucket is null)
        {
            bucket = new TrafficHistoryBucket { StartedAt = startedAt };
            _store.State.TrafficHistory.Add(bucket);
        }
        var proxyName = FriendlyProxyName(sample.AccountId, sample.ProxyName);
        var normalizedNodeId = string.IsNullOrWhiteSpace(nodeId) ? "local" : nodeId;
        var slice = bucket.Slices.FirstOrDefault(item =>
            item.AccountId == sample.AccountId
            && item.NodeId == normalizedNodeId
            && item.ProxyType.Equals(sample.ProxyType, StringComparison.OrdinalIgnoreCase)
            && item.ProxyName == proxyName);
        if (slice is null)
        {
            slice = new TrafficHistorySlice
            {
                AccountId = sample.AccountId,
                NodeId = normalizedNodeId,
                ProxyType = sample.ProxyType.ToLowerInvariant(),
                ProxyName = proxyName
            };
            bucket.Slices.Add(slice);
        }
        slice.TrafficInBytes = SaturatingAdd(slice.TrafficInBytes, inbound);
        slice.TrafficOutBytes = SaturatingAdd(slice.TrafficOutBytes, outbound);
    }

    private void PruneHistory(DateTimeOffset now)
    {
        var cutoff = now.Subtract(HistoryRetention);
        _store.State.TrafficHistory.RemoveAll(bucket => bucket.StartedAt < cutoff);
    }

    private static string FriendlyProxyName(string accountId, string proxyName)
    {
        var prefix = accountId + ".";
        return proxyName.StartsWith(prefix, StringComparison.Ordinal)
            ? proxyName[prefix.Length..]
            : proxyName;
    }

    private static (string Range, TimeSpan Duration, TimeSpan GroupSize) ParseRange(string range) =>
        range?.ToLowerInvariant() switch
        {
            "7d" => ("7d", TimeSpan.FromDays(7), TimeSpan.FromHours(6)),
            "30d" => ("30d", TimeSpan.FromDays(30), TimeSpan.FromDays(1)),
            _ => ("24h", TimeSpan.FromHours(24), TimeSpan.FromHours(1))
        };

    private static SortedDictionary<DateTimeOffset, TrafficTotals> CreateTimeline(
        DateTimeOffset from, DateTimeOffset to, TimeSpan groupSize)
    {
        var result = new SortedDictionary<DateTimeOffset, TrafficTotals>();
        for (var cursor = FloorUtc(from, groupSize); cursor <= to; cursor = cursor.Add(groupSize))
        {
            result[cursor] = new TrafficTotals();
        }
        return result;
    }

    private static DateTimeOffset FloorUtc(DateTimeOffset value, TimeSpan interval)
    {
        var utcTicks = value.UtcDateTime.Ticks;
        return new DateTimeOffset(utcTicks - utcTicks % interval.Ticks, TimeSpan.Zero);
    }

    private static void AddDimension(
        IDictionary<string, TrafficTotals> dimensions,
        string key,
        long inbound,
        long outbound)
    {
        key = string.IsNullOrWhiteSpace(key) ? "unknown" : key;
        if (!dimensions.TryGetValue(key, out var totals))
        {
            totals = new TrafficTotals();
            dimensions[key] = totals;
        }
        totals.In = SaturatingAdd(totals.In, inbound);
        totals.Out = SaturatingAdd(totals.Out, outbound);
    }

    private IReadOnlyList<TrafficDimensionItem> ToDimensions(
        IReadOnlyDictionary<string, TrafficTotals> source,
        Func<string, string> label,
        int limit,
        Func<string, string>? flagCode = null) => source
        .Select(pair => new TrafficDimensionItem(
            pair.Key,
            label(pair.Key),
            pair.Value.In,
            pair.Value.Out,
            SaturatingAdd(pair.Value.In, pair.Value.Out),
            flagCode?.Invoke(pair.Key) ?? ""))
        .OrderByDescending(item => item.TotalBytes)
        .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
        .Take(limit)
        .ToArray();

    private string NodeLabel(string nodeId)
    {
        if (IsLocalNode(nodeId))
        {
            return string.IsNullOrWhiteSpace(_store.State.LocalNodeName)
                ? string.IsNullOrWhiteSpace(_options.NodeName) ? "本机节点" : PlainNodeName(_options.NodeName)
                : PlainNodeName(_store.State.LocalNodeName);
        }
        var node = _store.State.Nodes.FirstOrDefault(item => item.Id == nodeId);
        return node is null || string.IsNullOrWhiteSpace(node.Name)
            ? nodeId
            : PlainNodeName(node.Name);
    }

    private string NodeFlagCode(string nodeId)
    {
        if (IsLocalNode(nodeId))
        {
            return NormalizeFlagCode(
                _store.State.LocalNodeFlagCode,
                string.IsNullOrWhiteSpace(_store.State.LocalNodeName)
                    ? _options.NodeName
                    : _store.State.LocalNodeName);
        }
        var node = _store.State.Nodes.FirstOrDefault(item => item.Id == nodeId);
        return node is null ? "" : NormalizeFlagCode(node.FlagCode, node.Name);
    }

    private string AccountLabel(string accountId)
    {
        if (accountId == UnattributedAccountId)
        {
            return "未归属流量";
        }
        var account = _store.State.Accounts.FirstOrDefault(item => item.Id == accountId);
        if (account is null)
        {
            return "已删除账号";
        }
        var type = account.Role.Equals("admin", StringComparison.OrdinalIgnoreCase)
            ? "管理员"
            : "客户";
        return $"{account.Username} · {type}";
    }

    private string TunnelLabel(string key)
    {
        var separator = key.IndexOf('\u001f');
        if (separator < 0)
        {
            return key;
        }
        var node = NodeLabel(key[..separator]);
        var proxy = key[(separator + 1)..];
        return $"{proxy} · {node}";
    }

    private string TunnelFlagCode(string key)
    {
        var separator = key.IndexOf('\u001f');
        return separator < 0 ? "" : NodeFlagCode(key[..separator]);
    }

    private bool IsLocalNode(string nodeId) =>
        string.IsNullOrWhiteSpace(nodeId)
        || nodeId.Equals("local", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(_options.NodeId)
            && nodeId.Equals(_options.NodeId, StringComparison.Ordinal));

    private static string NormalizeFlagCode(string? flagCode, string? decoratedName)
    {
        var normalized = flagCode?.Trim().ToUpperInvariant() ?? "";
        if (normalized.Length == 2 && normalized.All(character => character is >= 'A' and <= 'Z'))
        {
            return normalized;
        }
        return decoratedName?.TrimStart() switch
        {
            var value when value?.StartsWith("🇨🇳", StringComparison.Ordinal) == true => "CN",
            var value when value?.StartsWith("🇯🇵", StringComparison.Ordinal) == true => "JP",
            var value when value?.StartsWith("🇺🇸", StringComparison.Ordinal) == true => "US",
            var value when value?.StartsWith("🇸🇬", StringComparison.Ordinal) == true => "SG",
            var value when value?.StartsWith("🇭🇰", StringComparison.Ordinal) == true => "HK",
            var value when value?.StartsWith("🇰🇷", StringComparison.Ordinal) == true => "KR",
            var value when value?.StartsWith("🇩🇪", StringComparison.Ordinal) == true => "DE",
            var value when value?.StartsWith("🇬🇧", StringComparison.Ordinal) == true => "GB",
            var value when value?.StartsWith("🇫🇷", StringComparison.Ordinal) == true => "FR",
            _ => ""
        };
    }

    private static string PlainNodeName(string value)
    {
        var name = value.Trim();
        foreach (var flag in new[] { "🇨🇳", "🇯🇵", "🇺🇸", "🇸🇬", "🇭🇰", "🇰🇷", "🇩🇪", "🇬🇧", "🇫🇷" })
        {
            if (name.StartsWith(flag, StringComparison.Ordinal))
            {
                return name[flag.Length..].TrimStart();
            }
        }
        return name;
    }

    private long SumAccountHistory(string accountId, bool inbound)
    {
        long total = 0;
        foreach (var slice in _store.State.TrafficHistory.SelectMany(bucket => bucket.Slices)
                     .Where(slice => slice.AccountId == accountId))
        {
            total = SaturatingAdd(total, inbound ? slice.TrafficInBytes : slice.TrafficOutBytes);
        }
        return total;
    }

    private sealed class TrafficTotals
    {
        public long In { get; set; }
        public long Out { get; set; }
    }

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
}
