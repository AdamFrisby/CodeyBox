namespace CodeyBox.Orchestrator;

/// <summary>
/// Bounds retries used to persist an unexpected deep-audit failure after the
/// release has entered <see cref="CodeyBox.Core.ReleaseState.InReview"/>.
/// </summary>
public sealed class DeepAuditFailurePersistenceOptions
{
    public const int DefaultMaxAttempts = 3;
    public const int MaximumMaxAttempts = 10;
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>Total persistence attempts, including the initial attempt.</summary>
    public int MaxAttempts { get; set; } = DefaultMaxAttempts;

    /// <summary>Delay between attempts. Zero retries immediately.</summary>
    public TimeSpan RetryDelay { get; set; } = DefaultRetryDelay;

    public void Validate()
    {
        _ = CaptureValidated();
    }

    internal DeepAuditFailurePersistenceSettings CaptureValidated()
    {
        var maxAttempts = MaxAttempts;
        var retryDelay = RetryDelay;

        if (maxAttempts is < 1 or > MaximumMaxAttempts)
        {
            throw new InvalidOperationException(
                $"CodeyBox:DeepAuditFailurePersistence:MaxAttempts must be between 1 and {MaximumMaxAttempts}");
        }

        if (retryDelay < TimeSpan.Zero || retryDelay > MaximumRetryDelay)
        {
            throw new InvalidOperationException(
                $"CodeyBox:DeepAuditFailurePersistence:RetryDelay must be between TimeSpan.Zero and {MaximumRetryDelay}");
        }

        return new DeepAuditFailurePersistenceSettings(maxAttempts, retryDelay);
    }
}

internal readonly record struct DeepAuditFailurePersistenceSettings(
    int MaxAttempts,
    TimeSpan RetryDelay);
