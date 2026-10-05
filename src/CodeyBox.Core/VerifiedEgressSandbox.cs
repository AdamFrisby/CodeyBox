using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Core;

/// <summary>
/// Host-owned wrapper for a sandbox that passed its creation-time canary:
/// every exec first re-checks the provider filter signal and re-runs the
/// full canary when <see cref="EgressVerificationOptions.ReverifyInterval"/>
/// elapsed, so a filter that dies mid-run is caught instead of silently
/// serving unverified work. A failed re-check disposes the sandbox, demotes
/// the kind through the gate, records an alert event, and throws
/// <see cref="EgressVerificationFailedException"/> — an infrastructure
/// failure, never a verdict on the item's diff. Only placement creates this
/// wrapper, and only for profiled work on verified kinds.
/// </summary>
public sealed class VerifiedEgressSandbox : ISandboxDecorator
{
    private readonly ISandbox _inner;
    private readonly string _providerKind;
    private readonly EgressCanaryVerifier _verifier;
    private readonly Func<EgressVerificationOptions> _optionsAccessor;
    private readonly EgressVerificationGate _gate;
    private readonly IEgressVerificationEventSink _sink;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly string _workItemId;
    private readonly string _phase;
    private readonly SemaphoreSlim _verifyLock = new(1, 1);
    private DateTimeOffset _lastVerified;
    private int _disposed;
    private int _failed;

    public VerifiedEgressSandbox(
        ISandbox inner,
        string providerKind,
        EgressCanaryVerifier verifier,
        Func<EgressVerificationOptions> optionsAccessor,
        EgressVerificationGate gate,
        IEgressVerificationEventSink sink,
        TimeProvider clock,
        string workItemId,
        string phase,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        _inner = inner;
        _providerKind = providerKind.Trim().ToLowerInvariant();
        _verifier = verifier;
        _optionsAccessor = optionsAccessor;
        _gate = gate;
        _sink = sink;
        _clock = clock;
        _workItemId = workItemId;
        _phase = phase;
        _log = log ?? NullLogger.Instance;
        _lastVerified = clock.GetUtcNow();
    }

    /// <inheritdoc/>
    public ISandbox InnerSandbox => _inner;

    /// <inheritdoc/>
    public string Id => _inner.Id;

    /// <inheritdoc/>
    public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;

    /// <inheritdoc/>
    public SandboxBatchLaunchMode BatchLaunchMode => _inner.BatchLaunchMode;

    /// <inheritdoc/>
    public SandboxResourceMetrics? ResourceMetrics => _inner.ResourceMetrics;

    /// <summary>When the last passing canary completed (creation or re-check).</summary>
    public DateTimeOffset LastVerifiedAt => _lastVerified;

    /// <inheritdoc/>
    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_failed != 0)
            throw new EgressVerificationFailedException(_providerKind, Id, "sandbox already failed re-verification and was disposed.");
        await _verifyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var options = _optionsAccessor();
            if (IsFilterDead())
                await FailAsync("provider filter process is not alive.", options, ct).ConfigureAwait(false);
            else if (options.ReverifyInterval > TimeSpan.Zero
                && _clock.GetUtcNow() - _lastVerified >= options.ReverifyInterval)
                await ReverifyAsync(options, ct).ConfigureAwait(false);
        }
        finally
        {
            _verifyLock.Release();
        }
        return await _inner.ExecAsync(exec, ct).ConfigureAwait(false);
    }

    private static bool IsFilterDead(ISandbox sandbox)
    {
        try
        {
            return SandboxCapability.Find<IEgressFilterHealth>(sandbox) is { IsFilterAlive: false };
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private bool IsFilterDead() => IsFilterDead(_inner);

    private async Task ReverifyAsync(EgressVerificationOptions options, CancellationToken ct)
    {
        var result = await _verifier.VerifyAsync(_inner, _providerKind, options, ct).ConfigureAwait(false);
        if (result.Passed)
        {
            _lastVerified = _clock.GetUtcNow();
            await _sink.RecordAsync(ToEvent(result, alert: false), ct).ConfigureAwait(false);
            return;
        }
        await FailAsync(result.FailureReason ?? "periodic re-verification failed.", options, ct, result).ConfigureAwait(false);
    }

    private async Task FailAsync(
        string reason, EgressVerificationOptions options, CancellationToken ct, EgressCanaryResult? result = null)
    {
        _ = options;
        Interlocked.Exchange(ref _failed, 1);
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Verified-egress sandbox {SandboxId} on provider kind '{Kind}' failed re-verification and its disposal also failed; the kind is still demoted.",
                Id, _providerKind);
        }
        finally
        {
            Interlocked.Exchange(ref _disposed, 1);
        }
        _gate.RecordFailure(_providerKind);
        await _sink.RecordAsync(
            ToEvent(result, alert: true, reason), ct).ConfigureAwait(false);
        throw new EgressVerificationFailedException(_providerKind, Id, reason);
    }

    private EgressVerificationEvent ToEvent(EgressCanaryResult? result, bool alert, string? reason = null)
    {
        var checks = result?.Checks.Select(static c =>
            new EgressVerificationCheckEvent(c.Name, c.Passed, c.ExitCode, c.Elapsed.TotalMilliseconds)).ToArray()
            ?? [];
        return new EgressVerificationEvent(
            _workItemId, _phase, _providerKind, Id,
            Passed: result?.Passed ?? false,
            FailureReason: result?.FailureReason ?? reason,
            Checks: checks,
            StartedAt: result?.StartedAt ?? _clock.GetUtcNow(),
            FinishedAt: result?.FinishedAt ?? _clock.GetUtcNow(),
            Alert: alert);
    }

    /// <inheritdoc/>
    public Task SyncStateToHostAsync(CancellationToken ct = default) =>
        _inner.SyncStateToHostAsync(ct);

    /// <inheritdoc/>
    public Task KillActiveExecsAsync(CancellationToken ct = default) =>
        _inner.KillActiveExecsAsync(ct);

    /// <inheritdoc/>
    public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default) =>
        _inner.GetScreenshotAsync(ct);

    /// <inheritdoc/>
    public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default) =>
        _inner.SynthesizeInputAsync(events, ct);

    /// <inheritdoc/>
    public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default) =>
        _inner.GetAccessibilityAtPointAsync(x, y, ct);

    /// <inheritdoc/>
    public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default) =>
        _inner.GetAccessibilityTreeJsonAsync(ct);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
