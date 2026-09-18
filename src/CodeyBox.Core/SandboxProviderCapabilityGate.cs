namespace CodeyBox.Core;

/// <summary>
/// Intersects a member's operator-declared capabilities with the well-known
/// operations its backing provider actually implements (see
/// <see cref="ISandboxProvider.DeclaredCapabilities"/> and
/// <see cref="SandboxCapabilities"/>). A member whose config claims a
/// well-known operation its provider lacks (for example
/// <c>"suspend-resume"</c> on a provider that declares none) must not be
/// selected for work needing that operation: the claim is dropped before the
/// placement decider runs, so the member shows
/// <c>missing-capability:&lt;tag&gt;</c> and — when no other member covers
/// the tag — the refusal names the tag as unplaceable instead of dispatching
/// work to a provider that cannot perform it.
/// </summary>
/// <remarks>
/// Only well-known <see cref="SandboxCapabilities.All"/> tags are gated on
/// the provider. Any other tag is an operator clearance tag the provider has
/// no opinion on; it passes through on the member's declaration alone. The
/// pure decider (<see cref="ExecutorPlacement"/>) is unchanged — this gate
/// shapes its input, so there is exactly one eligibility implementation.
/// </remarks>
public static class SandboxProviderCapabilityGate
{
    /// <summary>
    /// Projects <paramref name="member"/> to the placement attributes the
    /// decider consumes, keeping every non-well-known capability tag and only
    /// those well-known tags <paramref name="provider"/> declares (ordinal,
    /// case-insensitive). All other placement attributes project verbatim via
    /// <see cref="SandboxMember.ToPlacementMember"/>.
    /// </summary>
    public static SandboxPlacementMember ApplyProviderCapabilities(
        SandboxMember member,
        ISandboxProvider provider)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(provider);

        var declared = provider.DeclaredCapabilities ?? [];
        var effective = new List<string>(member.Capabilities.Count);
        foreach (var raw in member.Capabilities)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var tag = raw.Trim();
            if (!IsWellKnown(tag) || Declares(declared, tag))
                effective.Add(tag);
        }

        var projected = member.ToPlacementMember();
        return projected with { Capabilities = effective };
    }

    private static bool IsWellKnown(string tag)
    {
        foreach (var known in SandboxCapabilities.All)
        {
            if (string.Equals(known, tag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool Declares(IReadOnlyList<string> declared, string tag)
    {
        foreach (var have in declared)
        {
            if (string.Equals(have?.Trim(), tag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
