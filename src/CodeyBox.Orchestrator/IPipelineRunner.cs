using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public interface IPipelineRunner
{
    Task RunAsync(WorkItem item, CancellationToken ct);
}
