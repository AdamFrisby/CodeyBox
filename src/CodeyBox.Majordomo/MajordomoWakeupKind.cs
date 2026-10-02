namespace CodeyBox.Majordomo;

/// <summary>
/// Why a majordomo wakeup fired. Scheduled wakeups run on the configured
/// cadence with the purpose prompt; event wakeups react to queue conditions
/// worth reacting to.
/// </summary>
public enum MajordomoWakeupKind
{
    /// <summary>Scheduled cadence tick — by default a queue-health pass.</summary>
    Scheduled,

    /// <summary>A work item entered a terminal failure.</summary>
    TerminalFailure,

    /// <summary>The queue stalled while dispatch capacity sits free.</summary>
    QueueStalled,
}
