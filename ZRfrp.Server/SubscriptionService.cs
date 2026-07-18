namespace ZRfrp.Server;

public sealed class SubscriptionService
{
    public const string Pending = "pending";
    public const string PendingPayment = "pending_payment";
    public const string Paid = "paid";
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    private static readonly HashSet<string> ValidKinds =
        new(StringComparer.Ordinal) { "monthly", "quarterly", "yearly", "traffic" };

    private readonly StateStore _store;
    private readonly ServerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SubscriptionService(StateStore store, ServerOptions options)
    {
        _store = store;
        _options = options;
    }

    public static bool IsValidKind(string? kind) =>
        ValidKinds.Contains((kind ?? "").Trim().ToLowerInvariant());

    public bool IsNodeAllowed(UserAccount account, string nodeId)
    {
        var allowed = account.AllowedNodeIds ?? [];
        return allowed.Count == 0 || allowed.Contains(nodeId, StringComparer.Ordinal);
    }

    public IReadOnlyList<(string Id, string Name)> AvailableNodes()
    {
        var result = new List<(string Id, string Name)>
        {
            (LocalNodeId(), string.IsNullOrWhiteSpace(_store.State.LocalNodeName)
                ? (string.IsNullOrWhiteSpace(_options.NodeName) ? "本机节点" : _options.NodeName)
                : _store.State.LocalNodeName)
        };
        result.AddRange(_store.State.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Id))
            .Select(node => (node.Id, string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name)));
        return result
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    public async Task<(SubscriptionPlan? Plan, string? Error)> CreatePlanAsync(
        SubscriptionPlanRequest request)
    {
        var validation = ValidatePlan(request);
        if (validation is not null) return (null, validation);

        await _gate.WaitAsync();
        try
        {
            var plan = new SubscriptionPlan();
            ApplyPlan(plan, request);
            _store.State.SubscriptionPlans.Add(plan);
            await _store.AuditAsync("subscription", $"创建订阅方案 {plan.Name}");
            return (plan, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(SubscriptionPlan? Plan, string? Error)> UpdatePlanAsync(
        string id, SubscriptionPlanRequest request)
    {
        var validation = ValidatePlan(request);
        if (validation is not null) return (null, validation);

        await _gate.WaitAsync();
        try
        {
            var plan = _store.State.SubscriptionPlans.FirstOrDefault(item => item.Id == id);
            if (plan is null) return (null, "订阅方案不存在。");
            ApplyPlan(plan, request);
            await _store.AuditAsync("subscription", $"更新订阅方案 {plan.Name}");
            return (plan, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> DeletePlanAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var plan = _store.State.SubscriptionPlans.FirstOrDefault(item => item.Id == id);
            if (plan is null) return "订阅方案不存在。";
            if (_store.State.SubscriptionOrders.Any(item =>
                    item.PlanId == id && IsOpenOrder(item.Status)))
            {
                return "该方案仍有未完成申请，请先完成审核或停用方案。";
            }
            _store.State.SubscriptionPlans.Remove(plan);
            await _store.AuditAsync("subscription", $"删除订阅方案 {plan.Name}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(SubscriptionOrder? Order, string? Error)> SubmitOrderAsync(
        UserAccount account, string planId)
    {
        await _gate.WaitAsync();
        try
        {
            var plan = _store.State.SubscriptionPlans.FirstOrDefault(item =>
                item.Id == planId && item.Enabled);
            if (plan is null) return (null, "订阅方案不存在或已停止受理。");
            if (_store.State.SubscriptionOrders.Any(item =>
                    item.AccountId == account.Id && item.PlanId == plan.Id
                    && IsOpenOrder(item.Status)))
            {
                return (null, "该方案已有未完成申请，请勿重复提交。");
            }

            var order = new SubscriptionOrder
            {
                AccountId = account.Id,
                Username = account.Username,
                PlanId = plan.Id,
                PlanName = plan.Name,
                Kind = plan.Kind,
                TrafficBytes = plan.TrafficBytes,
                PriceCents = plan.PriceCents,
                Currency = plan.Currency,
                AllowedNodeIds = [.. (plan.AllowedNodeIds ?? [])],
                MaxChannels = plan.MaxChannels,
                AutoApprove = plan.AutoApprove
            };

            if (plan.PriceCents > 0 && !AlipayPaymentService.IsConfigured(_store.State.Alipay))
            {
                return (null, "支付宝支付暂未启用或配置不完整，请联系管理员。");
            }

            if (plan.PriceCents > 0)
            {
                order.Status = PendingPayment;
                order.PaymentProvider = "alipay";
                order.PaymentStatus = "pending";
                order.OutTradeNo = CreateOutTradeNo();
            }
            else if (plan.AutoApprove)
            {
                ApproveOrder(order, account, "system");
            }

            _store.State.SubscriptionOrders.Add(order);
            var detail = order.Status switch
            {
                PendingPayment => $"{account.Username} 创建支付宝订单 {plan.Name}",
                Approved => $"{account.Username} 申请并自动生效 {plan.Name}",
                _ => $"{account.Username} 申请订阅 {plan.Name}"
            };
            await _store.AuditAsync("subscription", detail);
            return (order, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(SubscriptionOrder? Order, string? Error)> ReviewOrderAsync(
        string orderId, bool approved, string reviewer, string? note)
    {
        await _gate.WaitAsync();
        try
        {
            var order = _store.State.SubscriptionOrders.FirstOrDefault(item => item.Id == orderId);
            if (order is null) return (null, "订阅申请不存在。");
            if (order.Status is Approved or Rejected)
                return (null, "该申请已经审核，不能重复处理。");
            if (approved && order.Status == PendingPayment)
                return (null, "订单尚未完成支付，不能批准。");
            if (approved && order.Status is not (Pending or Paid))
                return (null, "订单当前状态不能批准。");

            var account = _store.State.Accounts.FirstOrDefault(item => item.Id == order.AccountId);
            if (account is null) return (null, "申请账号已不存在，无法完成审核。");

            order.ReviewNote = (note ?? "").Trim();
            if (approved)
            {
                ApproveOrder(order, account, reviewer);
            }
            else
            {
                order.Status = Rejected;
                order.ReviewedAt = DateTimeOffset.UtcNow;
                order.ReviewedBy = reviewer;
            }

            await _store.AuditAsync(
                "subscription",
                $"{reviewer} {(approved ? "批准" : "拒绝")} {order.Username} 的 {order.PlanName} 申请");
            return (order, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(SubscriptionOrder? Order, string? Error)> MarkPaidAsync(
        string outTradeNo, string transactionId, long paidCents)
    {
        await _gate.WaitAsync();
        try
        {
            var order = _store.State.SubscriptionOrders.FirstOrDefault(item =>
                item.OutTradeNo.Equals(outTradeNo, StringComparison.Ordinal));
            if (order is null) return (null, "支付订单不存在。");
            if (order.PriceCents != paidCents) return (null, "支付金额与订单不一致。");
            if (order.Status == Rejected) return (null, "订单已被拒绝。");
            if (order.PaymentStatus == "paid") return (order, null);
            if (order.Status != PendingPayment) return (null, "订单当前状态不能确认支付。");

            var account = _store.State.Accounts.FirstOrDefault(item => item.Id == order.AccountId);
            if (account is null) return (null, "支付账号已不存在。");
            order.PaymentStatus = "paid";
            order.TransactionId = transactionId.Trim();
            order.PaidAt = DateTimeOffset.UtcNow;
            if (order.AutoApprove)
            {
                ApproveOrder(order, account, "alipay");
            }
            else
            {
                order.Status = Paid;
            }
            await _store.AuditAsync(
                "payment", $"支付宝订单 {order.OutTradeNo} 已支付，客户 {order.Username}");
            return (order, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> UpdateAccountSubscriptionAsync(
        UserAccount account, AccountSubscriptionRequest request, string reviewer)
    {
        var name = (request.Name ?? "").Trim();
        if (name.Length > 48) return "订阅名称不能超过 48 个字符。";
        if (request.TrafficQuotaBytes < 0) return "流量额度不能小于 0。";
        if (request.MaxChannels is < 0 or > 10000) return "通道数需在 0 至 10000 之间。";
        var nodeError = ValidateNodeIds(request.AllowedNodeIds);
        if (nodeError is not null) return nodeError;

        await _gate.WaitAsync();
        try
        {
            account.ActiveSubscriptionName = name;
            account.SubscriptionExpiresAt = request.ExpiresAt;
            account.TrafficQuotaBytes = request.TrafficQuotaBytes;
            account.AllowedNodeIds = NormalizeNodeIds(request.AllowedNodeIds);
            account.MaxChannels = request.MaxChannels;
            await _store.AuditAsync("subscription", $"{reviewer} 直接更新 {account.Username} 的订阅权益");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? ValidatePlan(SubscriptionPlanRequest request)
    {
        var name = (request.Name ?? "").Trim();
        if (name.Length is < 2 or > 48) return "方案名称需为 2 至 48 个字符。";
        if (!IsValidKind(request.Kind)) return "订阅类型无效。";
        if (request.TrafficBytes <= 0) return "套餐流量必须大于 0。";
        if (request.PriceCents < 0) return "套餐价格不能小于 0。";
        if (request.SortOrder is < -9999 or > 9999) return "排序值超出允许范围。";
        if (request.MaxChannels is < 0 or > 10000) return "通道数需在 0 至 10000 之间。";
        return ValidateNodeIds(request.AllowedNodeIds);
    }

    private string? ValidateNodeIds(IReadOnlyList<string>? nodeIds)
    {
        var known = AvailableNodes().Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = NormalizeNodeIds(nodeIds).FirstOrDefault(id => !known.Contains(id));
        return unknown is null ? null : $"节点 {unknown} 不存在，请刷新节点列表后重试。";
    }

    private void ApplyPlan(SubscriptionPlan plan, SubscriptionPlanRequest request)
    {
        plan.Name = request.Name.Trim();
        plan.Kind = request.Kind.Trim().ToLowerInvariant();
        plan.TrafficBytes = request.TrafficBytes;
        plan.PriceCents = request.PriceCents;
        plan.Enabled = request.Enabled;
        plan.SortOrder = request.SortOrder;
        plan.AllowedNodeIds = NormalizeNodeIds(request.AllowedNodeIds);
        plan.MaxChannels = request.MaxChannels;
        plan.AutoApprove = request.AutoApprove;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void ApproveOrder(
        SubscriptionOrder order, UserAccount account, string reviewer)
    {
        if (order.Status == Approved) return;
        if (order.TrafficBytes > 0)
        {
            var currentAllowance = account.TrafficQuotaBytes > 0
                ? account.TrafficQuotaBytes
                : account.TrafficUsedBytes;
            account.TrafficQuotaBytes = SaturatingAdd(currentAllowance, order.TrafficBytes);
        }

        var months = DurationMonths(order.Kind);
        if (months > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var anchor = account.SubscriptionExpiresAt is { } current && current > now
                ? current
                : now;
            account.SubscriptionExpiresAt = anchor.AddMonths(months);
        }
        account.ActiveSubscriptionName = order.PlanName;
        account.AllowedNodeIds = [.. (order.AllowedNodeIds ?? [])];
        account.MaxChannels = order.MaxChannels;
        order.AppliedExpiresAt = account.SubscriptionExpiresAt;
        order.Status = Approved;
        order.ReviewedAt = DateTimeOffset.UtcNow;
        order.ReviewedBy = reviewer;
    }

    private static List<string> NormalizeNodeIds(IReadOnlyList<string>? values) =>
        (values ?? [])
        .Select(value => (value ?? "").Trim())
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    private static bool IsOpenOrder(string status) =>
        status is Pending or PendingPayment or Paid;

    private static int DurationMonths(string kind) => kind switch
    {
        "monthly" => 1,
        "quarterly" => 3,
        "yearly" => 12,
        _ => 0
    };

    private string LocalNodeId() =>
        string.IsNullOrWhiteSpace(_options.NodeId) ? "local" : _options.NodeId;

    private static string CreateOutTradeNo() =>
        $"ZRF{DateTimeOffset.UtcNow:yyyyMMddHHmmss}{Guid.NewGuid():N}"[..33];

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
}
