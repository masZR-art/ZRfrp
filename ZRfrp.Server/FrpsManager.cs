using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ZRfrp.Server;

public sealed class FrpsManager
{
    private readonly ServerOptions _options;
    private HttpClient _http;

    public FrpsManager(ServerOptions options)
    {
        _options = options;
        _http = CreateDashboardClient(options);
    }

    public void ApplyConfig(FrpsConfigModel model)
    {
        _options.FrpsBindPort = model.BindPort;
        _options.PortRangeStart = model.PortRangeStart;
        _options.PortRangeEnd = model.PortRangeEnd;
        _options.FrpAuthToken = model.AuthToken;
        _options.FrpsDashboardUrl = $"http://{model.DashboardAddress}:{model.DashboardPort}";
        _options.FrpsDashboardUser = model.DashboardUser;
        _options.FrpsDashboardPassword = model.DashboardPassword;
        _http = CreateDashboardClient(_options);
    }

    private static HttpClient CreateDashboardClient(ServerOptions options)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(options.FrpsDashboardUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(4)
        };
        if (!string.IsNullOrWhiteSpace(options.FrpsDashboardUser))
        {
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{options.FrpsDashboardUser}:{options.FrpsDashboardPassword}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", value);
        }
        return client;
    }

    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_options.FrpsAddress, _options.FrpsBindPort, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<JsonElement?> GetDashboardJsonAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(endpoint.TrimStart('/'), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> GetMetricsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetStringAsync("metrics", cancellationToken);
        }
        catch
        {
            return "";
        }
    }

    public async Task<string> ReadConfigAsync() =>
        File.Exists(_options.FrpsConfigPath)
            ? await File.ReadAllTextAsync(_options.FrpsConfigPath)
            : "";

    public async Task<(bool Success, string Message)> SaveConfigAsync(string content, bool restart)
    {
        var directory = Path.GetDirectoryName(_options.FrpsConfigPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return (false, "frps.toml 路径无效。");
        }

        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".frps-{Guid.NewGuid():N}.toml");
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false));

        var verify = await RunAsync(_options.FrpsBinaryPath, ["verify", "-c", temporary], TimeSpan.FromSeconds(12));
        if (verify.ExitCode != 0)
        {
            File.Delete(temporary);
            return (false, string.IsNullOrWhiteSpace(verify.Output) ? "frps 配置校验失败。" : verify.Output);
        }

        if (File.Exists(_options.FrpsConfigPath))
        {
            File.Copy(_options.FrpsConfigPath, _options.FrpsConfigPath + ".bak", true);
        }
        File.Move(temporary, _options.FrpsConfigPath, true);

        if (!restart)
        {
            return (true, "配置已保存。");
        }

        var result = await RunAsync("sudo", ["/usr/bin/systemctl", "restart", _options.FrpsServiceName], TimeSpan.FromSeconds(20));
        return result.ExitCode == 0
            ? (true, "配置已保存，frps 已重启。")
            : (false, $"配置已保存，但服务重启失败：{result.Output}");
    }

    public async Task<(int ExitCode, string Output)> ServiceActionAsync(string action)
    {
        if (action is not ("start" or "stop" or "restart"))
        {
            return (-1, "不支持的服务操作。");
        }
        return await RunAsync("sudo", ["/usr/bin/systemctl", action, _options.FrpsServiceName], TimeSpan.FromSeconds(20));
    }

    public bool IsInstalled =>
        File.Exists(_options.FrpsBinaryPath) && File.Exists(_options.FrpsConfigPath);

    public async Task<string> GetInstalledVersionAsync()
    {
        if (!File.Exists(_options.FrpsBinaryPath))
        {
            return "";
        }
        var result = await RunAsync(_options.FrpsBinaryPath, ["--version"], TimeSpan.FromSeconds(8));
        return result.ExitCode == 0 ? result.Output.Trim() : "";
    }

    public Task<(int ExitCode, string Output)> InstallAsync() =>
        RunAsync("sudo", ["/usr/local/sbin/zrfrp-install-frps"], TimeSpan.FromMinutes(3));

    public Task<(int ExitCode, string Output)> ScheduleServerUpdateAsync() =>
        RunAsync("sudo", ["/usr/local/sbin/zrfrp-update-server"], TimeSpan.FromSeconds(20));

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeoutSource = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(timeoutSource.Token);
            return (process.ExitCode, ((await stdout) + Environment.NewLine + (await stderr)).Trim());
        }
        catch (Exception exception)
        {
            try { process.Kill(true); } catch { }
            return (-1, exception.Message);
        }
    }
}
