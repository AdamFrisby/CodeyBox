using Microsoft.Extensions.Logging;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Api;

/// <summary>
/// Pure-function builder that translates the operator-facing
/// <see cref="CodeyBoxOptions.DiskGuard"/> section into the
/// <see cref="MultipassDiskGuardOptions"/> the multipass provider consumes.
/// Lives outside Program.cs so the startup-wiring branches (disabled,
/// non-positive threshold, RecheckIn parsing, state-database directory
/// auto-include) are unit-testable without spinning up a WebApplication.
/// </summary>
internal static class MultipassDiskGuardConfig
{
    /// <summary>
    /// Returns the <see cref="MultipassDiskGuardOptions"/> wiring derived
    /// from <paramref name="opts"/>, or <c>null</c> when the guard should
    /// be disabled. Mirrors the order Program.cs applied historically:
    /// disabled → null; non-positive threshold → warn + null; invalid
    /// RecheckIn → throw; otherwise build and auto-include the state-db
    /// directory.
    /// </summary>
    public static MultipassDiskGuardOptions? Build(CodeyBoxOptions opts, ILogger startupLog)
    {
        var resolved = SharedDiskGuardConfig.Resolve(opts, startupLog);
        if (resolved is null) return null;

        return new MultipassDiskGuardOptions
        {
            MinFreeBytes = resolved.MinFreeBytes,
            MultipassDataPath = opts.DiskGuard.MultipassDataPath,
            RecheckIn = resolved.RecheckIn,
            AdditionalPaths = resolved.AdditionalPaths,
        };
    }
}

/// <summary>
/// Provider-neutral parsing for the shared <c>CodeyBox:DiskGuard</c> policy.
/// Provider adapters add their own storage targets while consuming the same
/// enablement, threshold, recheck delay, and host-path set.
/// </summary>
internal static class SharedDiskGuardConfig
{
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

        // Auto-include the state-database directory so a write-side ENOSPC is
        // caught by the preflight before it surfaces as SQLITE_FULL.
        var paths = new List<string>(config.AdditionalPaths ?? []);
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
