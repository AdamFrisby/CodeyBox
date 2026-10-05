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

    /// <summary>
    /// Single source of truth for recognizing the shared typed conflict
    /// contract in an exception chain. The adapter and the orchestrator must
    /// agree on this rule or recovery routing silently forks, so both call
    /// here instead of walking <see cref="Exception.InnerException"/> inline.
    /// Arbitrary message text never qualifies — only the typed contract does.
    /// </summary>
    public static bool TryFindIn(
        Exception? source,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UpstreamPushReconcileConflictException? conflict)
    {
        for (var current = source; current is not null; current = current.InnerException)
        {
            if (current is UpstreamPushReconcileConflictException typed)
            {
                conflict = typed;
                return true;
            }
        }

        conflict = null;
        return false;
    }
}
