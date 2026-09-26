using System.Collections.Concurrent;
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
/// and applies approvals through <see cref="MajordomoMutateBackend"/> with
/// <c>commit: true</c> — the same backend methods with the same commit flag
/// the executor's Autonomous branch uses, so an approved proposal and an
/// autonomous call cannot diverge.
/// </summary>
/// <remarks>
/// Approval revalidates against live queue state at apply time instead of
/// replaying the proposal-time change set: the backend re-reads every target
/// (existence, terminal-state dependencies, cancel/ retry plan guards)
/// before any write, and plans fully before writing, so a proposal whose
/// precondition moved — a cancel whose target has since completed, a chain
/// depending on a since-deleted item — is refused with a reason and the
/// proposal stays pending rather than applied blindly.
/// <para>
/// Chain atomicity is inherited from the backend: multi-create chains commit
/// through a single atomic batch, so a chain that fails partway files
/// nothing. Approval itself is idempotent: a repeated approval of an
/// approved proposal returns the recorded result ids without touching the
/// queue again, and concurrent approvals of one proposal are serialized by a
/// keyed gate plus the store's compare-and-set transition.
/// </para>
/// <para>
/// Approvals deliberately bypass the per-turn mutation ledger: the ledger
/// throttles the model's autonomous bursts, while an approval is an explicit
/// operator authorization of exactly the reviewed change set.
/// </para>
/// </remarks>
internal sealed class MajordomoProposalService
{
    /// <summary>Longest accepted operator decision reason.</summary>
    public const int MaxDecisionReasonLength = 2000;

    private readonly IMajordomoProposalStore _store;
    private readonly MajordomoMutateBackend _mutates;
    private readonly IOptionsMonitor<MajordomoServerOptions> _options;
    private readonly TimeProvider _time;

    /// <summary>
    /// Per-proposal serialization for approvals. Proposal ids are
    /// server-minted GUIDs (a bounded set per queue lifetime segment the
    /// operator actually touches), and entries are dropped when no approval
    /// is in flight, so the map cannot grow with caller input.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _approvalGates = new(StringComparer.Ordinal);

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

    /// <summary>Persists a planned mutation as a pending proposal.</summary>
    public async Task<MajordomoProposalRecord> ProposeAsync(
        MajordomoTool tool,
        MajordomoMutateArgs args,
        string proposedBy,
        CancellationToken ct = default)
    {
        var record = MajordomoProposalRecord.Create(
            tool.Name, args, proposedBy, _time.GetUtcNow(), args.Reasoning);
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
        var gate = _approvalGates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            MajordomoProposalRecord? record;
            try
            {
                record = await _store.GetAsync(id, ct).ConfigureAwait(false);
            }
            catch (MajordomoProposalCorruptException ex)
            {
                return MajordomoProposalApprovalOutcome.Refused(new MajordomoRefusal(
                    MajordomoRefusalReasons.ProposalCorrupt, ex.Message));
            }

            if (record is null)
                return MajordomoProposalApprovalOutcome.Refused(new MajordomoRefusal(
                    MajordomoRefusalReasons.ProposalNotFound,
                    $"proposal '{Validation.DescribeUntrustedValue(id)}' does not exist"));

            var now = _time.GetUtcNow();
            var ttl = TimeSpan.FromSeconds(_options.CurrentValue.ProposalTimeToLiveSeconds);
            if (record.State == MajordomoProposalState.Pending && record.IsExpiredAt(now, ttl))
            {
                var expired = record with
                {
                    State = MajordomoProposalState.Expired,
                    DecidedAt = now,
                    DecidedBy = decidedBy,
                    DecisionReason = $"proposal expired after {ttl} without approval",
                };
                // A lost race here means someone else already moved it out of
                // pending — re-read and report the actual state below.
                if (await _store.TryTransitionAsync(id, MajordomoProposalState.Pending, expired, ct).ConfigureAwait(false))
                {
                    AuditLog.MajordomoToolOutcome(
                        id,
                        Validation.DescribeUntrustedValue(decidedBy),
                        Validation.DescribeUntrustedValue(record.ToolName),
                        MajordomoOutcomes.Refused,
                        "proposal expired");
                    return MajordomoProposalApprovalOutcome.Refused(new MajordomoRefusal(
                        MajordomoRefusalReasons.ProposalExpired,
                        $"proposal '{id}' expired after {ttl} without approval"));
                }

                record = await _store.GetAsync(id, ct).ConfigureAwait(false);
            }

            if (record is null)
                return MajordomoProposalApprovalOutcome.Refused(new MajordomoRefusal(
                    MajordomoRefusalReasons.ProposalNotFound,
                    $"proposal '{Validation.DescribeUntrustedValue(id)}' does not exist"));

            if (record.State != MajordomoProposalState.Pending)
            {
                // Idempotent replay: an already-approved proposal hands back
                // its recorded result without touching the queue again.
                if (record.State == MajordomoProposalState.Approved)
                    return MajordomoProposalApprovalOutcome.AlreadyApproved(
                        record.ResultAffectedItems ?? []);
                return MajordomoProposalApprovalOutcome.Refused(new MajordomoRefusal(
                    MajordomoRefusalReasons.ProposalNotPending,
                    $"proposal '{id}' is already {record.State.ToString().ToLowerInvariant()}"));
            }

            // The commit below runs the backend's full plan-then-write
            // sequence against live state — the revalidation. A refusal here
            // leaves the proposal pending (and, by the backend's contract,
            // files nothing new for the refused part), so the operator can
            // inspect, reject, or wait and retry.
            MajordomoMutationResult mutation;
            try
            {
                mutation = await CommitAsync(record, initiator, ct).ConfigureAwait(false);
            }
            catch (MajordomoProposalCorruptException ex)
            {
                return MajordomoProposalApprovalOutcome.Refused(new MajordomoRefusal(
                    MajordomoRefusalReasons.ProposalCorrupt, ex.Message));
            }

            if (mutation.Refusal is not null)
            {
                AuditLog.MajordomoToolOutcome(
                    id,
                    Validation.DescribeUntrustedValue(decidedBy),
                    Validation.DescribeUntrustedValue(record.ToolName),
                    MajordomoOutcomes.Refused,
                    mutation.Refusal.Detail);
                return MajordomoProposalApprovalOutcome.Refused(mutation.Refusal);
            }

            var approved = record with
            {
                State = MajordomoProposalState.Approved,
                DecidedAt = now,
                DecidedBy = decidedBy,
                DecisionReason = "approved by operator",
                ResultAffectedItems = mutation.ChangeSet!.AffectedItems,
            };
            if (!await _store.TryTransitionAsync(id, MajordomoProposalState.Pending, approved, ct).ConfigureAwait(false))
            {
                // Unreachable while the keyed gate serializes approvals of one
                // id in this process — fail loudly rather than report success
                // for a state we did not record. The queue mutation above
                // already landed; the audit record carries that fact.
                AuditLog.MajordomoToolOutcome(
                    id,
                    Validation.DescribeUntrustedValue(decidedBy),
                    Validation.DescribeUntrustedValue(record.ToolName),
                    MajordomoOutcomes.Error,
                    "approval committed but the proposal transition raced");
                throw new InvalidOperationException(
                    $"proposal '{id}' left pending after its approval committed");
            }

            AuditLog.MajordomoToolOutcome(
                id,
                Validation.DescribeUntrustedValue(decidedBy),
                Validation.DescribeUntrustedValue(record.ToolName),
                MajordomoOutcomes.Executed,
                $"proposal {id} approved");
            return MajordomoProposalApprovalOutcome.Approved(mutation.ChangeSet);
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1)
                _approvalGates.TryRemove(new KeyValuePair<string, SemaphoreSlim>(id, gate));
        }
    }

    /// <summary>Rejects a pending proposal; nothing is mutated.</summary>
    public async Task<MajordomoProposalDecisionOutcome> RejectAsync(
        string id,
        string decidedBy,
        string reason,
        CancellationToken ct = default) =>
        await DecideAsync(id, decidedBy, reason, MajordomoProposalState.Rejected, ct).ConfigureAwait(false);

    /// <summary>Marks a pending proposal superseded; nothing is mutated.</summary>
    public async Task<MajordomoProposalDecisionOutcome> SupersedeAsync(
        string id,
        string decidedBy,
        string reason,
        CancellationToken ct = default) =>
        await DecideAsync(id, decidedBy, reason, MajordomoProposalState.Superseded, ct).ConfigureAwait(false);

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

        MajordomoProposalRecord? record;
        try
        {
            record = await _store.GetAsync(id, ct).ConfigureAwait(false);
        }
        catch (MajordomoProposalCorruptException ex)
        {
            return MajordomoProposalDecisionOutcome.Refused(new MajordomoRefusal(
                MajordomoRefusalReasons.ProposalCorrupt, ex.Message));
        }

        if (record is null)
            return MajordomoProposalDecisionOutcome.Refused(new MajordomoRefusal(
                MajordomoRefusalReasons.ProposalNotFound,
                $"proposal '{Validation.DescribeUntrustedValue(id)}' does not exist"));
        if (record.State != MajordomoProposalState.Pending)
            return MajordomoProposalDecisionOutcome.Refused(new MajordomoRefusal(
                MajordomoRefusalReasons.ProposalNotPending,
                $"proposal '{id}' is already {record.State.ToString().ToLowerInvariant()}"));

        var decided = record with
        {
            State = terminal,
            DecidedAt = _time.GetUtcNow(),
            DecidedBy = decidedBy,
            DecisionReason = checkedReason,
        };
        if (!await _store.TryTransitionAsync(id, MajordomoProposalState.Pending, decided, ct).ConfigureAwait(false))
        {
            var current = await _store.GetAsync(id, ct).ConfigureAwait(false);
            var actual = current?.State.ToString().ToLowerInvariant() ?? "missing";
            return MajordomoProposalDecisionOutcome.Refused(new MajordomoRefusal(
                MajordomoRefusalReasons.ProposalNotPending,
                $"proposal '{id}' is already {actual}"));
        }

        AuditLog.MajordomoToolOutcome(
            id,
            Validation.DescribeUntrustedValue(decidedBy),
            Validation.DescribeUntrustedValue(record.ToolName),
            MajordomoOutcomes.Refused,
            $"proposal {id} {terminal.ToString().ToLowerInvariant()}: {checkedReason}");
        return MajordomoProposalDecisionOutcome.Decided(decided);
    }

    /// <summary>
    /// Commits a proposal through the mutate backend — the same per-tool
    /// methods with the same <c>commit: true</c> flag the executor's
    /// Autonomous branch runs. A cancel passes no pre-measured cascade so
    /// the backend enumerates the live set at apply time.
    /// </summary>
    private Task<MajordomoMutationResult> CommitAsync(
        MajordomoProposalRecord record,
        WorkInitiator initiator,
        CancellationToken ct)
    {
        if (record.Arguments.DryRun)
            throw new InvalidOperationException(
                $"proposal '{record.Id}' carries a dry-run call, which is never proposable");

        if (!MajordomoTools.TryGet(record.ToolName, out var tool) ||
            tool.Class != MajordomoToolClass.Mutate ||
            record.Arguments.GetType() != tool.ArgumentsType)
            throw new MajordomoProposalCorruptException(
                record.Id, $"tool '{record.ToolName}' is no longer a known MUTATE tool");

        return tool.Name switch
        {
            "create_work_item" => _mutates.CreateAsync(
                (CreateWorkItemArgs)record.Arguments, initiator, commit: true, ct),
            "create_work_item_chain" => _mutates.CreateChainAsync(
                (CreateWorkItemChainArgs)record.Arguments, initiator, commit: true, ct),
            "update_work_item" => _mutates.UpdateAsync(
                (UpdateWorkItemArgs)record.Arguments, commit: true, ct),
            "cancel_work_item" => _mutates.CancelAsync(
                (CancelWorkItemArgs)record.Arguments, cascadeTargets: null, commit: true, ct),
            "retry_work_item" => _mutates.RetryAsync(
                (RetryWorkItemArgs)record.Arguments, commit: true, ct),
            _ => throw new MajordomoProposalCorruptException(
                record.Id, $"tool '{record.ToolName}' has no commit path"),
        };
    }

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
