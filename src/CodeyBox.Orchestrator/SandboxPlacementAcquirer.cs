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
/// eligible members by <see cref="SandboxMember.PreferenceScore"/>, admits
/// the winner through its member gate, resolves the winner's provider from
/// the <see cref="ISandboxProviderRegistry"/>, and creates the sandbox on it.
/// </summary>
/// <remarks>
/// <para>Capacity accounting lives on the members. Each member owns a
/// <see cref="ResizableConcurrencyGate"/> sized from its
/// <see cref="SandboxMember.Capacity"/>, and the process-wide ceiling is
/// derived as the sum of member capacities (never an independently-imposed
/// scalar). Fine-grained admission happens at the member gate, after placement
/// has chosen the member: the fast path takes the first ranked member with
/// headroom atomically (bursts spill across members instead of piling onto
/// one), and a loser-of-a-race waits on the re-ranked winner through a true
/// async wait — never a poll loop. The member's registry provider still
/// carries its own admission wrapper, but that kind gate is derived from the
/// same catalog to always fit the member gates beneath it, so it never blocks
/// what a member gate admitted — it keeps owning lifecycle tracking, metrics,
/// and capability preservation. The permit is held for the sandbox's whole
/// lifetime and released exactly once on dispose; a create failure releases
/// it before rethrowing, so permits stay balanced on every path.</para>
/// <para>Refusals follow the decision's own classification. A permanent
/// refusal (<see cref="ExecutorPlacementDecision.IsUnplaceable"/> — a required
/// capability no available member declares) throws
/// <see cref="SandboxPlacementUnplaceableException"/> naming the unmet
/// capability so the item fails operator-visible instead of retry-looping. A
/// transient refusal (cordon, health, credential or profile mismatch on the
/// currently-available set) throws
/// <see cref="SandboxProvisioningDeferredException"/> carrying the existing
/// <c>PlacementRecheckIn</c> backoff so the item requeues under the standard
/// defer-and-requeue path. A capacity-only refusal waits for member headroom
/// instead of deferring — with a single member this is identical to the
/// former process-wide admission gate.</para>
/// <para>Loads passed to the decider are the live member-gate in-flight counts,
/// which cover held sandboxes for their whole lifetime (not just creations
/// in progress), so a member at its cap is excluded and demand spills by
/// least-load ranking. Every placement decision is logged
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
    private readonly object _gatesSync = new();
    private readonly Dictionary<string, ResizableConcurrencyGate> _gatesByMember = new(StringComparer.Ordinal);

    public SandboxPlacementAcquirer(
        SandboxClassesSnapshot classes,
        ISandboxProviderRegistry registry,
        ISandboxProvider? fallbackProvider = null,
        Func<ExecutorPhaseDispatchOptions>? optionsAccessor = null,
        ILogger<SandboxPlacementAcquirer>? log = null,
        int? maxConcurrentWorkers = null)
    {
        ArgumentNullException.ThrowIfNull(classes);
        ArgumentNullException.ThrowIfNull(registry);
        _classes = classes;
        _registry = registry;
        _fallback = fallbackProvider;
        _optionsAccessor = optionsAccessor ?? (() => new ExecutorPhaseDispatchOptions());
        _log = log ?? NullLogger<SandboxPlacementAcquirer>.Instance;
        SyncMemberCapacities(_classes.Current, maxConcurrentWorkers);
    }

    /// <summary>
    /// Derived process-wide sandbox ceiling: the sum of member capacities, as
    /// <see cref="MultiHostE2eExecutionPool.MaxConcurrent"/> derives its own
    /// ceiling from its host gates. Saturates at <see cref="int.MaxValue"/>
    /// when a member is unbounded. With an empty catalog (fallback path) this
    /// reports the fallback provider's admission ceiling when it exposes one.
    /// </summary>
    public int MaxConcurrent
    {
        get
        {
            lock (_gatesSync)
            {
                if (_gatesByMember.Count == 0)
                    return (_fallback as ISandboxAdmissionSnapshot)?.MaxConcurrentSandboxes ?? int.MaxValue;
                long total = 0;
                foreach (var gate in _gatesByMember.Values)
                {
                    total += gate.CurrentTarget;
                    if (total >= int.MaxValue)
                        return int.MaxValue;
                }
                return (int)total;
            }
        }
    }

    /// <summary>Live member-gate permits held across all members.</summary>
    public int InFlight
    {
        get
        {
            lock (_gatesSync)
            {
                long total = 0;
                foreach (var gate in _gatesByMember.Values)
                {
                    total += gate.CurrentInFlight;
                    if (total >= int.MaxValue)
                        return int.MaxValue;
                }
                if (_gatesByMember.Count == 0 && _fallback is ISandboxAdmissionSnapshot admission)
                    total += admission.CurrentAdmittedSandboxes;
                return (int)total;
            }
        }
    }

    /// <summary>
    /// Reconciles the per-member admission gates with <paramref name="nextCatalog"/>
    /// and re-checks the per-member deadlock invariant against
    /// <paramref name="maxConcurrentWorkers"/>: a worker holds its work sandbox
    /// while acquiring a second sandbox for audit, so a member below twice the
    /// worker count can deadlock the pipeline with every permit held by a worker
    /// waiting for a second permit that never frees — and no sandbox deletion
    /// releases the permit, only a restart does.
    /// <para>
    /// Every member is validated before any gate is touched, so a refused reload
    /// leaves every previous value in force. A member dropped below the minimum
    /// is refused with an error naming the member and the minimum valid capacity.
    /// Growing a gate admits queued waiters immediately, per the
    /// <see cref="ResizableConcurrencyGate.Resize"/> contract; shrinking never
    /// aborts holders. Every resize is logged with old target, new target, and
    /// in-flight count. A reload with unchanged capacities is a no-op.
    /// </para>
    /// <para>Pass <see langword="null"/> for <paramref name="maxConcurrentWorkers"/>
    /// to skip the deadlock check (startup already validated the catalog through
    /// the class builder); every reload passes the live worker count.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a member capacity is below 1 or — with a worker count — below
    /// the deadlock-safe minimum, naming the member and the minimum. No gate is
    /// mutated when validation fails.
    /// </exception>
    public void SyncMemberCapacities(IReadOnlyList<SandboxClass> nextCatalog, int? maxConcurrentWorkers)
    {
        ArgumentNullException.ThrowIfNull(nextCatalog);
        if (maxConcurrentWorkers is { } workers && workers < 1)
            throw new InvalidOperationException(
                $"Sandbox member gate sync requires MaxConcurrentWorkers >= 1 but observed {workers}. " +
                "Fix CodeyBox:WorkerPool:MaxConcurrentWorkers (or the legacy CodeyBox:Concurrency fallback).");

        var members = FlattenMembers(nextCatalog);
        foreach (var member in members)
        {
            if (member.Capacity < 1)
                throw new InvalidOperationException(
                    $"Sandbox member '{member.MemberId}' has Capacity={member.Capacity} which must be >= 1. " +
                    $"Give the member a positive Capacity.");
            if (maxConcurrentWorkers is { } w)
            {
                var minimumCapacity = checked(2 * w);
                if (member.Capacity < minimumCapacity)
                    throw new InvalidOperationException(
                        $"Sandbox member '{member.MemberId}' has Capacity={member.Capacity} " +
                        $"which is below the deadlock-safe minimum {minimumCapacity} for " +
                        $"{w} worker(s): a worker holds its work sandbox while acquiring " +
                        "a second sandbox for audit, so each member needs Capacity >= 2x the worker count. " +
                        $"Raise Capacity to at least {minimumCapacity} or lower MaxConcurrentWorkers.");
            }
        }

        lock (_gatesSync)
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                live.Add(member.MemberId);
                if (_gatesByMember.TryGetValue(member.MemberId, out var existing))
                {
                    if (existing.CurrentTarget != member.Capacity)
                    {
                        var result = existing.Resize(member.Capacity);
                        _log.LogInformation(
                            "Hot-reloaded SandboxClasses member '{MemberId}' capacity: {OldValue} → {NewValue} (in-flight={InFlight})",
                            member.MemberId,
                            result.OldTarget,
                            result.NewTarget,
                            result.InFlight);
                    }
                }
                else
                {
                    _gatesByMember[member.MemberId] = new ResizableConcurrencyGate(member.Capacity);
                }
            }

            // Forget gates for removed members without disposing them: queued
            // waiters drain as live holders release, and holders release
            // normally through the wrapper — disposing here would fault
            // waiters that chose the member before its removal.
            foreach (var stale in _gatesByMember.Keys.ToArray())
            {
                if (!live.Contains(stale))
                    _gatesByMember.Remove(stale);
            }
        }
    }

    /// <summary>
    /// Places the acquisition and creates its sandbox on the winning member's
    /// provider. Throws <see cref="SandboxPlacementUnplaceableException"/> for
    /// permanent refusals and <see cref="SandboxProvisioningDeferredException"/>
    /// (with the configured recheck backoff) for transient ones.
    /// Acquisitions naming a network profile additionally require enforced
    /// egress (see <see cref="SandboxEgressPolicy"/>): members on
    /// <see cref="EgressEnforcementLocation.NotEnforced"/> providers are
    /// excluded before the decider runs, and the acquisition is unplaceable —
    /// naming the refused kinds — when none remain.
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

        // Enforced-egress gate: a member whose provider kind is classified
        // NotEnforced (every plugin-contributed kind, plus bubblewrap/process)
        // must not serve work requiring a named network profile, whose
        // allowlist only exists as host-side nftables rules the provider never
        // attaches to. Refusal is explicit and names the kinds; enforced
        // members keep placing for the same profile.
        var selectable = ApplyEnforcedEgressRequirement(acquisition, candidates);

        var loads = SnapshotLoads();
        var decision = ExecutorPlacement.Decide(
            selectable.Select(static c => c.Placement).ToList(),
            requirements,
            loads,
            runtimeUnhealthy: null);

        var ranked = RankEligible(EligibleCandidates(selectable, decision), loads);
        if (ranked.Count != 0)
        {
            // Fast path: atomically take the first ranked member with headroom.
            // Bursts spill across members through the atomic take instead of
            // piling onto one member behind a shared stale load snapshot.
            foreach (var candidate in ranked)
            {
                var gate = GateFor(candidate.Member.MemberId, acquisition, options);
                if (gate.TryEnter())
                {
                    LogPlacement(acquisition, decision, candidate);
                    return await CreateOnMemberAsync(candidate.Provider, gate, acquisition.Spec, ct).ConfigureAwait(false);
                }
            }

            // Every eligible take lost a creation race. Re-decide on fresh
            // loads: a still-eligible winner is waited on through a true async
            // wait (never a poll loop); a now-empty set falls through to the
            // refusal analysis below.
            loads = SnapshotLoads();
            decision = ExecutorPlacement.Decide(
                selectable.Select(static c => c.Placement).ToList(),
                requirements,
                loads,
                runtimeUnhealthy: null);
            ranked = RankEligible(EligibleCandidates(selectable, decision), loads);
            if (ranked.Count != 0)
            {
                var winner = ranked[0];
                var gate = GateFor(winner.Member.MemberId, acquisition, options);
                LogPlacement(acquisition, decision, winner);
                await gate.WaitAsync(ct).ConfigureAwait(false);
                return await CreateOnMemberAsync(winner.Provider, gate, acquisition.Spec, ct).ConfigureAwait(false);
            }
        }

        // No eligible member right now. A permanent refusal fails
        // operator-visible; a capacity-only refusal waits for member headroom
        // (with one member this is the former process-wide gate); anything
        // else defers with the placement backoff for the requeue path.
        if (decision.UnmetCapability is not null)
            throw BuildRefusal(acquisition, decision, options);

        var blocked = CapacityBlockedCandidates(selectable, requirements, decision);
        if (blocked.Count != 0)
        {
            var waitLoads = SnapshotLoads();
            var target = RankEligible(blocked, waitLoads)[0];
            var gate = GateFor(target.Member.MemberId, acquisition, options);
            LogPlacement(acquisition, decision, target);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            return await CreateOnMemberAsync(target.Provider, gate, acquisition.Spec, ct).ConfigureAwait(false);
        }

        throw BuildRefusal(acquisition, decision, options);
    }

    private void LogPlacement(
        SandboxPlacementAcquisition acquisition,
        ExecutorPlacementDecision decision,
        (SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement) selected)
    {
        _log.LogInformation(
            "Sandbox placement for work item {WorkItemId} phase {Phase}: {Decision}; preference-selected member {MemberId} (score {Score}) on provider {Provider}.",
            acquisition.WorkItemId,
            acquisition.Phase,
            decision.Describe(),
            selected.Member.MemberId,
            selected.Member.PreferenceScore,
            selected.Provider.Name);
    }

    /// <summary>
    /// Applies the enforced-egress requirement for one acquisition: when the
    /// acquisition names a network profile, only members whose provider kind the
    /// host classifies as enforced may serve it. A <c>NotEnforced</c> member is
    /// dropped before the decider runs (never quietly used where enforcement was
    /// required); when no member can serve the profile the acquisition fails
    /// operator-visible with a reason naming every refused kind. Acquisitions with
    /// no profile pass through untouched.
    /// </summary>
    private List<(SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement)> ApplyEnforcedEgressRequirement(
        SandboxPlacementAcquisition acquisition,
        List<(SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement)> candidates)
    {
        if (!SandboxEgressPolicy.RequiresEnforcedEgress(acquisition.RequiredNetworkProfile))
            return candidates;
        var profile = acquisition.RequiredNetworkProfile!.Trim();
        var selectable = new List<(SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement)>(candidates.Count);
        var refusedKinds = new List<string>();
        var refusedMembers = new List<string>();
        foreach (var candidate in candidates)
        {
            var kind = candidate.Member.ProviderKind.Trim().ToLowerInvariant();
            if (SandboxEgressPolicy.IsEnforced(candidate.Member.ProviderKind))
            {
                selectable.Add(candidate);
                continue;
            }
            if (!refusedKinds.Contains(kind, StringComparer.Ordinal))
                refusedKinds.Add(kind);
            refusedMembers.Add($"'{candidate.Member.MemberId}' (kind '{kind}')");
        }
        if (refusedMembers.Count > 0)
        {
            _log.LogInformation(
                "Sandbox placement for work item {WorkItemId} phase {Phase}: network profile '{Profile}' requires enforced egress; " +
                "excluding {Count} member(s) on NotEnforced providers: {Members}.",
                acquisition.WorkItemId,
                acquisition.Phase,
                profile,
                refusedMembers.Count,
                string.Join(", ", refusedMembers));
        }
        if (selectable.Count == 0)
        {
            throw new SandboxPlacementUnplaceableException(
                "enforced-egress",
                $"work item '{acquisition.WorkItemId}' phase '{acquisition.Phase}' requires network profile '{profile}' " +
                $"which needs enforced egress, but every available member is backed by a NotEnforced provider " +
                $"({string.Join(", ", refusedMembers)}). A NotEnforced provider (every plugin-contributed kind, " +
                $"plus bubblewrap/process) may only serve sandboxes with no named network profile. " +
                $"Add a member on an enforced provider (incus, multipass, multipass-remote, sprites) or drop the profile requirement.");
        }
        return selectable;
    }

    private static List<(SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement)> EligibleCandidates(        List<(SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement)> candidates,
        ExecutorPlacementDecision decision)
    {
        var eligible = new List<(SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var outcome = decision.Candidates.FirstOrDefault(
                c => string.Equals(c.HostId, candidate.Member.MemberId, StringComparison.Ordinal));
            if (outcome is { Eligible: true })
                eligible.Add(candidate);
        }
        return eligible;
    }

    private static List<(SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement)> RankEligible(
        List<(SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement)> eligible,
        IReadOnlyDictionary<string, int> loads) =>
        eligible
            .OrderByDescending(static c => c.Member.PreferenceScore)
            .ThenBy(c => LoadRatio(c.Placement, loads))
            .ThenBy(c => Load(c.Placement, loads))
            .ThenBy(static c => c.Member.MemberId, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Members the decider excludes only because of load: eligible under zero
    /// load but not under the live loads. Load is the decider's sole
    /// load-dependent input (it only feeds the capacity check), so this
    /// difference is exactly the capacity-blocked set — no reason-string
    /// parsing required.
    /// </summary>
    private static List<(SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement)> CapacityBlockedCandidates(
        List<(SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement)> candidates,
        ExecutorPlacementRequirements requirements,
        ExecutorPlacementDecision liveDecision)
    {
        var liveEligible = new HashSet<string>(
            liveDecision.Candidates.Where(static c => c.Eligible).Select(static c => c.HostId),
            StringComparer.Ordinal);
        var zeroDecision = ExecutorPlacement.Decide(
            candidates.Select(static c => c.Placement).ToList(),
            requirements,
            loads: null,
            runtimeUnhealthy: null);
        var blocked = new List<(SandboxMember Member, ISandboxProvider Provider, SandboxPlacementMember Placement)>();
        foreach (var candidate in candidates)
        {
            if (liveEligible.Contains(candidate.Member.MemberId))
                continue;
            var outcome = zeroDecision.Candidates.FirstOrDefault(
                c => string.Equals(c.HostId, candidate.Member.MemberId, StringComparison.Ordinal));
            if (outcome is { Eligible: true })
                blocked.Add(candidate);
        }
        return blocked;
    }

    /// <summary>
    /// Creates the sandbox on the member's provider while holding exactly one
    /// permit on the member's gate. The permit moves into the returned wrapper
    /// for the sandbox's lifetime; a create failure (or cancellation) releases
    /// it before rethrowing, mirroring the
    /// <see cref="MultiHostE2eExecutionPool"/> lease discipline.
    /// </summary>
    private static async Task<ISandbox> CreateOnMemberAsync(
        ISandboxProvider provider,
        ResizableConcurrencyGate gate,
        SandboxSpec spec,
        CancellationToken ct)
    {
        try
        {
            var sandbox = await provider.CreateAsync(spec, ct).ConfigureAwait(false);
            return new MemberGateSandbox(sandbox, gate);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private Dictionary<string, int> SnapshotLoads()
    {
        lock (_gatesSync)
        {
            var loads = new Dictionary<string, int>(_gatesByMember.Count, StringComparer.Ordinal);
            foreach (var (memberId, gate) in _gatesByMember)
                loads[memberId] = gate.CurrentInFlight;
            return loads;
        }
    }

    private ResizableConcurrencyGate GateFor(
        string memberId,
        SandboxPlacementAcquisition acquisition,
        ExecutorPhaseDispatchOptions options)
    {
        lock (_gatesSync)
        {
            if (_gatesByMember.TryGetValue(memberId, out var gate))
                return gate;
        }

        // The member vanished from the catalog between placement and admission
        // (a reload dropped it). Defer with the placement backoff so the item
        // requeues and places against the new catalog instead of failing.
        _log.LogWarning(
            "Sandbox placement for work item {WorkItemId} phase {Phase}: member {MemberId} was removed before admission; deferring placement.",
            acquisition.WorkItemId,
            acquisition.Phase,
            memberId);
        throw new SandboxProvisioningDeferredException(
            "sandbox-placement",
            "placement",
            "member-removed",
            $"work item '{acquisition.WorkItemId}' phase '{acquisition.Phase}': member '{memberId}' was removed before admission",
            options.PlacementRecheckIn);
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

    /// <summary>
    /// <see cref="ISandboxDecorator"/> holding one member-gate permit for the
    /// sandbox's whole lifetime. Capabilities resolve through
    /// <see cref="SandboxCapability.Find{T}"/> across <see cref="InnerSandbox"/>,
    /// so — like the admission-control family — this wrapper does not
    /// re-implement the optional capability interfaces it merely forwards
    /// through. The permit releases exactly once when the sandbox disposes,
    /// even when inner disposal throws.
    /// </summary>
    private sealed class MemberGateSandbox : ISandboxDecorator
    {
        private readonly ISandbox _inner;
        private readonly ResizableConcurrencyGate _gate;
        private int _disposed;

        public MemberGateSandbox(ISandbox inner, ResizableConcurrencyGate gate)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(gate);
            _inner = inner;
            _gate = gate;
        }

        public ISandbox InnerSandbox => _inner;

        public string Id => _inner.Id;

        public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;

        public SandboxBatchLaunchMode BatchLaunchMode => _inner.BatchLaunchMode;

        public SandboxResourceMetrics? ResourceMetrics => _inner.ResourceMetrics;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            _inner.ExecAsync(exec, ct);

        public Task SyncStateToHostAsync(CancellationToken ct = default) =>
            _inner.SyncStateToHostAsync(ct);

        public Task KillActiveExecsAsync(CancellationToken ct = default) =>
            _inner.KillActiveExecsAsync(ct);

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default) =>
            _inner.GetScreenshotAsync(ct);

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default) =>
            _inner.SynthesizeInputAsync(events, ct);

        public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default) =>
            _inner.GetAccessibilityAtPointAsync(x, y, ct);

        public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default) =>
            _inner.GetAccessibilityTreeJsonAsync(ct);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
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
