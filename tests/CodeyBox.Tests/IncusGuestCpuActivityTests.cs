using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Incus;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Pure-function coverage for <see cref="IncusCpuActivityEvaluator"/>, the
/// <see cref="IncusStateParser"/> payload handling, and the input guards on
/// <see cref="DefaultIncusInstanceStateReader"/>.
/// </summary>
public sealed class IncusGuestCpuActivityTests
{
    private const double ThresholdPercent = 5.0;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private static IncusGuestCpuSample Sample(long cpuNanoseconds, long timestampSeconds = 0) =>
        new(cpuNanoseconds, DateTimeOffset.UnixEpoch.AddSeconds(timestampSeconds));

    [Fact]
    public void Evaluate_FirstSample_IsInactiveBaseline()
    {
        var evaluation = IncusCpuActivityEvaluator.Evaluate(
            previousSample: null,
            Sample(1_000_000_000),
            Interval,
            ThresholdPercent);

        Assert.False(evaluation.IsActive);
        Assert.Equal(0.0, evaluation.CpuFraction);
    }

    [Fact]
    public void Evaluate_BusyDelta_IsActiveAndReportsFraction()
    {
        // 4 CPU-seconds consumed over a 10-second interval = 0.4 cores = 40%.
        var evaluation = IncusCpuActivityEvaluator.Evaluate(
            Sample(1_000_000_000, timestampSeconds: 100),
            Sample(5_000_000_000, timestampSeconds: 110),
            Interval,
            ThresholdPercent);

        Assert.True(evaluation.IsActive);
        Assert.Equal(0.4, evaluation.CpuFraction, precision: 6);
    }

    [Fact]
    public void Evaluate_IdleDelta_IsInactive()
    {
        // ~8.8 ms of CPU over 10 s — the observed near-idle VM rate.
        var evaluation = IncusCpuActivityEvaluator.Evaluate(
            Sample(1_000_000_000, timestampSeconds: 100),
            Sample(1_008_800_000, timestampSeconds: 110),
            Interval,
            ThresholdPercent);

        Assert.False(evaluation.IsActive);
        Assert.Equal(0.00088, evaluation.CpuFraction, precision: 6);
    }

    [Fact]
    public void Evaluate_ExactlyAtThreshold_IsActive()
    {
        // 0.5 CPU-seconds over 10 s = exactly 5% of one core.
        var evaluation = IncusCpuActivityEvaluator.Evaluate(
            Sample(1_000_000_000),
            Sample(1_500_000_000),
            Interval,
            ThresholdPercent);

        Assert.True(evaluation.IsActive);
    }

    [Fact]
    public void Evaluate_JustBelowThreshold_IsInactive()
    {
        var evaluation = IncusCpuActivityEvaluator.Evaluate(
            Sample(1_000_000_000),
            Sample(1_499_999_999),
            Interval,
            ThresholdPercent);

        Assert.False(evaluation.IsActive);
    }

    [Fact]
    public void Evaluate_CounterResetOrRegression_IsInactive()
    {
        // A VM restart resets cpu.usage; the regression must not look like a
        // giant positive delta (and must not wrap to a bogus fraction).
        var evaluation = IncusCpuActivityEvaluator.Evaluate(
            Sample(50_000_000_000),
            Sample(1_000_000),
            Interval,
            ThresholdPercent);

        Assert.False(evaluation.IsActive);
        Assert.Equal(0.0, evaluation.CpuFraction);
    }

    [Fact]
    public void Evaluate_UnchangedCounter_IsInactive()
    {
        var evaluation = IncusCpuActivityEvaluator.Evaluate(
            Sample(7_000_000_000),
            Sample(7_000_000_000),
            Interval,
            ThresholdPercent);

        Assert.False(evaluation.IsActive);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Evaluate_NonPositiveElapsed_IsInactive(long elapsedTicks)
    {
        var evaluation = IncusCpuActivityEvaluator.Evaluate(
            Sample(1_000_000_000),
            Sample(9_000_000_000),
            TimeSpan.FromTicks(elapsedTicks),
            ThresholdPercent);

        Assert.False(evaluation.IsActive);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Evaluate_InvalidThreshold_IsInactive(double thresholdPercent)
    {
        var evaluation = IncusCpuActivityEvaluator.Evaluate(
            Sample(1_000_000_000),
            Sample(9_000_000_000),
            Interval,
            thresholdPercent);

        Assert.False(evaluation.IsActive);
    }

    [Theory]
    [InlineData(-5, 1_000_000_000)]
    [InlineData(1_000_000_000, -5)]
    public void Evaluate_NegativeCounter_IsInactive(long previousNs, long currentNs)
    {
        var evaluation = IncusCpuActivityEvaluator.Evaluate(
            Sample(previousNs),
            Sample(currentNs),
            Interval,
            ThresholdPercent);

        Assert.False(evaluation.IsActive);
    }

    [Fact]
    public void IsActive_MatchesEvaluateDecision()
    {
        Assert.True(IncusCpuActivityEvaluator.IsActive(
            Sample(1_000_000_000), Sample(5_000_000_000), Interval, ThresholdPercent));
        Assert.False(IncusCpuActivityEvaluator.IsActive(
            Sample(1_000_000_000), Sample(1_001_000_000), Interval, ThresholdPercent));
    }

    [Fact]
    public void TryParseGuestCpuUsage_MetadataWrappedPayload_ReadsUsage()
    {
        const string json = """
            {"metadata": {"status": "Running", "cpu": {"usage": 123456789}}}
            """;

        Assert.True(IncusStateParser.TryParseGuestCpuUsage(json, out var usage));
        Assert.Equal(123456789, usage);
    }

    [Fact]
    public void TryParseGuestCpuUsage_DirectPayload_ReadsUsage()
    {
        Assert.True(IncusStateParser.TryParseGuestCpuUsage(
            """{"status": "Running", "cpu": {"usage": 42}}""", out var usage));
        Assert.Equal(42, usage);
    }

    [Fact]
    public void TryParseGuestCpuUsage_NonRunningInstance_ReturnsFalse()
    {
        Assert.False(IncusStateParser.TryParseGuestCpuUsage(
            """{"status": "Stopped", "cpu": {"usage": 42}}""", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"cpu": {"usage": -5}}""")]
    [InlineData("""{"cpu": {}}""")]
    public void TryParseGuestCpuUsage_UnusablePayload_ReturnsFalse(string? json)
    {
        Assert.False(IncusStateParser.TryParseGuestCpuUsage(json, out var usage));
        Assert.Equal(0, usage);
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData("bad?name")]
    [InlineData("bad%2fname")]
    [InlineData("bad name")]
    public async Task ReadStateAsync_InvalidInstanceName_ThrowsBeforeQuerying(string instanceName)
    {
        var runner = new NeverInvokedProcessRunner();
        var reader = new DefaultIncusInstanceStateReader(
            new IncusCliRunner(runner), TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadStateAsync(new IncusSandboxOptions(), instanceName, CancellationToken.None));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task ReadStateAsync_InvalidProjectIdentity_ThrowsBeforeQuerying()
    {
        var runner = new NeverInvokedProcessRunner();
        var reader = new DefaultIncusInstanceStateReader(
            new IncusCliRunner(runner), TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadStateAsync(
                new IncusSandboxOptions { ProjectName = "bad/project" },
                "codeybox-vm",
                CancellationToken.None));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task ReadStateAsync_RunningInstance_ReturnsTimestampedSample()
    {
        var time = new ControllableTimeProvider(DateTimeOffset.UtcNow);
        var runner = new ScriptedStateRunner(
            """{"metadata": {"status": "Running", "cpu": {"usage": 987654321}}}""");
        var reader = new DefaultIncusInstanceStateReader(new IncusCliRunner(runner, time), time);

        var sample = await reader.ReadStateAsync(
            new IncusSandboxOptions(), "codeybox-vm-1", CancellationToken.None);

        Assert.NotNull(sample);
        Assert.Equal(987654321, sample!.Value.CpuUsageNanoseconds);
        Assert.Equal(time.GetUtcNow(), sample.Value.Timestamp);
        var argv = Assert.Single(runner.Arguments);
        Assert.Equal(["incus", "query", "/1.0/instances/codeybox-vm-1/state?project=codeybox"], argv);
    }

    [Fact]
    public async Task ReadStateAsync_FailedQuery_ReturnsNull()
    {
        var runner = new ScriptedStateRunner(null);
        var reader = new DefaultIncusInstanceStateReader(
            new IncusCliRunner(runner), TimeProvider.System);

        var sample = await reader.ReadStateAsync(
            new IncusSandboxOptions(), "codeybox-vm-1", CancellationToken.None);

        Assert.Null(sample);
    }

    private sealed class NeverInvokedProcessRunner : IProcessRunner
    {
        internal int Calls { get; private set; }

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
            Calls++;
            throw new InvalidOperationException("The Incus CLI must not run for rejected input.");
        }
    }

    private sealed class ScriptedStateRunner(string? stdout) : IProcessRunner
    {
        internal List<IReadOnlyList<string>> Arguments { get; } = [];

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
            Arguments.Add(argv.ToArray());
            return Task.FromResult(stdout is null
                ? new ProcessRunResult(1, string.Empty, "query failed")
                : new ProcessRunResult(0, stdout, string.Empty));
        }
    }
}
