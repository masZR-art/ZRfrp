using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace FrpDesktop;

public sealed class ZRfrpControlClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
            bandwidthLimit = proxy.BandwidthLimit
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
        if (string.IsNullOrWhiteSpace(profile.ControlApiKey))
        {
            throw new InvalidOperationException("请填写客户端 API Key。");
        }
        var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Add("X-ZRfrp-Key", profile.ControlApiKey);
        return client;
    }

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
