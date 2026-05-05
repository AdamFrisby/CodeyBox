namespace CodeyBox.Core;

/// <summary>
/// Raised when an upstream non-fast-forward push cannot be reconciled without
/// manual conflict resolution.
/// </summary>
public sealed class UpstreamPushReconcileConflictException : InvalidOperationException
{
    public UpstreamPushReconcileConflictException(string branch, string strategy)
        : base($"upstream {strategy} conflict on {branch}; manual resolution required")
    {
        Branch = branch;
        Strategy = strategy;
    }

    public string Branch { get; }
    public string Strategy { get; }
}
