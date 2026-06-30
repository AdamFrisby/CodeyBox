using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Tests.Uat.SandboxProviders;
using Serilog;
using Serilog.Events;
using Xunit;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class MultipassResourceMetricsTests : IDisposable
{
    private readonly TestSink _sink = new();
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"codeybox-resource-metrics-{Guid.NewGuid():N}.db");

    public MultipassResourceMetricsTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* best-effort */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task DisposeAsync_CapturesPersistsAndLogsResourceRecord()
    {
        using var store = new SqliteSandboxResourceUsageStore(_dbPath);
        var workItemId = WorkItemId.New();
        var runner = new RecordingMultipassRunner((argv, stdin, ct) =>
            Task.FromResult(IsMetricsExec(argv)
                ? new ProcessRunResult(0, MetricsStdout(), "")
                : new ProcessRunResult(0, "", "")));

        var sandbox = BuildSandbox(
            runner,
            CaptureOptions(),
            workItemId,
            resourceUsageStore: store);

        await sandbox.DisposeAsync();

        var metrics = sandbox.ResourceMetrics;
        Assert.NotNull(metrics);
        Assert.Equal(104857600L, metrics.PeakRamBytes);
        Assert.Equal(15.5, metrics.AvgCpuPercent);
        Assert.Equal(1048576L, metrics.NetRxBytes);
        Assert.Equal(2097152L, metrics.NetTxBytes);
        Assert.Equal(3145728L, metrics.TotalNetIoBytes);
        Assert.Equal(42.25, metrics.UptimeSeconds);
        Assert.Equal(0.10, metrics.LoadAvg1);
        Assert.Equal("cb-baseline-claude", metrics.BaselineRef);
        Assert.Equal("claude", metrics.NetworkProfile);
        Assert.Equal("work", metrics.Phase);

        var rows = await store.ListRecentAsync(10);
        var row = Assert.Single(rows);
        Assert.Equal(workItemId, row.WorkItemId);
        Assert.Equal("work", row.Phase);
        Assert.Equal("codeybox-test-vm", row.VmName);
        Assert.Equal(100.0, row.PeakRamMb);
        Assert.Equal(15.5, row.AvgCpuPercent);
        Assert.Equal(1.0, row.NetRxMb);
        Assert.Equal(2.0, row.NetTxMb);
        Assert.Equal(42.25, row.DurationSeconds);
        Assert.Equal("cb-baseline-claude", row.BaselineRef);
        Assert.Equal("claude", row.NetworkProfile);
        Assert.Equal(0.20, row.LoadAvg5);
        Assert.Equal(0.30, row.LoadAvg15);

        var evt = Assert.Single(_sink.Events, e => GetScalar<string>(e, "EventName") == "sandbox.disposed");
        Assert.Equal("codeybox-test-vm", GetScalar<string>(evt, "VmName"));
        Assert.Equal(104857600L, GetScalar<long>(evt, "PeakRamBytes"));
        Assert.Equal(1048576L, GetScalar<long>(evt, "NetRxBytes"));
        Assert.Equal(2097152L, GetScalar<long>(evt, "NetTxBytes"));
    }

    [Fact]
    public async Task DisposeAsync_CaptureResourceMetricsOff_SkipsInVmExec()
    {
        var execCalls = 0;
        var runner = new RecordingMultipassRunner((argv, stdin, ct) =>
        {
            if (argv.Contains("exec"))
                execCalls++;
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });

        var sandbox = BuildSandbox(runner, CaptureOptions(enabled: false), WorkItemId.New());

        await sandbox.DisposeAsync();

        Assert.Null(sandbox.ResourceMetrics);
        Assert.Equal(0, execCalls);
        Assert.Contains(runner.Calls, c => c.Argv.Contains("delete"));
    }

    [Fact]
    public async Task DisposeAsync_WhenMetricsScriptFails_DoesNotThrowAndMetricsAreNull()
    {
        var runner = new RecordingMultipassRunner((argv, stdin, ct) =>
            Task.FromResult(IsMetricsExec(argv)
                ? new ProcessRunResult(1, "", "error running script")
                : new ProcessRunResult(0, "", "")));

        var sandbox = BuildSandbox(runner, CaptureOptions(), WorkItemId.New());

        await sandbox.DisposeAsync();

        Assert.Null(sandbox.ResourceMetrics);
        Assert.Contains(runner.Calls, c => c.Argv.Contains("delete"));
    }

    [Fact]
    public async Task DisposeAsync_WhenMetricsCaptureTimesOut_StillDeletes()
    {
        var hungCapture = new TaskCompletionSource<ProcessRunResult>();
        var runner = new RecordingMultipassRunner((argv, stdin, ct) =>
            IsMetricsExec(argv)
                ? hungCapture.Task
                : Task.FromResult(new ProcessRunResult(0, "", "")));
        var sandbox = BuildSandbox(
            runner,
            CaptureOptions(timeout: TimeSpan.FromMilliseconds(20)),
            WorkItemId.New());

        var sw = Stopwatch.StartNew();
        await sandbox.DisposeAsync();
        sw.Stop();

        Assert.Null(sandbox.ResourceMetrics);
        Assert.Contains(runner.Calls, c => c.Argv.Contains("delete"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"dispose took {sw.Elapsed}");
    }

    [Fact]
    public async Task StopAndPreserveAsync_CapturesAndPersistsBeforeStop()
    {
        using var store = new SqliteSandboxResourceUsageStore(_dbPath);
        var order = new List<string>();
        var runner = new RecordingMultipassRunner((argv, stdin, ct) =>
        {
            if (IsMetricsExec(argv))
            {
                order.Add("metrics");
                return Task.FromResult(new ProcessRunResult(0, MetricsStdout(), ""));
            }
            if (argv.Contains("stop"))
            {
                order.Add("stop");
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv.Contains("info"))
                return Task.FromResult(new ProcessRunResult(0, "Name,State\ncodeybox-test-vm,Stopped\n", ""));
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });

        var sandbox = BuildSandbox(
            runner,
            CaptureOptions(),
            WorkItemId.New(),
            resourceUsageStore: store);

        await sandbox.StopAndPreserveAsync(CancellationToken.None);

        Assert.Equal(["metrics", "stop"], order);
        Assert.NotNull(sandbox.ResourceMetrics);
        Assert.Single(await store.ListRecentAsync(10));
    }

    [Fact]
    public void ResourceMetricsCaptureScript_UsesSamplerProcStatLoadavgUptimeAndEns4RxTx()
    {
        var script = MultipassSandbox.BuildResourceMetricsCaptureScript();

        Assert.Contains(MultipassSandboxProvider.PeakRamSamplerPath, script);
        Assert.Contains("/proc/stat", script);
        Assert.Contains("/proc/loadavg", script);
        Assert.Contains("/proc/uptime", script);
        Assert.Contains("/proc/net/dev", script);
        Assert.Contains(MultipassSandboxProvider.ResourceDataInterface, script);
        Assert.Contains("net_rx_bytes", script);
        Assert.Contains("net_tx_bytes", script);
        Assert.DoesNotContain("/sys/fs/cgroup/memory.peak", script);
    }

    [Fact]
    public void BuildCloudInit_InstallsPeakRamSamplerService()
    {
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(
            extraRuncmd: null,
            extraCloudInit: null);

        Assert.Contains("/usr/local/sbin/codeybox-peak-ram-sampler", cloudInit);
        Assert.Contains("/etc/systemd/system/codeybox-peak-ram-sampler.service", cloudInit);
        Assert.Contains("systemctl enable --now codeybox-peak-ram-sampler.service", cloudInit);
        Assert.Contains("MemTotal", cloudInit);
        Assert.Contains("MemAvailable", cloudInit);
    }

    [Fact]
    public void Wrappers_ForwardResourceMetrics()
    {
        var mockSandbox = new MockSandboxWithMetrics();

        var wrapMethod = typeof(WorkSandboxContext).GetMethod(
            "Wrap",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(wrapMethod);

        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions());
        var context = new WorkSandboxContext(new MockSandboxProvider(mockSandbox), tuning, NullLogger.Instance);

        var wrapped = (ISandbox)wrapMethod.Invoke(null, [mockSandbox, context])!;
        Assert.NotNull(wrapped.ResourceMetrics);
        Assert.Equal(12345L, wrapped.ResourceMetrics.PeakRamBytes);
        Assert.Equal(10L, wrapped.ResourceMetrics.NetRxBytes);

        var lease = new SandboxAdmissionLease(new SandboxAdmissionGate(1));
        var admissionControlled = new AdmissionControlledSandbox(
            mockSandbox,
            lease,
            (_, _, _, _) => ValueTask.CompletedTask,
            _ => { },
            NullLogger.Instance);

        Assert.NotNull(admissionControlled.ResourceMetrics);
        Assert.Equal(12345L, admissionControlled.ResourceMetrics.PeakRamBytes);
        Assert.Equal(20L, admissionControlled.ResourceMetrics.NetTxBytes);
    }

    private static MultipassSandbox BuildSandbox(
        IProcessRunner runner,
        MultipassSandboxOptions opts,
        WorkItemId workItemId,
        ISandboxResourceUsageStore? resourceUsageStore = null) =>
        new(
            "codeybox-test-vm",
            Path.Combine(Path.GetTempPath(), $"codeybox-test-root-{Guid.NewGuid():N}"),
            new SandboxSpec
            {
                ImageReference = "ubuntu",
                Limits = new SandboxResourceLimits { MemoryBytes = 1024L * 1024 * 1024 },
                Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            },
            opts,
            NullLogger<MultipassSandbox>.Instance,
            timingItemId: workItemId,
            timingPhase: "work",
            runner: runner,
            resourceUsageStore: resourceUsageStore,
            baselineRef: "cb-baseline-claude");

    private static MultipassSandboxOptions CaptureOptions(
        bool enabled = true,
        TimeSpan? timeout = null) => new()
        {
            MultipassBinary = "multipass",
            StagingDirectory = Path.Combine(Path.GetTempPath(), $"codeybox-test-staging-{Guid.NewGuid():N}"),
            CaptureResourceMetrics = enabled,
            ResourceMetricsCaptureTimeout = timeout ?? TimeSpan.FromSeconds(5),
        };

    private static bool IsMetricsExec(IReadOnlyList<string> argv) =>
        argv.Contains("exec")
        && argv.Any(a => a.Contains(MultipassSandboxProvider.PeakRamSamplerPath, StringComparison.Ordinal));

    private static string MetricsStdout() => """
        peak_ram_bytes=104857600
        avg_cpu_pct=15.5
        net_rx_bytes=1048576
        net_tx_bytes=2097152
        uptime_sec=42.25
        loadavg_1=0.10
        loadavg_5=0.20
        loadavg_15=0.30
        """;

    private static T GetScalar<T>(LogEvent evt, string propName)
    {
        if (evt.Properties.TryGetValue(propName, out var val) && val is ScalarValue sv)
            return (T)sv.Value!;
        throw new KeyNotFoundException($"Property '{propName}' not found or not scalar");
    }

    private sealed class MockSandboxWithMetrics : ISandbox
    {
        public string Id => "mock-vm";

        public SandboxResourceMetrics? ResourceMetrics => new(
            PeakRamBytes: 12345L,
            AvgCpuPercent: 50.0,
            NetRxBytes: 10L,
            NetTxBytes: 20L,
            UptimeSeconds: 30.0,
            LoadAvg1: 0.1,
            LoadAvg5: 0.2,
            LoadAvg15: 0.3,
            BaselineRef: "baseline",
            NetworkProfile: "profile",
            Phase: "work",
            CapturedAt: DateTimeOffset.UtcNow);

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockSandboxProvider : ISandboxProvider
    {
        private readonly ISandbox _sandbox;
        public MockSandboxProvider(ISandbox sandbox) => _sandbox = sandbox;
        public string Name => "mock";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) => Task.FromResult(_sandbox);
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => throw new NotImplementedException();
    }
}
