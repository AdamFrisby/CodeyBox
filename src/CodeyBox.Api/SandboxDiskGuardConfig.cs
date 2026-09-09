using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

/// <summary>
/// Provider-neutral parsing for the shared <c>CodeyBox:DiskGuard</c> policy.
/// Provider adapters add their own storage targets while consuming the same
/// enablement, threshold, recheck delay, and host-path set.
/// </summary>
internal static class SharedDiskGuardConfig
{
    private const int MaximumPathCharacters = 4096;
    private const int MaximumRecheckInCharacters = 64;

    internal static ResolvedDiskGuardConfig? Resolve(CodeyBoxOptions options, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        var config = options.DiskGuard;
        if (config is null || !config.Enabled) return null;
        if (config.MinFreeBytes <= 0)
        {
            log.LogWarning(
                "CodeyBox:DiskGuard:MinFreeBytes={MinFreeBytes} is non-positive; disabling disk-guard preflight",
                config.MinFreeBytes);
            return null;
        }

        var recheck = TimeSpan.FromMinutes(5);
        var configuredRecheckIn = config.RecheckIn;
        if (configuredRecheckIn is not null)
        {
            ConfigurationInputBounds.EnsureCharacterBound(
                configuredRecheckIn,
                MaximumRecheckInCharacters,
                "CodeyBox:DiskGuard:RecheckIn");
        }
        if (!string.IsNullOrWhiteSpace(configuredRecheckIn)
            && (!TimeSpan.TryParse(configuredRecheckIn, out recheck) || recheck <= TimeSpan.Zero))
        {
            throw new InvalidOperationException(
                $"CodeyBox:DiskGuard:RecheckIn '{configuredRecheckIn}' must be a positive TimeSpan (e.g. '00:05:00').");
        }

        var configuredPaths = config.AdditionalPaths ?? [];
        var paths = new List<string>(Math.Min(DiskGuardOptions.MaximumAdditionalPaths, 16));
        foreach (var path in configuredPaths)
        {
            if (paths.Count >= DiskGuardOptions.MaximumAdditionalPaths)
            {
                throw new InvalidOperationException(
                    $"CodeyBox:DiskGuard:AdditionalPaths cannot contain more than {DiskGuardOptions.MaximumAdditionalPaths} entries.");
            }
            ConfigurationInputBounds.EnsureCharacterBound(
                path,
                MaximumPathCharacters,
                "CodeyBox:DiskGuard:AdditionalPaths entry");
            paths.Add(path);
        }
        var stateDatabasePath = options.StateDatabasePath;
        if (stateDatabasePath is not null)
        {
            ConfigurationInputBounds.EnsureCharacterBound(
                stateDatabasePath,
                MaximumPathCharacters,
                "CodeyBox:StateDatabasePath");
        }
        if (!string.IsNullOrWhiteSpace(stateDatabasePath))
        {
            var databaseDirectory = Path.GetDirectoryName(stateDatabasePath);
            if (!string.IsNullOrEmpty(databaseDirectory)
                && !paths.Contains(databaseDirectory, StringComparer.Ordinal))
            {
                paths.Add(databaseDirectory);
            }
        }

        return new ResolvedDiskGuardConfig(
            config.MinFreeBytes,
            recheck,
            Array.AsReadOnly(paths.ToArray()));
    }
}

/// <summary>Cheap pre-scan guards for untrusted operator configuration text.</summary>
internal static class ConfigurationInputBounds
{
    internal static void EnsureCharacterBound(string? value, int maximumCharacters, string fieldName)
    {
        if (value is null)
            throw new InvalidOperationException($"{fieldName} cannot be null.");
        if (value.Length > maximumCharacters)
        {
            throw new InvalidOperationException(
                $"{fieldName} exceeds its {maximumCharacters}-character safety bound.");
        }
    }
}

internal sealed record ResolvedDiskGuardConfig(
    long MinFreeBytes,
    TimeSpan RecheckIn,
    IReadOnlyList<string> AdditionalPaths);
