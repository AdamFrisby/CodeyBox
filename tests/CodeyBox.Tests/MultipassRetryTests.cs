using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="MultipassRetry"/>: the SSH-not-ready retry
/// loop wrapped around <c>multipass transfer</c> in
/// <c>MultipassSandboxProvider.TransferEnvAsync</c>.
///
/// Background: <c>multipass launch</c> returns once the VM is in Running
/// state, but the in-VM <c>sshd</c> can take a few more seconds to bind.
/// Under audit parallelism the host is heavy and that race fires often;
/// SCP/SFTP-based operations come back with "Connection refused".
/// Retry-on-error is the fix — a blanket sleep would penalise every
/// healthy creation, and lowering parallelism would mask the symptom.
/// These tests exercise the retry-and-backoff helper in isolation, with
/// an injected delay shim so they run instantly.
/// </summary>
public sealed class MultipassRetryTests
{
    private static ProcessRunResult Ok(string stdout = "") => new(0, stdout, "");
    private static ProcessRunResult Refused(string extra = "") =>
        new(1, "", "ssh connection failed: 'Connection refused'" + extra);
    private static ProcessRunResult ResetByPeer() =>
        new(1, "", "ssh connection failed: 'Connection reset by peer'");
    private static ProcessRunResult NotFound() =>
        new(2, "", "instance \"codeybox-xxxx\" does not exist");

    /// <summary>
    /// The transfer fails once with the SSH-not-ready signature, then
    /// succeeds. The helper must retry rather than surface the first error.
    /// </summary>
    [Fact]
    public async Task RunWithRetryAsync_FirstAttemptRefused_SecondAttemptSucceeds_ReturnsSuccess()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        var result = await MultipassRetry.RunWithRetryAsync(
            action: _ => Task.FromResult(++attempts == 1 ? Refused() : Ok("done")),
            log: NullLogger.Instance,
            description: "test",
            ct: CancellationToken.None,
            delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("done", result.Stdout);
        Assert.Equal(2, attempts);
        Assert.Single(delays);
    }

    /// <summary>
    /// The "Connection reset by peer" sibling race is treated the same way:
    /// it's the second face of the same SSH-not-ready bug.
    /// </summary>
    [Fact]
    public async Task RunWithRetryAsync_ConnectionResetByPeer_IsRetried()
    {
        var attempts = 0;

        var result = await MultipassRetry.RunWithRetryAsync(
            action: _ => Task.FromResult(++attempts == 1 ? ResetByPeer() : Ok()),
            log: NullLogger.Instance,
            description: "test",
            ct: CancellationToken.None,
            delay: (_, _) => Task.CompletedTask);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// Persistent SSH refusal across the full retry budget: the helper
    /// surfaces the original failure rather than swallowing it. Audits
    /// must still fail loudly when the VM never becomes reachable.
    /// </summary>
    [Fact]
    public async Task RunWithRetryAsync_AllAttemptsRefused_SurfacesFailureAfterMaxAttempts()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        var result = await MultipassRetry.RunWithRetryAsync(
            action: _ => { attempts++; return Task.FromResult(Refused()); },
            log: NullLogger.Instance,
            description: "test",
            ct: CancellationToken.None,
            maxAttempts: 4,
            delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Connection refused", result.Stderr);
        Assert.Equal(4, attempts);
        // No delay before the first attempt, none after the last → N-1 delays.
        Assert.Equal(3, delays.Count);
    }

    /// <summary>
    /// Non-retryable errors (multipass "instance not found", auth failures,
    /// any other stderr) must fail fast. Burning the retry budget on a
    /// genuinely broken VM wastes 30 seconds and obscures the diagnostic.
    /// </summary>
    [Fact]
    public async Task RunWithRetryAsync_NonRetryableError_FailsFastWithoutRetry()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        var result = await MultipassRetry.RunWithRetryAsync(
            action: _ => { attempts++; return Task.FromResult(NotFound()); },
            log: NullLogger.Instance,
            description: "test",
            ct: CancellationToken.None,
            delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not exist", result.Stderr);
        Assert.Equal(1, attempts);
        Assert.Empty(delays);
    }

    /// <summary>
    /// The happy path — first attempt succeeds — must NOT retry and must
    /// NOT sleep. Confirms we don't tax healthy creations.
    /// </summary>
    [Fact]
    public async Task RunWithRetryAsync_FirstAttemptSucceeds_NoRetryNoDelay()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        var result = await MultipassRetry.RunWithRetryAsync(
            action: _ => { attempts++; return Task.FromResult(Ok("yay")); },
            log: NullLogger.Instance,
            description: "test",
            ct: CancellationToken.None,
            delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("yay", result.Stdout);
        Assert.Equal(1, attempts);
        Assert.Empty(delays);
    }

    /// <summary>
    /// Backoff doubles per attempt, capped at <c>max</c>. With initial=1s
    /// and max=8s, the sequence is 1s, 2s, 4s, 8s, 8s, 8s — never instant,
    /// never infinite, and saturating to the cap.
    /// </summary>
    [Fact]
    public void ComputeBackoff_DoublesUntilCappedAtMax()
    {
        var initial = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(8);

        Assert.Equal(TimeSpan.FromSeconds(1), MultipassRetry.ComputeBackoff(0, initial, max));
        Assert.Equal(TimeSpan.FromSeconds(2), MultipassRetry.ComputeBackoff(1, initial, max));
        Assert.Equal(TimeSpan.FromSeconds(4), MultipassRetry.ComputeBackoff(2, initial, max));
        Assert.Equal(TimeSpan.FromSeconds(8), MultipassRetry.ComputeBackoff(3, initial, max));
        Assert.Equal(TimeSpan.FromSeconds(8), MultipassRetry.ComputeBackoff(4, initial, max));
        Assert.Equal(TimeSpan.FromSeconds(8), MultipassRetry.ComputeBackoff(20, initial, max));
    }

    /// <summary>
    /// The retry loop records the doubling sequence in the order it would
    /// sleep — guards against off-by-one in the backoff index.
    /// </summary>
    [Fact]
    public async Task RunWithRetryAsync_BackoffSequence_IsExponentialAndCapped()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        // Force all attempts to fail with the retryable signature so we observe
        // every delay the helper would sleep in production.
        var result = await MultipassRetry.RunWithRetryAsync(
            action: _ => { attempts++; return Task.FromResult(Refused()); },
            log: NullLogger.Instance,
            description: "test",
            ct: CancellationToken.None,
            maxAttempts: 6,
            backoff: a => MultipassRetry.ComputeBackoff(a, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8)),
            delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(6, attempts);
        Assert.Equal(
            new[]
            {
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(8),
            },
            delays);
    }

    /// <summary>
    /// Cancellation token mid-loop is honoured: the helper does not keep
    /// sleeping after the orchestrator cancels.
    /// </summary>
    [Fact]
    public async Task RunWithRetryAsync_HonoursCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await MultipassRetry.RunWithRetryAsync(
                action: _ =>
                {
                    attempts++;
                    cts.Cancel();
                    return Task.FromResult(Refused());
                },
                log: NullLogger.Instance,
                description: "test",
                ct: cts.Token,
                delay: (_, ctInner) => Task.FromCanceled(ctInner));
        });

        Assert.Equal(1, attempts);
    }

    /// <summary>
    /// Predicate sanity: known retryable strings match (case-insensitive),
    /// unrelated errors don't.
    /// </summary>
    [Theory]
    [InlineData("ssh connection failed: 'Connection refused'", true)]
    [InlineData("CONNECTION REFUSED", true)]
    [InlineData("ssh: connect to host: Connection reset by peer", true)]
    [InlineData("instance \"foo\" does not exist", false)]
    [InlineData("authentication failed", false)]
    [InlineData("", false)]
    public void IsSshNotReady_ClassifiesKnownStderrStrings(string stderr, bool expected)
    {
        Assert.Equal(expected, MultipassRetry.IsSshNotReady(stderr));
    }
}
