using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    public MultipassResourceMetricsTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose() => Log.CloseAndFlush();

    private static T GetScalar<T>(LogEvent evt, string propName)
    {
        if (evt.Properties.TryGetValue(propName, out var val) && val is ScalarValue sv)
            return (T)sv.Value!;
        throw new KeyNotFoundException($"Property '{propName}' not found or not scalar");
    }

    [Fact]
    public async Task DisposeAsync_CapturesResourceMetricsAndLogsThem()
    {
        var runner = new RecordingMultipassRunner((argv, stdin, ct) =>
        {
            // If it is the exec command running the metrics script, return mocked metrics.
            if (argv.Contains("exec") && argv.Contains("sh") && argv.Any(a => a.Contains("peak_ram=")))
            {
                return Task.FromResult(new ProcessRunResult(0, "104857600 15 204800\n", ""));
            }
            // For other multipass commands (like delete), return success.
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });

        var opts = new MultipassSandboxOptions
        {
            MultipassBinary = "multipass",
            StagingDirectory = "/tmp/codeybox-test-staging"
        };

        var spec = new SandboxSpec
        {
            ImageReference = "ubuntu",
            Limits = new SandboxResourceLimits { MemoryBytes = 1073741824 }
        };

        var sandbox = new MultipassSandbox(
            "codeybox-test-vm",
            "/tmp/codeybox-test-root",
            spec,
            opts,
            NullLogger<MultipassSandbox>.Instance,
            runner: runner);

        // Act
        await sandbox.DisposeAsync();

        // Assert
        Assert.NotNull(sandbox.ResourceMetrics);
        Assert.Equal(104857600L, sandbox.ResourceMetrics.PeakRamBytes);
        Assert.Equal(15.0, sandbox.ResourceMetrics.AvgCpuPercent);
        Assert.Equal(204800L, sandbox.ResourceMetrics.TotalNetIoBytes);

        // Verify that the AuditLog.SandboxDisposed event was logged with the metrics
        var evt = Assert.Single(_sink.Events, e => GetScalar<string>(e, "EventName") == "sandbox.disposed");
        Assert.Equal("codeybox-test-vm", GetScalar<string>(evt, "VmName"));
        Assert.Equal(104857600L, GetScalar<long>(evt, "PeakRamBytes"));
        Assert.Equal(15.0, GetScalar<double>(evt, "AvgCpuPercent"));
        Assert.Equal(204800L, GetScalar<long>(evt, "TotalNetIoBytes"));
    }

    [Fact]
    public async Task DisposeAsync_WhenMetricsScriptFails_DoesNotThrowAndMetricsAreNull()
    {
        var runner = new RecordingMultipassRunner((argv, stdin, ct) =>
        {
            // Return non-zero exit code for the metrics script
            if (argv.Contains("exec") && argv.Contains("sh") && argv.Any(a => a.Contains("peak_ram=")))
            {
                return Task.FromResult(new ProcessRunResult(1, "", "error running script"));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });

        var opts = new MultipassSandboxOptions
        {
            MultipassBinary = "multipass",
            StagingDirectory = "/tmp/codeybox-test-staging"
        };

        var spec = new SandboxSpec
        {
            ImageReference = "ubuntu"
        };

        var sandbox = new MultipassSandbox(
            "codeybox-test-vm",
            "/tmp/codeybox-test-root",
            spec,
            opts,
            NullLogger<MultipassSandbox>.Instance,
            runner: runner);

        // Act
        await sandbox.DisposeAsync();

        // Assert
        Assert.Null(sandbox.ResourceMetrics);

        // Audit log should still be written but with null metrics
        var evt = Assert.Single(_sink.Events, e => GetScalar<string>(e, "EventName") == "sandbox.disposed");
        Assert.Equal("codeybox-test-vm", GetScalar<string>(evt, "VmName"));
        Assert.False(evt.Properties.TryGetValue("PeakRamBytes", out var peakVal) && ((ScalarValue)peakVal).Value != null);
    }

    [Fact]
    public void Wrappers_ForwardResourceMetrics()
    {
        var mockSandbox = new MockSandboxWithMetrics();
        
        // 1. ReusableSandbox (from WorkSandboxContext)
        var wrapMethod = typeof(WorkSandboxContext).GetMethod("Wrap", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(wrapMethod);
        
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions());
        var context = new WorkSandboxContext(new MockSandboxProvider(mockSandbox), tuning, NullLogger.Instance);
        
        var wrapped = (ISandbox)wrapMethod.Invoke(null, [mockSandbox, context])!;
        Assert.NotNull(wrapped.ResourceMetrics);
        Assert.Equal(12345L, wrapped.ResourceMetrics.PeakRamBytes);

        // 2. AdmissionControlledSandbox
        var lease = new SandboxAdmissionLease(new SandboxAdmissionGate(1));
        var admissionControlled = new AdmissionControlledSandbox(
            mockSandbox,
            lease,
            (_, _, _, _) => ValueTask.CompletedTask,
            _ => { },
            NullLogger.Instance);
            
        Assert.NotNull(admissionControlled.ResourceMetrics);
        Assert.Equal(12345L, admissionControlled.ResourceMetrics.PeakRamBytes);
    }

    private class MockSandboxWithMetrics : ISandbox
    {
        public string Id => "mock-vm";
        public SandboxResourceMetrics? ResourceMetrics => new SandboxResourceMetrics(12345L, 50.0, 99999L);
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class MockSandboxProvider : ISandboxProvider
    {
        private readonly ISandbox _sandbox;
        public MockSandboxProvider(ISandbox sandbox) => _sandbox = sandbox;
        public string Name => "mock";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) => Task.FromResult(_sandbox);
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => throw new NotImplementedException();
    }
}
