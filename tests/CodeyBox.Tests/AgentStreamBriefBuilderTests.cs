using System.Text;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance coverage for <see cref="AgentStreamBriefBuilder"/> — the
/// concrete <see cref="ICrossAgentHandoffBriefBuilder"/> wired into the
/// preprocessor chain. The end-to-end injection (fence + sanitisation +
/// length cap) is covered by <c>CrossAgentHandoffPromptPreprocessorTests</c>;
/// here we pin the builder's own behaviour.
/// </summary>
public sealed class AgentStreamBriefBuilderTests
{
    [Fact]
    public async Task BuildAsync_ReturnsCondensedSummary_FromRecordedStreamAndBranchState()
    {
        var stream = new StubStreamStore();
        var workItemId = WorkItemId.New();
        stream.AddCapture(workItemId, phase: "work", iteration: 1, json: """
            {"type":"system","subtype":"init","session_id":"abc","tools":["Read","Edit","Bash"]}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file":"a.cs"}}],"usage":{"input_tokens":1200,"output_tokens":42}}}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Edit","input":{"file":"a.cs"}}]}}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","content":"ok"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I refactored the auth middleware but the new tests still fail."}],"usage":{"input_tokens":1800,"output_tokens":120}}}
            {"type":"result","subtype":"success","result":"I refactored the auth middleware but the new tests still fail.","total_cost_usd":0.13,"duration_ms":42000}
            """);
        var sandbox = new ScriptedGitSandbox()
            .OnArgs(["git", "-C", "/work", "rev-parse", "--short", "HEAD"], "f00ba12")
            .OnArgs(["git", "-C", "/work", "log", "--no-color", "--max-count", "5", "--pretty=format:%h %s"],
                "f00ba12 wip: tests still red\nbeefcafe wip: refactor auth middleware")
            .OnArgs(["git", "-C", "/work", "diff", "--stat", "origin/main...HEAD"],
                " src/Auth.cs | 24 ++++++++++++--\n 1 file changed, 22 insertions(+), 2 deletions(-)");

        var tuning = NewTuning(enable: true);
        var builder = new AgentStreamBriefBuilder(
            tuning,
            NullLogger<AgentStreamBriefBuilder>.Instance,
            parsers: [new ClaudeStreamParser()],
            streams: stream);

        var brief = await builder.BuildAsync(
            NewContext(workItemId, sandbox, AgentKind.Codex, phase: AgentPromptPhase.Work, iteration: 1),
            priorAgent: AgentKind.Claude);

        Assert.NotNull(brief);
        Assert.Contains("Prior agent: claude", brief);
        Assert.Contains("Now routed to: codex", brief);
        Assert.Contains("Prior agent execution summary", brief);
        // Tool kinds are aggregated as a SUMMARY, not a per-call dump.
        Assert.Contains("Read×1", brief);
        Assert.Contains("Edit×1", brief);
        Assert.Contains("Prior agent's closing message (tail)", brief);
        Assert.Contains("auth middleware", brief);
        Assert.Contains("Branch state on disk", brief);
        Assert.Contains("f00ba12", brief);
        Assert.Contains("wip: tests still red", brief);
        Assert.Contains("1 file changed", brief);
    }

    [Fact]
    public async Task BuildAsync_ReturnsNull_WhenHandoffSeedingDisabled()
    {
        var stream = new StubStreamStore();
        var workItemId = WorkItemId.New();
        stream.AddCapture(workItemId, "work", 1, """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hello"}]}}
            {"type":"result","subtype":"success","result":"hello","duration_ms":1000}
            """);
        var sandbox = new ScriptedGitSandbox()
            .OnArgs(["git", "-C", "/work", "rev-parse", "--short", "HEAD"], "f00ba12");

        var tuning = NewTuning(enable: false);
        var builder = new AgentStreamBriefBuilder(
            tuning,
            NullLogger<AgentStreamBriefBuilder>.Instance,
            parsers: [new ClaudeStreamParser()],
            streams: stream);

        var brief = await builder.BuildAsync(
            NewContext(workItemId, sandbox, AgentKind.Codex, phase: AgentPromptPhase.Work, iteration: 1),
            priorAgent: AgentKind.Claude);

        Assert.Null(brief);
    }

    [Fact]
    public async Task BuildAsync_ReturnsNull_WhenStreamAndGitBothUnavailable()
    {
        var tuning = NewTuning(enable: true);
        var builder = new AgentStreamBriefBuilder(
            tuning,
            NullLogger<AgentStreamBriefBuilder>.Instance,
            parsers: [new ClaudeStreamParser()],
            streams: new StubStreamStore()); // no captures
        var sandbox = new ScriptedGitSandbox(); // every git command exits non-zero

        var brief = await builder.BuildAsync(
            NewContext(WorkItemId.New(), sandbox, AgentKind.Codex, phase: AgentPromptPhase.Work, iteration: 1),
            priorAgent: AgentKind.Claude);

        Assert.Null(brief);
    }

    [Fact]
    public async Task BuildAsync_FallsBackToBranchOnly_WhenStreamMissing()
    {
        var tuning = NewTuning(enable: true);
        var sandbox = new ScriptedGitSandbox()
            .OnArgs(["git", "-C", "/work", "rev-parse", "--short", "HEAD"], "deadbee")
            .OnArgs(["git", "-C", "/work", "log", "--no-color", "--max-count", "5", "--pretty=format:%h %s"],
                "deadbee wip: nothing yet");
        var builder = new AgentStreamBriefBuilder(
            tuning,
            NullLogger<AgentStreamBriefBuilder>.Instance,
            parsers: [new ClaudeStreamParser()],
            streams: new StubStreamStore());

        var brief = await builder.BuildAsync(
            NewContext(WorkItemId.New(), sandbox, AgentKind.Codex, phase: AgentPromptPhase.Work, iteration: 1),
            priorAgent: AgentKind.Claude);

        Assert.NotNull(brief);
        Assert.Contains("Prior agent: claude", brief);
        Assert.DoesNotContain("Prior agent execution summary", brief);
        Assert.Contains("Branch state on disk", brief);
        Assert.Contains("deadbee", brief);
    }

    [Fact]
    public async Task BuildAsync_ReturnsNull_AndDoesNotThrow_WhenStreamParserThrows()
    {
        var tuning = NewTuning(enable: true);
        var stream = new StubStreamStore();
        var workItemId = WorkItemId.New();
        stream.AddCapture(workItemId, "work", 1, "garbage");
        var sandbox = new ScriptedGitSandbox(); // nothing — brief would be null even with the throwing parser

        var builder = new AgentStreamBriefBuilder(
            tuning,
            NullLogger<AgentStreamBriefBuilder>.Instance,
            parsers: [new ThrowingStreamParser(AgentKind.Claude)],
            streams: stream);

        var brief = await builder.BuildAsync(
            NewContext(workItemId, sandbox, AgentKind.Codex, phase: AgentPromptPhase.Work, iteration: 1),
            priorAgent: AgentKind.Claude);

        // Graceful: the parser throwing inside TryRead must NOT be allowed to
        // propagate. The brief is null because neither the stream nor the
        // sandbox produced anything readable.
        Assert.Null(brief);
    }

    [Fact]
    public async Task BuildAsync_IsCappedToMaxBriefChars_SoOversizedStreamCannotBlowBudget()
    {
        var tuning = NewTuning(enable: true);
        var stream = new StubStreamStore();
        var workItemId = WorkItemId.New();
        // 64 KiB of repeating "0" — well above the builder's 8 KiB pre-cap.
        var hugeFinal = new string('0', 64 * 1024);
        stream.AddCapture(workItemId, "work", 1,
            "{\"type\":\"result\",\"subtype\":\"success\",\"result\":" + System.Text.Json.JsonSerializer.Serialize(hugeFinal) + ",\"duration_ms\":1000}");
        var sandbox = new ScriptedGitSandbox()
            .OnArgs(["git", "-C", "/work", "rev-parse", "--short", "HEAD"], "f00ba12");

        var builder = new AgentStreamBriefBuilder(
            tuning,
            NullLogger<AgentStreamBriefBuilder>.Instance,
            parsers: [new ClaudeStreamParser()],
            streams: stream);

        var brief = await builder.BuildAsync(
            NewContext(workItemId, sandbox, AgentKind.Codex, phase: AgentPromptPhase.Work, iteration: 1),
            priorAgent: AgentKind.Claude);

        Assert.NotNull(brief);
        Assert.True(
            brief!.Length <= AgentStreamBriefBuilder.MaxBriefChars,
            $"brief length {brief.Length} should be <= {AgentStreamBriefBuilder.MaxBriefChars}");
    }

    [Fact]
    public async Task BuildAsync_ReturnsNull_WhenGitExecThrows()
    {
        // Defence-in-depth: a sandbox that raises on every ExecAsync must not
        // poison the builder. The branch-state probe should catch and the
        // stream branch alone should still produce a brief if anything is
        // available. Here nothing is available, so we expect null.
        var tuning = NewTuning(enable: true);
        var builder = new AgentStreamBriefBuilder(
            tuning,
            NullLogger<AgentStreamBriefBuilder>.Instance,
            parsers: [new ClaudeStreamParser()],
            streams: new StubStreamStore());

        var brief = await builder.BuildAsync(
            NewContext(WorkItemId.New(), new ThrowingSandbox(), AgentKind.Codex, phase: AgentPromptPhase.Work, iteration: 1),
            priorAgent: AgentKind.Claude);

        Assert.Null(brief);
    }

    private static PromptContext NewContext(
        WorkItemId itemId,
        ISandbox sandbox,
        AgentKind currentAgent,
        AgentPromptPhase phase,
        int iteration)
        => new(
            itemId,
            currentAgent,
            phase,
            iteration,
            NewProject(),
            sandbox,
            "/work");

    private static Project NewProject() => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test Project",
        RepositoryUrl = "https://example.invalid/repo.git",
        DefaultBaseBranch = "main",
    };

    private static PipelineTuningSnapshot NewTuning(bool enable)
        => new(new PipelineTuningOptions { EnableHandoffSeeding = enable });

    private sealed class StubStreamStore : IAgentStreamStore
    {
        private readonly List<(WorkItemId Id, AgentStreamFile File, byte[] Body)> _captures = new();
        public AgentStreamsOptions Options { get; } = new() { Enabled = true, Path = "/tmp/cb-test", MaxFileSizeMb = 1 };

        public void AddCapture(WorkItemId id, string phase, int iteration, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var name = $"{phase}-{iteration}-{Guid.NewGuid().ToString("N")[..6]}.jsonl";
            _captures.Add((
                id,
                new AgentStreamFile(name, phase, iteration, body.Length, body.Count(b => b == (byte)'\n'), DateTimeOffset.UtcNow),
                body));
        }

        public Task<AgentStreamCapture?> BeginCaptureAsync(WorkItemId workItemId, string phase, int iteration, CancellationToken ct = default)
            => Task.FromResult<AgentStreamCapture?>(null);

        public Task<IReadOnlyList<AgentStreamFile>> ListAsync(WorkItemId workItemId, int limit = AgentStreamStore.DefaultListLimit, bool includeLineCount = false, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentStreamFile>>(
                _captures.Where(c => c.Id == workItemId).Select(c => c.File).ToList());

        public Task<AgentStreamFile?> GetAsync(WorkItemId workItemId, string fileName, bool includeLineCount = false, CancellationToken ct = default)
        {
            var hit = _captures.FirstOrDefault(c => c.Id == workItemId && c.File.FileName == fileName);
            return Task.FromResult<AgentStreamFile?>(hit.File);
        }

        public Task<Stream?> OpenReadAsync(WorkItemId workItemId, string fileName, CancellationToken ct = default)
        {
            var hit = _captures.FirstOrDefault(c => c.Id == workItemId && c.File.FileName == fileName);
            if (hit.Body is null)
                return Task.FromResult<Stream?>(null);
            return Task.FromResult<Stream?>(new MemoryStream(hit.Body, writable: false));
        }

        public Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class ScriptedGitSandbox : ISandbox
    {
        private readonly Dictionary<string, string> _responses = new();
        public string Id => "scripted";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ScriptedGitSandbox OnArgs(string[] argv, string stdout)
        {
            _responses[Key(argv)] = stdout;
            return this;
        }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (_responses.TryGetValue(Key(exec.Argv), out var stdout))
                return Task.FromResult(new SandboxExecResult(0, stdout, ""));
            return Task.FromResult(new SandboxExecResult(1, "", "no such ref"));
        }

        private static string Key(IReadOnlyList<string> argv) => string.Join('\0', argv);
    }

    private sealed class ThrowingSandbox : ISandbox
    {
        public string Id => "throwing";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => throw new InvalidOperationException("sandbox offline");
    }

    private sealed class ThrowingStreamParser(AgentKind kind) : IAgentStreamParser
    {
        public AgentKind Kind { get; } = kind;
        public Task<AgentStreamSummary> ParseAsync(Stream jsonlFile, CancellationToken ct = default)
            => throw new InvalidOperationException("parser exploded");
    }
}
