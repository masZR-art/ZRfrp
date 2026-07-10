using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace FrpDesktop;

public sealed record DesktopUpdateInfo(
    string CurrentVersion, string LatestVersion, bool UpdateAvailable, string DownloadUrl);

public sealed class DesktopUpdateService
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/3317603015whw-art/ZRfrp/releases/latest";
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public DesktopUpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZRfrp-Desktop", CurrentVersion));
    }

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<DesktopUpdateInfo> CheckAsync()
    {
        using var response = await _http.GetAsync(LatestReleaseUrl);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var latest = (document.RootElement.GetProperty("tag_name").GetString() ?? "v0.0.0").TrimStart('v');
        var asset = document.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(item =>
                (item.GetProperty("name").GetString() ?? "")
                .Equals($"ZRfrp-Desktop-v{latest}-win-x64.zip", StringComparison.OrdinalIgnoreCase));
        var downloadUrl = asset.ValueKind == JsonValueKind.Undefined
            ? ""
            : asset.GetProperty("browser_download_url").GetString() ?? "";
        return new DesktopUpdateInfo(
            CurrentVersion, latest, Compare(latest, CurrentVersion) > 0 && downloadUrl.Length > 0, downloadUrl);
    }

    public async Task DownloadAndApplyAsync(DesktopUpdateInfo update, string dataDirectory)
    {
        var updateDirectory = Path.Combine(dataDirectory, "update", update.LatestVersion);
        var extractDirectory = Path.Combine(updateDirectory, "files");
        Directory.CreateDirectory(updateDirectory);
        if (Directory.Exists(extractDirectory))
        {
            Directory.Delete(extractDirectory, true);
        }
        Directory.CreateDirectory(extractDirectory);
        var archivePath = Path.Combine(updateDirectory, "update.zip");
        await using (var source = await _http.GetStreamAsync(update.DownloadUrl))
        await using (var target = File.Create(archivePath))
        {
            await source.CopyToAsync(target);
        }
        ZipFile.ExtractToDirectory(archivePath, extractDirectory, true);

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。");
        var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var scriptPath = Path.Combine(updateDirectory, "apply-update.ps1");
        var script = string.Join(Environment.NewLine,
        [
            "param([int]$TargetProcessId)",
            "$ErrorActionPreference = 'Stop'",
            $"$logPath = '{Escape(Path.Combine(updateDirectory, "apply-update.log"))}'",
            "try {",
            "    for ($attempt = 0; $attempt -lt 150; $attempt++) {",
            "        if (-not (Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue)) { break }",
            "        Start-Sleep -Milliseconds 200",
            "    }",
            "    if (Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue) {",
            "        Stop-Process -Id $TargetProcessId -Force",
            "        Start-Sleep -Milliseconds 500",
            "    }",
            "    Start-Sleep -Milliseconds 500",
            $"    Copy-Item -Path '{Escape(extractDirectory)}\\*' -Destination '{Escape(installDirectory)}' -Recurse -Force",
            $"    Start-Process -FilePath '{Escape(executable)}' -WorkingDirectory '{Escape(installDirectory)}'",
            "    '更新完成。' | Set-Content -Path $logPath -Encoding UTF8",
            "} catch {",
            "    $_ | Out-File -Path $logPath -Encoding UTF8",
            "}"
        ]);
        await File.WriteAllTextAsync(scriptPath, script);
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -TargetProcessId {Environment.ProcessId}",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static string Escape(string value) => value.Replace("'", "''");
    private static int Compare(string left, string right)
    {
        _ = Version.TryParse(left, out var leftVersion);
        _ = Version.TryParse(right, out var rightVersion);
        return Comparer<Version>.Default.Compare(leftVersion ?? new(), rightVersion ?? new());
    }
}
