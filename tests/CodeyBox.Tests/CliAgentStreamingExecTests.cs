using System.Text;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the agent-runner side of the streaming exec policy: every agent
/// turn declares unbounded streaming explicitly, propagates the provider
/// output-bound signal, and keeps session-id extraction working from the
/// snooped stream head when the provider retains only a tail.
/// </summary>
public sealed class CliAgentStreamingExecTests : IDisposable
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
    public async Task RunAsync_DeclaresStreamingPolicyOnAgentTurnExec()
    {
        var sandbox = new StreamingFakeSandbox(
            stdoutChunks: ["hello\n"],
            stderrChunks: [],
            exitCode: 0,
            retainedStdout: "hello\n",
            outputLimitExceeded: false);
        var runner = new StreamingTestRunner();

        await runner.RunAsync(sandbox, "/work", "go", credential: null);

        var exec = sandbox.LastExec;
        Assert.True(exec?.StreamOutputWithoutKill == true);
        Assert.True(exec?.KillOnOutputLimit == false);
    }

    [Fact]
    public async Task RunAsync_VerboseAgentStream_CompletesWithFullFileAndBoundedTail()
    {
        // 10x the old Incus bound through the real file sink.
        var totalBytes = 10 * 4194304;
        var payload = BuildVolumePayload(totalBytes);
        const int chunkChars = 4096;
        var chunks = new List<string>();
        for (var offset = 0; offset < payload.Length; offset += chunkChars)
            chunks.Add(payload.Substring(offset, Math.Min(chunkChars, payload.Length - offset)));
        var tailBytes = 64 * 1024;
        var tail = payload[^tailBytes..];
        var sandbox = new StreamingFakeSandbox(
            stdoutChunks: chunks,
            stderrChunks: [],
            exitCode: 0,
            retainedStdout: tail,
            outputLimitExceeded: false);
        var runner = new StreamingTestRunner();
        var streamPath = NewTempPath("runner-stream");
        await using var capture = new AgentStreamCapture(streamPath, maxBytes: 256L * 1024 * 1024, "work", NullLogger.Instance);

        var result = await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            stdoutChunkCallback: capture.WriteChunk);

        await capture.DisposeAsync();

        Assert.True(result.Success, result.Stderr);
        Assert.False(result.OutputLimitExceeded);
        Assert.Equal(totalBytes, new FileInfo(streamPath).Length);
        var stdout = Assert.IsType<string>(result.Stdout);
        var retainedBytes = Encoding.UTF8.GetByteCount(stdout);
        Assert.True(retainedBytes <= tailBytes, $"retained {retainedBytes} bytes, bound {tailBytes}");
        Assert.Equal(tail, stdout);
    }

    [Fact]
    public async Task RunAsync_OutputBoundKill_PropagatesDistinctSignal()
    {
        var sandbox = new StreamingFakeSandbox(
            stdoutChunks: [],
            stderrChunks: [],
            exitCode: 137,
            retainedStdout: "partial\n",
            outputLimitExceeded: true);
        var runner = new StreamingTestRunner();

        var result = await runner.RunAsync(sandbox, "/work", "go", credential: null);

        Assert.False(result.Success);
        Assert.True(result.OutputLimitExceeded);
        Assert.True(AgentOutputBoundFailure.IsOutputBound(result));
        var detail = AgentOutputBoundFailure.DescribeTurn(new AgentKind("devin"), "work");
        Assert.Contains("output-bound", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("terminated by infrastructure", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TailOnlyRetention_StillExtractsSessionIdFromStreamHead()
    {
        // The provider retains only a tail that lost the leading init event;
        // the runner must still attach the session id snooped from the live
        // stream head so a failed verbose turn stays resumable.
        const string sessionId = "head-session-9";
        var filler = new string('f', 2 * 1024 * 1024);
        var headChunk = $"{{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{sessionId}\"}}\n";
        var sandbox = new StreamingFakeSandbox(
            stdoutChunks: [headChunk, filler],
            stderrChunks: [],
            exitCode: 1,
            retainedStdout: filler[^1024..],
            outputLimitExceeded: false);
        var runner = new HeadResumeRunner();

        var result = await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            stdoutChunkCallback: _ => { },
            captureStructuredStream: false);

        Assert.False(result.Success);
        Assert.Equal(1, sandbox.ExecCount);
        Assert.Equal(sessionId, result.NativeSessionId?.Value);
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

    private string NewTempPath(string purpose)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-runner-stream-{Guid.NewGuid():N}");
        _tempRoots.Add(root);
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{purpose}.jsonl");
    }

    private sealed class StreamingFakeSandbox : ISandbox
    {
        private readonly IReadOnlyList<string> _stdoutChunks;
        private readonly IReadOnlyList<string> _stderrChunks;
        private readonly int _exitCode;
        private readonly string _retainedStdout;
        private readonly bool _outputLimitExceeded;

        internal StreamingFakeSandbox(
            IReadOnlyList<string> stdoutChunks,
            IReadOnlyList<string> stderrChunks,
            int exitCode,
            string retainedStdout,
            bool outputLimitExceeded)
        {
            _stdoutChunks = stdoutChunks;
            _stderrChunks = stderrChunks;
            _exitCode = exitCode;
            _retainedStdout = retainedStdout;
            _outputLimitExceeded = outputLimitExceeded;
        }

        public string Id => "streaming-test-sandbox";
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => SandboxAgentOutputTransportKind.ExecPipe;
        public SandboxBatchLaunchMode BatchLaunchMode => SandboxBatchLaunchMode.Attached;
        public SandboxExec? LastExec { get; private set; }
        public int ExecCount { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            LastExec = exec;
            ExecCount++;
            foreach (var chunk in _stdoutChunks)
                exec.StdoutChunkCallback?.Invoke(chunk);
            foreach (var chunk in _stderrChunks)
                exec.StderrChunkCallback?.Invoke(chunk);
            return Task.FromResult(new SandboxExecResult(
                _exitCode,
                _retainedStdout,
                string.Concat(_stderrChunks),
                StdoutLimitExceeded: _outputLimitExceeded));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StreamingTestRunner : CliAgentRunnerBase
    {
        public override AgentKind Kind => new("test-streaming");

        protected override AgentInvocation BuildInvocation(
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            bool captureStructuredStream = false)
            => new(["sh", "-c", prompt]);
    }

    private sealed class HeadResumeRunner : CliAgentRunnerBase, ICliSessionResumableAgentRunner
    {
        public override AgentKind Kind => new("test-head-resume");

        public bool RequiresStructuredStreamForSessionId => false;

        public IQuotaFailureClassifier SessionResumeQuotaClassifier => new AllowNothingClassifier();

        public string? TryExtractSessionId(string? stdout)
        {
            if (string.IsNullOrEmpty(stdout))
                return null;
            foreach (var line in stdout.Split('\n'))
            {
                const string marker = "\"session_id\":\"";
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index < 0)
                    continue;
                var start = index + marker.Length;
                var end = line.IndexOf('"', start);
                if (end > start)
                    return line[start..end];
            }
            return null;
        }

        public AgentFailureClassification ClassifyFailure(AgentResult result) =>
            new(AgentFailureKind.AuthError, Reason: "test forces a non-resumable failure");

        protected override AgentInvocation BuildInvocation(
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            bool captureStructuredStream = false)
            => new(["sh", "-c", prompt]);

        private sealed class AllowNothingClassifier : IQuotaFailureClassifier
        {
            public QuotaFailureClassification Classify(AgentKind agent, string? stderr, string? stdout) =>
                QuotaFailureClassification.None;

            public QuotaDetection? Detect(AgentKind agent, string? stderr, string? stdout) => null;
        }
    }
}
