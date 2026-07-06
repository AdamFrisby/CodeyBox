using System.ComponentModel;
using CodeyBox.Sandbox.MultipassRemote;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for <see cref="OpenSshCliTransport.StartWithTextBusyRetry(Func{bool}, Action{int})"/>,
/// the bounded retry that absorbs transient ETXTBSY ("Text file busy") failures
/// when the orchestrator fork/execs the ssh client under heavy concurrency.
///
/// The retry is exercised through the internal test-seam overload with an
/// injected start delegate and a recording (non-sleeping) callback so the three
/// branches — retry-then-succeed, exhaustion after the attempt cap, and
/// immediate propagation of a non-ETXTBSY <see cref="Win32Exception"/> — are
/// covered without depending on real fork/exec timing.
/// </summary>
public sealed class OpenSshCliTransportTextBusyRetryTests
{
    private const int ETXTBSY = 26;

    [Fact]
    public void RetriesTransientTextBusy_ThenSucceeds()
    {
        var attempts = 0;
        var sleeps = new List<int>();

        var result = OpenSshCliTransport.StartWithTextBusyRetry(
            start: () =>
            {
                attempts++;
                // Fail the first two exec attempts with ETXTBSY, then succeed.
                if (attempts < 3)
                    throw new Win32Exception(ETXTBSY);
                return true;
            },
            onBusyRetry: attempt => sleeps.Add(attempt));

        Assert.True(result);
        Assert.Equal(3, attempts);
        // One backoff per failed attempt, and the callback receives the
        // 1-based attempt index each time.
        Assert.Equal(new[] { 1, 2 }, sleeps);
    }

    [Fact]
    public void ReturnsStartResult_WhenNoTextBusy()
    {
        var sleeps = new List<int>();

        // Process.Start() returns false when it reuses an existing process; the
        // retry wrapper must faithfully return whatever the start delegate does.
        var result = OpenSshCliTransport.StartWithTextBusyRetry(
            start: () => false,
            onBusyRetry: attempt => sleeps.Add(attempt));

        Assert.False(result);
        Assert.Empty(sleeps);
    }

    [Fact]
    public void ExhaustsRetries_ThenRethrowsTextBusy()
    {
        var attempts = 0;
        var sleeps = new List<int>();

        // Always busy: the loop must give up after the cap and surface the
        // ETXTBSY failure rather than spinning forever.
        var ex = Assert.Throws<Win32Exception>(() =>
            OpenSshCliTransport.StartWithTextBusyRetry(
                start: () =>
                {
                    attempts++;
                    throw new Win32Exception(ETXTBSY);
                },
                onBusyRetry: attempt => sleeps.Add(attempt)));

        Assert.Equal(ETXTBSY, ex.NativeErrorCode);
        // The final (cap-th) attempt throws out of the loop, so there is exactly
        // one fewer backoff than there were start attempts.
        Assert.Equal(attempts - 1, sleeps.Count);
        // Bounded: the cap keeps the spin tight (documented ~200ms budget).
        Assert.InRange(attempts, 2, 64);
    }

    [Fact]
    public void PropagatesNonTextBusyWin32Exception_Immediately()
    {
        var attempts = 0;
        var sleeps = new List<int>();

        // A different errno (e.g. ENOENT) is a genuine start failure and must
        // not be retried — it propagates on the first attempt.
        var ex = Assert.Throws<Win32Exception>(() =>
            OpenSshCliTransport.StartWithTextBusyRetry(
                start: () =>
                {
                    attempts++;
                    throw new Win32Exception(2); // ENOENT
                },
                onBusyRetry: attempt => sleeps.Add(attempt)));

        Assert.Equal(2, ex.NativeErrorCode);
        Assert.Equal(1, attempts);
        Assert.Empty(sleeps);
    }
}
