namespace CodeyBox.Core;

public sealed class TerminalQuotaError : Exception
{
    public QuotaFailureKind Kind { get; }
    public DateTimeOffset? ResetAt { get; }

    /// <summary>
    /// True when the quota evidence came from a provider-owned surface
    /// (process stderr, CLI-owned terminal region). Stdout/stream-only
    /// evidence is agent-quotable and defaults to false at the call sites
    /// that classify it; every legacy construction site is provider-backed,
    /// hence the default of true.
    /// </summary>
    public bool ProviderSurfaceMatch { get; }

    public TerminalQuotaError(QuotaFailureKind kind, string message, DateTimeOffset? resetAt = null, bool providerSurfaceMatch = true)
        : base(message)
    {
        Kind = kind;
        ResetAt = resetAt;
        ProviderSurfaceMatch = providerSurfaceMatch;
    }
}
