using System.Text.RegularExpressions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// A validated external-tool requirement declared by a plugin via
/// <c>CodeyBoxPluginRequiresToolAttribute</c>. Instances are only produced by
/// <see cref="TryCreate"/>: the declaration is untrusted input from the
/// host's perspective, so raw attribute strings never reach a sink — only
/// validated values do. Construct one per (plugin, binary) pair; the same
/// binary required by two plugins yields two instances so ownership stays
/// visible in logs and verification labels.
/// </summary>
public sealed record PluginToolRequirement
{
    /// <summary>Owning plugin ID (from <c>CodeyBoxPluginAttribute</c>).</summary>
    public required string PluginId { get; init; }

    /// <summary>Validated bare executable name, e.g. <c>"dotnet"</c>.</summary>
    public required string Binary { get; init; }

    /// <summary>Validated Debian package name, or null when not apt-installable.</summary>
    public string? AptPackage { get; init; }

    /// <summary>Sanitized operator guidance (display only, never executed).</summary>
    public string? InstallHint { get; init; }

    /// <summary>Maximum tools accepted from a single plugin; extras are dropped with a warning.</summary>
    public const int MaxToolsPerPlugin = 16;

    /// <summary>Maximum tools aggregated across all enabled plugins; extras are dropped with a warning.</summary>
    public const int MaxTotalTools = 64;

    /// <summary>Maximum characters kept from a plugin-supplied install hint.</summary>
    public const int MaxInstallHintLength = 512;

    // Bare executable name: first char alnum, then alnum/dot/underscore/hyphen.
    // No slashes (no paths), no whitespace, no shell metacharacters — safe to
    // pass as a single argv element or as "$1" to a host-owned sh -c script.
    private static readonly Regex BinaryNamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Debian package name shape (lowercase, must not start with a dash so it
    // can never be mistaken for an apt option). Tilde excluded: unnecessary
    // and tilde-expands at shell word start.
    private static readonly Regex AptPackagePattern = new(
        @"^[a-z0-9][a-z0-9+.\-]{0,126}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private PluginToolRequirement() { }

    /// <summary>
    /// Validates a raw plugin tool declaration. Returns false (fail closed —
    /// the caller skips the whole plugin) when any field is malformed;
    /// <paramref name="error"/> carries the operator-facing reason.
    /// </summary>
    public static bool TryCreate(
        string pluginId,
        string? binary,
        string? aptPackage,
        string? installHint,
        out PluginToolRequirement? requirement,
        out string? error)
    {
        requirement = null;
        error = null;

        if (string.IsNullOrWhiteSpace(pluginId))
        {
            error = "plugin id is blank";
            return false;
        }

        if (string.IsNullOrWhiteSpace(binary) || !BinaryNamePattern.IsMatch(binary))
        {
            error = $"binary name '{Truncate(binary)}' is not a bare executable name " +
                    "(letters, digits, dot, underscore, hyphen; no paths or shell characters)";
            return false;
        }

        string? package = null;
        if (!string.IsNullOrWhiteSpace(aptPackage))
        {
            if (!AptPackagePattern.IsMatch(aptPackage))
            {
                error = $"apt package '{Truncate(aptPackage)}' is not a valid package name " +
                        "(lowercase letters, digits, plus, dot)";
                return false;
            }
            package = aptPackage;
        }

        requirement = new PluginToolRequirement
        {
            PluginId = pluginId,
            Binary = binary,
            AptPackage = package,
            InstallHint = SanitizeHint(installHint),
        };
        return true;
    }

    /// <summary>
    /// Canonical fingerprint used in baseline-identity inputs: only validated
    /// fields, deterministic ordering left to the caller.
    /// </summary>
    public string ToFingerprint() =>
        AptPackage is null ? $"{PluginId}\u001f{BINARY_PREFIX}{Binary}" : $"{PluginId}\u001f{BINARY_PREFIX}{Binary}\u001f{APT_PREFIX}{AptPackage}";

    private const string BINARY_PREFIX = "bin=";
    private const string APT_PREFIX = "apt=";

    private static string? SanitizeHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return null;
        // Display text only: strip control characters (log/terminal-escape
        // safety) and cap length before buffering.
        var cleaned = new string(hint.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
            return null;
        return cleaned.Length > MaxInstallHintLength ? cleaned[..MaxInstallHintLength] : cleaned;
    }

    private static string Truncate(string? value, int max = 64)
    {
        if (string.IsNullOrEmpty(value))
            return "(empty)";
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length > max ? oneLine[..max] + "…" : oneLine;
    }
}
