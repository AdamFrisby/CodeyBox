namespace CodeyBox.Core;

/// <summary>
/// Pure placement-eligibility predicate for executor hosts. This is the single
/// place that decides whether a registered executor may receive new work, so
/// the "registers but is never selected" contract for zero-capacity and
/// cordoned executors cannot drift between call sites. Dispatch itself is a
/// separate item; this predicate is what dispatch will consult.
/// </summary>
public static class ExecutorEligibility
{
    /// <summary>
    /// True when the executor may be selected for a new placement given its
    /// current live load (<paramref name="currentLoad"/> sandboxes already
    /// running on it). An executor declaring zero capacity, declaring itself
    /// cordoned, or reporting unhealthy is registered but never selected.
    /// </summary>
    /// <param name="registration">The executor's declared attributes.</param>
    /// <param name="currentLoad">
    /// Sandboxes currently running on the host. Negative values are treated
    /// as zero (a caller bug must not make a full host eligible).
    /// </param>
    public static bool IsEligibleForPlacement(ExecutorRegistration registration, int currentLoad)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.Cordoned || !registration.Healthy)
            return false;
        if (registration.MaxConcurrentSandboxes is not { } capacity)
            return true;
        if (capacity <= 0)
            return false;
        return Math.Max(0, currentLoad) < capacity;
    }

    /// <summary>
    /// True when the executor accepts the given sandbox network profile.
    /// Mirrors <c>MultipassRemoteSandboxOptions.AllowedNetworkProfiles</c>
    /// semantics: an empty declaration or a "*" entry accepts every profile;
    /// otherwise the profile must match a declared entry by exact ordinal
    /// equality (never substring).
    /// </summary>
    public static bool AcceptsNetworkProfile(ExecutorRegistration registration, string? profileName)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var declared = registration.AllowedNetworkProfiles;
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
    /// True when the executor declares holding the credential set required by
    /// the given agent class. Credential names are opaque: matched by exact
    /// ordinal equality, never by substring, so "codex" never implies
    /// "codex-admin".
    /// </summary>
    public static bool HoldsCredential(ExecutorRegistration registration, string credentialName)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialName);
        var wanted = credentialName.Trim();
        foreach (var entry in registration.DeclaredCredentials)
        {
            if (string.Equals(entry?.Trim(), wanted, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the executor's declared capabilities cover every required
    /// tag. Uses the same vocabulary and comparison as the agent-class
    /// router's <c>RequiredCapabilities</c> gate: ordinal, case-insensitive,
    /// exact equality per tag. An empty required set is covered by any host.
    /// </summary>
    public static bool CoversRequiredCapabilities(
        ExecutorRegistration registration,
        IReadOnlyList<string>? required)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (required is null || required.Count == 0)
            return true;
        if (registration.DeclaredCapabilities.Count == 0)
            return false;
        foreach (var tag in required)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;
            var wanted = tag.Trim();
            var hit = false;
            foreach (var have in registration.DeclaredCapabilities)
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
    /// First required capability no host in <paramref name="hosts"/> declares
    /// (ordinal, case-insensitive), or null when every required tag is held
    /// by at least one host. Blank required entries are ignored.
    /// </summary>
    public static string? FindCapabilityNoHostProvides(
        IEnumerable<ExecutorRegistration> hosts,
        IReadOnlyList<string>? required)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        if (required is null || required.Count == 0)
            return null;
        foreach (var tag in required)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;
            var wanted = tag.Trim();
            var provided = false;
            foreach (var host in hosts)
            {
                ArgumentNullException.ThrowIfNull(host);
                foreach (var have in host.DeclaredCapabilities)
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
