using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Bounded-timeout wrap for post-agent steps (state transition, branch push,
/// commit import). Lifts the bound out of <see cref="PipelineRunner"/> so it
/// can be unit-tested without standing up the pipeline's many dependencies.
///
/// <para>
/// The agent subprocess itself already lives inside
/// <c>WorkItem.WorkTimeout</c>; this bound fences the work the pipeline does
/// AFTER the agent exits — exactly the wedge class the operator observed at
/// f9ea330a and 69ee86c4 where the agent reported completed but the worker
/// hung in the commit / state-transition step.
/// </para>
///
/// <para>
/// When the accessor is null or the configured timeout is non-positive,
/// the body runs unbounded against the caller's token — preserving legacy
/// behaviour for tests / embeddings that don't wire the watchdog options.
/// </para>
///
/// <para>
/// On timeout: emits a <see cref="AuditLog.WorkItemPostAgentTimeout"/> event
/// with the step name and converts the underlying
/// <see cref="OperationCanceledException"/> to a
/// <see cref="TimeoutException"/> so the pipeline's outer catch chain routes
/// through TransitionFailed (infrastructure failure kind) rather than the
/// operator-cancel handler.
/// </para>
/// </summary>
internal static class PostAgentTransitionBound
{
    public static async Task RunAsync(
        Func<WorkerProgressWatchdogOptions>? optionsAccessor,
        WorkItemId itemId,
        string stepName,
        CancellationToken ct,
        Func<CancellationToken, Task> body)
    {
        var timeout = optionsAccessor?.Invoke().PostAgentTransitionTimeout ?? TimeSpan.Zero;
        if (timeout <= TimeSpan.Zero)
        {
            await body(ct);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await body(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            AuditLog.WorkItemPostAgentTimeout(itemId, stepName, (long)timeout.TotalSeconds);
            throw new TimeoutException(
                $"Post-agent step '{stepName}' for work item {itemId} exceeded {timeout.TotalSeconds:F0}s; failing item to release pool slot.");
        }
    }
}
