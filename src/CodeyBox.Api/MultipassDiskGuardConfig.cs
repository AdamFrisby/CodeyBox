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
        var cfg = opts.DiskGuard;
        if (cfg is null || !cfg.Enabled) return null;
        if (cfg.MinFreeBytes <= 0)
        {
            startupLog.LogWarning(
                "CodeyBox:DiskGuard:MinFreeBytes={MinFreeBytes} is non-positive; disabling disk-guard preflight",
                cfg.MinFreeBytes);
            return null;
        }

        TimeSpan recheck = TimeSpan.FromMinutes(5);
        if (!string.IsNullOrWhiteSpace(cfg.RecheckIn))
        {
            if (!TimeSpan.TryParse(cfg.RecheckIn, out recheck) || recheck <= TimeSpan.Zero)
                throw new InvalidOperationException(
                    $"CodeyBox:DiskGuard:RecheckIn '{cfg.RecheckIn}' must be a positive TimeSpan (e.g. '00:05:00').");
        }

        // Auto-include the state-database directory so a write-side ENOSPC is
        // caught by the preflight before it surfaces as SQLITE_FULL.
        var extras = new List<string>(cfg.AdditionalPaths);
        if (!string.IsNullOrWhiteSpace(opts.StateDatabasePath))
        {
            var dbDir = Path.GetDirectoryName(opts.StateDatabasePath);
            if (!string.IsNullOrEmpty(dbDir) && !extras.Contains(dbDir, StringComparer.Ordinal))
                extras.Add(dbDir);
        }

        return new MultipassDiskGuardOptions
        {
            MinFreeBytes = cfg.MinFreeBytes,
            MultipassDataPath = cfg.MultipassDataPath,
            RecheckIn = recheck,
            AdditionalPaths = extras,
        };
    }
}
