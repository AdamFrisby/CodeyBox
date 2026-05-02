namespace CodeyBox.Core;

/// <summary>
/// Orchestrator host API version contract. Plugin authors declare
/// <c>MinHostApiVersion</c> in <c>[CodeyBoxPlugin]</c>; the loader rejects
/// plugins that require a newer host than this build provides.
///
/// Versioning rules:
/// - Major bump: breaking change to a Core interface (rename, signature change,
///   removed member). Plugins built against vN-major will not load.
/// - Minor bump: additive, backward-compatible change (new optional interface,
///   new Core type). Plugins built against vN.0 keep working on vN.x.
/// - Patch: no API-surface change; no version bump needed.
/// </summary>
public static class CodeyBoxApiVersion
{
    /// <summary>Current orchestrator host API version.</summary>
    public const string Current = "1.0";

    /// <summary>
    /// Returns true when this host satisfies the plugin's minimum version
    /// requirement. Same major required; host minor must be >= plugin minor.
    /// </summary>
    public static bool Satisfies(string pluginMin)
    {
        if (!TryParse(Current, out var curMajor, out var curMinor)) return false;
        if (!TryParse(pluginMin, out var reqMajor, out var reqMinor)) return false;
        if (curMajor != reqMajor) return false;
        return curMinor >= reqMinor;
    }

    private static bool TryParse(string version, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        var dot = version.IndexOf('.');
        if (dot < 1) return false;
        return int.TryParse(version[..dot], out major)
            && int.TryParse(version[(dot + 1)..], out minor);
    }
}
