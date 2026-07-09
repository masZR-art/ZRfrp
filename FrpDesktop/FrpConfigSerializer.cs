using System.Globalization;
using System.Text;

namespace FrpDesktop;

public static class FrpConfigSerializer
{
    public static string ToToml(FrpProfile profile)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"serverAddr = {Quote(profile.ServerAddr)}");
        builder.AppendLine($"serverPort = {profile.ServerPort.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"clientID = {Quote(profile.Id)}");
        if (!string.IsNullOrWhiteSpace(profile.AccountId))
        {
            builder.AppendLine($"user = {Quote(profile.AccountId)}");
        }
        builder.AppendLine($"metadatas.zrfrp_client_id = {Quote(profile.Id)}");
        if (!string.IsNullOrWhiteSpace(profile.AccountAccessToken))
        {
            builder.AppendLine($"metadatas.zrfrp_access_token = {Quote(profile.AccountAccessToken)}");
        }

        if (!string.IsNullOrWhiteSpace(profile.Token))
        {
            builder.AppendLine($"auth.token = {Quote(profile.Token)}");
        }

        foreach (var proxy in profile.Proxies.Where(proxy => proxy.Enabled))
        {
            builder.AppendLine();
            builder.AppendLine("[[proxies]]");
            builder.AppendLine($"name = {Quote(proxy.Name)}");
            builder.AppendLine($"type = {Quote(proxy.Type.ToLowerInvariant())}");
            builder.AppendLine($"localIP = {Quote(proxy.LocalIP)}");
            builder.AppendLine($"localPort = {proxy.LocalPort.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"metadatas.zrfrp_tunnel_id = {Quote(proxy.Id)}");

            if (UsesRemotePort(proxy.Type) && proxy.RemotePort > 0)
            {
                builder.AppendLine($"remotePort = {proxy.RemotePort.ToString(CultureInfo.InvariantCulture)}");
            }

            if (UsesCustomDomains(proxy.Type) && !string.IsNullOrWhiteSpace(proxy.CustomDomains))
            {
                builder.AppendLine($"customDomains = {ToTomlArray(proxy.CustomDomains)}");
            }

        }

        return builder.ToString();
    }

    public static FrpProfile FromToml(string toml, string frpcPath, string profileName)
    {
        var profile = new FrpProfile
        {
            Name = profileName,
            FrpcPath = frpcPath
        };

        FrpProxy? currentProxy = null;

        foreach (var rawLine in toml.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = StripComment(rawLine).Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Equals("[[proxies]]", StringComparison.OrdinalIgnoreCase))
            {
                currentProxy = new FrpProxy();
                profile.Proxies.Add(currentProxy);
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (currentProxy is null)
            {
                ApplyProfileValue(profile, key, value);
            }
            else
            {
                ApplyProxyValue(currentProxy, key, value);
            }
        }

        if (profile.Proxies.Count == 0)
        {
            profile.Proxies.Add(CreateDefaultProxy());
        }

        return profile;
    }

    public static FrpProxy CreateDefaultProxy()
    {
        return new FrpProxy
        {
            Name = "MC",
            Type = "tcp",
            LocalIP = "127.0.0.1",
            LocalPort = 25565,
            RemotePort = 25566
        };
    }

    private static void ApplyProfileValue(FrpProfile profile, string key, string value)
    {
        switch (key)
        {
            case "serverAddr":
                profile.ServerAddr = Unquote(value);
                break;
            case "serverPort":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var serverPort))
                {
                    profile.ServerPort = serverPort;
                }
                break;
            case "auth.token":
                profile.Token = Unquote(value);
                break;
        }
    }

    private static void ApplyProxyValue(FrpProxy proxy, string key, string value)
    {
        switch (key)
        {
            case "name":
                proxy.Name = Unquote(value);
                break;
            case "type":
                proxy.Type = Unquote(value);
                break;
            case "localIP":
                proxy.LocalIP = Unquote(value);
                break;
            case "localPort":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var localPort))
                {
                    proxy.LocalPort = localPort;
                }
                break;
            case "remotePort":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var remotePort))
                {
                    proxy.RemotePort = remotePort;
                }
                break;
            case "customDomains":
                proxy.CustomDomains = FromTomlArray(value);
                break;
            case "transport.bandwidthLimit":
                proxy.BandwidthLimit = Unquote(value);
                break;
        }
    }

    private static bool UsesRemotePort(string proxyType)
    {
        return proxyType.Equals("tcp", StringComparison.OrdinalIgnoreCase)
            || proxyType.Equals("udp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool UsesCustomDomains(string proxyType)
    {
        return proxyType.Equals("http", StringComparison.OrdinalIgnoreCase)
            || proxyType.Equals("https", StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        return $"\"{escaped}\"";
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            value = value[1..^1];
        }

        return value
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static string ToTomlArray(string csv)
    {
        var entries = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return $"[{string.Join(", ", entries.Select(Quote))}]";
    }

    private static string FromTomlArray(string value)
    {
        value = value.Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            value = value[1..^1];
        }

        return string.Join(", ",
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Unquote));
    }

    private static string StripComment(string line)
    {
        var inString = false;

        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inString = !inString;
            }

            if (!inString && line[i] == '#')
            {
                return line[..i];
            }
        }

        return line;
    }
}
