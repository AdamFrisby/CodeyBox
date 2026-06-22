using System.Text.Json;

namespace CodeyBox.Cli;

internal static class ConfigResolver
{
    internal static string ConfigDir =>
        Environment.GetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "codeybox");

    internal static string ConfigFilePath => Path.Combine(ConfigDir, "config.json");

    internal static string ApiBaseUrlPrecedence =>
        $"--api-url flag > CODEYBOX_CLI_API_URL environment variable > {ConfigFilePath} config file > built-in default http://localhost:5036";

    internal static ResolvedConfig Resolve(string? flagUrl, string? flagKey)
    {
        var result = new ResolvedConfig();

        var fileConfig = LoadConfigFile();
        if (fileConfig?.ApiBaseUrl is { Length: > 0 } fileUrl)
        {
            result.ApiBaseUrl = fileUrl;
            result.ApiBaseUrlSource = $"{ConfigFilePath} config file";
        }
        if (fileConfig?.ApiKey is { Length: > 0 } fileKey)
            result.ApiKey = fileKey;

        var envUrl = Environment.GetEnvironmentVariable("CODEYBOX_CLI_API_URL");
        if (!string.IsNullOrEmpty(envUrl))
        {
            result.ApiBaseUrl = envUrl;
            result.ApiBaseUrlSource = "CODEYBOX_CLI_API_URL environment variable";
        }
        var envKey = Environment.GetEnvironmentVariable("CODEYBOX_CLI_API_KEY");
        if (!string.IsNullOrEmpty(envKey)) result.ApiKey = envKey;

        if (!string.IsNullOrEmpty(flagUrl))
        {
            result.ApiBaseUrl = flagUrl;
            result.ApiBaseUrlSource = "--api-url flag";
        }
        if (!string.IsNullOrEmpty(flagKey)) result.ApiKey = flagKey;

        // Warn when --api-key flag is used: the value appears in the OS process list.
        if (!string.IsNullOrEmpty(flagKey) && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            Console.Error.WriteLine("Warning: --api-key is visible in the OS process list. Prefer CODEYBOX_CLI_API_KEY env var in scripts.");

        // Warn when a non-loopback HTTP URL is configured: bearer token would be transmitted in cleartext.
        if (Uri.TryCreate(result.ApiBaseUrl, UriKind.Absolute, out var apiUri)
            && apiUri.Scheme == "http"
            && !IsLoopbackHost(apiUri.Host))
            Console.Error.WriteLine(
                $"Warning: API base URL '{result.ApiBaseUrl}' uses plaintext HTTP on a non-loopback address; the bearer token will be sent unencrypted.");

        return result;
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "::1"
        || host == "[::1]";

    internal static CliConfig? LoadConfigFile()
    {
        var path = ConfigFilePath;
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, CliJsonContext.Default.CliConfig);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Console.Error.WriteLine($"Warning: config file exists but could not be loaded ({path}): {e.Message}");
            return null;
        }
    }

    internal static void SaveConfigFile(CliConfig config)
    {
        var dir = ConfigDir;
        Directory.CreateDirectory(dir);
        // Restrict directory to owner-only on Unix so the API key cannot be read by other local users (CWE-732).
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var path = ConfigFilePath;
        var json = JsonSerializer.Serialize(config, CliJsonContext.Default.CliConfig);
        // Write to a temp file and set permissions before renaming into place, to avoid a
        // window where the config file exists but is world-readable (TOCTOU, CWE-732).
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, json);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(tmpPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmpPath, path, overwrite: true);
    }
}
