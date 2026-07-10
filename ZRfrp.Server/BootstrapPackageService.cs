using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace ZRfrp.Server;

public sealed class BootstrapPackageService
{
    private const string Repository = "3317603015whw-art/ZRfrp";
    private const string FrpVersion = "0.69.1";
    private readonly string _cacheDirectory;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public BootstrapPackageService(ServerOptions options)
    {
        _cacheDirectory = Path.Combine(options.DataDirectory, "bootstrap-cache");
        Directory.CreateDirectory(_cacheDirectory);
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ZRfrp-Bootstrap", CurrentVersion));
    }

    public string CurrentVersion =>
        typeof(BootstrapPackageService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public Task<string> GetServerPackageAsync(string rid, CancellationToken cancellationToken)
    {
        if (rid is not ("linux-x64" or "linux-arm64"))
        {
            throw new ArgumentOutOfRangeException(nameof(rid), "不支持的 Linux 架构。");
        }
        var url =
            $"https://github.com/{Repository}/releases/download/v{CurrentVersion}/zrfrp-server-{rid}.tar.gz";
        return GetOrDownloadAsync($"zrfrp-server-v{CurrentVersion}-{rid}.tar.gz", url, cancellationToken);
    }

    public Task<string> GetFrpPackageAsync(string arch, CancellationToken cancellationToken)
    {
        if (arch is not ("amd64" or "arm64"))
        {
            throw new ArgumentOutOfRangeException(nameof(arch), "不支持的 frp 架构。");
        }
        var url =
            $"https://github.com/fatedier/frp/releases/download/v{FrpVersion}/frp_{FrpVersion}_linux_{arch}.tar.gz";
        return GetOrDownloadAsync($"frp-{FrpVersion}-linux-{arch}.tar.gz", url, cancellationToken);
    }

    public Task<string> ReadInstallerAsync() =>
        File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "deploy", "install.sh"));

    private async Task<string> GetOrDownloadAsync(
        string fileName, string url, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(_cacheDirectory, fileName);
        if (File.Exists(destination) && new FileInfo(destination).Length > 0)
        {
            return destination;
        }

        var gate = _locks.GetOrAdd(fileName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(destination) && new FileInfo(destination).Length > 0)
            {
                return destination;
            }

            var temporary = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using (var response = await _http.GetAsync(
                           url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var target = new FileStream(
                        temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, true);
                    await source.CopyToAsync(target, cancellationToken);
                    await target.FlushAsync(cancellationToken);
                }
                File.Move(temporary, destination, true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            return destination;
        }
        finally
        {
            gate.Release();
        }
    }
}
