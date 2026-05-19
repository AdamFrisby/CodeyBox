using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Phase-scoped cancellation primitive that records which contributor first
/// cancelled the underlying <see cref="CancellationTokenSource"/>. Wraps an
/// <see cref="OperationCanceledException"/> thrown inside the phase as a
/// <see cref="PhaseCancellationException"/> carrying the phase name and the
/// best-attributed source.
///
/// <para>
/// Replaces the old pattern of
/// <c>using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);</c>
/// followed by a generic <c>catch (OperationCanceledException)</c> that mapped
/// every cancellation flavour to <c>failureKind=timeout</c>, even when the
/// configured timeout hadn't actually elapsed. The new shape lets the outer
/// catch tell apart "configured timeout fired" from "transient host-side
/// cancellation we couldn't attribute".
/// </para>
///
/// <para>
/// Source attribution is best-effort and racy by design — only the FIRST
/// contributor to fire wins (CAS into <see cref="_source"/>). Subsequent
/// callbacks are no-ops. If no contributor records a source before the
/// linked CTS cancels (e.g. a leaked external token cancels the parent
/// without going through one of our registered hooks), <see cref="Source"/>
/// returns null and the outer catch falls back to
/// <see cref="CancellationSources.Unknown"/>.
/// </para>
/// </summary>
internal sealed class PhaseCancellation : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly List<IDisposable> _disposables = new();
    private string? _source;
    private bool _disposed;

    public string Phase { get; }
    public CancellationToken Token => _cts.Token;

    /// <summary>The contributor that first recorded itself, or null if none.</summary>
    public string? Source => Volatile.Read(ref _source);

    public PhaseCancellation(string phase, CancellationToken parentCt)
    {
        Phase = phase;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        if (parentCt.CanBeCanceled)
        {
            _disposables.Add(parentCt.Register(static state =>
                ((PhaseCancellation)state!).TryRecordSource(CancellationSources.Operator),
                this));
        }
    }

    /// <summary>
    /// Configures a wall-clock timeout for this phase. When the timeout fires
    /// the source is recorded as <c>"timeout:{Phase}"</c> before the linked
    /// CTS is cancelled, so the outer catch can attribute the cancellation
    /// to the configured limit rather than a transient host cancellation.
    /// </summary>
    public void SetPhaseTimeout(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero)
            return;
        var timeoutCts = new CancellationTokenSource(timeout);
        _disposables.Add(timeoutCts);
        _disposables.Add(timeoutCts.Token.Register(static state =>
        {
            var self = (PhaseCancellation)state!;
            self.TryRecordSource(CancellationSources.PhaseTimeout(self.Phase));
            try { self._cts.Cancel(); }
            catch (ObjectDisposedException) { /* phase already disposed; nothing to do */ }
        }, this));
    }

    /// <summary>
    /// Registers a host-shutdown hook. When the host begins stopping, the
    /// linked CTS is given a grace window (typically 60 s, matching the agent
    /// preempt + checkpoint drain time) before being force-cancelled. The
    /// source is recorded as <see cref="CancellationSources.HostShutdown"/>
    /// immediately, and as <see cref="CancellationSources.HostShutdownDeadline"/>
    /// only if the grace window elapses without the phase draining — distinct
    /// so the auto-retry path can decide whether to leave the item mid-flight
    /// (HostShutdown → recovery loop owns it) or treat the grace expiry as a
    /// transient retry candidate.
    /// </summary>
    public void HookHostShutdown(CancellationToken hostShutdownToken, TimeSpan grace)
    {
        if (!hostShutdownToken.CanBeCanceled)
            return;
        _disposables.Add(hostShutdownToken.Register(state =>
        {
            var self = (PhaseCancellation)state!;
            self.TryRecordSource(CancellationSources.HostShutdown);
            try { self._cts.CancelAfter(grace); }
            catch (ObjectDisposedException) { return; }

            if (grace <= TimeSpan.Zero) return;
            // A second token races the grace window: if the grace elapses
            // before the phase has finished draining, we upgrade the source
            // to HostShutdownDeadline so the operator can tell apart "host
            // asked us to stop and we did" from "host asked us to stop and
            // we ran past the grace". CompareExchange ignores the upgrade
            // if any other contributor already won the race.
            var deadlineCts = new CancellationTokenSource(grace);
            self._disposables.Add(deadlineCts);
            self._disposables.Add(deadlineCts.Token.Register(static s =>
            {
                var inner = (PhaseCancellation)s!;
                // Upgrade only if the previously-recorded source was HostShutdown
                // itself — otherwise we'd overwrite e.g. a stuck-probe source
                // that won the original race.
                Interlocked.CompareExchange(
                    ref inner._source,
                    CancellationSources.HostShutdownDeadline,
                    CancellationSources.HostShutdown);
            }, self));
        }, this));
    }

    /// <summary>
    /// Records that the stuck probe cancelled this phase. Called by
    /// <c>RunWithStuckProbeAsync</c> immediately before
    /// <see cref="CancellationTokenSource.CancelAsync"/> fires, so the source
    /// is set before any awaiter observes the cancellation.
    /// </summary>
    public void RecordStuckProbe() =>
        TryRecordSource(CancellationSources.StuckProbe);

    /// <summary>
    /// Translates an <see cref="OperationCanceledException"/> caught inside a
    /// phase into a <see cref="PhaseCancellationException"/> tagged with the
    /// phase and best-attributed source. Returns the original exception if it
    /// is already a <see cref="PhaseCancellationException"/> (avoids double
    /// wrapping when phases nest, e.g. audit-loop → rework).
    /// </summary>
    public PhaseCancellationException Wrap(OperationCanceledException inner)
    {
        if (inner is PhaseCancellationException already)
            return already;
        return new PhaseCancellationException(Phase, Source ?? CancellationSources.Unknown, inner);
    }

    /// <summary>
    /// Helper: runs <paramref name="work"/> with this phase's token and rethrows
    /// any <see cref="OperationCanceledException"/> as a
    /// <see cref="PhaseCancellationException"/> tagged with phase + source.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work)
    {
        try
        {
            return await work(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            throw Wrap(oce);
        }
    }

    /// <summary>Non-generic overload of <see cref="RunAsync{T}"/>.</summary>
    public async Task RunAsync(Func<CancellationToken, Task> work)
    {
        try
        {
            await work(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            throw Wrap(oce);
        }
    }

    /// <summary>
    /// Underlying linked CTS — exposed for callers that need to drive
    /// cancellation explicitly (e.g. the stuck-probe wrapper that calls
    /// <see cref="CancellationTokenSource.Cancel"/> from a background loop).
    /// Pair every direct cancel with a <see cref="RecordStuckProbe"/> /
    /// equivalent source call so attribution is preserved.
    /// </summary>
    internal CancellationTokenSource Cts => _cts;

    /// <summary>
    /// Logs a structured cancel-boundary event so post-incident triage can see
    /// every contributor at every catch site, not just the one that won the
    /// race. Called by the outer pipeline catch handlers when an OCE / a
    /// <see cref="PhaseCancellationException"/> bubbles past a phase boundary.
    /// </summary>
    public static void LogBoundary(
        ILogger log,
        string boundary,
        string phase,
        string source,
        bool operatorRequested,
        bool hostShutdown,
        Exception? exception)
    {
        log.LogWarning(
            exception,
            "Cancellation observed at boundary {Boundary}: phase={Phase} source={CancellationSource} operatorCancelled={OperatorRequested} hostShutdown={HostShutdown}",
            boundary, phase, source, operatorRequested, hostShutdown);
    }

    private void TryRecordSource(string source) =>
        Interlocked.CompareExchange(ref _source, source, null);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Dispose registrations and timers first so callbacks queued for the
        // already-cancelled token don't race with the underlying CTS' own
        // teardown. Best-effort: ObjectDisposedException on a stale callback
        // is harmless.
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { /* tear-down race; ignore */ }
        }
        _disposables.Clear();
        _cts.Dispose();
    }
}

/// <summary>
/// Cancellation tagged with the phase name and the contributor that first
/// cancelled the underlying token. The outer pipeline catch reads
/// <see cref="Phase"/> and <see cref="Source"/> to decide between:
/// <list type="bullet">
///   <item><description>operator-cancel path (transition to Cancelled)</description></item>
///   <item><description>host-shutdown path (leave mid-flight for recovery)</description></item>
///   <item><description>configured timeout (Failed with failureKind="timeout")</description></item>
///   <item><description>unknown source (transient — try auto-retry, then Failed with failureKind="cancelled")</description></item>
/// </list>
///
/// <para>
/// Inherits from <see cref="OperationCanceledException"/> so callers that
/// don't care about attribution (e.g. the agent runner's own
/// <c>catch (OperationCanceledException)</c>) continue to work unchanged.
/// More specific <c>catch (PhaseCancellationException)</c> blocks in the
/// pipeline runner pick this up first.
/// </para>
/// </summary>
public sealed class PhaseCancellationException : OperationCanceledException
{
    public string Phase { get; }

    /// <summary>
    /// Best-attributed cancellation source — one of the constants on
    /// <see cref="CancellationSources"/>. Named to disambiguate from the
    /// inherited <see cref="Exception.Source"/> property (which carries the
    /// originating app/object name).
    /// </summary>
    public new string Source { get; }

    public PhaseCancellationException(string phase, string source, Exception inner)
        : base(BuildMessage(phase, source, inner), inner, ResolveToken(inner))
    {
        Phase = phase;
        Source = source;
    }

    private static string BuildMessage(string phase, string source, Exception inner)
    {
        // Keep the inner message visible so existing log-aggregation regexes
        // matching "A task was canceled." still hit, but prefix with the
        // structured attribution so the new operator triage path is obvious.
        var innerMsg = inner.Message;
        return string.IsNullOrEmpty(innerMsg)
            ? $"phase '{phase}' cancelled (source={source})"
            : $"phase '{phase}' cancelled (source={source}): {innerMsg}";
    }

    private static CancellationToken ResolveToken(Exception inner) =>
        inner is OperationCanceledException oce ? oce.CancellationToken : CancellationToken.None;
}
