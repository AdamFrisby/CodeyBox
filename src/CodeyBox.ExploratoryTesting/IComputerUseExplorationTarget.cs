using CodeyBox.Core;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Minimal surface an explorer needs from a recording computer-use session.
/// </summary>
public interface IComputerUseExplorationTarget
{
    SessionTrace Trace { get; }

    void SetMetadata(string? targetName = null, string? entryUrl = null, byte[]? readinessScreenshotPng = null);

    Task<ComputerUseResult> ExecuteAsync(
        ISandbox sandbox,
        ComputerUseRequest request,
        CancellationToken ct = default);

    void EndTrace();
}
