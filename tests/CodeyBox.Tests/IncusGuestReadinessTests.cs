using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Incus;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

public sealed class IncusGuestReadinessTests
{
    private const string InstanceName = "codeybox-readiness01";

    private static IncusSandboxOptions ReadinessOptions() => new()
    {
        VmStartTimeout = TimeSpan.FromSeconds(5),
        ReadinessPollInterval = TimeSpan.FromSeconds(1),
        MaxReadinessPollInterval = TimeSpan.FromSeconds(4),
        OperationTimeout = TimeSpan.FromSeconds(1),
        DiskGuard = null,
    };

    [Fact]
    public async Task StartAndWaitForAgent_ThrowsTransientTimeout_WhenGuestAgentNeverReady()
    {
        var time = new ControllableTimeProvider();
        var runner = new ProbeStubRunner(guestAgentReadyAfterProbes: int.MaxValue);
        var options = ReadinessOptions();
        var authorized = false;

        var wait = IncusGuestLifecycle.StartAndWaitForAgentAsync(
            new IncusCliRunner(runner, time),
            options,
            InstanceName,
            time,
            _ =>
            {
                authorized = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);
        await DriveClockAsync(time, wait, TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<IncusTransientTimeoutException>(() => wait);
        Assert.Equal("guest-agent readiness", exception.Operation);
        Assert.Contains(
            "did not expose its guest agent within 5 seconds",
            exception.Message,
            StringComparison.Ordinal);
        Assert.True(authorized, "the start must be authorized before waiting for the guest agent");
        Assert.Equal(1, runner.StartCount);
    }

    [Fact]
    public async Task StartAndWaitForAgent_Returns_WhenGuestAgentBecomesReady()
    {
        var time = new ControllableTimeProvider();
        var runner = new ProbeStubRunner(guestAgentReadyAfterProbes: 3);
        var options = ReadinessOptions();

        var wait = IncusGuestLifecycle.StartAndWaitForAgentAsync(
            new IncusCliRunner(runner, time),
            options,
            InstanceName,
            time,
            _ => Task.CompletedTask,
            CancellationToken.None);
        await DriveClockAsync(time, wait, TimeSpan.FromSeconds(1));

        await wait; // must not throw
        Assert.Equal(1, runner.StartCount);
        Assert.Equal(3, runner.ProbeCount);
    }

    [Theory]
    [InlineData(1, 4, 2)]
    [InlineData(2, 4, 4)]
    [InlineData(4, 4, 4)]
    [InlineData(3, 4, 4)]
    [InlineData(5, 4, 4)]
    public void NextReadinessPollInterval_DoublesUpToCap(int currentSeconds, int maxSeconds, int expectedSeconds)
    {
        var next = IncusGuestLifecycle.NextReadinessPollInterval(
            TimeSpan.FromSeconds(currentSeconds),
            TimeSpan.FromSeconds(maxSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), next);
    }

    [Fact]
    public void TryBuildTransientProvisioningDeferral_ConvertsTransientTimeout()
    {
        var options = ReadinessOptions() with { ProvisioningRetryRecheckIn = TimeSpan.FromSeconds(42) };
        var transient = new IncusTransientTimeoutException(
            "guest-agent readiness",
            "Incus VM 'codeybox-x' did not expose its guest agent within 5 seconds.");

        var deferral = IncusSandboxProvider.TryBuildTransientProvisioningDeferral(transient, options);

        Assert.NotNull(deferral);
        Assert.Equal(IncusSandboxProvider.ProviderId, deferral!.Provider);
        Assert.Equal("guest-agent readiness", deferral.Operation);
        Assert.Equal("incus-liveness-timeout", deferral.ErrorClass);
        Assert.Equal(TimeSpan.FromSeconds(42), deferral.RecheckIn);
        Assert.Same(transient, deferral.InnerException);
    }

    [Fact]
    public void TryBuildTransientProvisioningDeferral_IgnoresNonTransientFailures()
    {
        var options = ReadinessOptions();

        Assert.Null(IncusSandboxProvider.TryBuildTransientProvisioningDeferral(
            new InvalidOperationException("Incus start VM failed with exit code 1: boom"),
            options));
        Assert.Null(IncusSandboxProvider.TryBuildTransientProvisioningDeferral(
            new TimeoutException("some generic non-incus timeout"),
            options));
    }

    // Advances the injected clock in bounded turns, yielding between turns so
    // the continuations released by the fake timer can run. The tiny real
    // delay only pumps the scheduler; it never drives the timeout outcome,
    // which is decided entirely by the fake clock crossing VmStartTimeout.
    private static async Task DriveClockAsync(
        ControllableTimeProvider time,
        Task target,
        TimeSpan step)
    {
        const int maximumTurns = 200;
        for (var turn = 0; turn < maximumTurns && !target.IsCompleted; turn++)
        {
            time.Advance(step);
            await Task.Delay(TimeSpan.FromMilliseconds(1));
        }

        Assert.True(target.IsCompleted, "the readiness wait did not settle after advancing the injected clock.");
    }

    private sealed class ProbeStubRunner(int guestAgentReadyAfterProbes) : IProcessRunner
    {
        internal int StartCount { get; private set; }
        internal int ProbeCount { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true)
        {
            ct.ThrowIfCancellationRequested();
            var isProbe = argv.Contains("exec", StringComparer.Ordinal)
                && argv.Contains("/bin/true", StringComparer.Ordinal);
            if (isProbe)
            {
                ProbeCount++;
                var ready = ProbeCount >= guestAgentReadyAfterProbes;
                return Task.FromResult(new ProcessRunResult(ready ? 0 : 1, string.Empty, string.Empty));
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                StartCount++;
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            }
            throw new InvalidOperationException($"Unexpected incus invocation: {string.Join(' ', argv)}");
        }
    }
}
