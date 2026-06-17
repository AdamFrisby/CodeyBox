using System.Collections.Concurrent;
using CodeyBox.Agents.Crock;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="CrockAgentRunner"/>'s submit→poll lifecycle and
/// for the <see cref="CrockStatusParser"/> terminal-state mapping. The tests
/// drive the runner through a scripted sandbox so the poll loop's mapping of
/// in-progress, succeeded, and failed status outputs can be exercised without
/// a live <c>crock</c> binary.
/// </summary>
public sealed class CrockAgentRunnerTests
{
    [Fact]
    public void Kind_IsCrock()
    {
        Assert.Equal(AgentKind.Crock, new CrockAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Crock_RoundTrips()
    {
        // Pins the kind value so a rename in AgentKind.cs would fail a test
        // here before it ripples through host-side config and DI keying.
        Assert.Equal(AgentKind.Crock, new AgentKind("crock"));
    }

    // --- Status parser terminal-state mapping ----------------------------
    // These cover the explicit acceptance criterion ("one in-progress, one
    // succeeded, one failed") on the bare classifier so the test stays
    // robust to future tweaks to the poll loop's wiring.

    [Fact]
    public void Classify_InProgressOutput_ReturnsInProgress()
    {
        var status = CrockStatusParser.Classify("state: running\ntask-id: task-abc\n");
        Assert.Equal(CrockTaskStateKind.InProgress, status.StateKind);
    }

    [Fact]
    public void Classify_SucceededOutput_ReturnsSucceeded()
    {
        var status = CrockStatusParser.Classify("state: succeeded\nresult: ok\n");
        Assert.Equal(CrockTaskStateKind.Succeeded, status.StateKind);
    }

    [Fact]
    public void Classify_FailedOutput_ReturnsFailed()
    {
        var status = CrockStatusParser.Classify("state: failed\nerror: timeout\n");
        Assert.Equal(CrockTaskStateKind.Failed, status.StateKind);
    }

    [Fact]
    public void Classify_TerminalFailedWinsOverHistoricalRunning()
    {
        // The poll loop should resolve to FAILED even if the status output
        // mentions an earlier in-progress state in its history. The parser
        // checks terminal kinds before in-progress to honour that ordering.
        var status = CrockStatusParser.Classify(
            "history:\n - state: running\n - state: failed\ncurrent: failed\n");
        Assert.Equal(CrockTaskStateKind.Failed, status.StateKind);
    }

    [Fact]
    public void Classify_EmptyOutput_ReturnsUnknown()
    {
        Assert.Equal(CrockTaskStateKind.Unknown, CrockStatusParser.Classify("").StateKind);
        Assert.Equal(CrockTaskStateKind.Unknown, CrockStatusParser.Classify(null).StateKind);
    }

    // --- Task-id extraction -----------------------------------------------

    [Fact]
    public void TryExtractTaskId_LabeledLine_ReturnsId()
    {
        Assert.Equal("abc123",
            CrockStatusParser.TryExtractTaskId("submitted!\nTask-Id: abc123\n"));
    }

    [Fact]
    public void TryExtractTaskId_BareTaskPrefix_ReturnsId()
    {
        Assert.Equal("task-9f8a7b",
            CrockStatusParser.TryExtractTaskId("task-9f8a7b\n"));
    }

    [Fact]
    public void TryExtractTaskId_NoMatch_ReturnsNull()
    {
        Assert.Null(CrockStatusParser.TryExtractTaskId(""));
        Assert.Null(CrockStatusParser.TryExtractTaskId(null));
    }

    // --- Runner end-to-end: submit + scripted polls ----------------------

    private static AgentCredential CrockCred(string configJson = "{}")
        => new(
            AgentKind.Crock,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CrockAgentRunner.ConfigEnvVar] = configJson,
            },
            new Dictionary<string, string>());

    [Fact]
    public async Task RunAsync_NoCredential_FailsWithoutExecutingSubmit()
    {
        var sandbox = new ScriptedSandbox(submit: ("task-abc", 0));
        var runner = new CrockAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        Assert.False(result.Success);
        Assert.Contains(CrockAgentRunner.ConfigEnvVar, result.Summary, StringComparison.Ordinal);
        Assert.False(sandbox.SubmitExecuted);
    }

    [Fact]
    public async Task RunAsync_SubmitFailure_ReturnsFailedResult()
    {
        // Non-zero exit on `crock submit` is a hard failure of the work item
        // — the runner must NOT proceed to poll a synthetic task-id.
        var sandbox = new ScriptedSandbox(submit: ("", 13));
        var runner = new CrockAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.False(result.Success);
        Assert.Contains("crock submit failed", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(sandbox.StatusPolled);
    }

    [Fact]
    public async Task RunAsync_SubmitNoTaskId_FailsAndDoesNotPoll()
    {
        // Submit succeeded but emitted no parseable task-id; the runner
        // must fail rather than fabricate one.
        var sandbox = new ScriptedSandbox(submit: ("nothing here\n", 0));
        var runner = new CrockAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.False(result.Success);
        Assert.Contains("task-id", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(sandbox.StatusPolled);
    }

    [Fact]
    public async Task RunAsync_FastSucceededPoll_ReturnsSuccess()
    {
        // First poll returns SUCCEEDED -> runner resolves the work item
        // positively and surfaces the status stdout through AgentResult.
        var runner = MakeRunnerWithZeroDelays();
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-fast\n", 0),
            statuses: new[] { ("state: succeeded\nresult: ok\n", 0) });

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.True(result.Success);
        Assert.Contains("succeeded", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(sandbox.StatusPolled);
    }

    [Fact]
    public async Task RunAsync_InProgressThenSucceeded_KeepsPollingUntilTerminal()
    {
        // Pins the in-progress branch: at least one running poll must occur
        // before the runner resolves on the succeeded poll.
        var runner = MakeRunnerWithZeroDelays();
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-slow\n", 0),
            statuses: new[]
            {
                ("state: running\n", 0),
                ("state: running\n", 0),
                ("state: completed\n", 0),
            });

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.True(result.Success);
        Assert.Equal(3, sandbox.StatusPollCount);
    }

    [Fact]
    public async Task RunAsync_FailedPoll_ReturnsFailedAgentResult()
    {
        var runner = MakeRunnerWithZeroDelays();
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-doomed\n", 0),
            statuses: new[] { ("state: failed\nerror: model_error\n", 0) });

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.False(result.Success);
        Assert.Contains("failed", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ProgressCallback_FiresOnEachPoll()
    {
        var runner = MakeRunnerWithZeroDelays();
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-stream\n", 0),
            statuses: new[]
            {
                ("state: running\n", 0),
                ("state: succeeded\n", 0),
            });
        var chunks = new ConcurrentQueue<string>();

        var result = await runner.RunAsync(sandbox, "/work", "prompt",
            credential: CrockCred(), stdoutChunkCallback: chunks.Enqueue);

        Assert.True(result.Success);
        // Submission + 2 polls = 3 progress envelopes minimum.
        Assert.True(chunks.Count >= 3, $"expected ≥3 progress chunks, got {chunks.Count}");
        Assert.Contains(chunks, c => c.Contains("codeybox.crock.progress", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_PromptIsPassedOnStdin_NotArgv()
    {
        // MAX_ARG_STRLEN is 128 KiB on Linux; rework prompts can exceed it.
        // The runner must put the prompt on the SandboxExec.Stdin channel
        // for the submit step, mirroring the other CLI runners.
        const string prompt = "do the thing in great detail";
        var runner = MakeRunnerWithZeroDelays();
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-stdin\n", 0),
            statuses: new[] { ("state: succeeded\n", 0) });

        await runner.RunAsync(sandbox, "/work", prompt, credential: CrockCred());

        Assert.NotNull(sandbox.CapturedSubmitExec);
        Assert.DoesNotContain(prompt, sandbox.CapturedSubmitExec!.Argv);
        Assert.Equal(prompt, sandbox.CapturedSubmitExec!.Stdin);
        Assert.Equal("crock", sandbox.CapturedSubmitExec!.Argv[0]);
        Assert.Equal("submit", sandbox.CapturedSubmitExec!.Argv[1]);
    }

    private static CrockAgentRunner MakeRunnerWithZeroDelays() => new()
    {
        // Sub-tick poll intervals so the test wall-clock stays in microseconds.
        InitialPollInterval = TimeSpan.FromMilliseconds(1),
        MaxPollInterval = TimeSpan.FromMilliseconds(1),
    };

    /// <summary>
    /// Sandbox that scripts a `crock submit` response and an ordered queue of
    /// `crock status` responses. After the queue is drained the sandbox keeps
    /// repeating the last status to mimic a CLI that returns the same
    /// terminal state on every subsequent poll.
    /// </summary>
    private sealed class ScriptedSandbox : ISandbox
    {
        private readonly (string Stdout, int ExitCode) _submit;
        private readonly Queue<(string Stdout, int ExitCode)> _statuses;
        private (string Stdout, int ExitCode)? _lastStatus;

        public ScriptedSandbox(
            (string Stdout, int ExitCode) submit,
            IEnumerable<(string Stdout, int ExitCode)>? statuses = null)
        {
            _submit = submit;
            _statuses = new Queue<(string, int)>(
                statuses ?? Array.Empty<(string, int)>());
        }

        public string Id => "scripted-crock";
        public bool SubmitExecuted { get; private set; }
        public bool StatusPolled => StatusPollCount > 0;
        public int StatusPollCount { get; private set; }
        public SandboxExec? CapturedSubmitExec { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var argv = exec.Argv;
            // Auth materialisation bash script — pass through with success.
            if (argv.Count >= 3
                && argv[0] == "bash"
                && argv[1] == "-c"
                && argv[2].Contains(CrockAgentRunner.ConfigEnvVar, StringComparison.Ordinal))
            {
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }

            if (argv.Count >= 2 && argv[0] == "crock" && argv[1] == "submit")
            {
                SubmitExecuted = true;
                CapturedSubmitExec = exec;
                return Task.FromResult(new SandboxExecResult(_submit.ExitCode, _submit.Stdout, ""));
            }

            if (argv.Count >= 2 && argv[0] == "crock" && argv[1] == "status")
            {
                StatusPollCount++;
                if (_statuses.Count > 0)
                {
                    _lastStatus = _statuses.Dequeue();
                }
                var resp = _lastStatus ?? ("state: running\n", 0);
                return Task.FromResult(new SandboxExecResult(resp.ExitCode, resp.Stdout, ""));
            }

            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
