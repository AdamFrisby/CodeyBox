namespace CodeyBox.Core;

public sealed class TerminalQuotaError : Exception
{
    public QuotaFailureKind Kind { get; }
    public DateTimeOffset? ResetAt { get; }

    public TerminalQuotaError(QuotaFailureKind kind, string message, DateTimeOffset? resetAt = null)
        : base(message)
    {
        Kind = kind;
        ResetAt = resetAt;
    }
}
