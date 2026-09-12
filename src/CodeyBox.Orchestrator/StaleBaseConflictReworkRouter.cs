using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Disposition of a stale-base rework routing decision.
/// </summary>
public enum StaleBaseReworkOutcome
{
    /// <summary>
    /// The work item was transitioned into
    /// <see cref="WorkItemState.ReworkingForConflict"/> and re-enqueued for the
    /// dispatcher to pick up. The caller must not park the item.
    /// </summary>
    Routed,

    /// <summary>
    /// The item has already accrued the configured maximum number of stale-base
    /// rework attempts. The caller should park it at
    /// <see cref="WorkItemState.MergeConflictResolutionFailed"/>.
    /// </summary>
    CapExhausted,

    /// <summary>
    /// Routing is disabled by configuration, or the atomic re-dispatch could not
    /// be applied (e.g. the item's state changed concurrently). The caller
    /// should fall back to its historical park / notify behaviour.
    /// </summary>
    NotEnabled,
}

/// <summary>
/// Routes a detected stale-base merge conflict into the existing conflict-rework
/// state machine instead of parking-and-notifying.
///
/// <para>Shared by both trigger surfaces described in the stale-base spec: the
/// out-of-band <see cref="StalePullRequestSweeper"/> (which discovers an already
/// open PR whose base moved after the item shipped) and the in-pipeline
/// upstream-push path (which discovers the same class of conflict at push time).
/// Centralising the policy here keeps a single source of truth for the enable
/// flag and the attempt cap.</para>
///
/// <para>Mechanism: it reserves one conflict-rework attempt by incrementing
/// <see cref="WorkItem.ConflictReworkAttempts"/> and atomically transitioning the
/// item to <see cref="WorkItemState.ReworkingForConflict"/> via
/// <see cref="WorkItemRetrier"/> with <see cref="RetryFromPolicy.ConflictRework"/>.
/// On pickup the pipeline skips work/audit, refreshes the canonical base, re-runs
/// the merge phase, and — on the resulting conflict — engages the agentic
/// conflict resolver. Reserving the attempt up-front (rather than letting the
/// merge-phase catch count it) is what makes the counter advance across
/// successive re-dispatches: the resume path deliberately does not double-count
/// an attempt it believes was already reserved, so without the reservation the
/// counter would stick and the loop would be unbounded.</para>
/// </summary>
public sealed class StaleBaseConflictReworkRouter
{
    private readonly IWorkItemStore _store;
    private readonly WorkItemRetrier _retrier;
    private readonly Func<StalePullRequestSweeperOptions> _optionsAccessor;
    private readonly ILogger<StaleBaseConflictReworkRouter> _log;

    public StaleBaseConflictReworkRouter(
        IWorkItemStore store,
        WorkItemRetrier retrier,
        Func<StalePullRequestSweeperOptions> optionsAccessor,
        ILogger<StaleBaseConflictReworkRouter> log)
    {
        _store = store;
        _retrier = retrier;
        _optionsAccessor = optionsAccessor;
        _log = log;
    }

    /// <summary>
    /// The settled, post-ship work-item states from which the out-of-band
    /// <see cref="StalePullRequestSweeper"/> may re-dispatch an item into
    /// stale-base rework. Every in-flight state (work/audit/merge/push in
    /// progress) is excluded so the sweeper never seizes an item another worker
    /// actively owns. Single source of truth for both the sweeper's up-front
    /// eligibility check and the router's atomic re-validation.
    /// </summary>
    public static readonly IReadOnlySet<WorkItemState> SweeperEligibleSourceStates =
        new HashSet<WorkItemState>
        {
            WorkItemState.Done,
            WorkItemState.Merged,
            WorkItemState.MergeConflictResolutionFailed,
            WorkItemState.Failed,
        };

    /// <summary>
    /// The in-pipeline upstream-push path routes from its own active push state.
    /// The current worker owns the item there, but the router still re-validates
    /// against this narrow set so an unexpected concurrent transition is rejected
    /// rather than clobbered.
    /// </summary>
    public static readonly IReadOnlySet<WorkItemState> PushPathEligibleSourceStates =
        new HashSet<WorkItemState> { WorkItemState.UpstreamPushing };

    /// <summary>
    /// Applies the single flooring rule for the configured attempt cap: a value
    /// below 1 is treated as 1 so the router always permits at least one rework
    /// attempt. Kept as one method so the rule has a single source of truth
    /// shared by <see cref="MaxReworkAttempts"/> and <see cref="TryRouteAsync"/>.
    /// </summary>
    private static int FloorAttemptCap(int configured) => Math.Max(1, configured);

    /// <summary>
    /// The effective, floored attempt cap. Exposed so callers can decide whether
    /// a park is a cap-exhaustion terminal without duplicating the flooring rule.
    /// </summary>
    public int MaxReworkAttempts => FloorAttemptCap(_optionsAccessor().MaxReworkAttempts);

    /// <summary>True when stale-base rework routing is enabled.</summary>
    public bool Enabled => _optionsAccessor().RouteToConflictRework;

    /// <summary>
    /// Attempts to route <paramref name="item"/> into conflict-rework.
    /// Re-reads the item from the store so the attempt-cap check and the
    /// re-dispatch operate on the authoritative current state (the caller may
    /// hold a stale snapshot). See <see cref="StaleBaseReworkOutcome"/> for the
    /// contract each disposition places on the caller.
    /// </summary>
    /// <param name="item">The work item owning the stale-base PR.</param>
    /// <param name="trigger">
    /// Short provenance label recorded on the retry audit trail (e.g.
    /// <c>"stale-pr-sweep"</c> or <c>"upstream-push-stale-base"</c>).
    /// </param>
    /// <param name="eligibleSourceStates">
    /// The states from which this caller's surface may legitimately re-dispatch
    /// (e.g. <see cref="SweeperEligibleSourceStates"/> for the out-of-band sweeper
    /// or <see cref="PushPathEligibleSourceStates"/> for the in-pipeline push
    /// path). The router re-validates the authoritative fresh read against this
    /// set so eligibility travels atomically with the state-changing write — the
    /// caller's own check was made on a snapshot that may have since raced.
    /// </param>
    public async Task<StaleBaseReworkOutcome> TryRouteAsync(
        WorkItem item,
        string trigger,
        IReadOnlySet<WorkItemState> eligibleSourceStates,
        CancellationToken ct)
    {
        // Snapshot the hot-reloadable options once so the enable-check and the
        // attempt-cap check below operate on a single, consistent configuration
        // view. Re-invoking the accessor across the intervening await could tear
        // a concurrent hot-reload — e.g. routing under a freshly-raised cap while
        // having gated on the old enable flag.
        var options = _optionsAccessor();
        if (!options.RouteToConflictRework)
            return StaleBaseReworkOutcome.NotEnabled;

        var current = await _store.GetAsync(item.Id, ct) ?? item;

        // Re-validate eligibility against the authoritative fresh read, not the
        // caller's (possibly stale) snapshot. The caller checked eligibility on a
        // snapshot that may have raced a concurrent transition; without re-checking
        // here the router would re-dispatch an item that has since moved into an
        // in-flight state another worker owns — clearing StartedAt and enabling a
        // second concurrent run of the same pipeline. Because the retrier's CAS
        // below is conditioned on this same current.State, the eligibility decision
        // travels atomically with the write: if the state changes between here and
        // the CAS, the CAS fails rather than clobbering the concurrent owner.
        if (!eligibleSourceStates.Contains(current.State))
        {
            _log.LogInformation(
                "Work item {Id} is in state {State}, no longer eligible for stale-base rework; not routing",
                current.Id, current.State);
            return StaleBaseReworkOutcome.NotEnabled;
        }

        var max = FloorAttemptCap(options.MaxReworkAttempts);
        if (current.ConflictReworkAttempts >= max)
        {
            _log.LogInformation(
                "Work item {Id} has exhausted stale-base rework attempts ({Attempts}/{Max}); caller should park",
                current.Id, current.ConflictReworkAttempts, max);
            return StaleBaseReworkOutcome.CapExhausted;
        }

        // Reserve the attempt as part of the same atomic transition the retrier
        // performs (WorkItem.With preserves ConflictReworkAttempts, so passing
        // the bumped snapshot writes the increment and the state change in one
        // conditional update — no partial-commit window).
        var reserved = current with { ConflictReworkAttempts = current.ConflictReworkAttempts + 1 };
        var (success, error, _, _, _) = await _retrier.RetryAsync(
            reserved,
            from: RetryFromPolicy.ConflictRework,
            trigger: trigger,
            ct: ct);
        if (!success)
        {
            _log.LogWarning(
                "Work item {Id} stale-base rework re-dispatch did not apply ({Error}); falling back to caller park",
                current.Id, error ?? "unknown");
            return StaleBaseReworkOutcome.NotEnabled;
        }

        _log.LogInformation(
            "Work item {Id} routed into conflict-rework for stale-base conflict (attempt {Attempt}/{Max}, trigger={Trigger})",
            current.Id, reserved.ConflictReworkAttempts, max, trigger);
        return StaleBaseReworkOutcome.Routed;
    }
}
