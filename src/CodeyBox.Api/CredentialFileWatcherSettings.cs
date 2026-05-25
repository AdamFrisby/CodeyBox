using Microsoft.Extensions.Configuration;

namespace CodeyBox.Api;

internal static class CredentialFileWatcherSettings
{
    public const string EnvironmentVariable = "CODEYBOX_CREDENTIAL_FILE_WATCHERS";
    public const string ConfigurationKey = "CodeyBox:CredentialFileWatchers";

    public static bool IsEnabled(IConfiguration configuration)
        => IsEnabled(configuration, Environment.GetEnvironmentVariable(EnvironmentVariable));

    internal static bool IsEnabled(IConfiguration configuration, string? environmentValue)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = environmentValue ?? configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(raw)) return true;

        return raw.Trim() switch
        {
            "0" => false,
            var value when value.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            var value when value.Equals("no", StringComparison.OrdinalIgnoreCase) => false,
            _ => true,
        };
    }
}
