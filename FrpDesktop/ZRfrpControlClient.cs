using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace FrpDesktop;

public sealed class ZRfrpControlClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ClientAccountSession> LoginAsync(
        string platformUrl, string username, string password, string clientId,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(platformUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress))
        {
            throw new InvalidOperationException("控制平台地址无效。");
        }
        using var client = CreateHttpClient(baseAddress);
        var payload = JsonSerializer.Serialize(new { username, password, clientId }, JsonOptions);
        using var response = await client.PostAsync(
            "api/client/login", new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = JsonSerializer.Deserialize<ControlError>(text, JsonOptions);
            throw new InvalidOperationException(error?.Error ?? "账号登录失败。");
        }
        return JsonSerializer.Deserialize<ClientAccountSession>(text, JsonOptions)
            ?? throw new InvalidOperationException("控制平台返回了无效登录结果。");
    }

    public async Task<NodeExportDocument> ExportNodesAsync(
        string platformUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(platformUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress))
        {
            throw new InvalidOperationException("控制平台地址无效。");
        }
        using var client = CreateHttpClient(baseAddress);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.GetAsync("api/customer/nodes/export", cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = JsonSerializer.Deserialize<ControlError>(text, JsonOptions);
            throw new InvalidOperationException(error?.Error ?? "节点配置导出失败。");
        }
        return JsonSerializer.Deserialize<NodeExportDocument>(text, JsonOptions)
            ?? throw new InvalidOperationException("控制平台返回了无效节点配置。");
    }

    public async Task<ManagedAllocation> AllocateAsync(
        FrpProfile profile, FrpProxy proxy, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(profile);
        var payload = new
        {
            clientId = profile.Id,
            profileId = profile.Id,
            tunnelId = proxy.Id,
            proxyName = proxy.Name,
            proxyType = proxy.Type.ToLowerInvariant(),
            bandwidthLimit = ""
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("api/client/allocate", content, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var error = JsonSerializer.Deserialize<ControlError>(responseText, JsonOptions);
                throw new InvalidOperationException(error?.Error ?? $"服务端拒绝了分配请求 ({(int)response.StatusCode})。");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"服务端拒绝了分配请求 ({(int)response.StatusCode})。");
            }
        }
        return JsonSerializer.Deserialize<ManagedAllocation>(responseText, JsonOptions)
            ?? throw new InvalidOperationException("服务端返回了无效的分配结果。");
    }

    public async Task ReleaseAsync(FrpProfile profile, string allocationId)
    {
        if (string.IsNullOrWhiteSpace(allocationId))
        {
            return;
        }
        using var client = CreateClient(profile);
        using var response = await client.DeleteAsync($"api/client/allocations/{Uri.EscapeDataString(allocationId)}");
        response.EnsureSuccessStatusCode();
    }

    private static HttpClient CreateClient(FrpProfile profile)
    {
        if (!Uri.TryCreate(profile.ControlApiUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress))
        {
            throw new InvalidOperationException("ZRfrp Server 面板地址无效。");
        }
        if (string.IsNullOrWhiteSpace(profile.ControlApiKey)
            && string.IsNullOrWhiteSpace(profile.AccountAccessToken))
        {
            throw new InvalidOperationException("请填写客户端 API Key。");
        }
        var client = CreateHttpClient(baseAddress);
        if (!string.IsNullOrWhiteSpace(profile.AccountAccessToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", profile.AccountAccessToken);
        }
        else
        {
            client.DefaultRequestHeaders.Add("X-ZRfrp-Key", profile.ControlApiKey);
        }
        return client;
    }

    private static HttpClient CreateHttpClient(Uri baseAddress) =>
        new(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(10)
        };

    private sealed record ControlError(string Error);
}

public sealed record ManagedAllocation(
    string AllocationId,
    string NodeName,
    string ServerAddress,
    int ServerPort,
    int RemotePort,
    string BandwidthLimit,
    bool Locked);

public sealed record ClientAccountSession(
    string AccountId,
    string Username,
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string ServerAddress,
    int ServerPort,
    string FrpToken,
    long TrafficQuotaBytes,
    long TrafficUsedBytes);
