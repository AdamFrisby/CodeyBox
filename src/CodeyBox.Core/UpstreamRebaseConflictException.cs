namespace CodeyBox.Core;

/// <summary>
/// Raised when an upstream non-fast-forward recovery cannot automatically
/// rebase the local branch onto the latest upstream tip.
/// </summary>
public sealed class UpstreamRebaseConflictException : InvalidOperationException
{
    public UpstreamRebaseConflictException(string message) : base(message)
    {
    }

    public UpstreamRebaseConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
