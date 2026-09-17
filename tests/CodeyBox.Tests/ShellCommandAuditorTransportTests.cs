using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// An auditor command whose exec channel drops (exit 255 with an
/// abnormal-closure diagnostic) produced no verdict. It must be retried with
/// backoff — never recorded as a code finding — and only surface as
/// infrastructure when the bounded retries are exhausted. A finding title is a
/// short summary; the executed command lives in the description.
/// </summary>
public sealed class ShellCommandAuditorTransportTests
{
    private const string TransportDiagnostic =
        "Error: websocket: close 1006 (abnormal closure): unexpected EOF";

    [Fact]
    public async Task TransportFailure_IsRetried_AndSuccessfulRetryYieldsVerdictWithNoFinding()
    {
        var commandExecs = 0;
        var clock = new RecordingTimeProvider();
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "csharp:test-pass",
            Argv = ["dotnet", "test", "--no-build"],
            TreatExit127AsMissingTool = false,
            TimeProvider = clock,
        });
        var sandbox = new FakeSandbox(exec =>
        {
            if (IsToolProbe(exec))
                return new SandboxExecResult(0, "/usr/bin/dotnet\n", "");
            commandExecs++;
            return commandExecs == 1
                ? new SandboxExecResult(255, "", TransportDiagnostic)
                : new SandboxExecResult(0, "Passed!\n", "");
        });

        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.Equal(2, commandExecs);
        Assert.Single(clock.RequestedDelays);
    }

    [Fact]
    public async Task TransportRetry_BackoffGrowsExponentially_UpToConfiguredCap()
    {
        var clock = new RecordingTimeProvider();
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "csharp:test-pass",
            Argv = ["dotnet", "test", "--no-build"],
            TreatExit127AsMissingTool = false,
            TransportRetryMaxAttempts = 4,
            TransportRetryBaseDelay = TimeSpan.FromSeconds(2),
            TransportRetryMaxDelay = TimeSpan.FromSeconds(5),
            TimeProvider = clock,
        });
        var sandbox = new FakeSandbox(exec =>
            IsToolProbe(exec)
                ? new SandboxExecResult(0, "/usr/bin/dotnet\n", "")
                : new SandboxExecResult(255, "", TransportDiagnostic));

        await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Equal(
            [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5)],
            clock.RequestedDelays);
    }

    [Fact]
    public async Task Exit255_WithoutTransportDiagnostic_IsStillABlockingFinding()
    {
        // 255 is a legal program exit code: without the abnormal-closure
        // diagnostic it must not be treated as a transport failure.
        var commandExecs = 0;
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "custom:test-pass",
            Argv = ["custom-test"],
            TreatExit127AsMissingTool = false,
        });
        var sandbox = new FakeSandbox(exec =>
        {
            if (IsToolProbe(exec))
                return new SandboxExecResult(0, "/usr/bin/custom-test\n", "");
            commandExecs++;
            return new SandboxExecResult(255, "", "Failed! - Failed: 3, Passed: 10, Total: 13");
        });

        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("custom:test-pass", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("command exited 255", finding.Title);
        Assert.Equal(1, commandExecs);
    }

    [Fact]
    public async Task Non255Exit_WithTransportShapedOutput_IsStillABlockingFinding()
    {
        // The diagnostic alone is not enough either: program output can
        // contain transport-shaped text while the command genuinely ran.
        var commandExecs = 0;
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "custom:test-pass",
            Argv = ["custom-test"],
            TreatExit127AsMissingTool = false,
        });
        var sandbox = new FakeSandbox(exec =>
        {
            if (IsToolProbe(exec))
                return new SandboxExecResult(0, "/usr/bin/custom-test\n", "");
            commandExecs++;
            return new SandboxExecResult(1, "", $"test failed; log line mentions {TransportDiagnostic}");
        });

        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("command exited 1", finding.Title);
        Assert.Equal(1, commandExecs);
    }

    [Fact]
    public async Task TransportFailureExhaustion_ThrowsInfrastructureFailure_WithNoFinding()
    {
        const int maxAttempts = 3;
        var commandExecs = 0;
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "csharp:test-pass",
            Argv = ["dotnet", "test", "--no-build"],
            TreatExit127AsMissingTool = false,
            TransportRetryMaxAttempts = maxAttempts,
            TransportRetryBaseDelay = TimeSpan.Zero,
        });
        var sandbox = new FakeSandbox(exec =>
        {
            if (IsToolProbe(exec))
                return new SandboxExecResult(0, "/usr/bin/dotnet\n", "");
            commandExecs++;
            return new SandboxExecResult(255, "", TransportDiagnostic);
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Equal(maxAttempts, commandExecs);
        Assert.False(ex.IsDeterministic);
        Assert.Equal(255, ex.ExitCode);
        Assert.Contains("could not run", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TransportDiagnostic, ex.Output ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindingTitle_StaysBounded_AndOmitsWrappedCommandBody()
    {
        // The production dotnet-test failure shape: the executed argv carries
        // the full multi-line NuGet-home self-heal wrapper (the exact finding
        // that parked the work item as diff-defective).
        var wrapped = NuGetHomeSelfHeal.WrapDotnetInvocation(["dotnet", "test", "--no-build"]);
        var joined = string.Join(' ', wrapped);
        Assert.True(joined.Length > 1000, "test precondition: wrapped argv is long");

        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "csharp:test-pass",
            Argv = ["dotnet", "test", "--no-build"],
            TreatExit127AsMissingTool = false,
            SelfHealNuGetHome = true,
        });
        var sandbox = new FakeSandbox(exec =>
            IsToolProbe(exec)
                ? new SandboxExecResult(0, "/usr/bin/dotnet\n", "")
                : new SandboxExecResult(1, "", "some ordinary failure output"));

        var audit = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(audit.Findings);
        Assert.Equal("command exited 1", finding.Title);
        Assert.DoesNotContain("nuget_home_broken", finding.Title, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', finding.Title);
        Assert.True(finding.Title.Length <= 64, $"title must stay bounded, was {finding.Title.Length} chars");
        Assert.Contains("dotnet test --no-build", finding.Description, StringComparison.Ordinal);
        Assert.Contains("nuget_home_broken", finding.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(255, "Error: websocket: close 1006 (abnormal closure): unexpected EOF", true)]
    [InlineData(255, "Error: websocket: close 1006 (abnormal closure): unexpected EOF\nmore output", true)]
    [InlineData(255, "WEBSOCKET: CLOSE 1006 (ABNORMAL CLOSURE): UNEXPECTED EOF", true)]
    [InlineData(255, "connection reset: abnormal closure of the channel", true)]
    [InlineData(255, "read: unexpected EOF", true)]
    [InlineData(255, "Failed! - Failed: 3, Passed: 10, Total: 13", false)]
    [InlineData(255, "", false)]
    [InlineData(1, "Error: websocket: close 1006 (abnormal closure): unexpected EOF", false)]
    [InlineData(0, "Error: websocket: close 1006 (abnormal closure): unexpected EOF", false)]
    public void IsTransportFailure_RequiresExit255AndDiagnostic(int exitCode, string stderr, bool expected)
    {
        Assert.Equal(expected, ExecTransportFailure.IsTransportFailure(new SandboxExecResult(exitCode, "", stderr)));
    }

    private static AuditContext FakeContext() =>
        new(WorkItemId.New(), "feature", "main", 1, "do x");

    private static bool IsToolProbe(SandboxExec exec) =>
        exec.Argv.Count >= 3 &&
        exec.Argv[0] == "sh" &&
        exec.Argv[1] == "-c" &&
        exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private sealed class FakeSandbox(Func<SandboxExec, SandboxExecResult> onExec) : ISandbox
    {
        private readonly Func<SandboxExec, SandboxExecResult> _onExec = onExec;
        public string Id => "fake";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(_onExec(exec));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingTimeProvider : TimeProvider
    {
        public List<TimeSpan> RequestedDelays { get; } = [];
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
                RequestedDelays.Add(dueTime);
            callback(state);
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            public void Dispose() { }
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        }
    }
}
