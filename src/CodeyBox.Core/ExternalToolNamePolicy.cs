using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Single source of truth for the bare-executable-name policy shared by the
/// plugin SDK execution path and the orchestrator install/verify path. Both
/// boundaries must agree on the same input: a name accepted at execution but
/// rejected at install (or vice versa) is a latent correctness fork.
/// </summary>
public static class ExternalToolNamePolicy
{
    /// <summary>Maximum total length of a bare executable name, in characters.</summary>
    public const int MaxLength = 64;

    // Bare executable name: first char alnum, then alnum/dot/underscore/hyphen.
    // No slashes (no paths), no whitespace, no shell metacharacters — safe to
    // pass as a single argv element or as "$1" to a host-owned sh -c script.
    private static readonly Regex BareNamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Returns true when <paramref name="binary"/> is a bare executable name
    /// under <see cref="MaxLength"/> characters.
    /// </summary>
    public static bool IsValidBinaryName(string? binary) =>
        !string.IsNullOrWhiteSpace(binary) && BareNamePattern.IsMatch(binary);
}
