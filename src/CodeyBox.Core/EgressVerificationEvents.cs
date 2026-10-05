using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Core;

/// <summary>
/// One recorded canary outcome: which sandbox, which phase, whether it
/// passed, and how long each probe took. Failures carry
/// <see cref="Alert"/> so operators can route them loudly.
/// </summary>
public sealed record EgressVerificationEvent(
    string WorkItemId,
    string Phase,
    string ProviderKind,
    string SandboxId,
    bool Passed,
    string? FailureReason,
    IReadOnlyList<EgressVerificationCheckEvent> Checks,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool Alert)
{
    /// <summary>Total canary wall time.</summary>
    public TimeSpan Elapsed => FinishedAt - StartedAt;
}

/// <summary>One probe inside an <see cref="EgressVerificationEvent"/>.</summary>
public sealed record EgressVerificationCheckEvent(
    string Name,
    bool Passed,
    int ExitCode,
    double ElapsedMilliseconds);

/// <summary>
/// Host-owned sink for canary outcomes. The creation-time canary records
/// every result (pass and fail, with timings); a failed canary additionally
/// raises an alert. Implementations must never log secrets or unescaped
/// guest output — check details here are host-generated and bounded.
/// </summary>
public interface IEgressVerificationEventSink
{
    /// <summary>Records one canary outcome.</summary>
    Task RecordAsync(EgressVerificationEvent evt, CancellationToken ct = default);
}

/// <summary>No-op sink for hosts that only want the logger path.</summary>
public sealed class NullEgressVerificationEventSink : IEgressVerificationEventSink
{
    /// <summary>Shared instance.</summary>
    public static NullEgressVerificationEventSink Instance { get; } = new();

    /// <inheritdoc/>
    public Task RecordAsync(EgressVerificationEvent evt, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Loud logger sink: failures log at critical (the alert), passes at
/// information with per-probe timings. Emits counts and exit codes only —
/// never guest output.
/// </summary>
public sealed class LoggerEgressVerificationEventSink : IEgressVerificationEventSink
{
    private readonly ILogger _log;

    /// <param name="log">Logger for the loud event. Null defaults to none.</param>
    public LoggerEgressVerificationEventSink(ILogger? log = null)
    {
        _log = log ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public Task RecordAsync(EgressVerificationEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var summary = string.Join(
            ", ",
            evt.Checks.Select(static c =>
                $"{c.Name}:{(c.Passed ? "pass" : "fail")}(exit {c.ExitCode},{c.ElapsedMilliseconds:0}ms)"));
        if (evt.Passed)
        {
            _log.LogInformation(
                "Egress canary passed for sandbox {SandboxId} on provider kind '{Kind}' (work item {WorkItemId} phase {Phase}) in {ElapsedMs:0}ms: {Checks}.",
                evt.SandboxId, evt.ProviderKind, evt.WorkItemId, evt.Phase, evt.Elapsed.TotalMilliseconds, summary);
        }
        else
        {
            _log.LogCritical(
                "Egress canary FAILED for sandbox {SandboxId} on provider kind '{Kind}' (work item {WorkItemId} phase {Phase}) in {ElapsedMs:0}ms: {Checks}. Reason: {Reason}. " +
                "The sandbox was disposed and the kind is demoted to NotEnforced for the configured cool-down.",
                evt.SandboxId, evt.ProviderKind, evt.WorkItemId, evt.Phase, evt.Elapsed.TotalMilliseconds, summary, evt.FailureReason);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Typed infrastructure failure for a verified-egress sandbox that stops
/// being verified: a failed canary (at creation or on periodic re-check)
/// or a dead provider filter. Never a verdict on the work item's diff —
/// the sandbox is disposed and the item must be re-placed, never failed
/// for the content it produced.
/// </summary>
public sealed class EgressVerificationFailedException : Exception
{
    /// <summary>Provider kind whose filter failed verification.</summary>
    public string ProviderKind { get; }

    /// <summary>Sandbox that failed, when one was created.</summary>
    public string? SandboxId { get; }

    public EgressVerificationFailedException(string providerKind, string? sandboxId, string detail)
        : base($"Egress verification failed for provider kind '{providerKind}'" +
               (string.IsNullOrWhiteSpace(sandboxId) ? string.Empty : $" sandbox '{sandboxId}'") +
               $": {detail}")
    {
        ProviderKind = providerKind;
        SandboxId = sandboxId;
    }
}
