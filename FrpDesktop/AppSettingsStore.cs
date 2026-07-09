using System.Text.Json;
using System.IO;

namespace FrpDesktop;

public sealed class AppSettingsStore
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public AppSettingsStore()
    {
        MigrateLegacySettings();
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(GeneratedConfigDirectory);
    }

    public string AppDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZRfrp");

    private string LegacyAppDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrpDesktop");

    public string GeneratedConfigDirectory =>
        Path.Combine(AppDataDirectory, "generated");

    public string SettingsPath =>
        Path.Combine(AppDataDirectory, "profiles.json");

    private string LegacySettingsPath =>
        Path.Combine(LegacyAppDataDirectory, "profiles.json");

    public AppState Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return CreateInitialState();
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var state = JsonSerializer.Deserialize<AppState>(json, _jsonOptions);
            return EnsureValidState(state);
        }
        catch
        {
            var backupPath = Path.Combine(AppDataDirectory, $"profiles.broken.{DateTime.Now:yyyyMMddHHmmss}.json");
            File.Copy(SettingsPath, backupPath, overwrite: true);
            return CreateInitialState();
        }
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(AppDataDirectory);
        var json = JsonSerializer.Serialize(state, _jsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    private void MigrateLegacySettings()
    {
        if (File.Exists(SettingsPath) || !File.Exists(LegacySettingsPath))
        {
            return;
        }

        Directory.CreateDirectory(AppDataDirectory);
        File.Copy(LegacySettingsPath, SettingsPath, overwrite: false);
    }

    public string GetGeneratedConfigPath(FrpProfile profile)
    {
        Directory.CreateDirectory(GeneratedConfigDirectory);
        var safeName = MakeSafeFileName(profile.Name);
        return Path.Combine(GeneratedConfigDirectory, $"{safeName}-{profile.Id[..Math.Min(8, profile.Id.Length)]}.toml");
    }

    private AppState CreateInitialState()
    {
        var bundledFrpcPath = Path.Combine(AppContext.BaseDirectory, "frpc.exe");
        return new AppState
        {
            ClientFrpcPath = File.Exists(bundledFrpcPath) ? bundledFrpcPath : "",
            Profiles = new()
        };
    }

    private static AppState EnsureValidState(AppState? state)
    {
        if (state is null)
        {
            return new AppSettingsStore().CreateInitialState();
        }

        foreach (var profile in state.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }

            if (profile.Proxies is null)
            {
                profile.Proxies = new();
            }
            foreach (var proxy in profile.Proxies)
            {
                if (string.IsNullOrWhiteSpace(proxy.Id))
                {
                    proxy.Id = Guid.NewGuid().ToString("N");
                }
            }
        }

        if (state.Profiles.Count == 0)
        {
            state.LastProfileId = null;
        }
        else
        {
            state.LastProfileId ??= state.Profiles[0].Id;
        }
        if (string.IsNullOrWhiteSpace(state.ClientFrpcPath))
        {
            state.ClientFrpcPath = state.Profiles
                .Select(profile => profile.FrpcPath)
                .FirstOrDefault(File.Exists) ?? "";
        }

        state.NetworkProxyMode = string.IsNullOrWhiteSpace(state.NetworkProxyMode) ? "none" : state.NetworkProxyMode;
        state.NetworkProxyType = string.IsNullOrWhiteSpace(state.NetworkProxyType) ? "HTTP" : state.NetworkProxyType;
        state.NetworkProxyHost ??= "";
        state.NetworkProxyUsername ??= "";
        state.NetworkProxyPassword ??= "";

        return state;
    }

    private static string MakeSafeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeChars = value.Select(character => invalidChars.Contains(character) ? '_' : character);
        var safeName = new string(safeChars.ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safeName) ? "profile" : safeName;
    }
}
