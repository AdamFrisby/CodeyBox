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

        var value = raw.Trim();
        if (bool.TryParse(value, out var enabled))
            return enabled;

        return value switch
        {
            "0" => false,
            "1" => true,
            var v when v.Equals("no", StringComparison.OrdinalIgnoreCase) => false,
            var v when v.Equals("off", StringComparison.OrdinalIgnoreCase) => false,
            var v when v.Equals("yes", StringComparison.OrdinalIgnoreCase) => true,
            var v when v.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
            _ => throw new InvalidOperationException(
                $"{EnvironmentVariable} or {ConfigurationKey} must be a boolean-like value: true/false, 1/0, yes/no, or on/off."),
        };
    }
}
