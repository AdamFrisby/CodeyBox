namespace CodeyBox.Core;

/// <summary>
/// Pure placement-eligibility predicate for sandbox placement members. This is
/// the single place that decides whether a registered member may receive new
/// work, so the "registers but is never selected" contract for zero-capacity
/// and cordoned members cannot drift between call sites. Dispatch itself is a
/// separate item; this predicate is what dispatch will consult.
/// </summary>
public static class ExecutorEligibility
{
    /// <summary>
    /// Normalises a required network profile to the comparison form the
    /// shared ordinal matcher consumes: trimmed and lowercased. Blank maps
    /// to null (the decider treats a missing profile as "(default)").
    /// Callers whose documented profile semantics are case-insensitive pass
    /// both the requirement and the declared allowlist through this seam (see
    /// <see cref="NormalizeNetworkProfilesForComparison"/>) so every
    /// placement path projects equivalent inputs and cannot drift apart.
    /// </summary>
    public static string? NormalizeRequiredNetworkProfile(string? profile) =>
        string.IsNullOrWhiteSpace(profile) ? null : profile.Trim().ToLowerInvariant();

    /// <summary>
    /// Projects a network-profile allowlist to the comparison form the
    /// shared ordinal matcher consumes: each entry trimmed and lowercased,
    /// case-insensitively deduplicated. Null entries are dropped; blank
    /// entries project to <see cref="string.Empty"/>, which matches no
    /// profile — mirroring the historical multipass-remote filter that
    /// skipped blanks without accepting on them, so an all-blank declaration
    /// still accepts nothing. An empty result accepts every profile per
    /// <see cref="AcceptsNetworkProfile"/>.
    /// </summary>
    public static IReadOnlyList<string> NormalizeNetworkProfilesForComparison(IEnumerable<string?> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);
        var normalised = new List<string>();
        foreach (var entry in declared)
        {
            if (entry is null)
                continue;
            var candidate = entry.Trim().ToLowerInvariant();
            if (!normalised.Contains(candidate, StringComparer.Ordinal))
                normalised.Add(candidate);
        }

        return normalised;
    }

    /// <summary>
    /// True when the member may be selected for a new placement given its
    /// current live load (<paramref name="currentLoad"/> sandboxes already
    /// running on it). A member declaring zero capacity, declaring itself
    /// cordoned, or reporting unhealthy is registered but never selected.
    /// </summary>
    /// <param name="member">The member's placement attributes.</param>
    /// <param name="currentLoad">
    /// Sandboxes currently running on the host. Negative values are treated
    /// as zero (a caller bug must not make a full host eligible).
    /// </param>
    public static bool IsEligibleForPlacement(SandboxPlacementMember member, int currentLoad)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.Cordoned || !member.Healthy)
            return false;
        if (member.MaxConcurrentSandboxes is not { } capacity)
            return true;
        if (capacity <= 0)
            return false;
        return Math.Max(0, currentLoad) < capacity;
    }

    /// <summary>
    /// True when the member accepts the given sandbox network profile.
    /// Mirrors <c>MultipassRemoteSandboxOptions.AllowedNetworkProfiles</c>
    /// semantics: an empty declaration or a "*" entry accepts every profile;
    /// otherwise the profile must match a declared entry by exact ordinal
    /// equality (never substring).
    /// </summary>
    public static bool AcceptsNetworkProfile(SandboxPlacementMember member, string? profileName)
    {
        ArgumentNullException.ThrowIfNull(member);
        var declared = member.NetworkProfiles;
        if (declared.Count == 0)
            return true;
        var profile = string.IsNullOrWhiteSpace(profileName) ? "(default)" : profileName.Trim();
        foreach (var entry in declared)
        {
            if (entry is null)
                continue;
            var candidate = entry.Trim();
            if (candidate is "*" || string.Equals(candidate, profile, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the member declares holding the credential set required by
    /// the given agent class. Credential names are opaque: matched by exact
    /// ordinal equality, never by substring, so "codex" never implies
    /// "codex-admin".
    /// </summary>
    public static bool HoldsCredential(SandboxPlacementMember member, string credentialName)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialName);
        var wanted = credentialName.Trim();
        foreach (var entry in member.Credentials)
        {
            if (string.Equals(entry?.Trim(), wanted, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the member's declared capabilities cover every required
    /// tag. Uses the same vocabulary and comparison as the agent-class
    /// router's <c>RequiredCapabilities</c> gate: ordinal, case-insensitive,
    /// exact equality per tag. An empty required set is covered by any member.
    /// </summary>
    public static bool CoversRequiredCapabilities(
        SandboxPlacementMember member,
        IReadOnlyList<string>? required)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (required is null || required.Count == 0)
            return true;
        if (member.Capabilities.Count == 0)
            return false;
        foreach (var tag in required)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;
            var wanted = tag.Trim();
            var hit = false;
            foreach (var have in member.Capabilities)
            {
                if (string.Equals(have?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    break;
                }
            }
            if (!hit)
                return false;
        }
        return true;
    }

    /// <summary>
    /// First required capability no member in <paramref name="members"/> declares
    /// (ordinal, case-insensitive), or null when every required tag is held
    /// by at least one member. Blank required entries are ignored.
    /// </summary>
    public static string? FindCapabilityNoHostProvides(
        IEnumerable<SandboxPlacementMember> members,
        IReadOnlyList<string>? required)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (required is null || required.Count == 0)
            return null;
        foreach (var tag in required)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;
            var wanted = tag.Trim();
            var provided = false;
            foreach (var member in members)
            {
                ArgumentNullException.ThrowIfNull(member);
                foreach (var have in member.Capabilities)
                {
                    if (string.Equals(have?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        provided = true;
                        break;
                    }
                }
                if (provided)
                    break;
            }
            if (!provided)
                return wanted;
        }
        return null;
    }
}
