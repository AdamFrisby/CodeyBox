namespace CodeyBox.Core;

/// <summary>
/// Thrown by an <see cref="IUpstreamRemote"/> implementation when the forge's
/// shared request budget says the call must not be sent yet — either the
/// primary rate limit is exhausted until its reset, a secondary limit
/// requested a cooldown via <c>Retry-After</c>, or a latency-tolerant
/// (background) read was deferred to preserve reserve budget for
/// pipeline-critical calls.
///
/// <para>This is a deliberate deferral, not a failure: callers that can wait
/// should skip the work and retry on their next natural tick; callers that
/// cannot should let it propagate like any other upstream failure. The
/// exception never carries credentials or response bodies.</para>
/// </summary>
public sealed class UpstreamApiDeferredException : Exception
{
    /// <summary>
    /// When the budget is expected to allow calls again, in UTC. Null when
    /// the forge did not report a reset/cooldown time.
    /// </summary>
    public DateTimeOffset? DeferredUntil { get; }

    public UpstreamApiDeferredException(string message, DateTimeOffset? deferredUntil, Exception? innerException = null)
        : base(message, innerException)
    {
        DeferredUntil = deferredUntil;
    }
}
