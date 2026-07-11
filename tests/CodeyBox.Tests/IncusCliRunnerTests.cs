using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Incus;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

public sealed class IncusCliRunnerTests
{
    private static readonly IncusSandboxOptions Options = new()
    {
        OperationTimeout = TimeSpan.FromSeconds(1),
        MaxConcurrentOperations = 1,
    };

    [Fact]
    public async Task RunCheckedAsync_ReturnsSuccessfulProcessResult()
    {
        var processResult = new ProcessRunResult(0, "ready\n", "");
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromResult(processResult)));

        var actual = await runner.RunCheckedAsync(
            "probe",
            Options,
            ["incus", "version"],
            stdin: null,
            timeout: null,
            CancellationToken.None);

        Assert.Equal(processResult, actual);
    }

    [Fact]
    public async Task RunCheckedAsync_SuppliesConfiguredOutputBoundsToProcessBoundary()
    {
        var process = new CapturingProcessRunner();
        var options = Options with
        {
            MaxCliStdoutBytes = 12_345,
            MaxCliStderrBytes = 6_789,
        };
        var runner = new IncusCliRunner(process);

        await runner.RunCheckedAsync(
            "probe",
            options,
            ["incus", "version"],
            stdin: null,
            timeout: null,
            CancellationToken.None);

        Assert.Equal(options.MaxCliStdoutBytes, process.MaxStdoutBytes);
        Assert.Equal(options.MaxCliStderrBytes, process.MaxStderrBytes);
        Assert.True(process.KillOnOutputLimit);
    }

    [Theory]
    [InlineData(null, 1024)]
    [InlineData(1024, null)]
    public async Task IncusProcessBoundary_RejectsMissingOutputBound(
        int? maxStdoutBytes,
        int? maxStderrBytes)
    {
        var runner = new IncusCliProcessRunner();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            ["/bin/true"],
            stdin: null,
            CancellationToken.None,
            maxStdoutBytes: maxStdoutBytes,
            maxStderrBytes: maxStderrBytes));

        Assert.Contains("requires explicit stdout and stderr limits", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncusProcessBoundary_RereadsAndValidatesCleanupPolicyPerInvocation()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var options = new IncusSandboxOptions();
        var runner = new IncusCliProcessRunner(() => options);

        var first = await runner.RunAsync(
            ["/bin/true"],
            stdin: null,
            CancellationToken.None,
            maxStdoutBytes: 1024,
            maxStderrBytes: 1024);
        options = options with { CliProcessCleanupTimeout = TimeSpan.Zero };

        Assert.Equal(0, first.ExitCode);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await runner.RunAsync(
                ["/bin/true"],
                stdin: null,
                CancellationToken.None,
                maxStdoutBytes: 1024,
                maxStderrBytes: 1024));
    }

    [Fact]
    public async Task RunCheckedAsync_MapsNonZeroExitToContextualBoundedError()
    {
        var longError = "daemon failed\r\n" + new string('x', 5000);
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(17, "ignored", longError))));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunCheckedAsync(
                "copy baseline",
                Options,
                ["incus", "copy", "source", "target"],
                stdin: null,
                timeout: null,
                CancellationToken.None));

        Assert.StartsWith(
            "Incus copy baseline failed with exit code 17: daemon failed  ",
            exception.Message,
            StringComparison.Ordinal);
        Assert.EndsWith("...", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', exception.Message);
        Assert.DoesNotContain('\n', exception.Message);
        Assert.True(exception.Message.Length < 4200, "diagnostic must remain bounded");
    }

    [Fact]
    public async Task RunCheckedAsync_MapsDeadlineCancellationToTimeout()
    {
        var time = new ControllableTimeProvider();
        var processStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new IncusCliRunner(new StubProcessRunner(async (_, _, ct) =>
        {
            processStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ProcessRunResult(0, "", "");
        }), time);

        var operation = runner.RunCheckedAsync(
            "start",
            Options,
            ["incus", "start", "codeybox-test"],
            stdin: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);
        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromSeconds(30));
        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            operation.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("Incus start exceeded its", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task RunCheckedAsync_PreservesCallerCancellation()
    {
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, ct) =>
            Task.FromCanceled<ProcessRunResult>(ct)));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var exception = await Record.ExceptionAsync(() =>
            runner.RunCheckedAsync(
                "list",
                Options,
                ["incus", "list"],
                stdin: null,
                timeout: TimeSpan.FromMinutes(1),
                canceled.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.IsNotType<TimeoutException>(exception);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RunCheckedAsync_RejectsOutputLimitOverflow(
        bool stdoutLimitExceeded,
        bool stderrLimitExceeded)
    {
        var result = new ProcessRunResult(
            0,
            "bounded stdout",
            "bounded stderr",
            stdoutLimitExceeded,
            stderrLimitExceeded);
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) => Task.FromResult(result)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunCheckedAsync(
                "list",
                Options,
                ["incus", "list"],
                stdin: null,
                timeout: null,
                CancellationToken.None));

        Assert.Contains("exceeded its configured output bound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCheckedAsync_PreservesUnexpectedRunnerFailureAsCause()
    {
        var cause = new IOException("process table unavailable");
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromException<ProcessRunResult>(cause)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunCheckedAsync(
                "version probe",
                Options,
                ["incus", "version"],
                stdin: null,
                timeout: null,
                CancellationToken.None));

        Assert.Same(cause, exception.InnerException);
        Assert.Equal("Incus version probe could not be executed.", exception.Message);
    }

    [Fact]
    public async Task RunAllowFailureAsync_PreservesUnexpectedRunnerFailureAsCause()
    {
        var cause = new IOException("process table unavailable");
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromException<ProcessRunResult>(cause)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAllowFailureAsync(
                Options,
                ["incus", "list"],
                stdin: null,
                timeout: null,
                CancellationToken.None));

        Assert.Same(cause, exception.InnerException);
        Assert.Equal("Incus CLI operation could not be executed.", exception.Message);
    }

    private sealed class StubProcessRunner(
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessRunResult>> handler)
        : IProcessRunner
    {
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
            => handler(argv, stdin, ct);
    }

    private sealed class CapturingProcessRunner : IProcessRunner
    {
        internal int? MaxStdoutBytes { get; private set; }
        internal int? MaxStderrBytes { get; private set; }
        internal bool KillOnOutputLimit { get; private set; }

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
            MaxStdoutBytes = maxStdoutBytes;
            MaxStderrBytes = maxStderrBytes;
            KillOnOutputLimit = killOnOutputLimit;
            return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
        }
    }
}
