namespace CodeyBox.Core;

/// <summary>
/// Optional sandbox capability exposing the liveness of the provider-host
/// packet filter for one sandbox (for Tart, the Softnet filter hosted by
/// the <c>tart run</c> process). The host's canary verifier and its periodic
/// re-verifier read this through <see cref="SandboxCapability.Find{T}"/>
/// and treat a dead filter as a failed canary. Providers without an
/// out-of-guest filter simply do not implement this interface — absence
/// means "no signal", never a failure.
/// </summary>
public interface IEgressFilterHealth : ISandbox
{
    /// <summary>
    /// True when no provider filter exists for this sandbox or the filter
    /// process is still running. False only on positive evidence that the
    /// filter died (never on unknown state).
    /// </summary>
    bool IsFilterAlive { get; }
}
