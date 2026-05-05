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

    internal static ResolvedConfig Resolve(string? flagUrl, string? flagKey)
    {
        var result = new ResolvedConfig();

        var fileConfig = LoadConfigFile();
        if (fileConfig?.ApiBaseUrl is { Length: > 0 } fileUrl)
            result.ApiBaseUrl = fileUrl;
        if (fileConfig?.ApiKey is { Length: > 0 } fileKey)
            result.ApiKey = fileKey;

        var envUrl = Environment.GetEnvironmentVariable("CODEYBOX_CLI_API_URL");
        if (!string.IsNullOrEmpty(envUrl)) result.ApiBaseUrl = envUrl;
        var envKey = Environment.GetEnvironmentVariable("CODEYBOX_CLI_API_KEY");
        if (!string.IsNullOrEmpty(envKey)) result.ApiKey = envKey;

        if (!string.IsNullOrEmpty(flagUrl)) result.ApiBaseUrl = flagUrl;
        if (!string.IsNullOrEmpty(flagKey)) result.ApiKey = flagKey;

        return result;
    }

    internal static CliConfig? LoadConfigFile()
    {
        var path = ConfigFilePath;
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, CliJsonContext.Default.CliConfig);
        }
        catch
        {
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
        File.WriteAllText(path, json);
        // Restrict file to owner read/write only on Unix.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
