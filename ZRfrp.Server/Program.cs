using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Json;
using ZRfrp.Server;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("ZRfrp").Get<ServerOptions>() ?? new();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<FrpsManager>();
builder.Services.AddSingleton<AllocationService>();
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
    });
builder.Services.AddAuthorization();

var app = builder.Build();
var stateStore = app.Services.GetRequiredService<StateStore>();
await InitializeSecretsAsync(stateStore, app.Logger);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context) =>
{
    if (!Security.Verify(request.Password ?? "", stateStore.State.AdminPasswordHash))
    {
        await Task.Delay(250);
        return Results.Json(new { error = "密码错误。" }, statusCode: 401);
    }
    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
}).RequireAuthorization();

app.MapGet("/api/session", (ClaimsPrincipal user) => Results.Ok(new { authenticated = user.Identity?.IsAuthenticated == true }));

app.MapGet("/api/overview", async (FrpsManager frps, StateStore store, CancellationToken cancellationToken) =>
{
    var serverTask = frps.GetDashboardJsonAsync("/api/serverinfo", cancellationToken);
    var clientsTask = frps.GetDashboardJsonAsync("/api/clients", cancellationToken);
    var tcpTask = frps.GetDashboardJsonAsync("/api/proxy/tcp", cancellationToken);
    var udpTask = frps.GetDashboardJsonAsync("/api/proxy/udp", cancellationToken);
    var httpTask = frps.GetDashboardJsonAsync("/api/proxy/http", cancellationToken);
    var httpsTask = frps.GetDashboardJsonAsync("/api/proxy/https", cancellationToken);
    var healthTask = frps.IsReachableAsync(cancellationToken);
    await Task.WhenAll(serverTask, clientsTask, tcpTask, udpTask, httpTask, httpsTask, healthTask);
    return Results.Ok(new
    {
        reachable = healthTask.Result,
        server = serverTask.Result,
        clients = clientsTask.Result,
        proxies = new { tcp = tcpTask.Result, udp = udpTask.Result, http = httpTask.Result, https = httpsTask.Result },
        allocations = store.State.Allocations.Where(item => item.Active),
        audit = store.State.Audit.Take(30)
    });
}).RequireAuthorization();

app.MapGet("/api/config", async (FrpsManager frps) =>
    Results.Ok(new { content = await frps.ReadConfigAsync() })).RequireAuthorization();

app.MapPut("/api/config", async (ConfigUpdateRequest request, FrpsManager frps, StateStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Content))
    {
        return Results.BadRequest(new { error = "配置内容不能为空。" });
    }
    var result = await frps.SaveConfigAsync(request.Content, request.Restart);
    await store.AuditAsync("config", result.Message);
    return result.Success ? Results.Ok(new { message = result.Message }) : Results.BadRequest(new { error = result.Message });
}).RequireAuthorization();

app.MapPost("/api/service/{action}", async (string action, FrpsManager frps, StateStore store) =>
{
    var result = await frps.ServiceActionAsync(action);
    await store.AuditAsync("service", $"{action}: {result.Output}");
    return result.ExitCode == 0 ? Results.Ok(new { message = $"frps 已{ActionName(action)}。" })
        : Results.BadRequest(new { error = result.Output });
}).RequireAuthorization();

app.MapGet("/api/allocations", (StateStore store) => Results.Ok(store.State.Allocations)).RequireAuthorization();
app.MapDelete("/api/allocations/{id}", async (string id, AllocationService allocations) =>
    await allocations.ReleaseAsync(id) ? Results.Ok() : Results.NotFound()).RequireAuthorization();

app.MapPost("/api/admin/password", async (PasswordChangeRequest request, StateStore store) =>
{
    if (!Security.Verify(request.CurrentPassword ?? "", store.State.AdminPasswordHash))
    {
        return Results.BadRequest(new { error = "当前密码不正确。" });
    }
    if (request.NewPassword?.Length < 10)
    {
        return Results.BadRequest(new { error = "新密码至少需要 10 个字符。" });
    }
    store.State.AdminPasswordHash = Security.Hash(request.NewPassword!);
    await store.AuditAsync("security", "管理员密码已更新");
    return Results.Ok();
}).RequireAuthorization();

app.MapPost("/api/client/allocate", async (
    AllocationRequest request, HttpContext context, AllocationService allocations, CancellationToken cancellationToken) =>
{
    if (!HasClientKey(context, stateStore.State.ClientApiKeyHash))
    {
        return Results.Unauthorized();
    }
    var result = await allocations.AllocateAsync(request, cancellationToken);
    return result.Result is not null ? Results.Ok(result.Result) : Results.BadRequest(new { error = result.Error });
});

app.MapDelete("/api/client/allocations/{id}", async (
    string id, HttpContext context, AllocationService allocations) =>
{
    if (!HasClientKey(context, stateStore.State.ClientApiKeyHash))
    {
        return Results.Unauthorized();
    }
    return await allocations.ReleaseAsync(id) ? Results.Ok() : Results.NotFound();
});

app.MapPost("/frp-plugin", async (HttpContext context, AllocationService allocations) =>
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

    if (operation == "NewProxy")
    {
        var user = content["user"] as JsonObject;
        var metas = user?["metas"] as JsonObject;
        var clientId = metas?["zrfrp_client_id"]?.GetValue<string>() ?? "";
        var proxyName = content["proxy_name"]?.GetValue<string>() ?? "";
        var allocation = allocations.FindForPlugin(clientId, proxyName);
        if (allocation is null)
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

    return Results.Ok(new { reject = false, unchange = true });
});

app.MapFallbackToFile("index.html");
app.Run();

static bool HasClientKey(HttpContext context, string hash)
{
    var key = context.Request.Headers["X-ZRfrp-Key"].ToString();
    return !string.IsNullOrWhiteSpace(key) && Security.Verify(key, hash);
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
