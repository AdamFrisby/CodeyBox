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
}
