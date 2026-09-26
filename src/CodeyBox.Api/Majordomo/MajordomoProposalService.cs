using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Majordomo;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Outcome of approving a proposal: either the committed change set (fresh
/// approval, or the ids a repeated approval replays without mutating) or a
/// refusal with a reason the operator can act on.
/// </summary>
internal sealed record MajordomoProposalApprovalOutcome(
    MajordomoChangeSet? ChangeSet,
    IReadOnlyList<WorkItemId>? AlreadyApprovedIds,
    MajordomoRefusal? Refusal)
{
    public static MajordomoProposalApprovalOutcome Approved(MajordomoChangeSet changeSet) =>
        new(changeSet, null, null);

    public static MajordomoProposalApprovalOutcome AlreadyApproved(IReadOnlyList<WorkItemId> ids) =>
        new(null, ids, null);

    public static MajordomoProposalApprovalOutcome Refused(MajordomoRefusal refusal) =>
        new(null, null, refusal);
}

/// <summary>
/// Outcome of rejecting or superseding a proposal: either the updated record
/// or a refusal naming the actual state.
/// </summary>
internal sealed record MajordomoProposalDecisionOutcome(
    MajordomoProposalRecord? Record,
    MajordomoRefusal? Refusal)
{
    public static MajordomoProposalDecisionOutcome Decided(MajordomoProposalRecord record) =>
        new(record, null);

    public static MajordomoProposalDecisionOutcome Refused(MajordomoRefusal refusal) =>
        new(null, refusal);
}

/// <summary>
/// The operator side of the majordomo proposal queue: persists proposals,
/// and applies approvals through <see cref="MajordomoMutateBackend.MutateAsync"/>
/// — the same dispatch an Autonomous-mode call runs, so an approved proposal
/// and an autonomous call cannot diverge.
/// </summary>
/// <remarks>
/// Approval revalidates against live queue state at apply time instead of
/// replaying the proposal-time change set: the backend re-plans every target
/// (existence, terminal-state dependencies, cancel/retry plan guards) before
/// any write, so a proposal whose precondition moved — a cancel whose target
/// has since completed, a chain depending on a since-deleted item — is
/// refused with a reason and the proposal stays pending rather than applied
/// blindly. The re-plan is also compared against the change set the operator
/// reviewed: a cancel whose cascade grew, for example, is refused as drifted
/// instead of silently committing a larger blast radius than was approved.
/// <para>
/// A decision claims the proposal before committing: the store's
/// compare-and-set moves it Pending→Applying, so whichever approval takes
/// that transition owns the commit — concurrent or cross-process approvals
/// cannot double-apply, and a crash mid-commit leaves the row in Applying
/// (re-approval refused) rather than in Pending where a retry would
/// duplicate the mutation. The operator closes an interrupted commit with
/// reject/supersede, which are allowed to settle Applying rows for exactly
/// that reason.
/// </para>
/// <para>
/// In-process, every decision on one proposal id — approval, rejection,
/// supersession — serializes through a fixed stripe of the
/// <see cref="DecisionGates"/> pool, so a rejection can never interpose
/// between an approval's commit and its recorded transition. The stripe
/// table is bounded and never evicts: keys hash a route-supplied id, and a
/// keyed dictionary that evicted while a caller still held the semaphore
/// would let two approvals of one proposal run concurrently.
/// </para>
/// <para>
/// Chain atomicity is inherited from the backend: multi-create chains commit
/// through a single atomic batch, so a chain that fails partway files
/// nothing. Approval itself is idempotent: a repeated approval of an
/// approved proposal returns the recorded result ids without touching the
/// queue again.
/// </para>
/// <para>
/// Approvals deliberately bypass the per-turn mutation ledger: the ledger
/// throttles the model's autonomous bursts, while an approval is an explicit
/// operator authorization of the reviewed change set.
/// </para>
/// </remarks>
internal sealed class MajordomoProposalService
{
    /// <summary>Longest accepted operator decision reason.</summary>
    public const int MaxDecisionReasonLength = 2000;

    /// <summary>Bound on read/claim attempts when a decision keeps racing a transition.</summary>
    private const int MaxDecisionAttempts = 3;

    /// <summary>Marker persisted on a claimed proposal while its commit is in flight.</summary>
    internal const string CommitInFlightReason = "commit claimed; applying the mutation";

    /// <summary>Appended to the decision reason when an operator closes a claimed commit.</summary>
    internal const string InterruptedCommitNote =
        " (closed a claimed commit — its mutation may have already applied)";

    /// <summary>
    /// Striped serialization for decisions on one proposal. A fixed pool
    /// keyed by the id's hash: bounded (caller-supplied route ids cannot grow
    /// it), never evicted (an evict-while-in-use race would hand two callers
    /// different semaphores for the same proposal). Unrelated ids may share
    /// a stripe — that only serializes rare operator decisions, never
    /// correctness.
    /// </summary>
    private static readonly SemaphoreSlim[] DecisionGates =
        Enumerable.Range(0, 64).Select(static _ => new SemaphoreSlim(1, 1)).ToArray();

    private readonly IMajordomoProposalStore _store;
    private readonly MajordomoMutateBackend _mutates;
    private readonly IOptionsMonitor<MajordomoServerOptions> _options;
    private readonly TimeProvider _time;

    public MajordomoProposalService(
        IMajordomoProposalStore store,
        MajordomoMutateBackend mutates,
        IOptionsMonitor<MajordomoServerOptions> options,
        TimeProvider? time = null)
    {
        _store = store;
        _mutates = mutates;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Persists a planned mutation as a pending proposal, carrying the dry-run
    /// change set the operator reviews — approval re-plans against live state
    /// and refuses when the live plan no longer matches what was shown.
    /// </summary>
    public async Task<MajordomoProposalRecord> ProposeAsync(
        MajordomoTool tool,
        MajordomoMutateArgs args,
        MajordomoChangeSet reviewedChangeSet,
        string proposedBy,
        CancellationToken ct = default)
    {
        var record = MajordomoProposalRecord.Create(
            tool.Name, args, proposedBy, _time.GetUtcNow(), args.Reasoning, reviewedChangeSet);
        await _store.EnqueueAsync(record, ct).ConfigureAwait(false);
        AuditLog.MajordomoToolCall(
            record.Id,
            Validation.DescribeUntrustedValue(proposedBy),
            Validation.DescribeUntrustedValue(tool.Name),
            MajordomoDecisions.Propose,
            $"proposal {record.Id}");
        return record;
    }

    /// <summary>
    /// Approves a pending proposal by committing it through the mutate
    /// backend — the same path an Autonomous-mode call takes.
    /// </summary>
    public async Task<MajordomoProposalApprovalOutcome> ApproveAsync(
        string id,
        string decidedBy,
        WorkInitiator initiator,
        CancellationToken ct = default)
    {
        var safeId = Validation.DescribeUntrustedValue(id);
        var gate = GateFor(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ttl = _options.CurrentValue.ToPolicy().ProposalTimeToLive;
            for (var attempt = 0; attempt < MaxDecisionAttempts; attempt++)
            {
                var (record, readRefusal) = await TryGetAsync(id, safeId, ct).ConfigureAwait(false);
                if (readRefusal is not null)
                    return MajordomoProposalApprovalOutcome.Refused(readRefusal);

                var now = _time.GetUtcNow();
                if (record!.State == MajordomoProposalState.Pending && record.IsExpiredAt(now, ttl))
                {
                    var expired = record with
                    {
                        State = MajordomoProposalState.Expired,
                        DecidedAt = now,
                        DecidedBy = decidedBy,
                        DecisionReason = $"proposal expired after {ttl} without approval",
                    };
                    // A lost race means another decision landed first — the
                    // loop re-reads and reports the state that actually won.
                    if (await _store.TryTransitionAsync(
                            id, MajordomoProposalState.Pending, expired, ct).ConfigureAwait(false))
                    {
                        AuditOutcome(id, decidedBy, record.ToolName,
                            MajordomoOutcomes.Refused, "proposal expired");
                        return Refused(new MajordomoRefusal(
                            MajordomoRefusalReasons.ProposalExpired,
                            $"proposal '{safeId}' expired after {ttl} without approval"));
                    }
                    continue;
                }

                if (record.State == MajordomoProposalState.Applying)
                    return Refused(new MajordomoRefusal(
                        MajordomoRefusalReasons.ProposalCommitIncomplete,
                        $"proposal '{safeId}' has a commit in flight or was interrupted before its " +
                        "outcome was recorded — inspect the work queue, then reject or supersede " +
                        "the proposal to close it out"));
                if (record.State == MajordomoProposalState.Approved)
                    // Idempotent replay: hand back the recorded result
                    // without touching the queue again.
                    return MajordomoProposalApprovalOutcome.AlreadyApproved(
                        record.ResultAffectedItems ?? []);
                if (record.IsDecided)
                    return Refused(NotPending(safeId, record.State));

                var (tool, commitRefusal) = ResolveCommitTool(record);
                if (commitRefusal is not null)
                    return Refused(commitRefusal);

                // Revalidation and review fidelity run before the claim: the
                // backend re-plans every target against live state (the
                // refusal leaves the proposal pending so the operator can
                // inspect, reject, or retry), and the re-planned change set
                // must still match the one the operator reviewed.
                var plan = await _mutates.MutateAsync(
                        tool!, record.Arguments, initiator,
                        cancelCascadeTargets: null, commit: false, ct)
                    .ConfigureAwait(false);
                if (plan.Refusal is not null)
                {
                    AuditOutcome(id, decidedBy, record.ToolName,
                        MajordomoOutcomes.Refused, plan.Refusal.Detail);
                    return Refused(plan.Refusal);
                }

                if (record.ReviewedChangeSet is { } reviewed && !SamePlan(reviewed, plan.ChangeSet!))
                {
                    var drifted = new MajordomoRefusal(
                        MajordomoRefusalReasons.ProposalDrifted,
                        $"proposal '{safeId}' no longer plans the change set that was reviewed — " +
                        "the queue moved since it was filed; inspect the live plan and let the " +
                        "majordomo re-propose");
                    AuditOutcome(id, decidedBy, record.ToolName,
                        MajordomoOutcomes.Refused, drifted.Detail);
                    return Refused(drifted);
                }

                // The claim CAS is the atomic guard across processes too:
                // whoever moves Pending→Applying owns the commit.
                var claim = record with
                {
                    State = MajordomoProposalState.Applying,
                    DecidedAt = now,
                    DecidedBy = decidedBy,
                    DecisionReason = CommitInFlightReason,
                };
                if (await _store.TryTransitionAsync(
                        id, MajordomoProposalState.Pending, claim, ct).ConfigureAwait(false))
                {
                    return await CommitClaimedAsync(
                        record, id, safeId, tool!, decidedBy, initiator, now, ct).ConfigureAwait(false);
                }
                // Lost the claim — loop re-reads and reports the winning state.
            }

            return Refused(new MajordomoRefusal(
                MajordomoRefusalReasons.ProposalNotPending,
                $"proposal '{safeId}' kept changing state while being decided — retry"));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Commits a claimed proposal: runs the backend with <c>commit: true</c>
    /// (the same dispatch an Autonomous call takes), then records the
    /// outcome. A refusal that wrote nothing releases the claim back to
    /// pending; a refusal after partial writes leaves the row in
    /// <see cref="MajordomoProposalState.Applying"/> with the reason on the
    /// record, because returning it to pending would invite a blind retry of
    /// a partially applied mutation.
    /// </summary>
    private async Task<MajordomoProposalApprovalOutcome> CommitClaimedAsync(
        MajordomoProposalRecord record,
        string id,
        string safeId,
        MajordomoTool tool,
        string decidedBy,
        WorkInitiator initiator,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var mutation = await _mutates.MutateAsync(
                tool, record.Arguments, initiator,
                cancelCascadeTargets: null, commit: true, ct)
            .ConfigureAwait(false);

        if (mutation.Refusal is { } refusal)
        {
            if (mutation.WritesApplied)
            {
                var stuck = record with
                {
                    State = MajordomoProposalState.Applying,
                    DecidedAt = now,
                    DecidedBy = decidedBy,
                    DecisionReason = $"commit partially applied then refused: {refusal.Detail}",
                };
                await _store.TryTransitionAsync(
                    id, MajordomoProposalState.Applying, stuck, ct).ConfigureAwait(false);
                refusal = refusal with
                {
                    Detail = refusal.Detail + " — part of the mutation was applied before this refusal; " +
                    "the proposal stays in 'applying' until an operator inspects the queue and closes it",
                };
            }
            else
            {
                // The commit planned everything before writing, so a refusal
                // with no writes frees the proposal back to pending.
                await _store.TryTransitionAsync(
                    id, MajordomoProposalState.Applying, record, ct).ConfigureAwait(false);
            }

            AuditOutcome(id, decidedBy, record.ToolName, MajordomoOutcomes.Refused, refusal.Detail);
            return Refused(refusal);
        }

        var approved = record with
        {
            State = MajordomoProposalState.Approved,
            DecidedAt = now,
            DecidedBy = decidedBy,
            DecisionReason = "approved by operator",
            ResultAffectedItems = mutation.ChangeSet!.AffectedItems,
        };
        if (!await _store.TryTransitionAsync(
                id, MajordomoProposalState.Applying, approved, ct).ConfigureAwait(false))
        {
            // The claim was ours, so a lost transition means a decider in
            // another process raced the commit — fail loudly rather than
            // report success for a state we did not record. The queue
            // mutation already landed; the audit record carries that fact.
            AuditOutcome(id, decidedBy, record.ToolName, MajordomoOutcomes.Error,
                "approval committed but the proposal transition raced");
            throw new InvalidOperationException(
                $"proposal '{safeId}' left applying after its approval committed");
        }

        AuditOutcome(id, decidedBy, record.ToolName, MajordomoOutcomes.Executed,
            $"proposal {safeId} approved");
        return MajordomoProposalApprovalOutcome.Approved(mutation.ChangeSet);
    }

    /// <summary>Rejects a pending (or interrupted-commit) proposal; nothing is mutated.</summary>
    public async Task<MajordomoProposalDecisionOutcome> RejectAsync(
        string id,
        string decidedBy,
        string reason,
        CancellationToken ct = default) =>
        await DecideAsync(id, decidedBy, reason, MajordomoProposalState.Rejected, ct).ConfigureAwait(false);

    /// <summary>Marks a pending (or interrupted-commit) proposal superseded; nothing is mutated.</summary>
    public async Task<MajordomoProposalDecisionOutcome> SupersedeAsync(
        string id,
        string decidedBy,
        string reason,
        CancellationToken ct = default) =>
        await DecideAsync(id, decidedBy, reason, MajordomoProposalState.Superseded, ct).ConfigureAwait(false);

    /// <summary>
    /// Shared reject/supersede path. Runs under the same per-id stripe as
    /// approval so a decision cannot interpose between an approval's commit
    /// and its recorded transition. A proposal found in
    /// <see cref="MajordomoProposalState.Applying"/> — a claimed commit that
    /// never recorded an outcome — is closable: the record's reason is
    /// annotated because its mutation may already have applied.
    /// </summary>
    private async Task<MajordomoProposalDecisionOutcome> DecideAsync(
        string id,
        string decidedBy,
        string reason,
        MajordomoProposalState terminal,
        CancellationToken ct)
    {
        string checkedReason;
        try
        {
            checkedReason = NormalizeDecisionReason(reason);
        }
        catch (ArgumentException ex)
        {
            return MajordomoProposalDecisionOutcome.Refused(new MajordomoRefusal(
                MajordomoRefusalReasons.InvalidArguments, ex.Message, Field: "reason"));
        }

        var safeId = Validation.DescribeUntrustedValue(id);
        var gate = GateFor(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (record, readRefusal) = await TryGetAsync(id, safeId, ct).ConfigureAwait(false);
            if (readRefusal is not null)
                return MajordomoProposalDecisionOutcome.Refused(readRefusal);

            var wasApplying = record!.State == MajordomoProposalState.Applying;
            if (record.IsDecided)
                return MajordomoProposalDecisionOutcome.Refused(NotPending(safeId, record.State));

            var decided = record with
            {
                State = terminal,
                DecidedAt = _time.GetUtcNow(),
                DecidedBy = decidedBy,
                DecisionReason = wasApplying ? checkedReason + InterruptedCommitNote : checkedReason,
            };
            if (!await _store.TryTransitionAsync(id, record.State, decided, ct).ConfigureAwait(false))
            {
                var current = await _store.GetAsync(id, ct).ConfigureAwait(false);
                var actual = current?.State.ToString().ToLowerInvariant() ?? "missing";
                return MajordomoProposalDecisionOutcome.Refused(NotPending(safeId, current?.State));
            }

            var detail = wasApplying
                ? $"proposal {safeId} {terminal.ToString().ToLowerInvariant()} to close a claimed commit: {checkedReason}"
                : $"proposal {safeId} {terminal.ToString().ToLowerInvariant()}: {checkedReason}";
            AuditOutcome(id, decidedBy, record.ToolName, MajordomoOutcomes.Decided, detail);
            return MajordomoProposalDecisionOutcome.Decided(decided);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Checks that a stored proposal still maps to a committable MUTATE tool.
    /// A tool that left the vocabulary is a retirement (a terminal refusal
    /// the operator resolves by rejecting); an argument payload that no
    /// longer matches its contract, a dry-run flag, or a vocabulary member
    /// with no backend arm is record corruption.
    /// </summary>
    private static (MajordomoTool? Tool, MajordomoRefusal? Refusal) ResolveCommitTool(
        MajordomoProposalRecord record)
    {
        if (!MajordomoTools.TryGet(record.ToolName, out var tool) || tool.Class != MajordomoToolClass.Mutate)
            return (null, new MajordomoRefusal(
                MajordomoRefusalReasons.ProposalToolRetired,
                $"tool '{Validation.DescribeUntrustedValue(record.ToolName)}' is no longer a known MUTATE tool — " +
                "reject or supersede the proposal to close it out"));

        if (record.Arguments.GetType() != tool.ArgumentsType || record.Arguments.DryRun)
            return (null, new MajordomoRefusal(
                MajordomoRefusalReasons.ProposalCorrupt,
                $"stored arguments for '{tool.Name}' do not match {tool.ArgumentsType.Name} or carry a dry-run flag"));

        // VerifyVocabularyWiring makes this unreachable for the real
        // vocabulary; a hand-seeded record could still reach it.
        if (!MajordomoMutateBackend.CanMutate(tool))
            return (null, new MajordomoRefusal(
                MajordomoRefusalReasons.ProposalCorrupt,
                $"tool '{tool.Name}' has no commit path"));

        return (tool, null);
    }

    /// <summary>
    /// Whether a live re-plan still matches the change set the operator
    /// reviewed. Compared through the wire serializer — the same contract
    /// the review rendered — so ordering or formatting differences cannot
    /// produce false drift.
    /// </summary>
    private static bool SamePlan(MajordomoChangeSet reviewed, MajordomoChangeSet replanned) =>
        string.Equals(
            JsonSerializer.Serialize(reviewed.Changes, MajordomoJson.Options),
            JsonSerializer.Serialize(replanned.Changes, MajordomoJson.Options),
            StringComparison.Ordinal);

    /// <summary>Loads a proposal, translating store failures into refusals.</summary>
    private async Task<(MajordomoProposalRecord? Record, MajordomoRefusal? Refusal)> TryGetAsync(
        string id, string safeId, CancellationToken ct)
    {
        MajordomoProposalRecord? record;
        try
        {
            record = await _store.GetAsync(id, ct).ConfigureAwait(false);
        }
        catch (MajordomoProposalCorruptException ex)
        {
            return (null, new MajordomoRefusal(MajordomoRefusalReasons.ProposalCorrupt, ex.Message));
        }

        return record is null ? (null, NotFound(safeId)) : (record, null);
    }

    private static MajordomoProposalApprovalOutcome Refused(MajordomoRefusal refusal) =>
        MajordomoProposalApprovalOutcome.Refused(refusal);

    private static MajordomoRefusal NotFound(string safeId) => new(
        MajordomoRefusalReasons.ProposalNotFound,
        $"proposal '{safeId}' does not exist");

    private static MajordomoRefusal NotPending(string safeId, MajordomoProposalState? state) => new(
        MajordomoRefusalReasons.ProposalNotPending,
        $"proposal '{safeId}' is already {(state?.ToString().ToLowerInvariant() ?? "missing")}");

    /// <summary>
    /// Emits the operator-decision audit record. <paramref name="callId"/>
    /// here is the proposal id — a route value, so it is echoed through the
    /// untrusted-value guard like the identity and tool name already are.
    /// </summary>
    private static void AuditOutcome(
        string callId, string decidedBy, string toolName, string outcome, string? detail) =>
        AuditLog.MajordomoToolOutcome(
            Validation.DescribeUntrustedValue(callId),
            Validation.DescribeUntrustedValue(decidedBy),
            Validation.DescribeUntrustedValue(toolName),
            outcome,
            detail);

    private static SemaphoreSlim GateFor(string id) =>
        DecisionGates[(id.GetHashCode() & int.MaxValue) % DecisionGates.Length];

    internal static string NormalizeDecisionReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required", nameof(reason));
        var trimmed = reason.Trim();
        if (trimmed.Length > MaxDecisionReasonLength)
            throw new ArgumentException(
                $"reason must be <= {MaxDecisionReasonLength} chars", nameof(reason));
        if (trimmed.Any(char.IsControl))
            throw new ArgumentException("reason must not contain control characters", nameof(reason));
        return trimmed;
    }
}
