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
    /// The effective, floored attempt cap. Exposed so callers can decide whether
    /// a park is a cap-exhaustion terminal without duplicating the flooring rule.
    /// </summary>
    public int MaxReworkAttempts => Math.Max(1, _optionsAccessor().MaxReworkAttempts);

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
    public async Task<StaleBaseReworkOutcome> TryRouteAsync(
        WorkItem item,
        string trigger,
        CancellationToken ct)
    {
        if (!_optionsAccessor().RouteToConflictRework)
            return StaleBaseReworkOutcome.NotEnabled;

        var current = await _store.GetAsync(item.Id, ct) ?? item;
        var max = MaxReworkAttempts;
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
