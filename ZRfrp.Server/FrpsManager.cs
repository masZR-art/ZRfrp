using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZRfrp.Server;

public sealed class FrpsManager
{
    private readonly ServerOptions _options;
    private HttpClient _http;
    public string LastDashboardError { get; private set; } = "";

    public FrpsManager(ServerOptions options)
    {
        _options = options;
        ApplyDashboardConfigFromFile(options);
        _http = CreateDashboardClient(options);
    }

    private static void ApplyDashboardConfigFromFile(ServerOptions options)
    {
        try
        {
            if (!File.Exists(options.FrpsConfigPath)) return;
            var text = File.ReadAllText(options.FrpsConfigPath);
            var address = ReadTomlString(text, "webServer.addr", "127.0.0.1");
            var port = ReadTomlInt(text, "webServer.port", 7500);
            options.FrpsDashboardUrl = $"http://{address}:{port}";
            options.FrpsDashboardUser = ReadTomlString(text, "webServer.user", "");
            options.FrpsDashboardPassword = ReadTomlString(text, "webServer.password", "");
        }
        catch { }
    }

    private static string ReadTomlString(string text, string key, string fallback)
    {
        var pattern = "(?m)^\\s*" + Regex.Escape(key)
            + "\\s*=\\s*\"(?<value>(?:\\\\.|[^\"])*)\"";
        var match = Regex.Match(text, pattern);
        return match.Success ? match.Groups["value"].Value.Replace("\\\"", "\"") : fallback;
    }

    private static int ReadTomlInt(string text, string key, int fallback)
    {
        var match = Regex.Match(text, $@"(?m)^\s*{Regex.Escape(key)}\s*=\s*(?<value>\d+)");
        return match.Success && int.TryParse(match.Groups["value"].Value, out var value) ? value : fallback;
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
                LastDashboardError = $"Dashboard {endpoint} 返回 HTTP {(int)response.StatusCode}";
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            LastDashboardError = "";
            return document.RootElement.Clone();
        }
        catch (Exception exception)
        {
            LastDashboardError = $"Dashboard {endpoint} 请求失败：{exception.Message}";
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
        var result = await RunAsync("sudo", ["/usr/bin/systemctl", action, _options.FrpsServiceName], TimeSpan.FromSeconds(20));
        if (result.ExitCode != 0 || action == "stop")
        {
            return result;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (await IsReachableAsync(timeout.Token))
            {
                return result;
            }
        }

        var status = await GetInstallStatusAsync(CancellationToken.None);
        return (-1, $"frps 服务命令已执行，但控制端口仍不可连接：{status.Message}");
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

    public Task<(int ExitCode, string Output)> RepairAsync() =>
        RunAsync("sudo", ["/usr/local/sbin/zrfrp-repair-frps"], TimeSpan.FromMinutes(3));

    public Task<(int ExitCode, string Output)> ScheduleServerUpdateAsync() =>
        RunAsync("sudo", ["/usr/local/sbin/zrfrp-update-server"], TimeSpan.FromMinutes(5));

    public async Task<FrpsInstallStatus> GetInstallStatusAsync(CancellationToken cancellationToken)
    {
        var binaryExists = File.Exists(_options.FrpsBinaryPath);
        var configExists = File.Exists(_options.FrpsConfigPath);
        var version = await GetInstalledVersionAsync();
        var reachable = await IsReachableAsync(cancellationToken);
        var service = await RunAsync("systemctl", ["is-active", _options.FrpsServiceName], TimeSpan.FromSeconds(5));
        var writable = await RunAsync("/bin/sh", ["-lc", $"test -w {ShellQuote(Path.GetDirectoryName(_options.FrpsBinaryPath) ?? "/opt/zrfrp")}"], TimeSpan.FromSeconds(5));
        var readOnly = await RunAsync("/bin/sh", ["-lc", $"findmnt -no OPTIONS -T {ShellQuote(_options.FrpsBinaryPath)} 2>/dev/null | tr ',' '\\n' | grep -qx ro"], TimeSpan.FromSeconds(5));
        var message = BuildStatusMessage(binaryExists, configExists, reachable, service.Output.Trim(), writable.ExitCode == 0, readOnly.ExitCode == 0);
        return new FrpsInstallStatus(
            binaryExists && configExists,
            version,
            reachable,
            string.IsNullOrWhiteSpace(service.Output) ? "unknown" : service.Output.Trim(),
            binaryExists,
            configExists,
            writable.ExitCode == 0,
            readOnly.ExitCode == 0,
            message);
    }

    private static string BuildStatusMessage(
        bool binaryExists, bool configExists, bool reachable, string serviceState, bool optWritable, bool fileSystemReadOnly)
    {
        if (reachable)
        {
            return "frps 正常运行。";
        }
        if (fileSystemReadOnly)
        {
            return "/opt/zrfrp 所在文件系统为只读，自动修复会尝试重新挂载，失败时需要检查云服务器磁盘。";
        }
        if (!optWritable)
        {
            return "/opt/zrfrp 当前不可写，自动修复会调整 systemd 沙箱和目录权限。";
        }
        if (!binaryExists)
        {
            return "未找到 frps 程序，自动修复会重新下载。";
        }
        if (!configExists)
        {
            return "未找到 frps.toml，自动修复会生成默认配置。";
        }
        return $"frps 控制端口不可连接，systemd 状态：{serviceState}.";
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''")}'";

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
