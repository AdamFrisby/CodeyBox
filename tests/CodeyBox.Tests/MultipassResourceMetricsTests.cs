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
        using var metricCapture = new MetricCapture(
            "codeybox.sandbox.resource.peak_ram_mb",
            "codeybox.sandbox.resource.avg_cpu_pct",
            "codeybox.sandbox.resource.net_rx_mb",
            "codeybox.sandbox.resource.net_tx_mb");
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

        var metricsCall = runner.Calls.Single(c => IsMetricsExec(c.Argv));
        Assert.Equal(MultipassSandbox.ResourceMetricsCaptureMaxStdoutBytes, metricsCall.MaxStdoutBytes);
        Assert.Equal(MultipassSandbox.ResourceMetricsCaptureMaxStderrBytes, metricsCall.MaxStderrBytes);
        Assert.True(metricsCall.KillOnOutputLimit);

        AssertMeasurement(metricCapture, "codeybox.sandbox.resource.peak_ram_mb", 100, "work", "claude");
        AssertMeasurement(metricCapture, "codeybox.sandbox.resource.avg_cpu_pct", 15.5, "work", "claude");
        AssertMeasurement(metricCapture, "codeybox.sandbox.resource.net_rx_mb", 1, "work", "claude");
        AssertMeasurement(metricCapture, "codeybox.sandbox.resource.net_tx_mb", 2, "work", "claude");

        var evt = Assert.Single(_sink.Events, e => GetScalar<string>(e, "EventName") == "sandbox.disposed");
        Assert.Equal("codeybox-test-vm", GetScalar<string>(evt, "VmName"));
        Assert.Equal(104857600L, GetScalar<long>(evt, "PeakRamBytes"));
        Assert.Equal(1048576L, GetScalar<long>(evt, "NetRxBytes"));
        Assert.Equal(2097152L, GetScalar<long>(evt, "NetTxBytes"));
    }

    [Fact]
    public async Task DisposeAsync_WhenResourceUsageStoreHangs_StillDeletesWithinCaptureTimeout()
    {
        var store = new HangingResourceUsageStore();
        var runner = new RecordingMultipassRunner((argv, stdin, ct) =>
            Task.FromResult(IsMetricsExec(argv)
                ? new ProcessRunResult(0, MetricsStdout(), "")
                : new ProcessRunResult(0, "", "")));
        var sandbox = BuildSandbox(
            runner,
            CaptureOptions(timeout: TimeSpan.FromMilliseconds(20)),
            WorkItemId.New(),
            resourceUsageStore: store);

        var sw = Stopwatch.StartNew();
        await sandbox.DisposeAsync();
        sw.Stop();

        Assert.NotNull(sandbox.ResourceMetrics);
        Assert.True(store.RecordStarted.Task.IsCompleted, "persistence was not attempted");
        Assert.Contains(runner.Calls, c => c.Argv.Contains("delete"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"dispose took {sw.Elapsed}");
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
    public async Task DisposeAsync_MarksDisposedBeforeCaptureSoConcurrentPreserveCannotWinRace()
    {
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingMultipassRunner(async (argv, stdin, ct) =>
        {
            if (IsMetricsExec(argv))
            {
                captureStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return new ProcessRunResult(0, "", "");
        });
        var sandbox = BuildSandbox(
            runner,
            CaptureOptions(timeout: TimeSpan.FromMilliseconds(20)),
            WorkItemId.New());

        var disposeTask = sandbox.DisposeAsync().AsTask();
        await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sandbox.StopAndPreserveAsync(CancellationToken.None);
        await disposeTask;

        Assert.DoesNotContain(runner.Calls, c => c.Argv.Contains("stop"));
        Assert.Contains(runner.Calls, c => c.Argv.Contains("delete"));
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
    public async Task StopAndPreserveAsync_CapturesEveryPreserveStop()
    {
        using var store = new SqliteSandboxResourceUsageStore(_dbPath);
        var captureCount = 0;
        var runner = new RecordingMultipassRunner((argv, stdin, ct) =>
        {
            if (IsMetricsExec(argv))
            {
                captureCount++;
                return Task.FromResult(new ProcessRunResult(0, MetricsStdout(peakRamBytes: captureCount * 100L * 1024 * 1024), ""));
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
        await sandbox.StopAndPreserveAsync(CancellationToken.None);

        var rows = await store.ListRecentAsync(10);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.PeakRamMb == 100);
        Assert.Contains(rows, r => r.PeakRamMb == 200);
        Assert.Equal(2, captureCount);
        Assert.Equal(200L * 1024 * 1024, sandbox.ResourceMetrics!.PeakRamBytes);
    }

    [Fact]
    public async Task ResourceMetricsCaptureScript_ComputesCpuNetUptimeAndLoadFromProcFiles()
    {
        var root = Directory.CreateTempSubdirectory("codeybox-fake-proc-").FullName;
        try
        {
            var proc = Path.Combine(root, "proc");
            Directory.CreateDirectory(Path.Combine(proc, "net"));
            await File.WriteAllTextAsync(Path.Combine(root, "peak"), "104857600\n");
            await File.WriteAllTextAsync(Path.Combine(proc, "stat"), "cpu  100 20 30 850 50 10 5 0 0 0\n");
            await File.WriteAllTextAsync(Path.Combine(proc, "loadavg"), "0.10 0.20 0.30 1/234 5678\n");
            await File.WriteAllTextAsync(Path.Combine(proc, "uptime"), "42.25 1000.00\n");
            await File.WriteAllTextAsync(Path.Combine(proc, "net", "dev"), """
                Inter-|   Receive                                                |  Transmit
                 face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                  ens4: 1234 1 2 3 4 5 6 7 5678 9 10 11 12 13 14 15
                """);

            var stdout = await RunResourceCaptureScriptAsync(proc, Path.Combine(root, "peak"));
            var sandbox = BuildSandbox(
                new RecordingMultipassRunner((_, _, _) => Task.FromResult(new ProcessRunResult(0, "", ""))),
                CaptureOptions(),
                WorkItemId.New());
            var metrics = sandbox.ParseResourceMetricsOutput(stdout, DateTimeOffset.UtcNow);

            Assert.NotNull(metrics);
            Assert.Equal(104857600L, metrics.PeakRamBytes);
            Assert.Equal(15.492958, metrics.AvgCpuPercent!.Value, 6);
            Assert.Equal(1234L, metrics.NetRxBytes);
            Assert.Equal(5678L, metrics.NetTxBytes);
            Assert.Equal(42.25, metrics.UptimeSeconds);
            Assert.Equal(0.10, metrics.LoadAvg1);
            Assert.Equal(0.20, metrics.LoadAvg5);
            Assert.Equal(0.30, metrics.LoadAvg15);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ResourceMetricsCaptureScript_UsesSamplerProcStatLoadavgUptimeAndEns4RxTx()
    {
        var script = MultipassSandbox.BuildResourceMetricsCaptureScript();

        Assert.Contains(MultipassSandboxProvider.PeakRamSamplerPath, script);
        Assert.Contains("proc_root=${CODEYBOX_PROC_ROOT:-/proc}", script);
        Assert.Contains("\"$proc_root/stat\"", script);
        Assert.Contains("\"$proc_root/loadavg\"", script);
        Assert.Contains("\"$proc_root/uptime\"", script);
        Assert.Contains("\"$proc_root/net/dev\"", script);
        Assert.Contains(MultipassSandboxProvider.ResourceDataInterface, script);
        Assert.Contains("net_rx_bytes", script);
        Assert.Contains("net_tx_bytes", script);
        Assert.Contains("head -c 64", script);
        Assert.DoesNotContain("/sys/fs/cgroup/memory.peak", script);
    }

    [Fact]
    public void BuildCloudInit_DefaultOmitsPeakRamSamplerService()
    {
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(
            extraRuncmd: null,
            extraCloudInit: null);

        Assert.DoesNotContain("/usr/local/sbin/codeybox-peak-ram-sampler", cloudInit);
        Assert.DoesNotContain("/etc/systemd/system/codeybox-peak-ram-sampler.service", cloudInit);
        Assert.DoesNotContain("systemctl enable --now codeybox-peak-ram-sampler.service", cloudInit);
    }

    [Fact]
    public void BuildCloudInit_WhenEnabledInstallsPeakRamSamplerService()
    {
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(
            extraRuncmd: null,
            extraCloudInit: null,
            includePeakRamSampler: true);

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

    [Theory]
    [InlineData("""
        avg_cpu_pct=15.5
        net_rx_bytes=1
        net_tx_bytes=2
        """)]
    [InlineData("""
        peak_ram_bytes=0
        avg_cpu_pct=15.5
        net_rx_bytes=1
        net_tx_bytes=2
        """)]
    [InlineData("""
        peak_ram_bytes=not-a-number
        avg_cpu_pct=15.5
        net_rx_bytes=1
        net_tx_bytes=2
        """)]
    [InlineData("""
        peak_ram_bytes=104857600
        avg_cpu_pct=101
        net_rx_bytes=1
        net_tx_bytes=2
        """)]
    [InlineData("""
        peak_ram_bytes=104857600
        avg_cpu_pct=NaN
        net_rx_bytes=1
        net_tx_bytes=2
        """)]
    [InlineData("""
        peak_ram_bytes=104857600
        avg_cpu_pct=15.5
        net_rx_bytes=0
        net_tx_bytes=0
        """)]
    public void ParseResourceMetricsOutput_RejectsMalformedOrIncompleteOutput(string stdout)
    {
        var sandbox = BuildSandbox(
            new RecordingMultipassRunner((_, _, _) => Task.FromResult(new ProcessRunResult(0, "", ""))),
            CaptureOptions(),
            WorkItemId.New());

        Assert.Null(sandbox.ParseResourceMetricsOutput(stdout, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ParseResourceMetricsOutput_CapsGuestPeakRamAtVmMemoryLimit()
    {
        var sandbox = BuildSandbox(
            new RecordingMultipassRunner((_, _, _) => Task.FromResult(new ProcessRunResult(0, "", ""))),
            CaptureOptions(),
            WorkItemId.New());

        var metrics = sandbox.ParseResourceMetricsOutput(MetricsStdout(peakRamBytes: 2L * 1024 * 1024 * 1024), DateTimeOffset.UtcNow);

        Assert.NotNull(metrics);
        Assert.Equal(1024L * 1024 * 1024, metrics.PeakRamBytes);
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

    private static string MetricsStdout(
        long peakRamBytes = 104857600,
        double avgCpuPct = 15.5,
        long netRxBytes = 1048576,
        long netTxBytes = 2097152) => FormattableString.Invariant($"""
        peak_ram_bytes={peakRamBytes}
        avg_cpu_pct={avgCpuPct}
        net_rx_bytes={netRxBytes}
        net_tx_bytes={netTxBytes}
        uptime_sec=42.25
        loadavg_1=0.10
        loadavg_5=0.20
        loadavg_15=0.30
        """);

    private static async Task<string> RunResourceCaptureScriptAsync(string procRoot, string peakRamPath)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-s");
        process.StartInfo.Environment["CODEYBOX_PROC_ROOT"] = procRoot;
        process.StartInfo.Environment["CODEYBOX_PEAK_RAM_SAMPLER_PATH"] = peakRamPath;
        Assert.True(process.Start());
        await process.StandardInput.WriteAsync(MultipassSandbox.BuildResourceMetricsCaptureScript());
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrEmpty(stderr), stderr);
        return stdout;
    }

    private static void AssertMeasurement(
        MetricCapture capture,
        string instrument,
        double value,
        string phase,
        string networkProfile)
    {
        Assert.Contains(capture.Items, item =>
            item.Instrument == instrument
            && Math.Abs(item.Value - value) < 0.000001
            && HasTag(item.Tags, "phase", phase)
            && HasTag(item.Tags, "network_profile", networkProfile));
    }

    private static bool HasTag(KeyValuePair<string, object?>[] tags, string key, string value) =>
        tags.Any(t => t.Key == key && string.Equals(t.Value?.ToString(), value, StringComparison.Ordinal));

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

    private sealed class HangingResourceUsageStore : ISandboxResourceUsageStore
    {
        public TaskCompletionSource RecordStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RecordAsync(SandboxResourceUsageRecord record, CancellationToken ct = default)
        {
            RecordStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan);
        }

        public Task<IReadOnlyList<SandboxResourceUsageRecord>> ListRecentAsync(
            int limit,
            DateTimeOffset? sinceUtc = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SandboxResourceUsageRecord>>([]);
    }
}
