namespace CodeyBox.Core;

/// <summary>
/// Shared singleton incremented by sandbox-leak reapers when they detect
/// new leaked VMs. Consumers read the counter to trigger notifications.
/// Thread-safe via Interlocked.
/// </summary>
public sealed class LeakDetectionSink
{
    private long _detectionCount;

    public long DetectionCount => Interlocked.Read(ref _detectionCount);

    public void Increment()
    {
        Interlocked.Increment(ref _detectionCount);
    }
}
