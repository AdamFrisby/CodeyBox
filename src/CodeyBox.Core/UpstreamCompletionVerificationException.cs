namespace CodeyBox.Core;

/// <summary>
/// Raised when an upstream remote cannot evaluate a pre-merge invariant it
/// depends on — for example the pushed-head/base ancestry assertion — because
/// the underlying probe failed or returned no verdict. This is deliberately
/// distinct from a proven negative: the orchestrator classifies it as an
/// infrastructure failure and retries, instead of letting an unevaluable
/// check false-pass a merge gate or misrouting the item into conflict
/// handling.
/// </summary>
public sealed class UpstreamCompletionVerificationException : InvalidOperationException
{
    public UpstreamCompletionVerificationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
