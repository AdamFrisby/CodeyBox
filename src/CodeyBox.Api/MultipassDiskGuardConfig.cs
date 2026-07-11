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
