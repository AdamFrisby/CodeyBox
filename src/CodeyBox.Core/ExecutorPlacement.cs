namespace CodeyBox.Core;

/// <summary>
/// Placement requirements for one phase dispatch. Derived from the
/// <see cref="ExecutorPhaseRequest"/> placement fields so the pure
/// <see cref="ExecutorPlacement"/> decider stays decoupled from the
/// dispatch envelope.
/// </summary>
public sealed record ExecutorPlacementRequirements
{
    /// <summary>Maximum entries accepted in <see cref="RequiredCapabilities"/>.</summary>
    public const int MaxRequiredCapabilities = 16;

    /// <summary>Maximum chars accepted in a credential, profile or capability entry.</summary>
    public const int MaxEntryLength = 128;

    /// <summary>Credential the route needs, or null when the phase needs none.</summary>
    public string? RequiredCredential { get; init; }

    /// <summary>Network profile the sandbox target requires, or null for "(default)".</summary>
    public string? RequiredNetworkProfile { get; init; }

    /// <summary>Clearance tags the work item demands, in the existing capability vocabulary.</summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];

    /// <summary>
    /// Builds requirements from a dispatch request. Trims entries; blank
    /// credential/profile become null; blank capability entries are dropped.
    /// Throws <see cref="ArgumentException"/> when a bound is exceeded so
    /// misconfigured callers fail fast instead of silently widening placement.
    /// </summary>
    public static ExecutorPlacementRequirements FromRequest(ExecutorPhaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var credential = string.IsNullOrWhiteSpace(request.RequiredCredential)
            ? null
            : request.RequiredCredential.Trim();
        if (credential is not null && credential.Length > MaxEntryLength)
            throw new ArgumentException(
                $"RequiredCredential must be at most {MaxEntryLength} characters.", nameof(request));
        if (credential is not null && credential.Any(char.IsControl))
            throw new ArgumentException("RequiredCredential must not contain control characters.", nameof(request));

        var profile = string.IsNullOrWhiteSpace(request.RequiredNetworkProfile)
            ? null
            : request.RequiredNetworkProfile.Trim();
        if (profile is not null && profile.Length > MaxEntryLength)
            throw new ArgumentException(
                $"RequiredNetworkProfile must be at most {MaxEntryLength} characters.", nameof(request));
        if (profile is not null && profile.Any(char.IsControl))
            throw new ArgumentException("RequiredNetworkProfile must not contain control characters.", nameof(request));

        var capabilities = request.RequiredCapabilities ?? [];
        if (capabilities.Count > MaxRequiredCapabilities)
            throw new ArgumentException(
                $"RequiredCapabilities may contain at most {MaxRequiredCapabilities} entries.", nameof(request));
        var normalised = new List<string>(capabilities.Count);
        foreach (var raw in capabilities)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var tag = raw.Trim();
            if (tag.Length > MaxEntryLength)
                throw new ArgumentException(
                    $"RequiredCapabilities entries must be at most {MaxEntryLength} characters.", nameof(request));
            if (tag.Any(char.IsControl))
                throw new ArgumentException("RequiredCapabilities entries must not contain control characters.", nameof(request));
            normalised.Add(tag);
        }

        return new ExecutorPlacementRequirements
        {
            RequiredCredential = credential,
            RequiredNetworkProfile = profile,
            RequiredCapabilities = normalised,
        };
    }
}

/// <summary>Per-candidate outcome of a placement decision, for observability.</summary>
public sealed record ExecutorPlacementCandidateOutcome
{
    public required string HostId { get; init; }

    /// <summary>True when this host may receive the phase.</summary>
    public required bool Eligible { get; init; }

    /// <summary>
    /// Machine-readable exclusion reason for ineligible hosts, or "eligible"
    /// / "selected" for eligible ones. Values: <c>selected</c>,
    /// <c>eligible</c>, <c>cordoned</c>, <c>unhealthy</c>,
    /// <c>runtime-unhealthy</c>, <c>at-capacity</c>,
    /// <c>missing-credential:&lt;name&gt;</c>,
    /// <c>network-profile:&lt;profile&gt;</c>,
    /// <c>missing-capability:&lt;tag&gt;</c>, <c>not-selected</c>.
    /// </summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Observable result of a placement decision: the chosen host (if any) plus
/// the per-candidate reason list. When no host is eligible,
/// <see cref="UnmetCapability"/> names the required tag no registered host
/// provides (permanent, unplaceable); null means the refusal is transient
/// (capacity, cordon, health, credential or profile mismatch on the
/// currently-available set) and the caller must requeue under backoff.
/// </summary>
public sealed record ExecutorPlacementDecision
{
    public required string? SelectedHostId { get; init; }

    public required IReadOnlyList<ExecutorPlacementCandidateOutcome> Candidates { get; init; }

    /// <summary>Required capability no registered host declares, if any.</summary>
    public string? UnmetCapability { get; init; }

    /// <summary>True when the item can never place until registration changes.</summary>
    public bool IsUnplaceable => UnmetCapability is not null;

    /// <summary>Human-readable one-line summary for logs and exception details.</summary>
    public string Describe()
    {
        if (SelectedHostId is not null)
            return $"selected={SelectedHostId}; candidates=[{string.Join(", ", Candidates.Select(c => $"{c.HostId}={c.Reason}"))}]";
        if (UnmetCapability is not null)
            return $"unplaceable missing-capability={UnmetCapability}; candidates=[{string.Join(", ", Candidates.Select(c => $"{c.HostId}={c.Reason}"))}]";
        return $"no-eligible-host; candidates=[{string.Join(", ", Candidates.Select(c => $"{c.HostId}={c.Reason}"))}]";
    }
}

/// <summary>
/// Pure executor-host placement decider. Matches a phase's requirements
/// (agent credential, network profile, required capabilities) against each
/// host's declared attributes, excluding cordoned and unhealthy hosts and
/// hosts at capacity. Deterministic: among eligible hosts the least-loaded
/// wins, ties broken by fewest in-flight reservations then ordinal host id —
/// mirroring the multipass-remote sandbox placement ordering so the two
/// placement paths cannot drift apart.
/// </summary>
public static class ExecutorPlacement
{
    /// <summary>
    /// Decides placement over the given hosts. <paramref name="loads"/>
    /// carries the current live load per host id (missing means zero);
    /// <paramref name="runtimeUnhealthy"/> lists host ids under runtime
    /// backoff (skipped like unhealthy hosts). Never throws for empty input:
    /// with no registered hosts the decision simply selects nothing.
    /// </summary>
    public static ExecutorPlacementDecision Decide(
        IReadOnlyList<ExecutorRegistration> hosts,
        ExecutorPlacementRequirements requirements,
        IReadOnlyDictionary<string, int>? loads = null,
        ISet<string>? runtimeUnhealthy = null)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(requirements);

        var outcomes = new List<ExecutorPlacementCandidateOutcome>(hosts.Count);
        string? selected = null;
        var selectedLoad = double.MaxValue;
        var selectedUsed = int.MaxValue;

        foreach (var host in hosts.OrderBy(h => h.HostId, StringComparer.Ordinal))
        {
            var used = 0;
            if (loads is not null && loads.TryGetValue(host.HostId, out var load))
                used = Math.Max(0, load);
            var reason = ExcludeReason(host, requirements, used, runtimeUnhealthy);
            if (reason is null)
            {
                var capacity = host.MaxConcurrentSandboxes is { } cap ? cap : int.MaxValue;
                var loadRatio = capacity == int.MaxValue ? 0.0d : (double)used / capacity;
                outcomes.Add(new ExecutorPlacementCandidateOutcome { HostId = host.HostId, Eligible = true, Reason = "eligible" });
                if (selected is null
                    || loadRatio < selectedLoad
                    || (Math.Abs(loadRatio - selectedLoad) < double.Epsilon && used < selectedUsed))
                {
                    selected = host.HostId;
                    selectedLoad = loadRatio;
                    selectedUsed = used;
                }
            }
            else
            {
                outcomes.Add(new ExecutorPlacementCandidateOutcome { HostId = host.HostId, Eligible = false, Reason = reason });
            }
        }

        for (var i = 0; i < outcomes.Count; i++)
        {
            if (selected is not null
                && outcomes[i].Eligible
                && string.Equals(outcomes[i].HostId, selected, StringComparison.Ordinal))
            {
                outcomes[i] = outcomes[i] with { Reason = "selected" };
            }
            else if (outcomes[i].Eligible)
            {
                outcomes[i] = outcomes[i] with { Reason = "not-selected" };
            }
        }

        string? unmet = null;
        if (selected is null && hosts.Count > 0)
            unmet = ExecutorEligibility.FindCapabilityNoHostProvides(hosts, requirements.RequiredCapabilities);

        return new ExecutorPlacementDecision
        {
            SelectedHostId = selected,
            Candidates = outcomes,
            UnmetCapability = unmet,
        };
    }

    private static string? ExcludeReason(
        ExecutorRegistration host,
        ExecutorPlacementRequirements requirements,
        int used,
        ISet<string>? runtimeUnhealthy)
    {
        if (host.Cordoned)
            return "cordoned";
        if (!host.Healthy)
            return "unhealthy";
        if (runtimeUnhealthy is not null && runtimeUnhealthy.Contains(host.HostId))
            return "runtime-unhealthy";
        if (host.MaxConcurrentSandboxes is { } capacity)
        {
            if (capacity <= 0)
                return $"at-capacity({used}/0)";
            if (used >= capacity)
                return $"at-capacity({used}/{capacity})";
        }
        if (!string.IsNullOrEmpty(requirements.RequiredCredential)
            && !ExecutorEligibility.HoldsCredential(host, requirements.RequiredCredential!))
            return $"missing-credential:{requirements.RequiredCredential}";
        var profile = string.IsNullOrWhiteSpace(requirements.RequiredNetworkProfile)
            ? null
            : requirements.RequiredNetworkProfile.Trim();
        if (!ExecutorEligibility.AcceptsNetworkProfile(host, profile))
            return $"network-profile:{(profile ?? "(default)")}";
        if (!ExecutorEligibility.CoversRequiredCapabilities(host, requirements.RequiredCapabilities))
        {
            var missing = FirstMissingCapability(host, requirements.RequiredCapabilities);
            return $"missing-capability:{missing}";
        }
        return null;
    }

    private static string FirstMissingCapability(
        ExecutorRegistration host,
        IReadOnlyList<string> required)
    {
        foreach (var tag in required)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;
            var wanted = tag.Trim();
            var hit = false;
            foreach (var have in host.DeclaredCapabilities)
            {
                if (string.Equals(have?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    break;
                }
            }
            if (!hit)
                return wanted;
        }
        return "(unknown)";
    }
}
