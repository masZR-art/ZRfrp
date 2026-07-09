using System.Security.Claims;
using System.Text.Json.Nodes;
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
builder.Services.AddSingleton<FrpsConfigService>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddHostedService<NodeHeartbeatService>();
builder.Services.AddHostedService<TrafficCollector>();
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
            if (string.IsNullOrWhiteSpace(accountId)
                || !store.State.Accounts.Any(account => account.Id == accountId && account.Enabled))
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
        new Claim(ClaimTypes.Role, account.Role)
    ], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(new { ok = true, role = account.Role, username = account.Username });
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
    await Task.WhenAll(serverTask, clientsTask, tcpTask, udpTask, httpTask, httpsTask, healthTask);
    var frpsStatus = await frps.GetInstallStatusAsync(cancellationToken);
    return Results.Ok(new
    {
        reachable = healthTask.Result,
        frpsStatus,
        server = serverTask.Result,
        clients = clientsTask.Result,
        proxies = new { tcp = tcpTask.Result, udp = udpTask.Result, http = httpTask.Result, https = httpsTask.Result },
        publicHost = PublicFrpsHost(serverOptions),
        bindPort = serverOptions.FrpsBindPort,
        allocations = store.State.Allocations.Where(item => item.Active),
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

app.MapGet("/api/allocations", (StateStore store) => Results.Ok(store.State.Allocations))
    .RequireAuthorization(policy => policy.RequireRole("admin"));
app.MapDelete("/api/allocations/{id}", async (string id, AllocationService allocations) =>
    await allocations.ReleaseAsync(id) ? Results.Ok() : Results.NotFound())
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
    if (account is null || account.Role != "customer")
    {
        return Results.Json(new { error = "账号或密码错误。" }, statusCode: 401);
    }
    if (accounts.IsQuotaExceeded(account))
    {
        return Results.Json(new { error = "该账号流量额度已用尽。" }, statusCode: 403);
    }
    var session = await accounts.CreateSessionAsync(account);
    return Results.Ok(new ClientLoginResponse(
        account.Id, account.Username, session.Token, session.ExpiresAt,
        string.IsNullOrWhiteSpace(serverOptions.PublicHost) ? serverOptions.FrpsAddress : serverOptions.PublicHost,
        serverOptions.FrpsBindPort, serverOptions.FrpAuthToken,
        account.TrafficQuotaBytes, account.TrafficUsedBytes));
});

app.MapGet("/api/customer/nodes/export", (
    HttpContext context, AccountService accounts, StateStore store, ServerOptions serverOptions) =>
{
    if (GetBearerAccount(context, accounts) is null
        && context.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(CreateNodeExport(store, serverOptions, context));
});

app.MapGet("/api/customer/me", (ClaimsPrincipal principal, AccountService accounts) =>
{
    var account = accounts.Find(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
    return account is null ? Results.NotFound() : Results.Ok(new
    {
        account.Id,
        account.Username,
        account.TrafficQuotaBytes,
        account.TrafficUsedBytes,
        remainingBytes = account.TrafficQuotaBytes <= 0
            ? -1
            : Math.Max(0, account.TrafficQuotaBytes - account.TrafficUsedBytes)
    });
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
        account.CreatedAt
    }))).RequireAuthorization(policy => policy.RequireRole("admin"));

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
        store.State.AccountSessions.RemoveAll(item => item.AccountId == account.Id);
    }
    account.TrafficQuotaBytes = Math.Max(0, request.TrafficQuotaBytes);
    account.Enabled = request.Enabled;
    await store.AuditAsync("account", $"更新账号 {account.Username}");
    return Results.Ok();
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/accounts/{id}/reset-traffic", async (
    string id, StateStore store) =>
{
    var account = store.State.Accounts.FirstOrDefault(item => item.Id == id);
    if (account is null) return Results.NotFound();
    account.TrafficUsedBytes = 0;
    foreach (var key in store.State.TrafficSnapshots.Keys
                 .Where(key => key.Contains(account.Id + ".", StringComparison.Ordinal))
                 .ToArray())
    {
        store.State.TrafficSnapshots[key] = long.MaxValue;
    }
    await store.AuditAsync("account", $"重置账号 {account.Username} 的流量");
    return Results.Ok();
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
    foreach (var node in store.State.Nodes)
    {
        node.Online = node.Online && node.LastSeen >= cutoff;
    }
    return Results.Ok(store.State.Nodes);
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/nodes/export", (
    StateStore store, ServerOptions serverOptions, HttpContext context) =>
    Results.Ok(CreateNodeExport(store, serverOptions, context)))
    .RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/admin/nodes/{id}", async (
    string id, NodeUpdateRequest request, StateStore store) =>
{
    var node = store.State.Nodes.FirstOrDefault(item => item.Id == id);
    if (node is null)
    {
        return Results.NotFound();
    }
    if (string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.BadRequest(new { error = "节点名称不能为空。" });
    }
    node.Name = request.Name.Trim();
    await store.AuditAsync("node", $"更新节点名称为 {node.Name}");
    return Results.Ok(new { node.Name });
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
    return response.IsSuccessStatusCode
        ? Results.Ok(new { message = $"节点 {node.Name} 已执行 {action}。" })
        : Results.BadRequest(new { error = await response.Content.ReadAsStringAsync() });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/peer/heartbeat", async (
    NodeHeartbeat heartbeat, HttpContext context, StateStore store, ServerOptions serverOptions) =>
{
    if (!ValidatePeerKey(context, serverOptions.PeerKey))
    {
        return Results.Unauthorized();
    }
    var node = store.State.Nodes.FirstOrDefault(item => item.Id == heartbeat.Id);
    if (node is null)
    {
        node = new ManagedNode { Id = heartbeat.Id };
        store.State.Nodes.Add(node);
    }
    node.Name = heartbeat.Name;
    node.PublicHost = heartbeat.PublicHost;
    node.ControlUrl = heartbeat.ControlUrl;
    node.FrpsPort = heartbeat.FrpsPort;
    node.Online = heartbeat.Online;
    node.ActiveClients = heartbeat.ActiveClients;
    node.ActiveProxies = heartbeat.ActiveProxies;
    node.Version = heartbeat.Version;
    node.LastSeen = DateTimeOffset.UtcNow;
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

app.MapPost("/api/client/allocate", async (
    AllocationRequest request,
    HttpContext context,
    AllocationService allocations,
    AccountService accounts,
    CancellationToken cancellationToken) =>
{
    var account = GetBearerAccount(context, accounts);
    if (account is null && !HasClientKey(context, stateStore.State.ClientApiKeyHash))
    {
        return Results.Unauthorized();
    }
    if (account is not null && accounts.IsQuotaExceeded(account))
    {
        return Results.Json(new { error = "账号流量额度已用尽。" }, statusCode: 403);
    }
    var result = await allocations.AllocateAsync(request, cancellationToken, account);
    return result.Result is not null ? Results.Ok(result.Result) : Results.BadRequest(new { error = result.Error });
});

app.MapDelete("/api/client/allocations/{id}", async (
    string id, HttpContext context, AllocationService allocations, AccountService accounts) =>
{
    if (GetBearerAccount(context, accounts) is null
        && !HasClientKey(context, stateStore.State.ClientApiKeyHash))
    {
        return Results.Unauthorized();
    }
    return await allocations.ReleaseAsync(id) ? Results.Ok() : Results.NotFound();
});

app.MapPost("/frp-plugin", async (
    HttpContext context, AllocationService allocations, AccountService accounts) =>
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
        var account = accounts.ValidateAccessToken(accessToken);
        if (account is null)
        {
            return Results.Ok(new { reject = true, reject_reason = "ZRfrp: account session is invalid or expired" });
        }
        if (accounts.IsQuotaExceeded(account))
        {
            return Results.Ok(new { reject = true, reject_reason = "ZRfrp: traffic quota exceeded" });
        }
        return Results.Ok(new { reject = false, unchange = true });
    }

    if (operation == "NewProxy")
    {
        var user = content["user"] as JsonObject;
        var metas = user?["metas"] as JsonObject;
        var clientId = metas?["zrfrp_client_id"]?.GetValue<string>() ?? "";
        var accessToken = metas?["zrfrp_access_token"]?.GetValue<string>() ?? "";
        var account = accounts.ValidateAccessToken(accessToken);
        if (account is null || accounts.IsQuotaExceeded(account))
        {
            return Results.Ok(new { reject = true, reject_reason = "ZRfrp: account is unavailable or over quota" });
        }
        var proxyName = content["proxy_name"]?.GetValue<string>() ?? "";
        var allocation = allocations.FindForPlugin(clientId, proxyName);
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
        var account = accounts.ValidateAccessToken(accessToken);
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

static UserAccount? GetBearerAccount(HttpContext context, AccountService accounts)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? accounts.ValidateAccessToken(authorization[7..].Trim())
        : null;
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

static NodeExportDocument CreateNodeExport(StateStore store, ServerOptions options, HttpContext context)
{
    var platformUrl = ExternalBaseUrl(context, options);
    var cutoff = DateTimeOffset.UtcNow.AddSeconds(-45);
    var nodes = new List<NodeExportEntry>
    {
        new(
            string.IsNullOrWhiteSpace(options.NodeId) ? "local" : options.NodeId,
            string.IsNullOrWhiteSpace(options.NodeName) ? "本机节点" : options.NodeName,
            PublicFrpsHost(options),
            options.FrpsBindPort,
            options.FrpAuthToken,
            platformUrl)
    };

    nodes.AddRange(store.State.Nodes
        .Where(node => node.Online && node.LastSeen >= cutoff && !string.IsNullOrWhiteSpace(node.PublicHost))
        .Select(node => new NodeExportEntry(
            node.Id,
            string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name,
            node.PublicHost,
            node.FrpsPort > 0 ? node.FrpsPort : options.FrpsBindPort,
            options.FrpAuthToken,
            platformUrl)));

    return new NodeExportDocument("zrfrp-node-export", 1, platformUrl, DateTimeOffset.UtcNow, nodes);
}

static string PublicFrpsHost(ServerOptions options) =>
    string.IsNullOrWhiteSpace(options.PublicHost) ? options.FrpsAddress : options.PublicHost;

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

static string ActionName(string action) => action switch
{
    "start" => "启动",
    "stop" => "停止",
    "restart" => "重启",
    _ => action
};
