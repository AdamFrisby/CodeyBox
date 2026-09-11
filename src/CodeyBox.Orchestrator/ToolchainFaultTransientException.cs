using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// A gate subprocess (e.g. <c>dotnet build</c>) failed with a retryable
/// toolchain-fault signature rather than genuine code breakage. The outer
/// pipeline catch routes this to the existing
/// <see cref="WorkItemState.WaitingForTransientRetry"/> path — same commit,
/// re-run — with its existing bounded attempts and jitter. Kept distinct from
/// <see cref="TestFailureAttribution"/> (flake attribution consults the base
/// branch); the two classifications never share a disposition.
/// </summary>
internal sealed class ToolchainFaultTransientException : Exception
{
    public ToolchainFaultClassification Classification { get; }
    public string? Phase { get; }

    public ToolchainFaultTransientException(
        ToolchainFaultClassification classification,
        string? phase,
        string message)
        : base(message)
    {
        Classification = classification;
        Phase = phase;
    }
}
