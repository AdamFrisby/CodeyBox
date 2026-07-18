using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Re-authoring hook for when a committed replay's step or selector breaks.
/// Full self-heal is a follow-up; this seam records the failed step index so a
/// cheap-model author can re-explore and refresh the artifact.
/// </summary>
public interface IE2eReauthoringHook
{
    /// <summary>
    /// Called when replay fails due to a broken selector/step. Returns true when
    /// a refreshed artifact was produced.
    /// </summary>
    Task<bool> TryReauthorAsync(
        AppUnderTestSession session,
        E2eExplorationPlan plan,
        E2eRunResult failedReplay,
        CancellationToken ct = default);
}

/// <summary>
/// Default no-op re-authoring hook. Wire a real implementation to refresh
/// artifacts after <see cref="E2eRunResult.FailedStepIndex"/> is set.
/// </summary>
public sealed class NullE2eReauthoringHook : IE2eReauthoringHook
{
    public static NullE2eReauthoringHook Instance { get; } = new();

    public Task<bool> TryReauthorAsync(
        AppUnderTestSession session,
        E2eExplorationPlan plan,
        E2eRunResult failedReplay,
        CancellationToken ct = default)
        => Task.FromResult(false);
}
