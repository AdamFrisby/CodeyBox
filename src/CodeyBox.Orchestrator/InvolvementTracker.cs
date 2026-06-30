using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal sealed class InvolvementTracker
{
    private const int PersistenceMaxAttempts = 4;
    private static readonly TimeSpan PersistenceRetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly IAgentInvolvementStore? _involvement;
    private readonly ILogger _log;

    public InvolvementTracker(IAgentInvolvementStore? involvement, ILogger log)
    {
        _involvement = involvement;
        _log = log;
    }

    /// <summary>
    /// Appends an in-progress <see cref="AgentInvolvement"/> row for the agent
    /// about to run a phase and returns its id (or null when no involvement store
    /// is wired). PipelineRunner is the single writer of involvement rows (the
    /// router selects but never persists), so every phase attempt that actually
    /// runs opens exactly one row here - no cross-component adoption handshake.
    /// Best-effort: a failure to persist never breaks the pipeline, mirroring the
    /// fallback-history recording.
    /// </summary>
    public async Task<Guid?> RecordStartAsync(
        WorkItemId workItemId, AgentKind agent, string? agentInstanceId, string? modelId, string phase, int? iteration)
    {
        if (_involvement is null) return null;

        var entry = new AgentInvolvement(
            Id: Guid.NewGuid(),
            WorkItemId: workItemId,
            AgentKind: agent,
            AgentInstanceId: agentInstanceId,
            ModelId: modelId,
            Phase: phase,
            StartedAt: DateTimeOffset.UtcNow,
            EndedAt: null,
            Iteration: iteration,
            Outcome: null);

        var persisted = await PersistWithRetryAsync(
            ct => _involvement.RecordStartAsync(entry, ct),
            op: "start record", phase: phase);
        return persisted ? entry.Id : null;
    }

    /// <summary>
    /// Stamps the completion outcome on a previously-started involvement row.
    /// No-op when no store is wired or no row was recorded. Uses
    /// <see cref="CancellationToken.None"/> so the audit stamp lands even when
    /// the phase was cancelled, and retries transient faults so the closing stamp
    /// survives a momentary store blip (see <see cref="PersistWithRetryAsync"/>).
    /// </summary>
    public async Task FinalizeAsync(Guid? involvementId, string outcome)
    {
        if (_involvement is null || involvementId is not { } id) return;
        await PersistWithRetryAsync(
            ct => _involvement.FinalizeAsync(id, DateTimeOffset.UtcNow, outcome, ct),
            op: "finalize", phase: outcome);
    }

    /// <summary>
    /// Maps an attempt-terminating exception to a compact involvement outcome
    /// label ("failure:&lt;reason&gt;") for operator-facing attribution.
    /// </summary>
    public static string OutcomeForFailure(Exception ex) => ex switch
    {
        TerminalQuotaError => "failure:quota",
        AuditorIdleTimeoutException => "failure:timeout",
        TerminalTransientNetworkError => "failure:transient",
        PipelineRunner.AgentAttemptTimeoutException => "failure:timeout",
        OperationCanceledException => "failure:cancelled",
        _ => "failure:agent",
    };

    /// <summary>
    /// Persists one involvement mutation (start insert or finalize update) with a
    /// bounded retry so a <em>transient</em> store fault (SQLite busy/locked, an
    /// <see cref="IOException"/>, a <see cref="TimeoutException"/>) does not drop
    /// an audit-trail row on the first blip - AC#1 requires a row on every phase
    /// transition and AC#6 a 1:1 phase-to-row mapping, so a momentary lock must not
    /// silently erode the trail. Retries share the DB with the work-item store, so
    /// a fault that survives all attempts means the DB is genuinely unhealthy and
    /// the next work-item write would fail the phase anyway; rather than abort a
    /// work item that did real work for an audit-trail write, the exhausted fault
    /// is logged at Warning and swallowed (returns false). An
    /// <see cref="ObjectDisposedException"/> from a store torn down during host
    /// shutdown is not retried (the host is going away) but is likewise tolerated.
    /// Cancellation and any unexpected exception (a wiring/programming bug) always
    /// propagate so they surface in CI instead of silently eroding the trail.
    /// </summary>
    private async Task<bool> PersistWithRetryAsync(
        Func<CancellationToken, Task> write, string op, string phase)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await write(CancellationToken.None);
                return true;
            }
            catch (Exception ex)
                when (IsTransientPersistenceFault(ex) && attempt < PersistenceMaxAttempts)
            {
                _log.LogDebug(ex,
                    "agent involvement {Op} transient fault for phase '{Phase}' (attempt {Attempt}/{Max}); retrying",
                    op, phase, attempt, PersistenceMaxAttempts);
                await Task.Delay(PersistenceRetryDelay * attempt, CancellationToken.None);
            }
            catch (Exception ex) when (IsTolerablePersistenceFault(ex))
            {
                // Transient fault that survived every retry, or a store disposed
                // during host shutdown. Logged at Warning (not Debug) so a dropped
                // audit-trail row stays operator-visible.
                _log.LogWarning(ex, "agent involvement {Op} failed for phase '{Phase}'", op, phase);
                return false;
            }
        }
    }

    /// <summary>
    /// Transient (retryable) involvement persistence faults: a contended store
    /// (any <see cref="System.Data.Common.DbException"/> such as SQLite
    /// busy/locked), an <see cref="IOException"/>, or a <see cref="TimeoutException"/>.
    /// These typically clear on a short retry, so the audit-trail row is preserved
    /// rather than dropped.
    /// </summary>
    private static bool IsTransientPersistenceFault(Exception ex) =>
        ex is System.Data.Common.DbException or IOException or TimeoutException;

    /// <summary>
    /// The bounded set of exceptions involvement persistence is allowed to swallow
    /// after retries: the transient faults above plus an
    /// <see cref="ObjectDisposedException"/> from a store torn down during host
    /// shutdown. Cancellation is excluded so it keeps propagating; anything else is
    /// an unexpected bug that must surface.
    /// </summary>
    private static bool IsTolerablePersistenceFault(Exception ex) =>
        ex is not OperationCanceledException
        && (IsTransientPersistenceFault(ex) || ex is ObjectDisposedException);
}
