using System.Security.Claims;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using ZRfrp.Server;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var options = builder.Configuration.GetSection("ZRfrp").Get<ServerOptions>() ?? new();
options.FrpAuthToken = ReadFrpsAuthToken(options.FrpsConfigPath, options.FrpAuthToken);
var dataProtectionDirectory = Path.Combine(options.DataDirectory, "keys");
Directory.CreateDirectory(dataProtectionDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory))
    .SetApplicationName("ZRfrp.Server");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<FrpsManager>();
builder.Services.AddSingleton<AllocationService>();
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<SubscriptionService>();
builder.Services.AddSingleton<AlipayPaymentService>();
builder.Services.AddSingleton<AnnouncementService>();
builder.Services.AddSingleton<AccountResolver>();
builder.Services.AddSingleton<TrafficAccountingService>();
builder.Services.AddSingleton<FrpsConfigService>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<BootstrapPackageService>();
builder.Services.AddSingleton<SmtpService>();
builder.Services.AddHostedService<NodeHeartbeatService>();
builder.Services.AddSingleton<TrafficCollector>();
builder.Services.AddHostedService(service => service.GetRequiredService<TrafficCollector>());
builder.Services.Configure<JsonOptions>(json => json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.Cookie.Name = "zrfrp.session";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Strict;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        cookie.ExpireTimeSpan = TimeSpan.FromHours(Math.Max(1, options.SessionHours));
        cookie.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        cookie.Events.OnValidatePrincipal = async context =>
        {
            var accountId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var store = context.HttpContext.RequestServices.GetRequiredService<StateStore>();
            var account = store.State.Accounts.FirstOrDefault(item =>
                item.Id == accountId && item.Enabled);
            var revisionValue = context.Principal?.FindFirstValue("zrfrp_auth_revision");
            var presentedRevision = int.TryParse(revisionValue, out var parsedRevision)
                ? parsedRevision
                : 0;
            if (account is null || presentedRevision != account.AuthRevision)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
var stateStore = app.Services.GetRequiredService<StateStore>();
await InitializeSecretsAsync(stateStore, app.Logger);
if (args.Length >= 2 && args[0].Equals("--reset-admin", StringComparison.OrdinalIgnoreCase))
{
    if (args[1].Length < 10)
    {
        Console.Error.WriteLine("新管理员密码至少需要 10 个字符。");
        Environment.ExitCode = 1;
        return;
    }
    var admin = stateStore.State.Accounts.FirstOrDefault(account =>
        account.Role == "admin" && account.Username.Equals("admin", StringComparison.OrdinalIgnoreCase));
    if (admin is null)
    {
        Console.Error.WriteLine("未找到 admin 管理员账号。");
        Environment.ExitCode = 1;
        return;
    }
    admin.PasswordHash = Security.Hash(args[1]);
    admin.AuthRevision = NextAuthRevision(admin.AuthRevision);
    admin.Enabled = true;
    stateStore.State.AdminPasswordHash = admin.PasswordHash;
    stateStore.State.AccountSessions.RemoveAll(session => session.AccountId == admin.Id);
    await stateStore.AuditAsync("security", "通过本机命令重置 admin 管理员密码");
    Console.WriteLine("admin 管理员密码已重置。");
    return;
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Context.Response.Headers.Pragma = "no-cache";
        context.Context.Response.Headers.Expires = "0";
    }
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/bootstrap/node/{token}/install.sh", (
    string token, StateStore store, ServerOptions serverOptions) =>
{
    var node = FindEnrollmentNode(store, token);
    return node is null
        ? Results.NotFound()
        : Results.Text(
            CreateMasterBootstrapScript(
                node, token, serverOptions.PeerKey, serverOptions.FrpAuthToken),
            "text/x-shellscript");
});

app.MapGet("/api/bootstrap/node/{token}/installer", async (
    string token, StateStore store, BootstrapPackageService packages) =>
{
    if (FindEnrollmentNode(store, token) is null)
    {
        return Results.NotFound();
    }
    return Results.Text(await packages.ReadInstallerAsync(), "text/x-shellscript");
});

app.MapGet("/api/bootstrap/node/{token}/offline/{rid}.sh", async (
    string token,
    string rid,
    StateStore store,
    ServerOptions serverOptions,
    BootstrapPackageService packages) =>
{
    var node = FindEnrollmentNode(store, token);
    if (node is null)
    {
        return Results.NotFound();
    }
    if (rid is not ("linux-x64" or "linux-arm64"))
    {
        return Results.BadRequest(new { error = "不支持的节点架构。" });
    }
    var script = CreateOfflineBootstrapScript(
        node,
        rid,
        serverOptions.PeerKey,
        serverOptions.FrpAuthToken,
        await packages.ReadInstallerAsync());
    return Results.File(
        Encoding.UTF8.GetBytes(script),
        "text/x-shellscript",
        "zrfrp-node-offline.sh");
});

app.MapGet("/api/bootstrap/node/{token}/server/{rid}", async (
    string token,
    string rid,
    StateStore store,
    BootstrapPackageService packages,
    CancellationToken cancellationToken) =>
{
    if (FindEnrollmentNode(store, token) is null)
    {
        return Results.NotFound();
    }
    try
    {
        var path = await packages.GetServerPackageAsync(rid, cancellationToken);
        return Results.File(path, "application/gzip", Path.GetFileName(path), enableRangeProcessing: true);
    }
    catch (ArgumentOutOfRangeException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapGet("/api/bootstrap/node/{token}/frp/{arch}", async (
    string token,
    string arch,
    StateStore store,
    BootstrapPackageService packages,
    CancellationToken cancellationToken) =>
{
    if (FindEnrollmentNode(store, token) is null)
    {
        return Results.NotFound();
    }
    try
    {
        var path = await packages.GetFrpPackageAsync(arch, cancellationToken);
        return Results.File(path, "application/gzip", Path.GetFileName(path), enableRangeProcessing: true);
    }
    catch (ArgumentOutOfRangeException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, AccountService accounts) =>
{
    var account = accounts.ValidatePassword(request.Username ?? "", request.Password ?? "");
    if (account is null)
    {
        await Task.Delay(250);
        return Results.Json(new { error = "账号或密码错误。" }, statusCode: 401);
    }
    var identity = new ClaimsIdentity([
        new Claim(ClaimTypes.Name, account.Username),
        new Claim(ClaimTypes.NameIdentifier, account.Id),
        new Claim(ClaimTypes.Role, account.Role),
        new Claim("zrfrp_auth_revision", account.AuthRevision.ToString())
    ], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(new { ok = true, role = account.Role, username = account.Username });
});

app.MapGet("/api/auth/registration", (StateStore store) => Results.Ok(new
{
    enabled = store.State.RegistrationEnabled,
    defaultTrafficQuotaBytes = store.State.RegistrationQuotaBytes,
    emailVerificationEnabled = store.State.Smtp.EmailVerificationEnabled
}));

app.MapPost("/api/auth/email-code", async (EmailCodeRequest request, StateStore store, SmtpService smtp) =>
{
    if (!store.State.RegistrationEnabled || !store.State.Smtp.EmailVerificationEnabled)
        return Results.Json(new { error = "当前未开启邮箱验证注册。" }, statusCode: 403);
    try
    {
        var email = SmtpService.NormalizeEmail(request.Email ?? "");
        if (store.State.Accounts.Any(item => item.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
            return Results.BadRequest(new { error = "该邮箱已注册。" });
        await smtp.SendVerificationCodeAsync(
            email, request.Username ?? "", SmtpService.RegistrationPurpose);
        return Results.Ok(new { message = $"验证码已发送，{Math.Clamp(store.State.Smtp.VerificationMinutes, 1, 120)} 分钟内有效。" });
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapPost("/api/auth/register", async (RegistrationRequest request, StateStore store, SmtpService smtp) =>
{
    var username = request.Username?.Trim() ?? "";
    if (!store.State.RegistrationEnabled)
    {
        return Results.Json(new { error = "当前平台暂未开放账号注册。" }, statusCode: StatusCodes.Status403Forbidden);
    }
    if (username.Length is < 3 or > 32 || request.Password?.Length < 8)
    {
        return Results.BadRequest(new { error = "用户名需为 3 至 32 个字符，密码至少需要 8 个字符。" });
    }
    if (username.Any(char.IsWhiteSpace) || store.State.Accounts.Any(item =>
            item.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.BadRequest(new { error = "用户名不可用或已存在。" });
    }

    var email = "";
    if (store.State.Smtp.EmailVerificationEnabled)
    {
        try { email = SmtpService.NormalizeEmail(request.Email ?? ""); }
        catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        if (store.State.Accounts.Any(item => item.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
            return Results.BadRequest(new { error = "该邮箱已注册。" });
        if (!smtp.VerifyCode(
                email, request.VerificationCode ?? "", SmtpService.RegistrationPurpose))
        {
            await store.SaveAsync();
            return Results.BadRequest(new { error = "邮箱验证码无效、已过期或尝试次数过多。" });
        }
    }

    var account = new UserAccount
    {
        Username = username,
        PasswordHash = Security.Hash(request.Password ?? ""),
        Role = "customer",
        Enabled = true,
        TrafficQuotaBytes = Math.Max(0, store.State.RegistrationQuotaBytes),
        Email = email
    };
    store.State.Accounts.Add(account);
    await store.AuditAsync("account", $"客户自助注册账号 {account.Username}");
    return Results.Ok(new { message = "账号注册成功，请使用新账号登录。", username = account.Username });
});

app.MapPost("/api/auth/password-reset/code", async (
    PasswordResetCodeRequest request, StateStore store, SmtpService smtp) =>
{
    if (!store.State.Smtp.EmailVerificationEnabled)
    {
        return Results.Json(
            new { error = "平台尚未启用邮箱验证码服务，请联系管理员。" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    string email;
    try
    {
        email = SmtpService.NormalizeEmail(request.Email ?? "");
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }

    var username = request.Username?.Trim() ?? "";
    var account = store.State.Accounts.FirstOrDefault(item =>
        item.Enabled
        && item.Role.Equals("customer", StringComparison.OrdinalIgnoreCase)
        && item.Username.Equals(username, StringComparison.OrdinalIgnoreCase)
        && item.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
    if (account is not null)
    {
        try
        {
            await smtp.SendVerificationCodeAsync(
                email, account.Username, SmtpService.PasswordResetPurpose);
        }
        catch (Exception exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }
    else
    {
        await Task.Delay(250);
    }

    return Results.Ok(new
    {
        message = $"若账号与邮箱匹配，验证码将在 {Math.Clamp(store.State.Smtp.VerificationMinutes, 1, 120)} 分钟内送达。"
    });
});

app.MapPost("/api/auth/password-reset", async (
    PasswordResetRequest request, StateStore store, SmtpService smtp) =>
{
    if (request.NewPassword?.Length < 10)
    {
        return Results.BadRequest(new { error = "新密码至少需要 10 个字符。" });
    }

    string email;
    try
    {
        email = SmtpService.NormalizeEmail(request.Email ?? "");
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }

    var username = request.Username?.Trim() ?? "";
    var account = store.State.Accounts.FirstOrDefault(item =>
        item.Enabled
        && item.Role.Equals("customer", StringComparison.OrdinalIgnoreCase)
        && item.Username.Equals(username, StringComparison.OrdinalIgnoreCase)
        && item.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
    if (account is null
        || !smtp.VerifyCode(
            email, request.VerificationCode ?? "", SmtpService.PasswordResetPurpose))
    {
        await store.SaveAsync();
        await Task.Delay(250);
        return Results.BadRequest(new { error = "账号、邮箱或验证码不正确，验证码也可能已经过期。" });
    }

    account.PasswordHash = Security.Hash(request.NewPassword!);
    account.AuthRevision = NextAuthRevision(account.AuthRevision);
    store.State.AccountSessions.RemoveAll(item => item.AccountId == account.Id);
    await store.AuditAsync("security", $"客户账号 {account.Username} 通过邮箱重置密码");
    return Results.Ok(new { message = "密码已重置，旧登录授权已全部失效，请使用新密码登录。" });
});

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
}).RequireAuthorization();

app.MapGet("/api/session", (ClaimsPrincipal user) => Results.Ok(new
{
    authenticated = user.Identity?.IsAuthenticated == true,
    username = user.Identity?.Name,
    role = user.FindFirstValue(ClaimTypes.Role)
}));

app.MapGet("/api/overview", async (FrpsManager frps, StateStore store, ServerOptions serverOptions, CancellationToken cancellationToken) =>
{
    var serverTask = frps.GetDashboardJsonAsync("/api/serverinfo", cancellationToken);
    var clientsTask = frps.GetDashboardJsonAsync("/api/clients", cancellationToken);
    var tcpTask = frps.GetDashboardJsonAsync("/api/proxy/tcp", cancellationToken);
    var udpTask = frps.GetDashboardJsonAsync("/api/proxy/udp", cancellationToken);
    var httpTask = frps.GetDashboardJsonAsync("/api/proxy/http", cancellationToken);
    var httpsTask = frps.GetDashboardJsonAsync("/api/proxy/https", cancellationToken);
    var healthTask = frps.IsReachableAsync(cancellationToken);
    var allocationsTask = GetManagedAllocationsAsync(store, serverOptions, cancellationToken);
    await Task.WhenAll(
        serverTask, clientsTask, tcpTask, udpTask, httpTask, httpsTask, healthTask, allocationsTask);
    var frpsStatus = await frps.GetInstallStatusAsync(cancellationToken);
    var clientCutoff = DateTimeOffset.UtcNow.AddSeconds(-60);
    store.State.Clients.RemoveAll(client => client.LastSeen < clientCutoff);
    var trackedClients = store.State.Clients.Select(client => new
    {
        clientID = client.ClientId,
        clientAddress = client.Address,
        status = "online",
        protocol = client.Protocol,
        connectTime = client.ConnectedAt,
        lastSeen = client.LastSeen,
        username = client.Username
    }).ToArray();
    object clients = DashboardHasItems(clientsTask.Result)
        ? clientsTask.Result!.Value
        : trackedClients;
    var proxyTraffic = SumProxyTraffic(
        tcpTask.Result, udpTask.Result, httpTask.Result, httpsTask.Result);
    return Results.Ok(new
    {
        reachable = healthTask.Result,
        frpsStatus,
        server = serverTask.Result,
        clients,
        trackedClients,
        proxies = new { tcp = tcpTask.Result, udp = udpTask.Result, http = httpTask.Result, https = httpsTask.Result },
        proxyTraffic = new
        {
            totalTrafficIn = proxyTraffic.In,
            totalTrafficOut = proxyTraffic.Out,
            source = "frps-proxy-today"
        },
        accountedTraffic = new
        {
            totalTrafficIn = store.State.TotalTrafficInBytes,
            totalTrafficOut = store.State.TotalTrafficOutBytes,
            source = "zrfrp-all-nodes-persistent"
        },
        publicHost = PublicFrpsHost(serverOptions),
        bindPort = serverOptions.FrpsBindPort,
        localNodeName = LocalNodeName(store, serverOptions),
        localNodeFlagCode = LocalNodeFlagCode(store, serverOptions),
        allocations = allocationsTask.Result,
        audit = store.State.Audit.Take(30)
    });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/config", async (FrpsManager frps) =>
    Results.Ok(new { content = await frps.ReadConfigAsync() }))
    .RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/config", async (ConfigUpdateRequest request, FrpsManager frps, StateStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Content))
    {
        return Results.BadRequest(new { error = "配置内容不能为空。" });
    }
    var result = await frps.SaveConfigAsync(request.Content, request.Restart);
    await store.AuditAsync("config", result.Message);
    return result.Success ? Results.Ok(new { message = result.Message }) : Results.BadRequest(new { error = result.Message });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/service/{action}", async (string action, FrpsManager frps, StateStore store) =>
{
    var result = await frps.ServiceActionAsync(action);
    await store.AuditAsync("service", $"{action}: {result.Output}");
    return result.ExitCode == 0 ? Results.Ok(new { message = $"frps 已{ActionName(action)}。" })
        : Results.BadRequest(new { error = result.Output });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/allocations", async (
    StateStore store, ServerOptions serverOptions, CancellationToken cancellationToken) =>
    Results.Ok(await GetManagedAllocationsAsync(store, serverOptions, cancellationToken)))
    .RequireAuthorization(policy => policy.RequireRole("admin"));
app.MapDelete("/api/allocations/{id}", async (
    string id,
    string? nodeId,
    AllocationService allocations,
    StateStore store,
    ServerOptions serverOptions,
    CancellationToken cancellationToken) =>
{
    var requestedNodeId = string.IsNullOrWhiteSpace(nodeId)
        ? LocalNodeId(serverOptions)
        : nodeId.Trim();
    if (requestedNodeId.Equals(LocalNodeId(serverOptions), StringComparison.Ordinal))
    {
        return await allocations.ReleaseAsync(id) ? Results.Ok() : Results.NotFound();
    }

    var node = store.State.Nodes.FirstOrDefault(item =>
        item.Id.Equals(requestedNodeId, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(item.ControlUrl));
    if (node is null)
    {
        return Results.NotFound();
    }
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    using var request = new HttpRequestMessage(
        HttpMethod.Delete,
        $"{node.ControlUrl.TrimEnd('/')}/api/peer/allocations/{Uri.EscapeDataString(id)}");
    request.Headers.Add("X-ZRfrp-Peer-Key", serverOptions.PeerKey);
    using var response = await client.SendAsync(request, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)response.StatusCode);
    }
    await store.AuditAsync("release", $"释放远程节点 {node.Name} 的租约 {id}");
    return Results.Ok();
})
    .RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/password", async (
    PasswordChangeRequest request, ClaimsPrincipal principal, StateStore store) =>
{
    var account = store.State.Accounts.FirstOrDefault(item =>
        item.Id == principal.FindFirstValue(ClaimTypes.NameIdentifier));
    if (account is null || !Security.Verify(request.CurrentPassword ?? "", account.PasswordHash))
    {
        return Results.BadRequest(new { error = "当前密码不正确。" });
    }
    if (request.NewPassword?.Length < 10)
    {
        return Results.BadRequest(new { error = "新密码至少需要 10 个字符。" });
    }
    account.PasswordHash = Security.Hash(request.NewPassword!);
    if (account.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
    {
        store.State.AdminPasswordHash = account.PasswordHash;
    }
    store.State.AccountSessions.RemoveAll(item => item.AccountId == account.Id);
    await store.AuditAsync("security", "管理员密码已更新");
    return Results.Ok();
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/client/login", async (
    ClientLoginRequest request, AccountService accounts, ServerOptions serverOptions) =>
{
    var account = accounts.ValidatePassword(request.Username ?? "", request.Password ?? "");
    if (account is null)
    {
        return Results.Json(new { error = "账号或密码错误。" }, statusCode: 401);
    }
    if (accounts.IsQuotaExceeded(account))
    {
        return Results.Json(new { error = "该账号流量额度已用尽。" }, statusCode: 403);
    }
    if (accounts.IsSubscriptionExpired(account))
    {
        return Results.Json(new { error = "该账号订阅已到期，请先在客户面板续订。" }, statusCode: 403);
    }
    var session = await accounts.CreateSessionAsync(account);
    return Results.Ok(new ClientLoginResponse(
        account.Id, account.Username, session.Token, session.ExpiresAt,
        session.RefreshToken, session.RefreshExpiresAt,
        string.IsNullOrWhiteSpace(serverOptions.PublicHost) ? serverOptions.FrpsAddress : serverOptions.PublicHost,
        serverOptions.FrpsBindPort, serverOptions.FrpAuthToken,
        account.TrafficQuotaBytes, account.TrafficUsedBytes));
});

app.MapGet("/api/customer/nodes/export", (
    HttpContext context, AccountService accounts, StateStore store, ServerOptions serverOptions) =>
{
    var account = GetBearerAccount(context, accounts)
        ?? accounts.Find(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
    if (account is null)
    {
        return Results.Json(new { error = "登录会话已失效，请重新登录。" }, statusCode: 401);
    }

    return Results.Ok(CreateNodeExport(
        store, serverOptions, context,
        account.Role.Equals("customer", StringComparison.OrdinalIgnoreCase) ? account : null));
});

app.MapGet("/api/customer/me", (ClaimsPrincipal principal, AccountService accounts) =>
{
    var account = accounts.Find(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
    return account is null ? Results.NotFound() : Results.Ok(new
    {
        account.Id,
        account.Username,
        hasEmail = !string.IsNullOrWhiteSpace(account.Email),
        maskedEmail = MaskEmail(account.Email),
        account.TrafficQuotaBytes,
        account.TrafficUsedBytes,
        remainingBytes = account.TrafficQuotaBytes <= 0
            ? -1
            : Math.Max(0, account.TrafficQuotaBytes - account.TrafficUsedBytes),
        account.SubscriptionExpiresAt,
        account.ActiveSubscriptionName,
        account.AllowedNodeIds,
        account.MaxChannels,
        subscriptionExpired = accounts.IsSubscriptionExpired(account)
    });
}).RequireAuthorization();

app.MapGet("/api/customer/announcements", (AnnouncementService announcements) =>
    Results.Ok(announcements.ActiveAnnouncements(DateTimeOffset.UtcNow).Select(item => new
    {
        item.Id,
        item.Title,
        item.Content,
        item.PublishedAt,
        item.ExpiresAt,
        item.UpdatedAt
    }))).RequireAuthorization(policy => policy.RequireRole("customer"));

app.MapGet("/api/customer/subscriptions", (
    ClaimsPrincipal principal, AccountService accounts, StateStore store,
    SubscriptionService subscriptions) =>
{
    var account = accounts.Find(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
    if (account is null || !account.Role.Equals("customer", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Unauthorized();
    }

    var plans = store.State.SubscriptionPlans
        .Where(item => item.Enabled)
        .OrderBy(item => item.SortOrder)
        .ThenBy(item => item.PriceCents)
        .ThenBy(item => item.Name)
        .Select(item => new
        {
            item.Id, item.Name, item.Kind, item.TrafficBytes, item.PriceCents,
            item.Currency, item.SortOrder, item.AllowedNodeIds,
            item.MaxChannels, item.AutoApprove
        });
    var orders = store.State.SubscriptionOrders
        .Where(item => item.AccountId == account.Id)
        .OrderByDescending(item => item.CreatedAt)
        .Select(item => new
        {
            item.Id, item.PlanId, item.PlanName, item.Kind, item.TrafficBytes,
            item.PriceCents, item.Currency, item.Status, item.ReviewNote,
            item.CreatedAt, item.ReviewedAt, item.AppliedExpiresAt,
            item.AllowedNodeIds, item.MaxChannels, item.AutoApprove,
            item.PaymentProvider, item.PaymentStatus, item.OutTradeNo, item.PaidAt
        });
    return Results.Ok(new
    {
        account = new
        {
            account.TrafficQuotaBytes,
            account.TrafficUsedBytes,
            account.SubscriptionExpiresAt,
            account.ActiveSubscriptionName,
            account.AllowedNodeIds,
            account.MaxChannels,
            subscriptionExpired = accounts.IsSubscriptionExpired(account)
        },
        nodes = subscriptions.AvailableNodes().Select(node => new { node.Id, node.Name }),
        paymentConfigured = AlipayPaymentService.IsConfigured(store.State.Alipay),
        plans,
        orders
    });
}).RequireAuthorization(policy => policy.RequireRole("customer"));

app.MapPost("/api/customer/subscriptions/orders", async (
    SubscriptionOrderRequest request,
    ClaimsPrincipal principal,
    AccountService accounts,
    SubscriptionService subscriptions) =>
{
    var account = accounts.Find(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
    if (account is null || !account.Role.Equals("customer", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Unauthorized();
    }
    var result = await subscriptions.SubmitOrderAsync(account, request.PlanId ?? "");
    if (result.Order is null) return Results.BadRequest(new { error = result.Error });
    var order = result.Order;
    var message = order.Status switch
    {
        SubscriptionService.PendingPayment => "订单已创建，请前往支付宝完成支付。",
        SubscriptionService.Approved => "订阅已自动批准并生效。",
        _ => "订阅申请已提交，等待管理员审核。"
    };
    return Results.Ok(new
    {
        order.Id,
        order.Status,
        requiresPayment = order.Status == SubscriptionService.PendingPayment,
        paymentUrl = order.Status == SubscriptionService.PendingPayment
            ? $"/api/customer/subscriptions/orders/{order.Id}/pay"
            : "",
        message
    });
}).RequireAuthorization(policy => policy.RequireRole("customer"));

app.MapGet("/api/customer/subscriptions/orders/{id}/pay", (
    string id,
    ClaimsPrincipal principal,
    AccountService accounts,
    StateStore store,
    AlipayPaymentService payments,
    HttpContext context) =>
{
    var account = accounts.Find(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
    if (account is null || !account.Role.Equals("customer", StringComparison.OrdinalIgnoreCase))
        return Results.Unauthorized();
    var order = store.State.SubscriptionOrders.FirstOrDefault(item =>
        item.Id == id && item.AccountId == account.Id);
    if (order is null) return Results.NotFound();
    try
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var mobile = Regex.IsMatch(userAgent, "Android|iPhone|iPad|Mobile", RegexOptions.IgnoreCase);
        return Results.Content(payments.BuildPaymentHtml(order, mobile), "text/html", Encoding.UTF8);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(policy => policy.RequireRole("customer"));

app.MapPost("/api/payments/alipay/notify", async (
    HttpContext context, AlipayPaymentService payments) =>
{
    try
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var values = form.ToDictionary(item => item.Key, item => item.Value.ToString(), StringComparer.Ordinal);
        var result = await payments.HandleNotificationAsync(values);
        return Results.Text(result.Success ? "success" : "failure", "text/plain", Encoding.UTF8);
    }
    catch
    {
        return Results.Text("failure", "text/plain", Encoding.UTF8);
    }
});

app.MapGet("/api/payments/alipay/return", (
    HttpContext context, AlipayPaymentService payments) =>
{
    var values = context.Request.Query.ToDictionary(
        item => item.Key, item => item.Value.ToString(), StringComparer.Ordinal);
    return Results.Redirect(payments.VerifyReturn(values)
        ? "/?payment=processing"
        : "/?payment=invalid");
});

app.MapPost("/api/customer/password-reset/code", async (
    ClaimsPrincipal principal, AccountService accounts, StateStore store, SmtpService smtp) =>
{
    var account = accounts.Find(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
    if (account is null || !account.Role.Equals("customer", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Unauthorized();
    }
    if (string.IsNullOrWhiteSpace(account.Email))
    {
        return Results.BadRequest(new { error = "当前账号没有绑定邮箱，请联系管理员重置密码。" });
    }
    if (!store.State.Smtp.EmailVerificationEnabled)
    {
        return Results.Json(
            new { error = "平台尚未启用邮箱验证码服务，请联系管理员。" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        await smtp.SendVerificationCodeAsync(
            account.Email, account.Username, SmtpService.PasswordResetPurpose);
        return Results.Ok(new
        {
            message = $"验证码已发送至 {MaskEmail(account.Email)}，{Math.Clamp(store.State.Smtp.VerificationMinutes, 1, 120)} 分钟内有效。"
        });
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(policy => policy.RequireRole("customer"));

app.MapPost("/api/customer/password-reset", async (
    CustomerPasswordResetRequest request,
    HttpContext context,
    AccountService accounts,
    StateStore store,
    SmtpService smtp) =>
{
    var account = accounts.Find(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
    if (account is null || !account.Role.Equals("customer", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Unauthorized();
    }
    if (request.NewPassword?.Length < 10)
    {
        return Results.BadRequest(new { error = "新密码至少需要 10 个字符。" });
    }
    if (string.IsNullOrWhiteSpace(account.Email)
        || !smtp.VerifyCode(
            account.Email, request.VerificationCode ?? "", SmtpService.PasswordResetPurpose))
    {
        await store.SaveAsync();
        return Results.BadRequest(new { error = "邮箱验证码无效、已过期或尝试次数过多。" });
    }

    account.PasswordHash = Security.Hash(request.NewPassword!);
    account.AuthRevision = NextAuthRevision(account.AuthRevision);
    store.State.AccountSessions.RemoveAll(item => item.AccountId == account.Id);
    await store.AuditAsync("security", $"客户账号 {account.Username} 在客户面板重置密码");
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "密码已更新，旧登录授权已全部失效，请重新登录。" });
}).RequireAuthorization(policy => policy.RequireRole("customer"));

app.MapGet("/api/traffic/statistics", async (
    string? range,
    ClaimsPrincipal principal,
    TrafficAccountingService accounting,
    CancellationToken cancellationToken) =>
{
    var role = principal.FindFirstValue(ClaimTypes.Role);
    var accountId = role == "customer"
        ? principal.FindFirstValue(ClaimTypes.NameIdentifier)
        : null;
    if (role is not ("admin" or "customer")
        || (role == "customer" && string.IsNullOrWhiteSpace(accountId)))
    {
        return Results.Unauthorized();
    }

    var statistics = await accounting.GetStatisticsAsync(
        range ?? "24h", accountId, cancellationToken);
    return Results.Ok(statistics);
}).RequireAuthorization();

app.MapGet("/api/admin/accounts", (StateStore store) =>
    Results.Ok(store.State.Accounts.Select(account => new
    {
        account.Id,
        account.Username,
        account.Role,
        account.Enabled,
        account.TrafficQuotaBytes,
        account.TrafficUsedBytes,
        account.CreatedAt,
        account.SubscriptionExpiresAt,
        account.ActiveSubscriptionName,
        account.AllowedNodeIds,
        account.MaxChannels
    }))).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/subscriptions", (
    StateStore store, SubscriptionService subscriptions) =>
{
    var plans = store.State.SubscriptionPlans
        .OrderBy(item => item.SortOrder)
        .ThenBy(item => item.Name)
        .ToArray();
    var orders = store.State.SubscriptionOrders
        .OrderBy(item => item.Status is SubscriptionService.Pending or SubscriptionService.Paid ? 0
            : item.Status == SubscriptionService.PendingPayment ? 1 : 2)
        .ThenByDescending(item => item.CreatedAt)
        .ToArray();
    var nodes = subscriptions.AvailableNodes().Select(node => new { node.Id, node.Name });
    return Results.Ok(new { plans, orders, nodes });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/subscriptions/plans", async (
    SubscriptionPlanRequest request, SubscriptionService subscriptions) =>
{
    var result = await subscriptions.CreatePlanAsync(request);
    return result.Plan is not null
        ? Results.Ok(new { result.Plan.Id, message = "订阅方案已创建。" })
        : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/admin/subscriptions/plans/{id}", async (
    string id, SubscriptionPlanRequest request, SubscriptionService subscriptions) =>
{
    var result = await subscriptions.UpdatePlanAsync(id, request);
    return result.Plan is not null
        ? Results.Ok(new { message = "订阅方案已保存。" })
        : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapDelete("/api/admin/subscriptions/plans/{id}", async (
    string id, SubscriptionService subscriptions) =>
{
    var error = await subscriptions.DeletePlanAsync(id);
    return error is null
        ? Results.Ok(new { message = "订阅方案已删除。" })
        : Results.BadRequest(new { error });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/subscriptions/orders/{id}/review", async (
    string id,
    SubscriptionOrderReviewRequest request,
    ClaimsPrincipal principal,
    SubscriptionService subscriptions) =>
{
    if ((request.Note ?? "").Length > 240)
    {
        return Results.BadRequest(new { error = "审核备注不能超过 240 个字符。" });
    }
    var reviewer = principal.FindFirstValue(ClaimTypes.Name) ?? "admin";
    var result = await subscriptions.ReviewOrderAsync(
        id, request.Approved, reviewer, request.Note);
    return result.Order is not null
        ? Results.Ok(new { message = request.Approved ? "申请已批准并生效。" : "申请已拒绝。" })
        : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/admin/accounts/{id}/subscription", async (
    string id,
    AccountSubscriptionRequest request,
    ClaimsPrincipal principal,
    StateStore store,
    SubscriptionService subscriptions) =>
{
    var account = store.State.Accounts.FirstOrDefault(item => item.Id == id);
    if (account is null) return Results.NotFound();
    if (!account.Role.Equals("customer", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "管理员账号不使用客户订阅权益。" });
    var reviewer = principal.FindFirstValue(ClaimTypes.Name) ?? "admin";
    var error = await subscriptions.UpdateAccountSubscriptionAsync(account, request, reviewer);
    return error is null
        ? Results.Ok(new { message = "账户订阅权益已更新。" })
        : Results.BadRequest(new { error });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/payment-settings", (StateStore store) =>
{
    var settings = store.State.Alipay ?? new();
    var baseUrl = settings.PublicBaseUrl.TrimEnd('/');
    return Results.Ok(new
    {
        settings.Enabled,
        settings.AppId,
        settings.SellerId,
        hasPrivateKey = !string.IsNullOrWhiteSpace(settings.MerchantPrivateKey),
        settings.AlipayPublicKey,
        settings.Gateway,
        settings.PublicBaseUrl,
        configured = AlipayPaymentService.IsConfigured(settings),
        notifyUrl = string.IsNullOrWhiteSpace(baseUrl) ? "" : baseUrl + "/api/payments/alipay/notify",
        returnUrl = string.IsNullOrWhiteSpace(baseUrl) ? "" : baseUrl + "/api/payments/alipay/return"
    });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/admin/payment-settings", async (
    AlipaySettingsRequest request, AlipayPaymentService payments) =>
{
    var error = await payments.SaveSettingsAsync(request);
    return error is null
        ? Results.Ok(new { message = "支付宝支付配置已保存。" })
        : Results.BadRequest(new { error });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/announcements", (StateStore store) =>
    Results.Ok(store.State.Announcements
        .OrderByDescending(item => item.PublishedAt)
        .ToArray())).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/announcements", async (
    AnnouncementRequest request, AnnouncementService announcements) =>
{
    var result = await announcements.CreateAsync(request);
    return result.Announcement is not null
        ? Results.Ok(new { result.Announcement.Id, message = "公告已创建。" })
        : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/admin/announcements/{id}", async (
    string id, AnnouncementRequest request, AnnouncementService announcements) =>
{
    var result = await announcements.UpdateAsync(id, request);
    return result.Announcement is not null
        ? Results.Ok(new { message = "公告已保存。" })
        : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapDelete("/api/admin/announcements/{id}", async (
    string id, AnnouncementService announcements) =>
    await announcements.DeleteAsync(id)
        ? Results.Ok(new { message = "公告已删除。" })
        : Results.NotFound()).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/accounts", async (AccountRequest request, StateStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || request.Password?.Length < 8)
    {
        return Results.BadRequest(new { error = "用户名不能为空，密码至少需要 8 个字符。" });
    }
    if (store.State.Accounts.Any(item =>
        item.Username.Equals(request.Username, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.BadRequest(new { error = "用户名已存在。" });
    }
    var account = new UserAccount
    {
        Username = request.Username.Trim(),
        PasswordHash = Security.Hash(request.Password!),
        Role = request.Role == "admin" ? "admin" : "customer",
        TrafficQuotaBytes = Math.Max(0, request.TrafficQuotaBytes),
        Enabled = request.Enabled
    };
    store.State.Accounts.Add(account);
    await store.AuditAsync("account", $"创建账号 {account.Username}");
    return Results.Ok(new { account.Id });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/admin/accounts/{id}", async (
    string id, AccountRequest request, StateStore store) =>
{
    var account = store.State.Accounts.FirstOrDefault(item => item.Id == id);
    if (account is null)
    {
        return Results.NotFound();
    }
    if (!string.IsNullOrWhiteSpace(request.Password))
    {
        if (request.Password.Length < 8)
        {
            return Results.BadRequest(new { error = "密码至少需要 8 个字符。" });
        }
        account.PasswordHash = Security.Hash(request.Password);
        account.AuthRevision = NextAuthRevision(account.AuthRevision);
        store.State.AccountSessions.RemoveAll(item => item.AccountId == account.Id);
    }
    account.TrafficQuotaBytes = Math.Max(0, request.TrafficQuotaBytes);
    account.Enabled = request.Enabled;
    await store.AuditAsync("account", $"更新账号 {account.Username}");
    return Results.Ok();
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapDelete("/api/admin/accounts/{id}", async (
    string id,
    StateStore store,
    TrafficAccountingService accounting,
    CancellationToken cancellationToken) =>
{
    var account = store.State.Accounts.FirstOrDefault(item => item.Id == id);
    if (account is null)
    {
        return Results.NotFound();
    }
    if (!account.Role.Equals("customer", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "管理员账号不能在客户账号列表中删除。" });
    }

    await accounting.RemoveAccountDataAsync(account.Id, cancellationToken);
    store.State.Accounts.Remove(account);
    store.State.AccountSessions.RemoveAll(item => item.AccountId == account.Id);
    store.State.Clients.RemoveAll(item => item.AccountId == account.Id);
    store.State.Allocations.RemoveAll(item => item.AccountId == account.Id);
    store.State.SubscriptionOrders.RemoveAll(item => item.AccountId == account.Id);
    await store.AuditAsync("account", $"删除客户账号 {account.Username}");
    return Results.Ok(new { message = $"客户账号 {account.Username} 已删除。" });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/accounts/{id}/reset-traffic", async (
    string id,
    StateStore store,
    TrafficAccountingService accounting,
    CancellationToken cancellationToken) =>
{
    var account = store.State.Accounts.FirstOrDefault(item => item.Id == id);
    if (account is null) return Results.NotFound();
    await accounting.ResetAccountAsync(account.Id, cancellationToken);
    await store.AuditAsync("account", $"重置账号 {account.Username} 的流量");
    return Results.Ok();
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/registration-settings", (StateStore store) => Results.Ok(new
{
    enabled = store.State.RegistrationEnabled,
    defaultTrafficQuotaBytes = store.State.RegistrationQuotaBytes
})).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/admin/registration-settings", async (
    RegistrationSettingsRequest request, StateStore store) =>
{
    store.State.RegistrationEnabled = request.Enabled;
    store.State.RegistrationQuotaBytes = Math.Max(0, request.DefaultTrafficQuotaBytes);
    await store.AuditAsync(
        "account",
        $"自助注册已{(request.Enabled ? "开启" : "关闭")}，默认额度 {request.DefaultTrafficQuotaBytes} B");
    return Results.Ok(new
    {
        enabled = store.State.RegistrationEnabled,
        defaultTrafficQuotaBytes = store.State.RegistrationQuotaBytes
    });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/smtp-settings", (StateStore store) =>
{
    var smtp = store.State.Smtp;
    return Results.Ok(new
    {
        smtp.EmailVerificationEnabled, smtp.Host, smtp.Port, smtp.Username,
        hasPassword = !string.IsNullOrWhiteSpace(smtp.Password),
        smtp.FromEmail, smtp.FromName, smtp.EnableSsl, smtp.VerificationMinutes,
        smtp.SubjectTemplate, smtp.HtmlTemplate
    });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/admin/smtp-settings", async (SmtpSettingsRequest request, StateStore store) =>
{
    if (request.Port is < 1 or > 65535)
        return Results.BadRequest(new { error = "SMTP 端口必须在 1 到 65535 之间。" });
    var current = store.State.Smtp;
    var smtp = new SmtpSettings
    {
        EmailVerificationEnabled = request.EmailVerificationEnabled,
        Host = request.Host?.Trim() ?? "",
        Port = request.Port,
        Username = request.Username?.Trim() ?? "",
        Password = string.IsNullOrWhiteSpace(request.Password) ? current.Password : request.Password,
        FromEmail = request.FromEmail?.Trim() ?? "",
        FromName = request.FromName?.Trim() ?? "ZRfrp",
        EnableSsl = request.EnableSsl,
        VerificationMinutes = Math.Clamp(request.VerificationMinutes, 1, 120),
        SubjectTemplate = string.IsNullOrWhiteSpace(request.SubjectTemplate)
            ? "[{{site_name}}] 邮箱验证码" : request.SubjectTemplate,
        HtmlTemplate = string.IsNullOrWhiteSpace(request.HtmlTemplate)
            ? new SmtpSettings().HtmlTemplate : request.HtmlTemplate
    };
    if (smtp.EmailVerificationEnabled
        && (string.IsNullOrWhiteSpace(smtp.Host) || string.IsNullOrWhiteSpace(smtp.FromEmail)))
        return Results.BadRequest(new { error = "开启邮箱验证前请完整配置 SMTP 主机和发件人邮箱。" });
    store.State.Smtp = smtp;
    await store.AuditAsync("security", $"SMTP 设置已更新，邮箱验证{(smtp.EmailVerificationEnabled ? "已开启" : "已关闭")}");
    return Results.Ok(new { message = "SMTP 设置已保存。" });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/smtp-test", async (TestEmailRequest request, SmtpService smtp) =>
{
    try
    {
        await smtp.SendTestAsync(request.RecipientEmail ?? "");
        return Results.Ok(new { message = "测试邮件已发送。" });
    }
    catch (Exception exception) { return Results.BadRequest(new { error = $"发送失败：{exception.Message}" }); }
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/traffic-status", (TrafficCollector collector, FrpsManager frps) =>
{
    var healthy = collector.LastSuccessAt is not null && string.IsNullOrWhiteSpace(collector.LastError);
    var message = healthy
        ? $"最近成功：{collector.LastSuccessAt:yyyy-MM-dd HH:mm:ss} UTC；Dashboard 通道 {collector.LastDashboardProxyCount}，匹配账号样本 {collector.LastMatchedSampleCount}，未匹配 {collector.LastUnmatchedProxyCount}，本轮新增 {collector.LastAppliedBytes} B。"
        : $"采集异常：{collector.LastError} {frps.LastDashboardError}".Trim();
    if (healthy && collector.LastUnmatchedProxyCount > 0)
    {
        message += $" 未匹配摘要：{collector.LastUnmatchedSummary}";
    }
    return Results.Ok(new
    {
        healthy, message, collector.LastAttemptAt, collector.LastSuccessAt,
        collector.LastDashboardProxyCount, collector.LastMatchedSampleCount,
        collector.LastUnmatchedProxyCount, collector.LastUnmatchedSummary,
        collector.LastAppliedBytes, collector.LastError, frps.LastDashboardError
    });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/session-settings", (StateStore store, ServerOptions serverOptions) => Results.Ok(new
{
    sessionHours = store.State.SessionHours > 0
        ? store.State.SessionHours
        : Math.Clamp(serverOptions.SessionHours, 1, 8760)
})).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/admin/session-settings", async (
    SessionSettingsRequest request, StateStore store) =>
{
    if (request.SessionHours is < 1 or > 8760)
    {
        return Results.BadRequest(new { error = "Desktop 登录授权时间必须在 1 到 8760 小时之间。" });
    }
    store.State.SessionHours = request.SessionHours;
    await store.AuditAsync("security", $"Desktop 登录授权时间调整为 {request.SessionHours} 小时");
    return Results.Ok(new { sessionHours = store.State.SessionHours });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/config/model", async (FrpsConfigService config) =>
    Results.Ok(await config.ReadModelAsync()))
    .RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/config/model", async (
    FrpsConfigModel model, FrpsConfigService config, FrpsManager frps, StateStore store) =>
{
    if (model.BindPort is < 1 or > 65535
        || model.PortRangeStart < 1
        || model.PortRangeEnd > 65535
        || model.PortRangeStart > model.PortRangeEnd)
    {
        return Results.BadRequest(new { error = "端口配置无效。" });
    }
    var result = await frps.SaveConfigAsync(config.Render(model), true);
    if (result.Success)
    {
        frps.ApplyConfig(model);
    }
    await store.AuditAsync("config", result.Message);
    return result.Success
        ? Results.Ok(new { message = result.Message })
        : Results.BadRequest(new { error = result.Message });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/frps/install-status", async (FrpsManager frps, CancellationToken cancellationToken) =>
    Results.Ok(await frps.GetInstallStatusAsync(cancellationToken)))
    .RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/frps/install", async (FrpsManager frps, StateStore store) =>
{
    var result = await frps.InstallAsync();
    await store.AuditAsync("install", result.Output);
    return result.ExitCode == 0
        ? Results.Ok(new { message = "frps 已安装并启动。" })
        : Results.BadRequest(new { error = result.Output });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/frps/repair", async (FrpsManager frps, StateStore store, CancellationToken cancellationToken) =>
{
    var result = await frps.RepairAsync();
    await store.AuditAsync("repair", result.Output);
    if (result.ExitCode != 0)
    {
        return Results.BadRequest(new { error = result.Output });
    }

    var status = await frps.GetInstallStatusAsync(cancellationToken);
    return status.Reachable
        ? Results.Ok(new { message = "frps 已修复并启动。", status })
        : Results.BadRequest(new { error = $"修复命令已完成，但 frps 仍不可连接：{status.Message}" });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/update", async (UpdateService updates, CancellationToken cancellationToken) =>
    Results.Ok(await updates.CheckAsync(cancellationToken))).RequireAuthorization();

app.MapPost("/api/update", async (ClaimsPrincipal principal, FrpsManager frps) =>
{
    if (!principal.IsInRole("admin"))
    {
        return Results.Forbid();
    }
    var result = await frps.ScheduleServerUpdateAsync();
    return result.ExitCode == 0
        ? Results.Ok(new { message = "更新已下载，服务即将重启。" })
        : Results.BadRequest(new { error = result.Output });
}).RequireAuthorization();

app.MapGet("/api/admin/nodes", (StateStore store, ServerOptions serverOptions) =>
{
    var cutoff = DateTimeOffset.UtcNow.AddSeconds(-45);
    return Results.Ok(store.State.Nodes.Select(node => new
    {
        node.Id,
        name = PlainNodeName(node.Name),
        flagCode = NodeFlagCode(node.FlagCode, node.Name),
        node.PublicHost,
        node.ControlUrl,
        node.FrpsPort,
        online = node.Online && node.LastSeen >= cutoff,
        node.FrpsOnline,
        node.ActiveClients,
        node.ActiveProxies,
        node.LastSeen,
        node.Version
    }).ToArray());
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/nodes/enrollment", async (
    NodeEnrollmentRequest request, StateStore store, ServerOptions serverOptions) =>
{
    var name = request.Name?.Trim() ?? "";
    var publicHost = request.PublicHost?.Trim() ?? "";
    var masterUrl = request.MasterUrl?.Trim().TrimEnd('/') ?? "";
    if (name.Length is < 2 or > 64 || string.IsNullOrWhiteSpace(publicHost)
        || publicHost.Any(char.IsWhiteSpace)
        || publicHost.Contains('/') || publicHost.Contains('\\'))
    {
        return Results.BadRequest(new { error = "请填写有效的节点名称和公网地址。" });
    }
    if (!Uri.TryCreate(masterUrl, UriKind.Absolute, out var masterUri)
        || masterUri.Scheme is not ("http" or "https"))
    {
        return Results.BadRequest(new { error = "主控面板地址必须是可被新节点访问的 http:// 或 https:// 地址。" });
    }
    if (string.IsNullOrWhiteSpace(serverOptions.PeerKey))
    {
        return Results.BadRequest(new { error = "主控尚未配置节点 Peer Key，请重新安装或检查服务端配置。" });
    }
    var rid = request.Architecture == "linux-arm64" ? "linux-arm64" : "linux-x64";
    var frpArch = rid == "linux-arm64" ? "arm64" : "amd64";

    var id = $"node-{Guid.NewGuid():N}"[..17];
    var enrollmentToken = Security.CreateSecret(32);
    var node = new ManagedNode
    {
        Id = id,
        Name = name,
        FlagCode = NodeFlagCode(request.FlagCode, name),
        PublicHost = publicHost,
        PublicHostLocked = true,
        ControlUrl = $"http://{publicHost}:7600",
        FrpsPort = serverOptions.FrpsBindPort,
        Online = false,
        FrpsOnline = false,
        LastSeen = DateTimeOffset.UtcNow,
        EnrollmentTokenHash = Security.HashToken(enrollmentToken),
        EnrollmentExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
        EnrollmentMasterUrl = masterUri.ToString().TrimEnd('/')
    };
    store.State.Nodes.Add(node);
    await store.AuditAsync("node", $"创建待接入节点 {name} ({publicHost})");

    var bootstrapPrefix =
        $"{node.EnrollmentMasterUrl}/api/bootstrap/node/{Uri.EscapeDataString(enrollmentToken)}";
    var serverFileName = $"zrfrp-server-{rid}.tar.gz";
    var frpFileName = $"frp_{BootstrapPackageService.FrpVersion}_linux_{frpArch}.tar.gz";

    return Results.Ok(new NodeEnrollmentResponse(
        id,
        name,
        CreateNodeEnrollmentCommand(node.EnrollmentMasterUrl, enrollmentToken),
        $"{bootstrapPrefix}/offline/{rid}.sh",
        $"https://github.com/masZR-art/ZRfrp/releases/download/v{UpdateService.CurrentVersion}/{serverFileName}",
        $"https://github.com/fatedier/frp/releases/download/v{BootstrapPackageService.FrpVersion}/{frpFileName}",
        serverFileName,
        frpFileName));
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/nodes/export", (
    StateStore store, ServerOptions serverOptions, HttpContext context) =>
    Results.Ok(CreateNodeExport(store, serverOptions, context)))
    .RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/admin/nodes/{id}", async (
    string id, NodeUpdateRequest request, StateStore store, ServerOptions serverOptions) =>
{
    var name = PlainNodeName(request.Name);
    var flagCode = NodeFlagCode(request.FlagCode, request.Name);
    var publicHost = request.PublicHost?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(name))
    {
        return Results.BadRequest(new { error = "节点名称不能为空。" });
    }
    if (!string.IsNullOrWhiteSpace(publicHost)
        && (publicHost.Any(char.IsWhiteSpace) || publicHost.Contains('/') || publicHost.Contains('\\')))
    {
        return Results.BadRequest(new { error = "公网地址格式无效。" });
    }
    if (id.Equals("local", StringComparison.OrdinalIgnoreCase))
    {
        store.State.LocalNodeName = name;
        store.State.LocalNodeFlagCode = flagCode;
        await store.AuditAsync("node", $"更新本机节点名称为 {store.State.LocalNodeName}");
        return Results.Ok(new { name = LocalNodeName(store, serverOptions), flagCode = LocalNodeFlagCode(store, serverOptions) });
    }

    var node = store.State.Nodes.FirstOrDefault(item => item.Id == id);
    if (node is null)
    {
        return Results.NotFound();
    }
    node.Name = name;
    node.FlagCode = flagCode;
    if (!string.IsNullOrWhiteSpace(publicHost))
    {
        node.PublicHost = publicHost;
        node.ControlUrl = $"http://{publicHost}:7600";
        node.PublicHostLocked = true;
    }
    await store.AuditAsync("node", $"更新节点名称为 {node.Name}");
    return Results.Ok(new { node.Name, node.FlagCode, node.PublicHost });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapDelete("/api/admin/nodes/{id}", async (string id, StateStore store) =>
{
    if (id.Equals("local", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "本机节点不能删除。" });
    }
    var node = store.State.Nodes.FirstOrDefault(item => item.Id == id);
    if (node is null)
    {
        return Results.NotFound();
    }
    store.State.Nodes.Remove(node);
    store.State.RevokedNodeIds.Add(id);
    store.State.Allocations.RemoveAll(item => item.NodeId == id);
    await store.AuditAsync("node", $"已删除节点 {node.Name}，该节点将无法重新注册。");
    return Results.Ok(new { message = "节点已删除。" });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/nodes/{id}/service/{action}", async (
    string id, string action, StateStore store, ServerOptions serverOptions) =>
{
    var node = store.State.Nodes.FirstOrDefault(item => item.Id == id);
    if (node is null || string.IsNullOrWhiteSpace(node.ControlUrl))
    {
        return Results.NotFound();
    }
    if (action is not ("start" or "stop" or "restart"))
    {
        return Results.BadRequest(new { error = "不支持的节点操作。" });
    }
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    using var request = new HttpRequestMessage(
        HttpMethod.Post, $"{node.ControlUrl.TrimEnd('/')}/api/peer/service/{action}");
    request.Headers.Add("X-ZRfrp-Peer-Key", serverOptions.PeerKey);
    using var response = await client.SendAsync(request);
    if (response.IsSuccessStatusCode)
    {
        return Results.Ok(new { message = $"节点 {node.Name} 已执行 {action}。" });
    }
    var errorBody = await response.Content.ReadAsStringAsync();
    try
    {
        using var document = JsonDocument.Parse(errorBody);
        errorBody = document.RootElement.TryGetProperty("error", out var error)
            ? error.GetString() ?? errorBody
            : errorBody;
    }
    catch (JsonException)
    {
        // Keep the remote response when it is not JSON.
    }
    return Results.BadRequest(new { error = $"节点 {node.Name} 的 frps 操作失败：{errorBody}" });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/peer/heartbeat", async (
    NodeHeartbeat heartbeat, HttpContext context, StateStore store, ServerOptions serverOptions) =>
{
    if (!ValidatePeerKey(context, serverOptions.PeerKey))
    {
        return Results.Unauthorized();
    }
    if (store.State.RevokedNodeIds.Contains(heartbeat.Id, StringComparer.Ordinal))
    {
        return Results.StatusCode(StatusCodes.Status410Gone);
    }
    var node = store.State.Nodes.FirstOrDefault(item => item.Id == heartbeat.Id);
    if (node is null)
    {
        node = new ManagedNode
        {
            Id = heartbeat.Id,
            Name = PlainNodeName(heartbeat.Name),
            PublicHost = heartbeat.PublicHost,
            ControlUrl = heartbeat.ControlUrl
        };
        store.State.Nodes.Add(node);
    }
    if (string.IsNullOrWhiteSpace(node.Name))
    {
        node.Name = PlainNodeName(heartbeat.Name);
    }
    if (!node.PublicHostLocked)
    {
        node.PublicHost = heartbeat.PublicHost;
        node.ControlUrl = heartbeat.ControlUrl;
    }
    node.FrpsPort = heartbeat.FrpsPort;
    node.Online = true;
    node.FrpsOnline = heartbeat.FrpsOnline ?? heartbeat.Online;
    node.ActiveClients = heartbeat.ActiveClients;
    node.ActiveProxies = heartbeat.ActiveProxies;
    node.Version = heartbeat.Version;
    if (!string.IsNullOrWhiteSpace(heartbeat.FrpAuthToken))
    {
        node.FrpAuthToken = heartbeat.FrpAuthToken;
    }
    node.LastSeen = DateTimeOffset.UtcNow;
    node.EnrollmentTokenHash = "";
    node.EnrollmentExpiresAt = default;
    await store.SaveAsync();
    return Results.Ok();
});

app.MapPost("/api/peer/service/{action}", async (
    string action, HttpContext context, FrpsManager frps, ServerOptions serverOptions) =>
{
    if (!ValidatePeerKey(context, serverOptions.PeerKey))
    {
        return Results.Unauthorized();
    }
    var result = await frps.ServiceActionAsync(action);
    return result.ExitCode == 0 ? Results.Ok() : Results.BadRequest(new { error = result.Output });
});

app.MapPost("/api/client/refresh", async (
    ClientRefreshRequest request, AccountService accounts, ServerOptions serverOptions) =>
{
    var session = await accounts.RefreshSessionAsync(request.RefreshToken ?? "");
    if (session is null)
        return Results.Json(new { error = "登录授权已失效，请重新登录。" }, statusCode: 401);
    var value = session.Value;
    return Results.Ok(new ClientLoginResponse(
        value.Account.Id, value.Account.Username, value.Token, value.ExpiresAt,
        value.RefreshToken, value.RefreshExpiresAt,
        string.IsNullOrWhiteSpace(serverOptions.PublicHost) ? serverOptions.FrpsAddress : serverOptions.PublicHost,
        serverOptions.FrpsBindPort, serverOptions.FrpAuthToken,
        value.Account.TrafficQuotaBytes, value.Account.TrafficUsedBytes));
});

app.MapPost("/api/peer/account/validate", (
    PeerAccountValidationRequest request,
    HttpContext context,
    AccountService accounts,
    ServerOptions serverOptions) =>
{
    if (!ValidatePeerKey(context, serverOptions.PeerKey))
    {
        return Results.Unauthorized();
    }
    var account = accounts.ValidateAccessToken(request.AccessToken ?? "");
    return account is null
        ? Results.Unauthorized()
        : Results.Ok(new PeerAccountValidationResponse(
            account.Id, account.Username, account.TrafficQuotaBytes, account.TrafficUsedBytes));
});

app.MapPost("/api/peer/traffic", async (
    PeerTrafficReport report,
    HttpContext context,
    StateStore store,
    TrafficAccountingService accounting,
    ServerOptions serverOptions,
    CancellationToken cancellationToken) =>
{
    if (!ValidatePeerKey(context, serverOptions.PeerKey))
    {
        return Results.Unauthorized();
    }
    if (string.IsNullOrWhiteSpace(report.NodeId)
        || store.State.RevokedNodeIds.Contains(report.NodeId, StringComparer.Ordinal))
    {
        return Results.StatusCode(StatusCodes.Status410Gone);
    }
    if (!store.State.Nodes.Any(node => node.Id == report.NodeId))
    {
        return Results.NotFound(new { error = "节点尚未在主控注册。" });
    }

    var appliedBytes = await accounting.ApplyAsync(
        report.NodeId, report.Samples ?? [], cancellationToken);
    return Results.Ok(new { appliedBytes });
});

app.MapPost("/api/peer/allocate", async (
    PeerAllocationRequest request,
    HttpContext context,
    AllocationService allocations,
    ServerOptions serverOptions,
    CancellationToken cancellationToken) =>
{
    if (!ValidatePeerKey(context, serverOptions.PeerKey))
    {
        return Results.Unauthorized();
    }
    var account = string.IsNullOrWhiteSpace(request.AccountId)
        ? null
        : new UserAccount { Id = request.AccountId, Enabled = true };
    var localRequest = request.Allocation with { NodeId = LocalNodeId(serverOptions) };
    var result = await allocations.AllocateAsync(localRequest, cancellationToken, account);
    return result.Result is not null
        ? Results.Ok(result.Result)
        : Results.BadRequest(new { error = result.Error });
});

app.MapGet("/api/peer/allocations", (
    HttpContext context, StateStore store, ServerOptions serverOptions) =>
{
    if (!ValidatePeerKey(context, serverOptions.PeerKey))
    {
        return Results.Unauthorized();
    }
    return Results.Ok(store.State.Allocations.Where(item => item.Active).ToArray());
});

app.MapDelete("/api/peer/allocations/{id}", async (
    string id, HttpContext context, AllocationService allocations, ServerOptions serverOptions) =>
{
    if (!ValidatePeerKey(context, serverOptions.PeerKey))
    {
        return Results.Unauthorized();
    }
    return await allocations.ReleaseAsync(id) ? Results.Ok() : Results.NotFound();
});

app.MapPost("/api/client/allocate", async (
    AllocationRequest request,
    HttpContext context,
    AllocationService allocations,
    AccountService accounts,
    SubscriptionService subscriptions,
    AccountResolver accountResolver,
    StateStore store,
    ServerOptions serverOptions,
    CancellationToken cancellationToken) =>
{
    var account = await accountResolver.ResolveAsync(GetBearerToken(context), cancellationToken);
    if (account is null && !HasClientKey(context, stateStore.State.ClientApiKeyHash))
    {
        return Results.Json(new { error = "登录会话已失效，请重新登录。" }, statusCode: 401);
    }
    if (account is not null && accounts.IsQuotaExceeded(account))
    {
        return Results.Json(new { error = "账号流量额度已用尽。" }, statusCode: 403);
    }
    var requestedNodeId = string.IsNullOrWhiteSpace(request.NodeId)
        ? LocalNodeId(serverOptions)
        : request.NodeId.Trim();
    if (account is not null && !subscriptions.IsNodeAllowed(account, requestedNodeId))
    {
        return Results.Json(new { error = "当前订阅不包含所选服务节点。" }, statusCode: 403);
    }
    if (account is not null && account.MaxChannels > 0)
    {
        var managed = await GetManagedAllocationsAsync(store, serverOptions, cancellationToken);
        var activeKeys = managed
            .Where(item => item.Active && item.AccountId == account.Id)
            .Select(item => $"{item.ClientId}\u001f{item.TunnelId}")
            .ToHashSet(StringComparer.Ordinal);
        var requestedKey = $"{request.ClientId}\u001f{request.TunnelId}";
        if (!activeKeys.Contains(requestedKey) && activeKeys.Count >= account.MaxChannels)
        {
            return Results.Json(new
            {
                error = $"当前订阅最多允许 {account.MaxChannels} 个通道，请先关闭其他通道。"
            }, statusCode: 403);
        }
    }
    if (requestedNodeId.Equals(LocalNodeId(serverOptions), StringComparison.Ordinal))
    {
        var result = await allocations.AllocateAsync(
            request with { NodeId = requestedNodeId }, cancellationToken, account);
        return result.Result is not null
            ? Results.Ok(result.Result)
            : Results.BadRequest(new { error = result.Error });
    }

    var cutoff = DateTimeOffset.UtcNow.AddSeconds(-45);
    var node = store.State.Nodes.FirstOrDefault(item =>
        item.Id.Equals(requestedNodeId, StringComparison.Ordinal)
        && item.Online && item.FrpsOnline && item.LastSeen >= cutoff
        && !string.IsNullOrWhiteSpace(item.ControlUrl));
    if (node is null)
    {
        return Results.BadRequest(new { error = "所选远程节点离线或不可用。" });
    }

    var staleLocalAllocations = store.State.Allocations.Where(item =>
        item.Active
        && item.ClientId.Equals(request.ClientId, StringComparison.Ordinal)
        && item.TunnelId.Equals(request.TunnelId, StringComparison.Ordinal)
        && (string.IsNullOrWhiteSpace(item.NodeId)
            || item.NodeId.Equals(LocalNodeId(serverOptions), StringComparison.Ordinal))).ToArray();
    if (staleLocalAllocations.Length > 0)
    {
        foreach (var stale in staleLocalAllocations)
        {
            stale.Active = false;
            stale.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await store.SaveAsync();
    }

    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    using var peerRequest = new HttpRequestMessage(
        HttpMethod.Post, node.ControlUrl.TrimEnd('/') + "/api/peer/allocate");
    peerRequest.Headers.Add("X-ZRfrp-Peer-Key", serverOptions.PeerKey);
    peerRequest.Content = JsonContent.Create(new PeerAllocationRequest(
        request with { NodeId = requestedNodeId }, account?.Id ?? ""));
    using var peerResponse = await client.SendAsync(peerRequest, cancellationToken);
    var peerBody = await peerResponse.Content.ReadAsStringAsync(cancellationToken);
    if (!peerResponse.IsSuccessStatusCode)
    {
        return Results.BadRequest(new { error = ReadRemoteError(peerBody, "远程节点拒绝了端口分配。") });
    }
    var peerAllocation = JsonSerializer.Deserialize<AllocationResponse>(
        peerBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (peerAllocation is not null
        && !string.Equals(peerAllocation.NodeId, requestedNodeId, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "远程节点返回了不匹配的节点标识，分配已拒绝。" });
    }
    if (peerAllocation is null)
    {
        return Results.BadRequest(new { error = "远程节点返回了无效分配结果。" });
    }

    // The master owns the externally reachable address. A cloud node may only
    // see its private NIC address, so never leak the node-local discovery result
    // into the desktop allocation response.
    var publicAllocation = peerAllocation with
    {
        NodeName = DecoratedNodeName(
            string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name,
            NodeFlagCode(node.FlagCode, node.Name)),
        ServerAddress = node.PublicHost,
        ServerPort = node.FrpsPort > 0 ? node.FrpsPort : serverOptions.FrpsBindPort,
        Locked = true,
        NodeId = node.Id
    };
    return Results.Ok(publicAllocation);
});

app.MapDelete("/api/client/allocations/{id}", async (
    string id,
    string? nodeId,
    HttpContext context,
    AllocationService allocations,
    AccountService accounts,
    StateStore store,
    ServerOptions serverOptions,
    CancellationToken cancellationToken) =>
{
    if (GetBearerAccount(context, accounts) is null
        && !HasClientKey(context, stateStore.State.ClientApiKeyHash))
    {
        return Results.Json(new { error = "登录会话已失效，请重新登录。" }, statusCode: 401);
    }
    var requestedNodeId = string.IsNullOrWhiteSpace(nodeId) ? LocalNodeId(serverOptions) : nodeId.Trim();
    if (requestedNodeId.Equals(LocalNodeId(serverOptions), StringComparison.Ordinal))
    {
        return await allocations.ReleaseAsync(id) ? Results.Ok() : Results.NotFound();
    }
    var node = store.State.Nodes.FirstOrDefault(item =>
        item.Id.Equals(requestedNodeId, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(item.ControlUrl));
    if (node is null)
    {
        return Results.NotFound();
    }
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    using var peerRequest = new HttpRequestMessage(
        HttpMethod.Delete, $"{node.ControlUrl.TrimEnd('/')}/api/peer/allocations/{Uri.EscapeDataString(id)}");
    peerRequest.Headers.Add("X-ZRfrp-Peer-Key", serverOptions.PeerKey);
    using var peerResponse = await client.SendAsync(peerRequest, cancellationToken);
    return peerResponse.IsSuccessStatusCode ? Results.Ok() : Results.StatusCode((int)peerResponse.StatusCode);
});

app.MapPost("/frp-plugin", async (
    HttpContext context,
    AllocationService allocations,
    AccountService accounts,
    AccountResolver accountResolver,
    StateStore store) =>
{
    if (context.Connection.RemoteIpAddress is null
        || !System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    var operation = context.Request.Query["op"].ToString();
    var body = await JsonNode.ParseAsync(context.Request.Body) as JsonObject;
    var content = body?["content"] as JsonObject;
    if (content is null)
    {
        return Results.BadRequest();
    }

    if (operation == "Login")
    {
        var metas = content["metas"] as JsonObject;
        var accessToken = metas?["zrfrp_access_token"]?.GetValue<string>() ?? "";
        var account = await accountResolver.ResolveAsync(accessToken, context.RequestAborted);
        if (account is null)
        {
            return Results.Ok(new { reject = true, reject_reason = "ZRfrp: account session is invalid or expired" });
        }
        if (accounts.IsQuotaExceeded(account))
        {
            return Results.Ok(new { reject = true, reject_reason = "ZRfrp: traffic quota exceeded" });
        }
        await TrackClientAsync(store, account, context, content);
        return Results.Ok(new { reject = false, unchange = true });
    }

    if (operation == "Ping")
    {
        var metas = FindMetas(content);
        var accessToken = metas?["zrfrp_access_token"]?.GetValue<string>() ?? "";
        var account = await accountResolver.ResolveAsync(accessToken, context.RequestAborted);
        if (account is not null)
        {
            await TrackClientAsync(store, account, context, content);
        }
        return Results.Ok(new { reject = false, unchange = true });
    }

    if (operation == "NewProxy")
    {
        var user = content["user"] as JsonObject;
        var metas = user?["metas"] as JsonObject;
        var clientId = metas?["zrfrp_client_id"]?.GetValue<string>() ?? "";
        var accessToken = metas?["zrfrp_access_token"]?.GetValue<string>() ?? "";
        var account = await accountResolver.ResolveAsync(accessToken, context.RequestAborted);
        if (account is null || accounts.IsQuotaExceeded(account))
        {
            return Results.Ok(new { reject = true, reject_reason = "ZRfrp: account is unavailable or over quota" });
        }
        var proxyName = content["proxy_name"]?.GetValue<string>() ?? "";
        var proxyMetas = content["metas"] as JsonObject;
        var tunnelId = proxyMetas?["zrfrp_tunnel_id"]?.GetValue<string>() ?? "";
        var allocation = allocations.FindForPlugin(clientId, proxyName, tunnelId);
        if (allocation is null || allocation.AccountId != account.Id)
        {
            return Results.Ok(new { reject = true, reject_reason = "ZRfrp: tunnel has no active server allocation" });
        }

        content["remote_port"] = allocation.RemotePort;
        if (!string.IsNullOrWhiteSpace(allocation.BandwidthLimit))
        {
            content["bandwidth_limit"] = allocation.BandwidthLimit;
            content["bandwidth_limit_mode"] = "server";
        }
        return Results.Ok(new { reject = false, unchange = false, content });
    }

    if (operation == "NewUserConn")
    {
        var user = content["user"] as JsonObject;
        var metas = user?["metas"] as JsonObject;
        var accessToken = metas?["zrfrp_access_token"]?.GetValue<string>() ?? "";
        var account = await accountResolver.ResolveAsync(accessToken, context.RequestAborted);
        if (account is null || accounts.IsQuotaExceeded(account))
        {
            return Results.Ok(new { reject = true, reject_reason = "ZRfrp: traffic quota exceeded" });
        }
    }

    return Results.Ok(new { reject = false, unchange = true });
});

app.MapFallbackToFile("index.html");
app.Run();

static bool HasClientKey(HttpContext context, string hash)
{
    var key = context.Request.Headers["X-ZRfrp-Key"].ToString();
    return !string.IsNullOrWhiteSpace(key) && Security.Verify(key, hash);
}

static string ReadFrpsAuthToken(string configPath, string fallback)
{
    try
    {
        if (!File.Exists(configPath))
        {
            return fallback;
        }
        var text = File.ReadAllText(configPath);
        var match = Regex.Match(
            text,
            @"(?m)^\s*auth\.token\s*=\s*""(?<value>(?:\\.|[^""])*)""");
        return match.Success
            ? match.Groups["value"].Value.Replace("\\\"", "\"")
            : fallback;
    }
    catch
    {
        return fallback;
    }
}

static UserAccount? GetBearerAccount(HttpContext context, AccountService accounts)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? accounts.ValidateAccessToken(authorization[7..].Trim())
        : null;
}

static string GetBearerToken(HttpContext context)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? authorization[7..].Trim()
        : "";
}

static string LocalNodeId(ServerOptions options) =>
    string.IsNullOrWhiteSpace(options.NodeId) ? "local" : options.NodeId;

static string ReadRemoteError(string body, string fallback)
{
    try
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("error", out var error)
            ? error.GetString() ?? fallback
            : fallback;
    }
    catch (JsonException)
    {
        return string.IsNullOrWhiteSpace(body) ? fallback : body;
    }
}

static bool DashboardHasItems(JsonElement? element)
{
    if (element is null)
    {
        return false;
    }

    var value = element.Value;
    if (value.ValueKind == JsonValueKind.Array)
    {
        return value.GetArrayLength() > 0;
    }

    if (value.ValueKind != JsonValueKind.Object)
    {
        return false;
    }

    foreach (var propertyName in new[] { "clients" })
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            continue;
        }
        if (property.ValueKind == JsonValueKind.Array && property.GetArrayLength() > 0)
        {
            return true;
        }
    }

    return false;
}

static async Task TrackClientAsync(StateStore store, UserAccount account, HttpContext context, JsonObject content)
{
    var clientId = ReadClientId(content);
    if (string.IsNullOrWhiteSpace(clientId))
    {
        clientId = account.Id;
    }

    var now = DateTimeOffset.UtcNow;
    var client = store.State.Clients.FirstOrDefault(item =>
        item.ClientId.Equals(clientId, StringComparison.Ordinal));
    if (client is null)
    {
        client = new ManagedClient
        {
            ClientId = clientId,
            ConnectedAt = now
        };
        store.State.Clients.Add(client);
    }

    client.AccountId = account.Id;
    client.Username = account.Username;
    client.Address = context.Connection.RemoteIpAddress?.ToString() ?? "";
    client.Protocol = "frpc";
    client.LastSeen = now;
    await store.SaveAsync();
}

static string ReadClientId(JsonObject content)
{
    var metas = FindMetas(content);
    var fromMeta = metas?["zrfrp_client_id"]?.GetValue<string>() ?? "";
    if (!string.IsNullOrWhiteSpace(fromMeta))
    {
        return fromMeta;
    }

    foreach (var key in new[] { "client_id", "clientID", "run_id", "runId" })
    {
        if (content[key] is JsonValue value && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
    }

    return "";
}

static JsonObject? FindMetas(JsonObject content)
{
    if (content["metas"] is JsonObject metas)
    {
        return metas;
    }
    if (content["user"] is JsonObject user && user["metas"] is JsonObject userMetas)
    {
        return userMetas;
    }
    return null;
}

static bool ValidatePeerKey(HttpContext context, string expected)
{
    if (string.IsNullOrWhiteSpace(expected))
    {
        return false;
    }
    var supplied = context.Request.Headers["X-ZRfrp-Peer-Key"].ToString();
    var left = System.Text.Encoding.UTF8.GetBytes(supplied);
    var right = System.Text.Encoding.UTF8.GetBytes(expected);
    return left.Length == right.Length
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
}

static NodeExportDocument CreateNodeExport(
    StateStore store, ServerOptions options, HttpContext context, UserAccount? account = null)
{
    var platformUrl = ExternalBaseUrl(context, options);
    var cutoff = DateTimeOffset.UtcNow.AddSeconds(-45);
    var allowedNodeIds = account?.AllowedNodeIds ?? [];
    bool IsAllowed(string nodeId) => account is null
        || allowedNodeIds.Count == 0
        || allowedNodeIds.Contains(nodeId, StringComparer.Ordinal);
    var localNodeId = string.IsNullOrWhiteSpace(options.NodeId) ? "local" : options.NodeId;
    var nodes = new List<NodeExportEntry>();
    if (IsAllowed(localNodeId))
    {
        nodes.Add(new NodeExportEntry(
            localNodeId,
            DecoratedNodeName(LocalNodeName(store, options), LocalNodeFlagCode(store, options)),
            PublicFrpsHost(options),
            options.FrpsBindPort,
            options.FrpAuthToken,
            platformUrl));
    }

    nodes.AddRange(store.State.Nodes
        .Where(node => node.Online && node.FrpsOnline && node.LastSeen >= cutoff
            && !string.IsNullOrWhiteSpace(node.PublicHost)
            && IsAllowed(node.Id))
        .Select(node => new NodeExportEntry(
            node.Id,
            DecoratedNodeName(
                string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name,
                NodeFlagCode(node.FlagCode, node.Name)),
            node.PublicHost,
            node.FrpsPort > 0 ? node.FrpsPort : options.FrpsBindPort,
            string.IsNullOrWhiteSpace(node.FrpAuthToken) ? options.FrpAuthToken : node.FrpAuthToken,
            platformUrl)));

    return new NodeExportDocument("zrfrp-node-export", 1, platformUrl, DateTimeOffset.UtcNow, nodes);
}

static (long In, long Out) SumProxyTraffic(params JsonElement?[] documents)
{
    long trafficIn = 0;
    long trafficOut = 0;
    foreach (var document in documents)
    {
        if (document is null) continue;
        var root = document.Value;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("proxies", out var proxies)
            || proxies.ValueKind != JsonValueKind.Array) continue;
        foreach (var proxy in proxies.EnumerateArray())
        {
            trafficIn = SaturatingAdd(trafficIn, ReadTrafficValue(proxy, "todayTrafficIn", "today_traffic_in"));
            trafficOut = SaturatingAdd(trafficOut, ReadTrafficValue(proxy, "todayTrafficOut", "today_traffic_out"));
        }
    }
    return (trafficIn, trafficOut);
}

static long ReadTrafficValue(JsonElement element, params string[] names)
{
    foreach (var name in names)
    {
        if (!element.TryGetProperty(name, out var value)) continue;
        if (value.TryGetInt64(out var number)) return Math.Max(0, number);
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            return Math.Max(0, number);
    }
    return 0;
}

static long SaturatingAdd(long left, long right) =>
    right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;

static int NextAuthRevision(int current) => current == int.MaxValue ? 1 : current + 1;

static string MaskEmail(string email)
{
    if (string.IsNullOrWhiteSpace(email))
    {
        return "未绑定";
    }
    var separator = email.IndexOf('@');
    if (separator <= 0 || separator == email.Length - 1)
    {
        return "***";
    }
    var local = email[..separator];
    var visible = local.Length <= 2 ? 1 : 2;
    return $"{local[..Math.Min(visible, local.Length)]}***{email[separator..]}";
}

static async Task<List<PortAllocation>> GetManagedAllocationsAsync(
    StateStore store, ServerOptions options, CancellationToken cancellationToken)
{
    var localNodeId = LocalNodeId(options);
    var localNodeName = LocalNodeName(store, options);
    var result = store.State.Allocations.Where(item => item.Active).ToList();
    foreach (var allocation in result)
    {
        if (string.IsNullOrWhiteSpace(allocation.NodeId))
        {
            allocation.NodeId = localNodeId;
        }
        if (string.IsNullOrWhiteSpace(allocation.NodeName))
        {
            allocation.NodeName = localNodeName;
        }
    }

    var cutoff = DateTimeOffset.UtcNow.AddSeconds(-45);
    var nodes = store.State.Nodes.Where(node =>
        node.Online && node.LastSeen >= cutoff && !string.IsNullOrWhiteSpace(node.ControlUrl)).ToArray();
    if (nodes.Length == 0)
    {
        return result;
    }

    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    var tasks = nodes.Select(async node =>
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, node.ControlUrl.TrimEnd('/') + "/api/peer/allocations");
            request.Headers.Add("X-ZRfrp-Peer-Key", options.PeerKey);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<PortAllocation>();
            }
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var allocations = JsonSerializer.Deserialize<PortAllocation[]>(
                body, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            foreach (var allocation in allocations)
            {
                allocation.NodeId = node.Id;
                allocation.NodeName = string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name;
            }
            return allocations;
        }
        catch
        {
            return Array.Empty<PortAllocation>();
        }
    });
    foreach (var allocations in await Task.WhenAll(tasks))
    {
        result.AddRange(allocations);
    }
    return result;
}

static string PublicFrpsHost(ServerOptions options) =>
    string.IsNullOrWhiteSpace(options.PublicHost) ? options.FrpsAddress : options.PublicHost;

static string LocalNodeName(StateStore store, ServerOptions options)
{
    if (!string.IsNullOrWhiteSpace(store.State.LocalNodeName))
    {
        return PlainNodeName(store.State.LocalNodeName);
    }
    return PlainNodeName(string.IsNullOrWhiteSpace(options.NodeName) ? "本机节点" : options.NodeName);
}

static string LocalNodeFlagCode(StateStore store, ServerOptions options) =>
    NodeFlagCode(store.State.LocalNodeFlagCode, store.State.LocalNodeName);

static string ExternalBaseUrl(HttpContext context, ServerOptions options)
{
    var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()
        ?? context.Request.Host.Value;
    var scheme = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()
        ?? context.Request.Scheme;
    if (string.IsNullOrWhiteSpace(host))
    {
        host = PublicFrpsHost(options);
    }
    return $"{scheme}://{host}".TrimEnd('/');
}

static async Task InitializeSecretsAsync(StateStore store, ILogger logger)
{
    var changed = false;
    const string legacyEmailTemplate = "<h2>{{site_name}} 邮箱验证码</h2><p>您的验证码是：</p><h1>{{code}}</h1><p>验证码将在 {{expires_minutes}} 分钟后失效。</p>";
    if (string.IsNullOrWhiteSpace(store.State.Smtp.HtmlTemplate)
        || store.State.Smtp.HtmlTemplate.Equals(legacyEmailTemplate, StringComparison.Ordinal)
        || store.State.Smtp.HtmlTemplate.Contains("{{verification_code}}", StringComparison.Ordinal)
        || store.State.Smtp.HtmlTemplate.Contains("{{expires_in_minutes}}", StringComparison.Ordinal)
        || (store.State.Smtp.HtmlTemplate.Contains("max-width:640px", StringComparison.Ordinal)
            && store.State.Smtp.HtmlTemplate.Contains("line-height:1.8", StringComparison.Ordinal)
            && store.State.Smtp.HtmlTemplate.Contains("letter-spacing:10px", StringComparison.Ordinal)))
    {
        store.State.Smtp.HtmlTemplate = new SmtpSettings().HtmlTemplate;
        changed = true;
    }
    if (string.IsNullOrWhiteSpace(store.State.AdminPasswordHash))
    {
        var password = Environment.GetEnvironmentVariable("ZRFRP_ADMIN_PASSWORD") ?? Security.CreateSecret(15);
        store.State.AdminPasswordHash = Security.Hash(password);
        logger.LogWarning("ZRfrp 初始管理员密码: {Password}", password);
        changed = true;
    }
    if (string.IsNullOrWhiteSpace(store.State.ClientApiKeyHash))
    {
        var apiKey = Environment.GetEnvironmentVariable("ZRFRP_CLIENT_API_KEY") ?? Security.CreateSecret(32);
        store.State.ClientApiKeyHash = Security.Hash(apiKey);
        logger.LogWarning("ZRfrp 客户端 API Key: {ApiKey}", apiKey);
        changed = true;
    }
    if (store.State.Accounts.Count == 0)
    {
        store.State.Accounts.Add(new UserAccount
        {
            Username = "admin",
            PasswordHash = store.State.AdminPasswordHash,
            Role = "admin",
            Enabled = true
        });
        changed = true;
    }
    if (changed)
    {
        await store.SaveAsync();
    }
}

static string CreateNodeEnrollmentCommand(string masterUrl, string token) =>
    $"curl -fsSL {ShellQuote($"{masterUrl}/api/bootstrap/node/{Uri.EscapeDataString(token)}/install.sh")} | sudo bash";

static ManagedNode? FindEnrollmentNode(StateStore store, string token)
{
    if (string.IsNullOrWhiteSpace(token))
    {
        return null;
    }
    var now = DateTimeOffset.UtcNow;
    return store.State.Nodes.FirstOrDefault(node =>
        node.EnrollmentExpiresAt > now
        && !string.IsNullOrWhiteSpace(node.EnrollmentTokenHash)
        && Security.VerifyToken(token, node.EnrollmentTokenHash));
}

static string CreateMasterBootstrapScript(
    ManagedNode node, string token, string peerKey, string frpAuthToken)
{
    var prefix =
        $"{node.EnrollmentMasterUrl.TrimEnd('/')}/api/bootstrap/node/{Uri.EscapeDataString(token)}";
    return string.Join('\n',
    [
        "#!/usr/bin/env bash",
        "set -euo pipefail",
        "case \"$(uname -m)\" in",
        "  x86_64|amd64) RID=\"linux-x64\"; FRP_ARCH=\"amd64\" ;;",
        "  aarch64|arm64) RID=\"linux-arm64\"; FRP_ARCH=\"arm64\" ;;",
        "  *) echo \"暂不支持的架构: $(uname -m)\" >&2; exit 1 ;;",
        "esac",
        $"PREFIX={ShellQuote(prefix)}",
        "export ZRFRP_SERVER_URL=\"${PREFIX}/server/${RID}\"",
        "export ZRFRP_FRP_URL=\"${PREFIX}/frp/${FRP_ARCH}\"",
        "export ZRFRP_REINSTALL_FRPS=1",
        "export ZRFRP_MODE=node",
        $"export ZRFRP_NODE_ID={ShellQuote(node.Id)}",
        $"export ZRFRP_NODE_NAME={ShellQuote(node.Name)}",
        $"export ZRFRP_PUBLIC_HOST={ShellQuote(node.PublicHost)}",
        $"export ZRFRP_MASTER_URL={ShellQuote(node.EnrollmentMasterUrl.TrimEnd('/'))}",
        $"export ZRFRP_MASTER_KEY={ShellQuote(peerKey)}",
        $"export ZRFRP_PEER_KEY={ShellQuote(peerKey)}",
        $"export ZRFRP_FRP_TOKEN={ShellQuote(frpAuthToken)}",
        "curl --fail --silent --show-error --location \"${PREFIX}/installer\" | bash"
    ]);
}

static string CreateOfflineBootstrapScript(
    ManagedNode node,
    string rid,
    string peerKey,
    string frpAuthToken,
    string installer)
{
    var machineArchitecture = rid == "linux-arm64" ? "aarch64|arm64" : "x86_64|amd64";
    var frpArch = rid == "linux-arm64" ? "arm64" : "amd64";
    var serverFileName = $"zrfrp-server-{rid}.tar.gz";
    var frpFileName = $"frp_{BootstrapPackageService.FrpVersion}_linux_{frpArch}.tar.gz";
    return string.Join('\n',
    [
        "#!/usr/bin/env bash",
        "set -euo pipefail",
        "SCRIPT_PATH=\"$(readlink -f \"$0\")\"",
        "BASE_DIR=\"$(dirname \"${SCRIPT_PATH}\")\"",
        $"case \"$(uname -m)\" in {machineArchitecture}) ;; *) echo \"安装包与服务器架构不匹配: $(uname -m)\" >&2; exit 1 ;; esac",
        $"SERVER_FILE=\"${{BASE_DIR}}/{serverFileName}\"",
        $"FRP_FILE=\"${{BASE_DIR}}/{frpFileName}\"",
        "[[ -s \"${SERVER_FILE}\" ]] || { echo \"缺少 ${SERVER_FILE}\" >&2; exit 1; }",
        "[[ -s \"${FRP_FILE}\" ]] || { echo \"缺少 ${FRP_FILE}\" >&2; exit 1; }",
        "export ZRFRP_SERVER_URL=\"file://${SERVER_FILE}\"",
        "export ZRFRP_FRP_URL=\"file://${FRP_FILE}\"",
        "export ZRFRP_REINSTALL_FRPS=1",
        "export ZRFRP_MODE=node",
        $"export ZRFRP_NODE_ID={ShellQuote(node.Id)}",
        $"export ZRFRP_NODE_NAME={ShellQuote(node.Name)}",
        $"export ZRFRP_PUBLIC_HOST={ShellQuote(node.PublicHost)}",
        $"export ZRFRP_MASTER_URL={ShellQuote(node.EnrollmentMasterUrl.TrimEnd('/'))}",
        $"export ZRFRP_MASTER_KEY={ShellQuote(peerKey)}",
        $"export ZRFRP_PEER_KEY={ShellQuote(peerKey)}",
        $"export ZRFRP_FRP_TOKEN={ShellQuote(frpAuthToken)}",
        "INSTALLER=\"$(mktemp)\"",
        "trap 'rm -f \"${INSTALLER}\"' EXIT",
        "cat >\"${INSTALLER}\" <<'__ZRFRP_OFFLINE_INSTALLER__'",
        installer.TrimEnd(),
        "__ZRFRP_OFFLINE_INSTALLER__",
        "bash \"${INSTALLER}\"",
        "rm -f -- \"${SCRIPT_PATH}\"",
        "echo \"离线部署完成，包含节点密钥的部署脚本已自动删除。\""
    ]);
}

static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

static string NodeFlagCode(string? requestedCode, string? name)
{
    var code = (requestedCode ?? "").Trim().ToUpperInvariant();
    if (code is "CN" or "JP" or "US" or "SG" or "HK" or "KR" or "DE" or "GB" or "FR")
    {
        return code;
    }
    var value = (name ?? "").TrimStart().Replace("️", "");
    return value.StartsWith("🇨🇳", StringComparison.Ordinal) ? "CN"
        : value.StartsWith("🇯🇵", StringComparison.Ordinal) ? "JP"
        : value.StartsWith("🇺🇸", StringComparison.Ordinal) ? "US"
        : value.StartsWith("🇸🇬", StringComparison.Ordinal) ? "SG"
        : value.StartsWith("🇭🇰", StringComparison.Ordinal) ? "HK"
        : value.StartsWith("🇰🇷", StringComparison.Ordinal) ? "KR"
        : value.StartsWith("🇩🇪", StringComparison.Ordinal) ? "DE"
        : value.StartsWith("🇬🇧", StringComparison.Ordinal) ? "GB"
        : value.StartsWith("🇫🇷", StringComparison.Ordinal) ? "FR"
        : "";
}

static string PlainNodeName(string? value)
{
    var name = (value ?? "").Trim();
    foreach (var flag in new[] { "🇨🇳", "🇯🇵", "🇺🇸", "🇸🇬", "🇭🇰", "🇰🇷", "🇩🇪", "🇬🇧", "🇫🇷" })
    {
        if (name.Replace("️", "").StartsWith(flag, StringComparison.Ordinal))
        {
            return name[(flag.Length)..].TrimStart();
        }
    }
    return name;
}

static string DecoratedNodeName(string name, string flagCode)
{
    var emoji = flagCode switch
    {
        "CN" => "🇨🇳",
        "JP" => "🇯🇵",
        "US" => "🇺🇸",
        "SG" => "🇸🇬",
        "HK" => "🇭🇰",
        "KR" => "🇰🇷",
        "DE" => "🇩🇪",
        "GB" => "🇬🇧",
        "FR" => "🇫🇷",
        _ => ""
    };
    return string.IsNullOrWhiteSpace(emoji) ? PlainNodeName(name) : $"{emoji}{PlainNodeName(name)}";
}

static string ActionName(string action) => action switch
{
    "start" => "启动",
    "stop" => "停止",
    "restart" => "重启",
    _ => action
};
