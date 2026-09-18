using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MsLogger = Microsoft.Extensions.Logging.ILogger;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the durable agent-turn checkpoint exec-output mismatch:
/// the checkpoint transfer historically requested ~44 MiB of stdout against the
/// 4 MiB provider-wide Incus CLI bound, so <c>ValidateExec</c> threw
/// <c>ArgumentOutOfRangeException</c> and no checkpoint was ever written.
/// The provider now clamps over-bound requests (naming both values) and the
/// checkpoint path derives its caps from the advertised provider bound.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AgentTurnCheckpointLimitsTests : IDisposable
{
    private static readonly int CheckpointBase64Bytes =
        checked(((AgentTurnScratchpadArchive.MaximumBytes + 2) / 3) * 4 + 16);

    private readonly TestSink _sink = new();

    public AgentTurnCheckpointLimitsTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose() => Log.CloseAndFlush();

    [Fact]
    public async Task ExecAtProviderBound_SucceedsWithoutClampWarning()
    {
        var logger = new CapturingLogger();
        var runner = new CapturingRunner(SuccessfulExecHandler());
        var sandbox = CreateSandbox("codeybox-limit-at-bound", 4096, 4096, runner, logger);
        try
        {
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["true"],
                MaxStdoutBytes = 4096,
                MaxStderrBytes = 4096,
            });

            Assert.True(result.Success, result.Stderr);
            Assert.Equal(4096, runner.WrapperMaxStdoutBytes);
            Assert.Equal(4096, runner.WrapperMaxStderrBytes);
            Assert.Empty(logger.Warnings);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecAboveProviderBound_IsClampedToBoundAndNamesBothValues()
    {
        const int requested = 44_739_260;
        var logger = new CapturingLogger();
        var runner = new CapturingRunner(SuccessfulExecHandler());
        var sandbox = CreateSandbox("codeybox-limit-clamped", 4096, 4096, runner, logger);
        try
        {
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["true"],
                MaxStdoutBytes = requested,
                MaxStderrBytes = requested,
            });

            Assert.True(result.Success, result.Stderr);
            Assert.Equal(4096, runner.WrapperMaxStdoutBytes);
            Assert.Equal(4096, runner.WrapperMaxStderrBytes);
            Assert.Equal(2, logger.Warnings.Count);
            Assert.All(logger.Warnings, warning =>
            {
                Assert.Contains(requested.ToString(System.Globalization.CultureInfo.InvariantCulture), warning, StringComparison.Ordinal);
                Assert.Contains("4096", warning, StringComparison.Ordinal);
            });
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecAboveProviderBound_ObservedCheckpointConfiguration_Succeeds()
    {
        // End-to-end shape of the observed failure: default provider bounds
        // (4 MiB) with the checkpoint transfer's base64 cap (~44 MiB). Before
        // the fix this exact request threw ArgumentOutOfRangeException and no
        // checkpoint could be committed; now the exec proceeds at the bound.
        var logger = new CapturingLogger();
        var runner = new CapturingRunner(SuccessfulExecHandler());
        var options = new IncusSandboxOptions
        {
            CaptureResourceMetrics = false,
            DiskGuard = null,
        };
        var sandbox = CreateSandbox("codeybox-limit-observed", options.MaxCliStdoutBytes, options.MaxCliStderrBytes, runner, logger);
        try
        {
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["true"],
                MaxStdoutBytes = CheckpointBase64Bytes,
                MaxStderrBytes = CheckpointBase64Bytes,
            });

            Assert.True(result.Success, result.Stderr);
            Assert.Equal(options.MaxCliStdoutBytes, runner.WrapperMaxStdoutBytes);
            Assert.Equal(options.MaxCliStderrBytes, runner.WrapperMaxStderrBytes);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public void ResolveExecOutputLimits_DerivesFromAdvertisedProviderBound()
    {
        var (stdout, stderr) = AgentTurnCheckpointLimits.ResolveExecOutputLimits(
            new BoundedFakeSandbox(4096, 2048),
            CheckpointBase64Bytes,
            4096);

        Assert.Equal(4096, stdout);
        Assert.Equal(2048, stderr);
    }

    [Fact]
    public void ResolveExecOutputLimits_PassesThroughDesiredValuesBelowBound()
    {
        var (stdout, stderr) = AgentTurnCheckpointLimits.ResolveExecOutputLimits(
            new BoundedFakeSandbox(4 * 1024 * 1024, 4 * 1024 * 1024),
            1024,
            512);

        Assert.Equal(1024, stdout);
        Assert.Equal(512, stderr);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveExecOutputLimits_PassesThroughWhenNoBoundAdvertised(bool nullSandbox)
    {
        ISandbox? sandbox = nullSandbox ? null : new UnboundedFakeSandbox();

        var (stdout, stderr) = AgentTurnCheckpointLimits.ResolveExecOutputLimits(
            sandbox,
            CheckpointBase64Bytes,
            4096);

        Assert.Equal(CheckpointBase64Bytes, stdout);
        Assert.Equal(4096, stderr);
    }

    [Fact]
    public void AgentTurnCheckpointDegraded_EmitsOperatorVisibleWarning()
    {
        var id = WorkItemId.New();

        AuditLog.AgentTurnCheckpointDegraded(id, "publish", "ArgumentOutOfRangeException: boom");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal(LogEventLevel.Warning, evt.Level);
        Assert.True(GetScalar<bool>(evt, "Audit") is true);
        Assert.Equal("agent_turn.checkpoint_degraded", GetScalar<string>(evt, "EventName"));
        Assert.Contains(id.ToString(), evt.RenderMessage(), StringComparison.Ordinal);
        Assert.Contains("publish", evt.RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReportDegraded_MarksOnceAndPreservesOriginalFailure()
    {
        var id = WorkItemId.New();
        var failure = new InvalidOperationException("original agent failure");

        Assert.True(AgentTurnCheckpointLimits.ShouldReportDegraded(failure));
        AgentTurnCheckpointLimits.ReportDegraded(id, "recoverable-turn", failure, NullLogger.Instance);

        Assert.False(AgentTurnCheckpointLimits.ShouldReportDegraded(failure));
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal("original agent failure", failure.Message);
        Assert.Equal("recoverable-turn", failure.Data[AgentTurnCheckpointLimits.DegradedReportedKey]);
    }

    [Fact]
    public void ReportDegraded_EmitsDegradedMeterWithStage()
    {
        var measurements = new ConcurrentQueue<(string? Outcome, string? Stage)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "CodeyBox.Pipeline"
                && instrument.Name == "codeybox.agent_turn.checkpoints")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? outcome = null;
            string? stage = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                    outcome = tag.Value?.ToString();
                if (tag.Key == "stage")
                    stage = tag.Value?.ToString();
            }
            measurements.Enqueue((outcome, stage));
        });
        listener.Start();

        AgentTurnCheckpointLimits.ReportDegraded(
            WorkItemId.New(), "publish", new InvalidOperationException("nope"), NullLogger.Instance);

        Assert.Contains(measurements, m => m.Outcome == "degraded" && m.Stage == "publish");
    }

    private static T? GetScalar<T>(LogEvent evt, string name)
    {
        if (!evt.Properties.TryGetValue(name, out var property) || property is not ScalarValue scalar)
            return default;
        return scalar.Value is T typed ? typed : default;
    }

    // ── Incus sandbox scaffolding (mirrors IncusSandboxLifecycleTests) ───────

    private static Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessRunResult>> SuccessfulExecHandler()
    {
        var environmentAbsenceChecks = 0;
        return (argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath))
                return Task.FromResult(Success("completed\n"));
            if (IsFileCommand(argv, "pull") && argv.Any(a => a.Contains("/complete-", StringComparison.Ordinal)))
                return Task.FromResult(Success("0\n"));
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (IsFileCommand(argv, "delete"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, "test"))
                return Task.FromResult(++environmentAbsenceChecks == 1 ? Failure() : Success());
            if (argv.Contains("stop", StringComparer.Ordinal))
                return Task.FromResult(Success());
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(Success("[]"));
            throw new InvalidOperationException($"Unexpected test command: {string.Join(' ', argv)}");
        };
    }

    private static IncusSandbox CreateSandbox(
        string sandboxName,
        int maxStdoutBytes,
        int maxStderrBytes,
        CapturingRunner runner,
        MsLogger logger)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-checkpoint-limits-{Guid.NewGuid():N}");
        var sandboxRoot = Path.Combine(root, sandboxName);
        Directory.CreateDirectory(sandboxRoot);
        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, DateTimeOffset.UtcNow);
        var options = new IncusSandboxOptions
        {
            CaptureResourceMetrics = false,
            DiskGuard = null,
            OperationTimeout = TimeSpan.FromSeconds(30),
            ExecTimeout = TimeSpan.FromSeconds(2),
            VmStopTimeout = TimeSpan.FromMilliseconds(100),
            ReadinessPollInterval = TimeSpan.FromMilliseconds(1),
            InterruptedExecRecoveryRetryAttempts = 0,
            MaxCliStdoutBytes = maxStdoutBytes,
            MaxCliStderrBytes = maxStderrBytes,
        };
        var spec = new SandboxSpec { ImageReference = "local-image" };
        var authorization = IncusRecoveryAuthorization.CaptureValidated(
            null,
            [],
            spec.Mounts.Select(static mount => mount.SandboxPath).ToArray(),
            [],
            options);
        var token = $"test-recovery-token-{sandboxName}";
        var lease = new SandboxRecoveryLease(IncusSandboxProvider.ProviderId, sandboxName, token);
        var manifest = IncusRecoveryManifest.Create(
            sandboxName,
            spec,
            options,
            IncusRecoveryManifestCodec.ComputeTokenSha256(token),
            null,
            authorization);
        var store = IncusRecoveryManifestStore.Acquire(sandboxRoot);
        store.Write(manifest, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        return new IncusSandbox(
            sandboxName,
            sandboxRoot,
            root,
            spec,
            options,
            new IncusCliRunner(runner),
            logger,
            timings: null,
            WorkItemId.New(),
            "work",
            baselineRef: null,
            resourceUsageStore: null,
            _ => { },
            authorization,
            lease,
            manifest,
            store,
            newGuid: static () => Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    }

    private static ProcessRunResult Success(string stdout = "") => new(0, stdout, string.Empty);

    private static ProcessRunResult Failure(string stderr = "") => new(1, string.Empty, stderr);

    private static bool IsFileCommand(IReadOnlyList<string> argv, string verb) =>
        argv.Contains("file", StringComparer.Ordinal) && argv.Contains(verb, StringComparer.Ordinal);

    private static bool IsGuestCommand(IReadOnlyList<string> argv, string executable) =>
        argv.Contains("exec", StringComparer.Ordinal) && argv.Contains(executable, StringComparer.Ordinal);

    private sealed class CapturingRunner(
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessRunResult>> handler) : IProcessRunner
    {
        internal int? WrapperMaxStdoutBytes { get; private set; }
        internal int? WrapperMaxStderrBytes { get; private set; }

        public async Task<ProcessRunResult> RunAsync(
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
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath))
            {
                WrapperMaxStdoutBytes = maxStdoutBytes;
                WrapperMaxStderrBytes = maxStderrBytes;
            }
            return await handler(argv, stdin, ct);
        }
    }

    private sealed class CapturingLogger : MsLogger
    {
        internal List<string> Warnings { get; } = [];

        IDisposable? MsLogger.BeginScope<TState>(TState state) => null;

        bool MsLogger.IsEnabled(LogLevel logLevel) => true;

        void MsLogger.Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class BoundedFakeSandbox(int stdoutBound, int stderrBound) : ISandbox, ISandboxExecOutputLimits
    {
        public string Id => "bounded-fake";
        public int MaxStdoutBytes => stdoutBound;
        public int MaxStderrBytes => stderrBound;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnboundedFakeSandbox : ISandbox
    {
        public string Id => "unbounded-fake";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
