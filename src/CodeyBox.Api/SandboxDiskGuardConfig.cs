using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

/// <summary>
/// Provider-neutral parsing for the shared <c>CodeyBox:DiskGuard</c> policy.
/// Provider adapters add their own storage targets while consuming the same
/// enablement, threshold, recheck delay, and host-path set.
/// </summary>
internal static class SharedDiskGuardConfig
{
    private const int MaximumAdditionalPaths = 64;

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
        if (!string.IsNullOrWhiteSpace(config.RecheckIn)
            && (!TimeSpan.TryParse(config.RecheckIn, out recheck) || recheck <= TimeSpan.Zero))
        {
            throw new InvalidOperationException(
                $"CodeyBox:DiskGuard:RecheckIn '{config.RecheckIn}' must be a positive TimeSpan (e.g. '00:05:00').");
        }

        var configuredPaths = config.AdditionalPaths ?? [];
        if (configuredPaths.Count > MaximumAdditionalPaths)
        {
            throw new InvalidOperationException(
                $"CodeyBox:DiskGuard:AdditionalPaths cannot contain more than {MaximumAdditionalPaths} entries.");
        }
        var paths = new List<string>(configuredPaths);
        if (!string.IsNullOrWhiteSpace(options.StateDatabasePath))
        {
            var databaseDirectory = Path.GetDirectoryName(options.StateDatabasePath);
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

internal sealed record ResolvedDiskGuardConfig(
    long MinFreeBytes,
    TimeSpan RecheckIn,
    IReadOnlyList<string> AdditionalPaths);
