using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Bounds derivation and degraded-signal bookkeeping for durable agent-turn
/// checkpoints. The checkpoint transfer execs historically requested an
/// independent base64 limit derived only from the maximum archive size, which
/// exceeded the Incus provider-wide CLI output bound and made every checkpoint
/// write throw before any resumption evidence existed. Requested limits are now
/// derived from the sandbox's advertised provider bound so the two cannot drift
/// apart again; the provider additionally clamps any residual over-request at
/// its own sink. A checkpoint that genuinely cannot be written is reported on
/// the audit log and the <c>codeybox.agent_turn.checkpoints</c> meter — a
/// degraded-capability signal operators can see — while the original agent
/// failure remains authoritative.
/// </summary>
internal static class AgentTurnCheckpointLimits
{
    internal const string DegradedReportedKey = "CodeyBox.AgentTurnCheckpointDegraded";

    /// <summary>
    /// Resolves the per-exec stdout/stderr caps for checkpoint transfer execs.
    /// When <paramref name="sandbox"/> advertises provider-wide output bounds
    /// (<see cref="ISandboxExecOutputLimits"/>), the returned caps never exceed
    /// them; otherwise the desired values pass through unchanged. Pure.
    /// </summary>
    internal static (int MaxStdoutBytes, int MaxStderrBytes) ResolveExecOutputLimits(
        ISandbox? sandbox,
        int desiredStdoutBytes,
        int desiredStderrBytes)
    {
        if (sandbox is not ISandboxExecOutputLimits limits)
            return (desiredStdoutBytes, desiredStderrBytes);
        return (
            Math.Min(desiredStdoutBytes, limits.MaxStdoutBytes),
            Math.Min(desiredStderrBytes, limits.MaxStderrBytes));
    }

    /// <summary>
    /// Whether <paramref name="failure"/> has not yet produced the degraded
    /// checkpoint signal. The publish path reports first and marks the
    /// exception, so an outer recovery handler can avoid a duplicate signal
    /// for the same failure. Pure.
    /// </summary>
    internal static bool ShouldReportDegraded(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return !failure.Data.Contains(DegradedReportedKey);
    }

    /// <summary>
    /// Emits the operator-visible degraded signal for a checkpoint that cannot
    /// be written and marks <paramref name="failure"/> as reported. The
    /// exception instance is left otherwise untouched so the caller still
    /// preserves the original failure.
    /// </summary>
    internal static void ReportDegraded(
        WorkItemId workItemId,
        string stage,
        Exception failure,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(log);
        CodeyBoxMeters.AgentTurnCheckpoints.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "degraded"),
            new KeyValuePair<string, object?>("stage", stage));
        AuditLog.AgentTurnCheckpointDegraded(
            workItemId,
            stage,
            $"{failure.GetType().Name}: {failure.Message}");
        failure.Data[DegradedReportedKey] = stage;
        log.LogWarning(
            failure,
            "Durable agent-turn checkpoint unavailable for work item {WorkItemId} (stage {Stage}); reported as degraded, the original failure stands",
            workItemId.ToString(),
            stage);
    }
}
