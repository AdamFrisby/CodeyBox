using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public interface IPipelineRunner
{
    /// <summary>
    /// Runs the full pipeline for <paramref name="item"/>.
    /// </summary>
    /// <param name="item">The work item to execute.</param>
    /// <param name="ct">
    /// Per-item token linked to both the operator-cancel signal and the host
    /// shutdown token. Cancelling this token means "stop now for some reason".
    /// </param>
    /// <param name="hostShutdownToken">
    /// The <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.ApplicationStopping"/>
    /// token. When this fires the pipeline leaves the item in its current
    /// mid-flight state so the recovery loop can pick it up on next startup.
    /// When only <paramref name="ct"/> fires (this token is not cancelled), the
    /// cancellation was operator-requested and the item is transitioned to
    /// <see cref="WorkItemState.Cancelled"/>.
    /// </param>
    Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default);
}
