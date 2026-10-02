using System.Text;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the agent-turn streaming exec policy on the Incus provider: an
/// agent turn that emits many times the old 4 MiB CLI bound must complete
/// with the full volume in the stream sink and only a bounded tail in
/// memory, while a bounded control-plane exec over its cap is still killed
/// and reported as output-bound.
/// </summary>
public sealed class IncusAgentStreamingExecTests : IDisposable
{
    private readonly List<string> _tempRoots = new();

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    [Fact]
    public async Task StreamingExec_VerboseAgentStream_CompletesWithFullSinkAndBoundedTail()
    {
        // 10x the old provider-wide bound, delivered incrementally.
        var totalBytes = 10 * IncusSandboxOptions.DefaultMaxCliOutputBytes;
        var payload = BuildVolumePayload(totalBytes);
        Assert.Equal(totalBytes, Encoding.UTF8.GetByteCount(payload));
        var runner = new VolumeCliRunner(payload, killOverCap: false);
        var tailBytes = 64 * 1024;
        var options = FastOptions() with
        {
            AgentExecTailStdoutBytes = tailBytes,
            AgentExecTailStderrBytes = tailBytes,
        };
        var sandbox = CreateSandbox("codeybox-stream-agent", options, runner);
        var streamPath = NewTempPath("agent-stream");
        await using var capture = new AgentStreamCapture(streamPath, maxBytes: 256L * 1024 * 1024, "work", NullLogger.Instance);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["devin", "acp", "run"],
            StreamOutputWithoutKill = true,
            KillOnOutputLimit = false,
            StdoutChunkCallback = capture.WriteChunk,
        });

        await capture.DisposeAsync();

        Assert.True(result.Success, result.Stderr);
        Assert.False(result.StdoutLimitExceeded);
        Assert.False(result.StderrLimitExceeded);
        Assert.False(result.ExecutionUnavailable);
        var wrapperCall = Assert.Single(
            runner.Calls.Where(call => call.IsWrapperExec).ToList());
        Assert.False(wrapperCall.KillOnOutputLimit);
        Assert.Equal(tailBytes, wrapperCall.MaxStdoutBytes);
        var streamBytes = new FileInfo(streamPath).Length;
        Assert.Equal(totalBytes, streamBytes);
        var retainedBytes = Encoding.UTF8.GetByteCount(result.Stdout);
        Assert.True(retainedBytes <= tailBytes, $"retained {retainedBytes} bytes, bound {tailBytes}");
        Assert.Equal(payload[^retainedBytes..], result.Stdout);
        Assert.DoesNotContain(payload[..1024], result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingExec_WithExplicitRetainedOverride_UsesOverrideAsProcessBound()
    {
        var payload = BuildVolumePayload(256 * 1024);
        var runner = new VolumeCliRunner(payload, killOverCap: false);
        var options = FastOptions();
        var sandbox = CreateSandbox("codeybox-stream-override", options, runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent"],
            StreamOutputWithoutKill = true,
            KillOnOutputLimit = false,
            MaxRetainedStdoutBytes = 8 * 1024,
        });

        Assert.True(result.Success, result.Stderr);
        var wrapperCall = Assert.Single(
            runner.Calls.Where(call => call.IsWrapperExec).ToList());
        Assert.False(wrapperCall.KillOnOutputLimit);
        Assert.Equal(8 * 1024, wrapperCall.MaxStdoutBytes);
        Assert.Equal(payload[^Encoding.UTF8.GetByteCount(result.Stdout)..], result.Stdout);
    }

    [Fact]
    public async Task StreamingExec_RejectsContradictoryKillPolicy()
    {
        var runner = new VolumeCliRunner("ok\n", killOverCap: false);
        var sandbox = CreateSandbox("codeybox-stream-reject", FastOptions(), runner);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["agent"],
                StreamOutputWithoutKill = true,
                KillOnOutputLimit = true,
            }));

        Assert.Contains("KillOnOutputLimit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundedExec_OverCap_IsStillKilledAndReportedAsOutputBound()
    {
        var totalBytes = 10 * IncusSandboxOptions.DefaultMaxCliOutputBytes;
        var payload = BuildVolumePayload(totalBytes);
        var runner = new VolumeCliRunner(payload, killOverCap: true);
        var sandbox = CreateSandbox("codeybox-bounded-kill", FastOptions(), runner);

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["cat", "huge"] });

        Assert.False(result.Success);
        Assert.Equal(137, result.ExitCode);
        Assert.True(result.StdoutLimitExceeded);
        var wrapperCall = Assert.Single(
            runner.Calls.Where(call => call.IsWrapperExec).ToList());
        Assert.True(wrapperCall.KillOnOutputLimit);
        var agentResult = new AgentResult(
            Success: false,
            Summary: $"agent exited {result.ExitCode}",
            Stdout: result.Stdout,
            Stderr: result.Stderr)
        {
            OutputLimitExceeded = result.OutputLimitExceeded,
        };
        Assert.True(AgentOutputBoundFailure.IsOutputBound(agentResult));
        var detail = AgentOutputBoundFailure.DescribeTurn(new AgentKind("devin"), "work");
        Assert.Contains("output-bound", detail, StringComparison.Ordinal);
        Assert.Contains("devin", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("terminated by infrastructure", detail, StringComparison.Ordinal);
        Assert.Equal("output-bound", WorkItemFailureKinds.OutputBound);
        Assert.True(WorkItemFailureKinds.IsInfraShaped(WorkItemFailureKinds.OutputBound));
        var resumeDetail = AgentOutputBoundFailure.DescribeResumeExhausted(new AgentKind("devin"));
        Assert.Contains("output-bound", resumeDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCapture_AtDiskCap_StopsWritingWithMarkerAndCompletes()
    {
        var streamPath = NewTempPath("agent-stream-cap");
        await using var capture = new AgentStreamCapture(streamPath, maxBytes: 4096, "work", NullLogger.Instance);
        var line = new string('a', 200) + "\n";
        for (var i = 0; i < 200; i++)
            capture.WriteChunk(line);
        // Writes past the cap must be accepted (dropped) without failing —
        // the agent turn continues while the file stays capped.
        capture.WriteChunk(line);
        await capture.DisposeAsync();

        var size = new FileInfo(streamPath).Length;
        Assert.True(size <= 4096, $"stream file is {size} bytes, cap 4096");
        var content = await File.ReadAllTextAsync(streamPath);
        Assert.Contains("[...truncated by ", content, StringComparison.Ordinal);
    }

    private static string BuildVolumePayload(int totalBytes)
    {
        const int lineBytes = 128;
        var lines = totalBytes / lineBytes;
        var builder = new StringBuilder(totalBytes);
        for (var i = 0; i < lines; i++)
        {
            var head = $"[{i:D7}] devin-acp tool_result ";
            builder.Append(head);
            builder.Append('.', lineBytes - head.Length - 1);
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static IncusSandboxOptions FastOptions() => new()
    {
        CaptureResourceMetrics = false,
        DiskGuard = null,
        OperationTimeout = TimeSpan.FromSeconds(30),
        ExecTimeout = TimeSpan.FromSeconds(30),
        VmStopTimeout = TimeSpan.FromMilliseconds(100),
        ReadinessPollInterval = TimeSpan.FromMilliseconds(1),
        InterruptedExecRecoveryRetryAttempts = 0,
    };

    private IncusSandbox CreateSandbox(
        string sandboxName,
        IncusSandboxOptions options,
        IProcessRunner runner)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-stream-{Guid.NewGuid():N}");
        _tempRoots.Add(root);
        var sandboxRoot = Path.Combine(root, sandboxName);
        Directory.CreateDirectory(sandboxRoot);
        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, DateTimeOffset.UtcNow);
        var spec = new SandboxSpec { ImageReference = "local-image" };
        var authorization = IncusRecoveryAuthorization.CaptureValidated(
            bridge: null,
            mounts: [],
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
            baselineRef: null,
            authorization);
        var store = IncusRecoveryManifestStore.Acquire(sandboxRoot);
        _ = store.Write(manifest, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        return new IncusSandbox(
            sandboxName,
            sandboxRoot,
            root,
            spec,
            options,
            new IncusCliRunner(runner, TimeProvider.System),
            NullLogger.Instance,
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
            TimeProvider.System);
    }

    private string NewTempPath(string purpose)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-stream-{Guid.NewGuid():N}");
        _tempRoots.Add(root);
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{purpose}.jsonl");
    }

    private sealed record CliCall(
        string Command,
        int? MaxStdoutBytes,
        int? MaxStderrBytes,
        bool KillOnOutputLimit,
        bool IsWrapperExec);

    private sealed class VolumeCliRunner : IProcessRunner
    {
        private readonly string _payload;
        private readonly bool _killOverCap;

        internal VolumeCliRunner(string payload, bool killOverCap)
        {
            _payload = payload;
            _killOverCap = killOverCap;
        }

        internal List<CliCall> Calls { get; } = new();

        public async Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true,
            string? workingDirectory = null)
        {
            var command = string.Join(' ', argv);
            var isWrapperExec = argv.Any(argument =>
                string.Equals(argument, IncusCloudInit.ExecWrapperPath, StringComparison.Ordinal));
            Calls.Add(new CliCall(command, maxStdoutBytes, maxStderrBytes, killOnOutputLimit, isWrapperExec));
            if (command.Contains("file push", StringComparison.Ordinal))
                return new ProcessRunResult(0, "", "");
            if (isWrapperExec)
            {
                // Deliver the volume incrementally, as a live CLI would.
                const int chunkChars = 4096;
                for (var offset = 0; offset < _payload.Length; offset += chunkChars)
                {
                    ct.ThrowIfCancellationRequested();
                    var length = Math.Min(chunkChars, _payload.Length - offset);
                    stdoutChunkCallback?.Invoke(_payload.Substring(offset, length));
                    if (offset % (chunkChars * 16) == 0)
                        await Task.Yield();
                }
                var payloadBytes = Encoding.UTF8.GetByteCount(_payload);
                if (_killOverCap && killOnOutputLimit && payloadBytes > (maxStdoutBytes ?? int.MaxValue))
                {
                    var retained = _payload[..Math.Min(_payload.Length, maxStdoutBytes ?? _payload.Length)];
                    return new ProcessRunResult(137, retained, "", StdoutLimitExceeded: true);
                }
                var prefix = _payload[..Math.Min(_payload.Length, maxStdoutBytes ?? _payload.Length)];
                return new ProcessRunResult(
                    0,
                    prefix,
                    "",
                    StdoutLimitExceeded: payloadBytes > (maxStdoutBytes ?? int.MaxValue));
            }
            if (command.Contains("file pull", StringComparison.Ordinal))
                return new ProcessRunResult(0, "0\n", "");
            return new ProcessRunResult(0, "", "");
        }
    }
}
