using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Placement inputs for one sandbox acquisition on the pipeline path: the
/// work item's <see cref="WorkItem.RequiredCapabilities"/> plus the network
/// profile and credential the phase's sandbox target already needs — the same
/// inputs executor phase dispatch assembles, so sandbox placement and
/// executor placement gate on identical requirements.
/// </summary>
/// <param name="WorkItemId">Work item the sandbox belongs to.</param>
/// <param name="Phase">Pipeline phase acquiring the sandbox (for example "work").</param>
/// <param name="RequiredCapabilities">
/// Clearance tags the work item demands, in the existing capability
/// vocabulary. Empty means no clearance required.
/// </param>
/// <param name="RequiredCredential">
/// Agent credential set the phase's route requires (for example "claude").
/// Null means the phase needs no specific credential and any member may
/// serve it. Carries the credential NAME only — never secret material.
/// </param>
/// <param name="RequiredNetworkProfile">
/// Sandbox network profile the phase's sandbox target requires. Null means
/// "(default)".
/// </param>
/// <param name="Spec">Sandbox spec to create on the selected member's provider.</param>
public sealed record SandboxPlacementAcquisition(
    WorkItemId WorkItemId,
    string Phase,
    IReadOnlyList<string> RequiredCapabilities,
    string? RequiredCredential,
    string? RequiredNetworkProfile,
    SandboxSpec Spec);

/// <summary>
/// Acquires pipeline sandboxes through placement: builds requirements with
/// the shared <see cref="ExecutorPlacementRequirements"/> assembler, asks the
/// <see cref="ExecutorPlacement"/> decider for an eligible member, ranks the
/// eligible members by <see cref="SandboxMember.PreferenceScore"/>, resolves
/// the winner's provider from the <see cref="ISandboxProviderRegistry"/>, and
/// creates the sandbox on it.
/// </summary>
/// <remarks>
/// <para>Refusals follow the decision's own classification. A permanent
/// refusal (<see cref="ExecutorPlacementDecision.IsUnplaceable"/> — a required
/// capability no available member declares) throws
/// <see cref="SandboxPlacementUnplaceableException"/> naming the unmet
/// capability so the item fails operator-visible instead of retry-looping. A
/// transient refusal (capacity, cordon, health, credential or profile mismatch
/// on the currently-available set) throws
/// <see cref="SandboxProvisioningDeferredException"/> carrying the existing
/// <c>PlacementRecheckIn</c> backoff so the item requeues under the standard
/// defer-and-requeue path.</para>
/// <para>Loads passed to the decider cover this process's in-flight
/// creations only; the global admission gate still owns real throttling until
/// the per-member-gates item replaces it. Every placement decision is logged
/// with the decision's own <c>Describe()</c> output — the per-candidate
/// reason list is the diagnostic that makes a refused placement debuggable.
/// </para>
/// <para>An empty catalog (minimal embeddings / tests that wire no
/// <c>SandboxClass</c>) delegates to the fallback provider — the legacy
/// single-provider path, unchanged.</para>
/// </remarks>
public sealed class SandboxPlacementAcquirer
{
    private readonly SandboxClassesSnapshot _classes;
    private readonly ISandboxProviderRegistry _registry;
    private readonly ISandboxProvider? _fallback;
    private readonly Func<ExecutorPhaseDispatchOptions> _optionsAccessor;
    private readonly ILogger<SandboxPlacementAcquirer> _log;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _inflightByMember =
        new(StringComparer.Ordinal);

    public SandboxPlacementAcquirer(
        SandboxClassesSnapshot classes,
        ISandboxProviderRegistry registry,
        ISandboxProvider? fallbackProvider = null,
        Func<ExecutorPhaseDispatchOptions>? optionsAccessor = null,
        ILogger<SandboxPlacementAcquirer>? log = null)
    {
        ArgumentNullException.ThrowIfNull(classes);
        ArgumentNullException.ThrowIfNull(registry);
        _classes = classes;
        _registry = registry;
        _fallback = fallbackProvider;
        _optionsAccessor = optionsAccessor ?? (() => new ExecutorPhaseDispatchOptions());
        _log = log ?? NullLogger<SandboxPlacementAcquirer>.Instance;
    }

    /// <summary>
    /// Places the acquisition and creates its sandbox on the winning member's
    /// provider. Throws <see cref="SandboxPlacementUnplaceableException"/> for
    /// permanent refusals and <see cref="SandboxProvisioningDeferredException"/>
    /// (with the configured recheck backoff) for transient ones.
    /// </summary>
    public async Task<ISandbox> AcquireAsync(
        SandboxPlacementAcquisition acquisition,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(acquisition.Spec);
        if (string.IsNullOrWhiteSpace(acquisition.Phase))
            throw new ArgumentException("Phase must be non-empty.", nameof(acquisition));

        var members = FlattenMembers(_classes.Current);
        if (members.Count == 0)
        {
            if (_fallback is null)
                throw new InvalidOperationException(
                    $"Sandbox placement for work item '{acquisition.WorkItemId}' phase '{acquisition.Phase}': " +
                    "no sandbox classes are configured and no fallback provider is wired. " +
                    "Configure CodeyBox:SandboxClasses or wire the legacy provider as fallback.");
            _log.LogDebug(
                "Sandbox placement for work item {WorkItemId} phase {Phase}: no sandbox classes configured; using the fallback provider.",
                acquisition.WorkItemId, acquisition.Phase);
            return await _fallback.CreateAsync(acquisition.Spec, ct).ConfigureAwait(false);
        }

        var options = _optionsAccessor();
        options.Validate();

        // The shared assembler — the same normalisation and bounds executor
        // phase dispatch uses, so the two placement paths cannot drift apart.
        var requirements = ExecutorPlacementRequirements.FromValues(
            acquisition.RequiredCredential,
            acquisition.RequiredNetworkProfile,
            acquisition.RequiredCapabilities);

        // Shape decider input through the provider-capability gate: a member
        // whose provider does not implement a required well-known operation
        // must not be selectable for it, even when the member config claims it.
        var candidates = new List<(SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement)>(members.Count);
        foreach (var member in members)
        {
            var provider = _registry.Resolve(member);
            candidates.Add((member, provider, SandboxProviderCapabilityGate.ApplyProviderCapabilities(member, provider)));
        }

        var loads = new Dictionary<string, int>(_inflightByMember, StringComparer.Ordinal);
        var decision = ExecutorPlacement.Decide(
            candidates.Select(static c => c.Placement).ToList(),
            requirements,
            loads,
            runtimeUnhealthy: null);

        var eligible = new List<(SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var outcome = decision.Candidates.FirstOrDefault(
                c => string.Equals(c.HostId, candidate.Member.MemberId, StringComparison.Ordinal));
            if (outcome is { Eligible: true })
                eligible.Add(candidate);
        }

        if (eligible.Count == 0)
            throw BuildRefusal(acquisition, decision, options);

        var winner = eligible
            .OrderByDescending(static c => c.Member.PreferenceScore)
            .ThenBy(c => LoadRatio(c.Placement, loads))
            .ThenBy(c => Load(c.Placement, loads))
            .ThenBy(static c => c.Member.MemberId, StringComparer.Ordinal)
            .First();

        _log.LogInformation(
            "Sandbox placement for work item {WorkItemId} phase {Phase}: {Decision}; preference-selected member {MemberId} (score {Score}) on provider {Provider}.",
            acquisition.WorkItemId,
            acquisition.Phase,
            decision.Describe(),
            winner.Member.MemberId,
            winner.Member.PreferenceScore,
            winner.Provider.Name);

        _inflightByMember.AddOrUpdate(winner.Member.MemberId, 1, static (_, count) => count + 1);
        try
        {
            return await winner.Provider.CreateAsync(acquisition.Spec, ct).ConfigureAwait(false);
        }
        finally
        {
            _inflightByMember.AddOrUpdate(
                winner.Member.MemberId,
                0,
                static (_, count) => Math.Max(0, count - 1));
        }
    }

    private Exception BuildRefusal(
        SandboxPlacementAcquisition acquisition,
        ExecutorPlacementDecision decision,
        ExecutorPhaseDispatchOptions options)
    {
        if (decision.UnmetCapability is not null)
        {
            _log.LogWarning(
                "Sandbox placement for work item {WorkItemId} phase {Phase} is unplaceable: {Decision}.",
                acquisition.WorkItemId,
                acquisition.Phase,
                decision.Describe());
            return new SandboxPlacementUnplaceableException(
                decision.UnmetCapability,
                $"work item '{acquisition.WorkItemId}' phase '{acquisition.Phase}' requires it; {decision.Describe()}",
                decision);
        }

        _log.LogWarning(
            "Sandbox placement for work item {WorkItemId} phase {Phase} deferred (no eligible member right now): {Decision}. Requeueing in {RecheckIn}.",
            acquisition.WorkItemId,
            acquisition.Phase,
            decision.Describe(),
            options.PlacementRecheckIn);
        return new SandboxProvisioningDeferredException(
            "sandbox-placement",
            "placement",
            "no-eligible-host",
            $"work item '{acquisition.WorkItemId}' phase '{acquisition.Phase}': {decision.Describe()}",
            options.PlacementRecheckIn);
    }

    private static IReadOnlyList<SandboxMember> FlattenMembers(IReadOnlyList<SandboxClass> catalog)
    {
        var flattened = new List<SandboxMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sandboxClass in catalog)
        {
            if (sandboxClass?.Members is null)
                continue;
            foreach (var member in sandboxClass.Members)
            {
                if (member is null)
                    throw new InvalidOperationException("Sandbox class catalog contains a null member; refusing to place.");
                if (string.IsNullOrWhiteSpace(member.MemberId))
                    throw new InvalidOperationException("Sandbox class catalog contains a member with a blank MemberId; refusing to place.");
                if (!seen.Add(member.MemberId))
                    continue;
                flattened.Add(member);
            }
        }
        return flattened;
    }

    private static int Load(SandboxPlacementMember placement, IReadOnlyDictionary<string, int> loads) =>
        loads.TryGetValue(placement.MemberId, out var used) ? Math.Max(0, used) : 0;

    private static double LoadRatio(SandboxPlacementMember placement, IReadOnlyDictionary<string, int> loads)
    {
        var capacity = placement.MaxConcurrentSandboxes ?? int.MaxValue;
        return capacity == int.MaxValue ? 0.0d : (double)Load(placement, loads) / capacity;
    }
}
