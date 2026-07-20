using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

public sealed class StreamPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codeybox-agent-streams-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private AgentStreamStore Store(int maxFileSizeMb = 32, int retainedDays = 14, bool enabled = true) =>
        new(new AgentStreamsOptions
        {
            Enabled = enabled,
            Path = _root,
            MaxFileSizeMb = maxFileSizeMb,
            RetainedDays = retainedDays,
        }, NullLogger<AgentStreamStore>.Instance);

    [Fact]
    public async Task FakeAgentLines_ArePersistedToExpectedJsonlFile()
    {
        var itemId = WorkItemId.New();
        var store = Store();

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
        {
            Assert.NotNull(capture);
            capture!.WriteChunk("{\"type\":\"system\"}\n{\"type\":\"assistant\"}\n{\"type\":\"result\"}\n");
        }

        var defaultFiles = await store.ListAsync(itemId);
        Assert.Null(Assert.Single(defaultFiles).LineCount);

        var files = await store.ListAsync(itemId, includeLineCount: true);
        var file = Assert.Single(files);
        Assert.Equal("work", file.Phase);
        Assert.Equal(1, file.Iteration);
        Assert.Equal(3L, file.LineCount);
        Assert.Matches(@"^work-1-[0-9a-f]{6}\.jsonl$", file.FileName);

        var lines = await File.ReadAllLinesAsync(Path.Combine(_root, itemId.ToString(), file.FileName));
        Assert.Equal(["system", "assistant", "result"], lines.Select(ReadType).ToArray());
        Assert.All(lines, AssertDoesNotHaveCreatedAt);
    }

    [Fact]
    public async Task ExistingEventTimestamps_ArePreservedWithoutDuplicateCaptureTimestamp()
    {
        var itemId = WorkItemId.New();
        var store = Store();

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
            capture!.WriteChunk("{\"type\":\"assistant\",\"timestamp\":\"2026-01-01T00:00:00Z\"}\n");

        var file = Assert.Single(await store.ListAsync(itemId));
        var line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_root, itemId.ToString(), file.FileName)));
        using var parsed = JsonDocument.Parse(line);
        Assert.Equal("2026-01-01T00:00:00Z", parsed.RootElement.GetProperty("timestamp").GetString());
        Assert.False(parsed.RootElement.TryGetProperty("created_at", out _));
    }

    [Fact]
    public async Task ListAsync_LastActivityAt_ReflectsLastWriteAdvancingPastCreation()
    {
        // Deliverable #1 (crock batch-latency liveness) depends on the store
        // mapping LastActivityAt to the file's LAST-WRITE, not its creation
        // instant: a crock run appends a per-poll chunk to a SINGLE .jsonl while
        // waiting on the Anthropic Message Batches API, so only the last-write
        // advances. The progress watchdog reads LastActivityAt as the liveness
        // signal, so a mapping that pinned it to creation would let the watchdog
        // kill an actively-polling batch. Drive the real store, advance the
        // file's last-write past its creation instant, and assert the mapping
        // surfaces that as LastActivityAt while CapturedAt stays at creation.
        var itemId = WorkItemId.New();
        var store = Store();

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
            capture!.WriteChunk("{\"type\":\"assistant\"}\n");

        var before = Assert.Single(await store.ListAsync(itemId));
        // A single-append file: last-write == creation, so the two stamps agree.
        Assert.Equal(before.CapturedAt, before.LastActivityAt);

        // A later per-poll append advances the file's last-write beyond creation.
        var path = Path.Combine(_root, itemId.ToString(), before.FileName);
        var advanced = before.CapturedAt.UtcDateTime.AddMinutes(10);
        File.SetLastWriteTimeUtc(path, advanced);

        var after = Assert.Single(await store.ListAsync(itemId));
        Assert.True(
            after.LastActivityAt > after.CapturedAt,
            $"LastActivityAt ({after.LastActivityAt:O}) must advance past CapturedAt ({after.CapturedAt:O})");
        Assert.Equal(new DateTimeOffset(advanced, TimeSpan.Zero), after.LastActivityAt);
    }

    [Fact]
    public async Task ListAsync_LastActivityAt_ClampsToCreationWhenLastWritePrecedesIt()
    {
        // Defensive clamp: a filesystem (or a manual mtime rewind) whose
        // last-write precedes the creation instant must never move the liveness
        // stamp BEFORE CapturedAt, which would make a fresh file look stale.
        var itemId = WorkItemId.New();
        var store = Store();

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
            capture!.WriteChunk("{\"type\":\"assistant\"}\n");

        var before = Assert.Single(await store.ListAsync(itemId));
        var path = Path.Combine(_root, itemId.ToString(), before.FileName);
        File.SetLastWriteTimeUtc(path, before.CapturedAt.UtcDateTime.AddMinutes(-10));

        var after = Assert.Single(await store.ListAsync(itemId));
        Assert.Equal(after.CapturedAt, after.LastActivityAt);
    }

    private static string ReadType(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("type").GetString() ?? "";
    }

    private static void AssertDoesNotHaveCreatedAt(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        Assert.False(parsed.RootElement.TryGetProperty("created_at", out _));
    }
}

[Collection("Pipeline integration")]
public sealed class PipelineAgentStreamPersistenceTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-agent-stream-pipeline-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task PipelineRunner_WiresAgentStdoutChunksIntoWorkPhaseJsonlFile()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var streamStore = new AgentStreamStore(
            new AgentStreamsOptions { Path = Path.Combine(_workspace, "streams") },
            NullLogger<AgentStreamStore>.Instance);
        var timingStore = new RecordingTimingStore();
        using var tp = TestSupport.BuildPipeline(_workspace, seed, agentStreams: streamStore, timingStore: timingStore);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("streamed.txt", "streamed\n"));
        tp.Agent.StdoutChunks.Enqueue("{\"type\":\"system\"}\n");
        tp.Agent.StdoutChunks.Enqueue("{\"type\":\"assistant\",\"delta\":\"hello\"}\n{\"type\":\"result\"}\n");
        tp.Agent.ResultStdout = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}}\n{\"type\":\"result\",\"result\":\"done\"}\n";

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "stream integration",
            Prompt = "write a file",
            WorkBranch = "feature/stream-integration",
            State = WorkItemState.Queued,
            WorkTimeout = TimeSpan.FromMinutes(5),
            MergeTimeout = TimeSpan.FromMinutes(5),
        };
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        var workFile = Assert.Single(await streamStore.ListAsync(item.Id), f => f.Phase == "work");
        var lines = await File.ReadAllLinesAsync(Path.Combine(streamStore.Options.Path, item.Id.ToString(), workFile.FileName));
        Assert.Equal(["system", "assistant", "result"], lines.Select(ReadType).ToArray());
        Assert.All(lines, AssertDoesNotHaveCreatedAt);
        Assert.DoesNotContain(timingStore.CompletedRows, r => r.Step.StartsWith("agent.tool_call.", StringComparison.Ordinal));
        Assert.DoesNotContain(timingStore.CompletedRows, r => r.Step == "agent.thinking_aggregate");
    }

    private static string ReadType(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("type").GetString() ?? "";
    }

    private static void AssertDoesNotHaveCreatedAt(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        Assert.False(parsed.RootElement.TryGetProperty("created_at", out _));
    }

    // NOTE: merge-phase stream capture was dropped when the merge phase moved
    // to a host-side clean merge (no agent → no stream) and an in-VM agentic
    // conflict resolver that does not wire the AgentStreamStore capture sink
    // (AgenticConflictResolver runs the agent with stdoutChunkCallback=null and
    // never opens a merge stream file; PipelineRunner's mergeStructuredStreamCaptured
    // is now vestigially always false). The merge-stream coverage this test used
    // to provide is therefore no longer producible from the test harness; it now
    // verifies the work/audit/rework streams only. See the report flag for the
    // production gap (resolver should capture its agent stream).
    [Fact]
    public async Task PipelineRunner_CapturesWorkAuditAndReworkStreams()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var streamStore = new AgentStreamStore(
            new AgentStreamsOptions { Path = Path.Combine(_workspace, "streams") },
            NullLogger<AgentStreamStore>.Instance);
        var auditors = new IAuditor[]
        {
            new StreamingLlmAuditor("security:llm-review", failFirstIteration: true),
            new StreamingLlmAuditor("completeness:llm-review", failFirstIteration: false),
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditors),
            maxAuditIterations: 2,
            maxLlmAuditorParallelism: 1,
            agentStreams: streamStore);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("streamed.txt", "work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("streamed.txt", "rework\n"));
        tp.Agent.StdoutChunkBatches.Enqueue(["{\"type\":\"result\",\"phase\":\"work\"}\n"]);
        tp.Agent.StdoutChunkBatches.Enqueue(["{\"type\":\"result\",\"phase\":\"rework\"}\n"]);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "multi phase stream integration",
            Prompt = "write and revise a file",
            WorkBranch = "feature/multi-phase-streams",
            State = WorkItemState.Queued,
            WorkTimeout = TimeSpan.FromMinutes(5),
            MergeTimeout = TimeSpan.FromMinutes(5),
        };
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        var files = await streamStore.ListAsync(item.Id);
        Assert.Contains(files, f => f.Phase == "work" && f.Iteration == 1);
        Assert.Contains(files, f => f.Phase == "audit-llm-security:llm-review" && f.Iteration == 1);
        Assert.Contains(files, f => f.Phase == "audit-llm-completeness:llm-review" && f.Iteration == 1);
        // Rework that addresses audit iteration N is dispatched as iteration N+1
        // (the input audit iteration N+1 will evaluate); the stream filename
        // therefore carries iteration=2 for the first rework, matching the
        // work_item_iterations dispatch row.
        Assert.Contains(files, f => f.Phase == "rework" && f.Iteration == 2);
        Assert.Contains(files, f => f.Phase == "audit-llm-security:llm-review" && f.Iteration == 2);
        Assert.Contains(files, f => f.Phase == "audit-llm-completeness:llm-review" && f.Iteration == 2);
        // The merge phase no longer captures an agent stream (clean merge = no
        // agent; the agentic conflict resolver does not wire a stream sink), so
        // there is no merge stream file and the total is 6 (work + 2×audit ×2
        // iterations + rework).
        Assert.DoesNotContain(files, f => f.Phase == "merge");
        Assert.Equal(6, files.Count);

        foreach (var file in files)
        {
            var path = Path.Combine(streamStore.Options.Path, item.Id.ToString(), file.FileName);
            var line = Assert.Single(await File.ReadAllLinesAsync(path));
            using var parsed = JsonDocument.Parse(line);
            Assert.Equal("result", parsed.RootElement.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task PipelineRunner_WhenStructuredStreamUnsupported_StillOpensCaptureFileForPlaintextFallback()
    {
        // Anti-regression: a previous condition only opened the
        // AgentStreamStore capture file when CanCaptureStructuredStreamAsync
        // returned true, which left plaintext agents (opencode, agy without
        // --output-format stream-json) with zero captured files and therefore
        // zero summary rows. The fix lifted the capture open to "agent
        // streams enabled" — verify it here so a regressing edit (restoring
        // `streamCapture = canCaptureStructuredStream ? Begin... : null`) is
        // caught at the production code path, not just via the
        // StreamAnalysisService parser-fallback tests.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var streamStore = new AgentStreamStore(
            new AgentStreamsOptions { Path = Path.Combine(_workspace, "streams-plaintext") },
            NullLogger<AgentStreamStore>.Instance);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, agentStreams: streamStore);
        tp.Agent.StructuredStreamSupportResult = false;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("plaintext.txt", "plaintext\n"));
        // Plaintext stdout — no JSON framing, like a real opencode/agy run.
        tp.Agent.StdoutChunkBatches.Enqueue([
            "starting opencode run\n",
            "applied patch to plaintext.txt\n",
            "done after 12.7s\n",
        ]);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "plaintext capture",
            Prompt = "write a file",
            WorkBranch = "feature/plaintext-capture",
            State = WorkItemState.Queued,
            WorkTimeout = TimeSpan.FromMinutes(5),
            MergeTimeout = TimeSpan.FromMinutes(5),
        };
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // The agent was not asked to emit structured stream-json on the work
        // dispatch, but the capture file must still exist with the plaintext
        // stdout teed in. (A clean merge runs host-side with no agent, so there
        // is no merge stream to capture; the merge-stream coverage this test
        // used to assert is no longer producible — see the report flag for the
        // resolver stream-capture gap.)
        Assert.NotEmpty(tp.Agent.CaptureStructuredStreamCalls);
        Assert.All(tp.Agent.CaptureStructuredStreamCalls, Assert.False);
        var files = await streamStore.ListAsync(item.Id);
        var workFile = Assert.Single(files, f => f.Phase == "work");
        var workContents = await File.ReadAllTextAsync(Path.Combine(streamStore.Options.Path, item.Id.ToString(), workFile.FileName));
        Assert.Contains("starting opencode run", workContents);
        Assert.Contains("done after 12.7s", workContents);
        Assert.DoesNotContain(files, f => f.Phase == "merge");
    }

    [Fact]
    public async Task PipelineRunner_LlmAuditWhenStructuredStreamUnsupported_StillOpensPlaintextCaptureFile()
    {
        // Normal work-item LLM auditors use PipelineRunner.ExecAuditorAsync,
        // not ReleaseService. Pin that branch separately: when the selected
        // runner says structured stream-json is unavailable, the auditor must
        // still receive a stdout capture callback and the persisted file must
        // carry the plaintext chunks.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var streamStore = new AgentStreamStore(
            new AgentStreamsOptions { Path = Path.Combine(_workspace, "streams-plaintext-audit") },
            NullLogger<AgentStreamStore>.Instance);
        var auditor = new PlaintextLlmAuditor();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest([auditor]),
            maxAuditIterations: 1,
            agentStreams: streamStore);
        tp.Agent.StructuredStreamSupportResult = false;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("plaintext-audit.txt", "plaintext\n"));

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "plaintext audit capture",
            Prompt = "write a file",
            WorkBranch = "feature/plaintext-audit-capture",
            State = WorkItemState.Queued,
            WorkTimeout = TimeSpan.FromMinutes(5),
            MergeTimeout = TimeSpan.FromMinutes(5),
        };
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([false], auditor.CaptureStructuredStreamCalls);
        var auditFile = Assert.Single(await streamStore.ListAsync(item.Id), f => f.Phase == "audit-llm-plaintext:llm-review");
        var auditContents = await File.ReadAllTextAsync(Path.Combine(streamStore.Options.Path, item.Id.ToString(), auditFile.FileName));
        Assert.Contains("plaintext audit chunk", auditContents);
    }

    [Fact]
    public async Task PipelineRunner_WhenAgentStreamsDisabled_DoesNotProbeStructuredCapture()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var streamStore = new AgentStreamStore(
            new AgentStreamsOptions { Enabled = false, Path = Path.Combine(_workspace, "streams-disabled") },
            NullLogger<AgentStreamStore>.Instance);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, agentStreams: streamStore);
        var method = typeof(PipelineRunner).GetMethod(
            "CanCaptureStructuredStreamAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task<bool>)method.Invoke(
            tp.Pipeline,
            [tp.Agent, new AlwaysSucceedSandbox(), "work", CancellationToken.None])!;

        Assert.False(await task);
        Assert.Equal(0, tp.Agent.StructuredStreamSupportProbeCount);
        Assert.False(Directory.Exists(streamStore.Options.Path));
    }

    [Fact]
    public async Task PipelineRunner_WhenAgentStreamsDisabled_ResumableRunnerStillForcesStructuredCapture()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var streamStore = new AgentStreamStore(
            new AgentStreamsOptions { Enabled = false, Path = Path.Combine(_workspace, "streams-disabled-marker") },
            NullLogger<AgentStreamStore>.Instance);
        var auditor = new CaptureRecordingLlmAuditor();
        // The resumable runner must force structured capture on EVERY dispatch,
        // including the merge agent. A clean merge runs host-side with no agent,
        // so induce a README conflict (work writes README; the one-shot auditor
        // advances main's README during audit) → the merge runs the agentic
        // resolver, giving the third forced-capture dispatch.
        var mergeConflictAuditor = new MergeConflictAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest([auditor, mergeConflictAuditor]),
            maxAuditIterations: 2,
            agentStreams: streamStore,
            cliSessionResumableAgent: true);
        mergeConflictAuditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "rework\n"));
        tp.Agent.ConflictResolutionPlan.Enqueue(_ => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["README.md"] = "main side\nrework\n",
        });

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "marker capture integration",
            Prompt = "write and revise a file",
            WorkBranch = "feature/marker-capture",
            State = WorkItemState.Queued,
            WorkTimeout = TimeSpan.FromMinutes(5),
            MergeTimeout = TimeSpan.FromMinutes(5),
        };
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([true, true, true], tp.Agent.CaptureStructuredStreamCalls);
        Assert.Equal([true, true], auditor.CaptureStructuredStreamCalls);
        Assert.Equal(0, tp.Agent.StructuredStreamSupportProbeCount);
        Assert.False(Directory.Exists(streamStore.Options.Path));
    }

    /// <summary>
    /// One-shot tool auditor that advances <c>main</c>'s copy of a file on the
    /// first audit iteration so a work branch touching the same file merges with
    /// a conflict — routing the merge phase through the agentic conflict resolver
    /// (which runs the merge agent). Advancing only once keeps later audit
    /// iterations from re-committing an unchanged tree (which git rejects).
    /// </summary>
    private sealed class MergeConflictAdvancingAuditor : IAuditor
    {
        private readonly string _workspace;
        private readonly string _path;
        private readonly string _content;
        private bool _advanced;

        public string? GitRoot { get; set; }
        public string Name => "advance-main";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public MergeConflictAdvancingAuditor(string workspace, string path, string content)
        {
            _workspace = workspace;
            _path = path;
            _content = content;
        }

        public async Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
            if (_advanced)
                return new AuditResult(true, []);
            if (GitRoot is null)
                throw new InvalidOperationException("GitRoot must be assigned before the auditor runs.");
            var barePath = Path.Combine(GitRoot, context.WorkItemId + ".git");
            var clone = Path.Combine(_workspace, "advance-main-" + Guid.NewGuid().ToString("N")[..8]);
            await TestSupport.RunGit(_workspace, "clone", barePath, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", context.BaseBranch);
            await File.WriteAllTextAsync(Path.Combine(clone, _path), _content);
            await TestSupport.RunGit(clone, "commit", "-am", "advance main during audit");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{context.BaseBranch}");
            _advanced = true;
            return new AuditResult(true, []);
        }
    }

    private sealed class StreamingLlmAuditor : IAuditor
    {
        private readonly bool _failFirstIteration;

        public StreamingLlmAuditor(string name, bool failFirstIteration)
        {
            Name = name;
            _failFirstIteration = failFirstIteration;
        }

        public string Name { get; }
        public string Kind => "llm";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            context.StdoutChunkCallback?.Invoke(
                $"{{\"type\":\"result\",\"auditor\":\"{Name}\",\"iteration\":{context.Iteration}}}\n");
            if (_failFirstIteration && context.Iteration == 1)
            {
                return Task.FromResult(new AuditResult(false, [
                    new AuditFinding(Name, AuditSeverity.Error, "needs rework", "first pass fails"),
                ], RawOutput: $"audit {Name} failed"));
            }

            return Task.FromResult(new AuditResult(true, [], RawOutput: $"audit {Name} passed"));
        }
    }

    private sealed class CaptureRecordingLlmAuditor : IAuditor
    {
        private int _calls;

        public string Name => "capture-recording";
        public string Kind => "llm";
        public AuditCapabilities Required => AuditCapabilities.None;
        public List<bool> CaptureStructuredStreamCalls { get; } = new();

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
            CaptureStructuredStreamCalls.Add(context.CaptureStructuredStream);
            _calls++;
            if (_calls == 1)
            {
                return Task.FromResult(new AuditResult(false, [
                    new AuditFinding(Name, AuditSeverity.Error, "needs rework", "first pass fails"),
                ]));
            }

            return Task.FromResult(new AuditResult(true, []));
        }
    }

    private sealed class PlaintextLlmAuditor : IAuditor
    {
        public string Name => "plaintext:llm-review";
        public string Kind => "llm";
        public AuditCapabilities Required => AuditCapabilities.None;
        public List<bool> CaptureStructuredStreamCalls { get; } = new();

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
            CaptureStructuredStreamCalls.Add(context.CaptureStructuredStream);
            context.StdoutChunkCallback?.Invoke("plaintext audit chunk\n");
            return Task.FromResult(new AuditResult(true, [], RawOutput: "plaintext audit chunk\n"));
        }
    }

    private sealed class RecordingTimingStore : ITimingStore
    {
        private readonly Dictionary<string, TimingRecord> _inFlight = new();
        private readonly List<TimingRecord> _completed = new();

        public IReadOnlyList<TimingRecord> CompletedRows
        {
            get { lock (_completed) return [.. _completed]; }
        }

        public Task BeginAsync(TimingRecord record, CancellationToken ct = default)
        {
            lock (_inFlight) _inFlight[record.Id] = record;
            return Task.CompletedTask;
        }

        public Task EndAsync(string id, DateTimeOffset endedAt, long durationMs, CancellationToken ct = default)
        {
            TimingRecord? rec;
            lock (_inFlight)
            {
                _inFlight.TryGetValue(id, out rec);
                _inFlight.Remove(id);
            }
            if (rec is not null)
            {
                var completed = rec with { EndedAt = endedAt, DurationMs = durationMs };
                lock (_completed) _completed.Add(completed);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TimingRecord>> GetByWorkItemAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TimingRecord>>(CompletedRows.Where(r => r.WorkItemId == id).ToList());

        public Task DeleteByWorkItemAsync(WorkItemId id, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<TimingRecord> StreamCompletedAsync(
            int workItemLimit,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var row in CompletedRows)
                yield return row;
        }
    }
}

public sealed class RedactionAppliedToStreamTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codeybox-agent-streams-{Guid.NewGuid():N}");
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task SecretValuePatterns_AreRedactedBeforePersistence()
    {
        var itemId = WorkItemId.New();
        var store = new AgentStreamStore(new AgentStreamsOptions { Path = _root }, NullLogger<AgentStreamStore>.Instance);

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
        {
            // Inputs use obvious <FAKE-...> placeholder tokens so GitHub's
            // secret scanner does not flag this file as containing real
            // credentials. The strings still match the redaction regex
            // patterns in SensitiveDataRedactionEnricher, which is what the
            // test is asserting.
            capture!.WriteChunk("{\"github\":\"gho_FAKE_REDACTION_TEST_TOKEN_XXX\",\"openai\":\"sk-proj-FAKE-REDACTION-TEST-TOKEN-XXX\"}\n");
        }

        var file = Assert.Single(await store.ListAsync(itemId));
        var contents = await File.ReadAllTextAsync(Path.Combine(_root, itemId.ToString(), file.FileName));
        Assert.Contains("***", contents);
        Assert.DoesNotContain("gho_", contents);
        Assert.DoesNotContain("sk-proj-", contents);
        JsonDocument.Parse(contents);
    }

    [Fact]
    public async Task CurrentCommonSecretValues_AreRedactedBeforePersistence()
    {
        var itemId = WorkItemId.New();
        var store = new AgentStreamStore(new AgentStreamsOptions { Path = _root }, NullLogger<AgentStreamStore>.Instance);

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
        {
            // Each input below is an obvious <FAKE-...> placeholder that still
            // matches the corresponding regex in SensitiveDataRedactionEnricher
            // (AKIA[A-Z0-9]{16}, xox[baprs]-[A-Za-z0-9-]{10,},
            // sk_live_[A-Za-z0-9]{16,}, ghs_[A-Za-z0-9_]+, PEM block).
            // We deliberately keep the strings low-entropy and contain the
            // word FAKE so GitHub's secret scanner does not flag them.
            capture!.WriteChunk("{\"aws\":\"AKIAFAKEREDACTIONXX0\",\"slack\":\"xoxb-FAKE-REDACTION-TEST-TOKEN\",\"stripe\":\"sk_live_FAKEREDACTIONTESTKEY\",\"github\":\"ghs_FAKE_REDACTION_TEST_TOKEN\",\"pem\":\"-----BEGIN OPENSSH PRIVATE KEY-----FAKE-PLACEHOLDER-----END OPENSSH PRIVATE KEY-----\"}\n");
        }

        var file = Assert.Single(await store.ListAsync(itemId));
        var contents = await File.ReadAllTextAsync(Path.Combine(_root, itemId.ToString(), file.FileName));
        Assert.DoesNotContain("AKIAFAKEREDACTIONXX0", contents);
        Assert.DoesNotContain("xoxb-", contents);
        Assert.DoesNotContain("sk_live_", contents);
        Assert.DoesNotContain("ghs_", contents);
        Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", contents);
        Assert.Equal(5, contents.Split("***").Length - 1);
        JsonDocument.Parse(contents);
    }

    [Fact]
    public async Task MultiLinePrivateKeyBlock_IsRedactedBeforePersistence()
    {
        var itemId = WorkItemId.New();
        var store = new AgentStreamStore(new AgentStreamsOptions { Path = _root }, NullLogger<AgentStreamStore>.Instance);

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
        {
            // PEM block body uses obvious <FAKE-...> placeholder strings (no
            // valid base64 / no high-entropy bytes) so GitHub's scanner does
            // not flag the file. The redaction regex still matches because it
            // anchors only on the BEGIN/END markers.
            capture!.WriteChunk("-----BEGIN OPENSSH PRIVATE KEY-----\n");
            capture.WriteChunk("FAKE_REDACTION_TEST_BODY_LINE_ONE\n");
            capture.WriteChunk("FAKE_REDACTION_TEST_BODY_LINE_TWO\n");
            capture.WriteChunk("-----END OPENSSH PRIVATE KEY-----\n");
            capture.WriteChunk("{\"type\":\"result\"}\n");
        }

        var file = Assert.Single(await store.ListAsync(itemId));
        var contents = await File.ReadAllTextAsync(Path.Combine(_root, itemId.ToString(), file.FileName));
        Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", contents);
        Assert.DoesNotContain("FAKE_REDACTION_TEST_BODY_LINE_ONE", contents);
        Assert.DoesNotContain("FAKE_REDACTION_TEST_BODY_LINE_TWO", contents);
        Assert.DoesNotContain("END OPENSSH PRIVATE KEY", contents);
        Assert.Contains("\"type\":\"result\"", contents);
        Assert.DoesNotContain("\"created_at\":", contents);
    }

    [Fact]
    public async Task MultiLinePrivateKeyInsideJsonEvents_KeepsJsonEnvelope()
    {
        var itemId = WorkItemId.New();
        var store = new AgentStreamStore(new AgentStreamsOptions { Path = _root }, NullLogger<AgentStreamStore>.Instance);

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
        {
            capture!.WriteChunk("{\"type\":\"assistant\",\"message\":\"-----BEGIN OPENSSH PRIVATE KEY-----\"}\n");
            capture.WriteChunk("{\"type\":\"assistant\",\"message\":\"FAKE_REDACTION_TEST_BODY\"}\n");
            capture.WriteChunk("{\"type\":\"assistant\",\"message\":\"-----END OPENSSH PRIVATE KEY-----\"}\n");
            capture.WriteChunk("{\"type\":\"result\",\"message\":\"done\"}\n");
        }

        var file = Assert.Single(await store.ListAsync(itemId));
        var lines = await File.ReadAllLinesAsync(Path.Combine(_root, itemId.ToString(), file.FileName));
        Assert.Equal(4, lines.Length);
        Assert.All(lines, line =>
        {
            using var parsed = JsonDocument.Parse(line);
            Assert.True(parsed.RootElement.TryGetProperty("type", out _));
        });
        Assert.All(lines.Take(3), line =>
        {
            using var parsed = JsonDocument.Parse(line);
            Assert.Equal("assistant", parsed.RootElement.GetProperty("type").GetString());
            Assert.Equal("***", parsed.RootElement.GetProperty("message").GetString());
        });
        Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", string.Join('\n', lines));
        Assert.DoesNotContain("FAKE_REDACTION_TEST_BODY", string.Join('\n', lines));
    }

    [Fact]
    public async Task SensitiveKeyValues_AreRedactedBeforePersistence()
    {
        var itemId = WorkItemId.New();
        var store = new AgentStreamStore(new AgentStreamsOptions { Path = _root }, NullLogger<AgentStreamStore>.Instance);

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
        {
            capture!.WriteChunk("{\"Authorization\":\"Bearer plain-secret\",\"CODEX_AUTH_JSON\":\"{\\\"refresh_token\\\":\\\"plain-refresh\\\"}\",\"message\":\"safe\"}\n");
            capture.WriteChunk("OPENAI_API_KEY=plain-openai-key\n");
        }

        var file = Assert.Single(await store.ListAsync(itemId));
        var contents = await File.ReadAllTextAsync(Path.Combine(_root, itemId.ToString(), file.FileName));
        Assert.Contains("\"Authorization\":\"***\"", contents);
        Assert.Contains("\"CODEX_AUTH_JSON\":\"***\"", contents);
        Assert.Contains("OPENAI_API_KEY=***", contents);
        Assert.Contains("\"message\":\"safe\"", contents);
        Assert.DoesNotContain("plain-secret", contents);
        Assert.DoesNotContain("plain-refresh", contents);
        Assert.DoesNotContain("plain-openai-key", contents);
    }
}

public sealed class ReleaseDeepAuditAgentStreamPersistenceTests : IDisposable
{
    private const string AuditorName = "deep-llm-review";

    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-agent-stream-release-").FullName;
    private readonly string _dbPath;
    private readonly SqliteReleaseStore _releaseStore;
    private readonly SqliteWorkItemStore _workItemStore;

    public ReleaseDeepAuditAgentStreamPersistenceTests()
    {
        _dbPath = Path.Combine(_workspace, "state.db");
        _releaseStore = new SqliteReleaseStore(_dbPath);
        _workItemStore = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _workItemStore.Dispose();
        _releaseStore.Dispose();
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task ReleaseService_CapturesLlmDeepAuditorAgentStream()
    {
        var streamStore = new AgentStreamStore(
            new AgentStreamsOptions { Path = Path.Combine(_workspace, "streams") },
            NullLogger<AgentStreamStore>.Instance);
        var agent = new StreamingDeepAuditAgent();
        var service = ReleaseTestHelper.BuildService(
            _releaseStore,
            _workItemStore,
            new InMemoryProjectRepository(ReleaseTestHelper.EnabledProjectWithDeepAuditors(AuditorName, maxIterations: 1)),
            new NullWebhookDispatcher(),
            deepAuditors: [new AgentBackedDeepAuditor(AuditorName)],
            sandboxes: new AlwaysSucceedSandboxProvider(),
            gitHost: new DeepAuditTestGitHost(),
            agents: new SingleAgentRegistry(agent),
            agentStreams: streamStore);
        var release = ReleaseTestHelper.SeedRelease(ReleaseState.Closed, branchName: "release/v1.0");
        await _releaseStore.CreateAsync(release);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = release.ProjectId,
            Title = "done",
            Prompt = "done",
            Agent = AgentKind.Claude,
            ReleaseId = release.Id,
        };
        await _workItemStore.CreateAsync(item);
        await _workItemStore.UpdateAsync(item.With(WorkItemState.Done));

        await service.OnWorkItemTerminalAsync(release.Id, CancellationToken.None);

        var files = await PollForFilesAsync(streamStore, new WorkItemId(release.Id.Value));
        var file = Assert.Single(files);
        Assert.Equal($"audit-llm-{AuditorName}", file.Phase);
        Assert.Equal(1, file.Iteration);
        Assert.Equal([true], agent.CaptureStructuredStreamCalls);
        var line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(streamStore.Options.Path, release.Id.ToString(), file.FileName)));
        using var parsed = JsonDocument.Parse(line);
        Assert.Equal("result", parsed.RootElement.GetProperty("type").GetString());
        Assert.Equal("deep", parsed.RootElement.GetProperty("auditor").GetString());
        Assert.False(parsed.RootElement.TryGetProperty("created_at", out _));
    }

    [Fact]
    public async Task ReleaseService_DeepAudit_PlaintextRunner_StillOpensCaptureFile()
    {
        // Anti-regression: the deep-audit capture condition was widened so a
        // capture file is opened whenever LLM audit streams are enabled — NOT
        // only when SupportsStructuredStreamAsync returned true. If that
        // condition regresses to `canCaptureStructuredStream ? Begin : null`,
        // a plaintext-only auditor agent (opencode-style) silently stops
        // producing a release-level capture and the existing structured-only
        // test would still pass. Pin both: capture file opens, and the agent
        // is asked NOT to emit structured stream-json.
        var streamStore = new AgentStreamStore(
            new AgentStreamsOptions { Path = Path.Combine(_workspace, "streams-plaintext-release") },
            NullLogger<AgentStreamStore>.Instance);
        var agent = new PlaintextDeepAuditAgent();
        var service = ReleaseTestHelper.BuildService(
            _releaseStore,
            _workItemStore,
            new InMemoryProjectRepository(ReleaseTestHelper.EnabledProjectWithDeepAuditors(AuditorName, maxIterations: 1)),
            new NullWebhookDispatcher(),
            deepAuditors: [new AgentBackedDeepAuditor(AuditorName)],
            sandboxes: new AlwaysSucceedSandboxProvider(),
            gitHost: new DeepAuditTestGitHost(),
            agents: new SingleAgentRegistry(agent),
            agentStreams: streamStore);
        var release = ReleaseTestHelper.SeedRelease(ReleaseState.Closed, branchName: "release/v1.0-plain");
        await _releaseStore.CreateAsync(release);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = release.ProjectId,
            Title = "done",
            Prompt = "done",
            Agent = AgentKind.Claude,
            ReleaseId = release.Id,
        };
        await _workItemStore.CreateAsync(item);
        await _workItemStore.UpdateAsync(item.With(WorkItemState.Done));

        await service.OnWorkItemTerminalAsync(release.Id, CancellationToken.None);

        var files = await PollForFilesAsync(streamStore, new WorkItemId(release.Id.Value));
        var file = Assert.Single(files);
        Assert.Equal($"audit-llm-{AuditorName}", file.Phase);
        // The auditor said it does NOT support structured stream-json…
        Assert.Equal([false], agent.CaptureStructuredStreamCalls);
        // …yet the capture file MUST exist and carry the plaintext tee. A
        // regression to canCaptureStructuredStream ? Begin : null would
        // leave SizeBytes at 0 here.
        Assert.True(file.SizeBytes > 0);
        var contents = await File.ReadAllTextAsync(
            Path.Combine(streamStore.Options.Path, release.Id.ToString(), file.FileName));
        Assert.Contains("plaintext deep audit chunk", contents);
    }

    private static async Task<IReadOnlyList<AgentStreamFile>> PollForFilesAsync(
        AgentStreamStore streamStore,
        WorkItemId id)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var files = await streamStore.ListAsync(id);
            if (files.Any(f => f.SizeBytes > 0))
                return files;
            await Task.Delay(20);
        }

        return await streamStore.ListAsync(id);
    }

    private sealed class AgentBackedDeepAuditor : IDeepAuditor
    {
        public AgentBackedDeepAuditor(string name) => Name = name;
        public string Name { get; }
        public string Kind => "llm";
        public AuditCapabilities Required => AuditCapabilities.AgentCredentials | AuditCapabilities.Network;

        public async Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            DeepAuditContext context,
            CancellationToken ct = default)
        {
            var agent = Assert.IsAssignableFrom<IAgentRunner>(context.AuditRunner);
            var result = await agent.RunAsync(
                sandbox,
                workingDirectory,
                "deep audit",
                credential: null,
                ct: ct,
                stdoutChunkCallback: context.StdoutChunkCallback,
                captureStructuredStream: context.CaptureStructuredStream);
            return new AuditResult(result.Success, []);
        }
    }

    private sealed class StreamingDeepAuditAgent : IStructuredStreamAgentRunner
    {
        public List<bool> CaptureStructuredStreamCalls { get; } = new();
        public AgentKind Kind => AgentKind.Claude;

        public Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
        {
            CaptureStructuredStreamCalls.Add(captureStructuredStream);
            stdoutChunkCallback?.Invoke("{\"type\":\"result\",\"auditor\":\"deep\"}\n");
            return Task.FromResult(new AgentResult(true, "ok", "{\"type\":\"result\",\"auditor\":\"deep\"}\n", null));
        }
    }

    private sealed class PlaintextDeepAuditAgent : IStructuredStreamAgentRunner
    {
        public List<bool> CaptureStructuredStreamCalls { get; } = new();
        public AgentKind Kind => AgentKind.Claude;

        public Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
        {
            CaptureStructuredStreamCalls.Add(captureStructuredStream);
            stdoutChunkCallback?.Invoke("plaintext deep audit chunk\n");
            return Task.FromResult(new AgentResult(true, "ok", "plaintext deep audit chunk\n", null));
        }
    }

    private sealed class SingleAgentRegistry : IAgentRegistry
    {
        private readonly IAgentRunner _agent;
        public SingleAgentRegistry(IAgentRunner agent) => _agent = agent;
        public bool TryGet(AgentKind kind, out IAgentRunner runner)
        {
            runner = _agent;
            return kind == _agent.Kind;
        }
        public IReadOnlyCollection<AgentKind> Available => [_agent.Kind];
    }
}

public sealed class AgentStreamRetentionSweepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codeybox-agent-streams-{Guid.NewGuid():N}");
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Sweep_RemovesFilesOlderThanRetainedDays()
    {
        var itemId = WorkItemId.New();
        var store = new AgentStreamStore(
            new AgentStreamsOptions { Path = _root, RetainedDays = 14 },
            NullLogger<AgentStreamStore>.Instance);

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
            capture!.WriteChunk("{\"type\":\"result\"}\n");

        var file = Assert.Single(await store.ListAsync(itemId));
        var path = Path.Combine(_root, itemId.ToString(), file.FileName);
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-20));

        var deleted = await store.SweepAsync(DateTimeOffset.UtcNow);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(path));
    }
}

public sealed class MaxSizeTruncationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codeybox-agent-streams-{Guid.NewGuid():N}");
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task StreamLargerThanCap_IsTruncatedWithMarkerLine()
    {
        var itemId = WorkItemId.New();
        var store = new AgentStreamStore(
            new AgentStreamsOptions { Path = _root, MaxFileSizeMb = 1 },
            NullLogger<AgentStreamStore>.Instance);
        var largeLine = new string('x', 1024 * 1024) + "\n";

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
        {
            capture!.WriteChunk("{\"type\":\"first\"}\n");
            capture.WriteChunk(largeLine);
        }

        var file = Assert.Single(await store.ListAsync(itemId));
        var lines = await File.ReadAllLinesAsync(Path.Combine(_root, itemId.ToString(), file.FileName));
        using (var parsed = JsonDocument.Parse(lines[0]))
        {
            Assert.Equal("first", parsed.RootElement.GetProperty("type").GetString());
            Assert.False(parsed.RootElement.TryGetProperty("created_at", out _));
        }
        Assert.Contains(lines, l => l.StartsWith("[...truncated by ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VeryLargeLineWithoutNewline_IsTruncatedWithoutBufferingFullLine()
    {
        var itemId = WorkItemId.New();
        var store = new AgentStreamStore(
            new AgentStreamsOptions { Path = _root, MaxFileSizeMb = 1 },
            NullLogger<AgentStreamStore>.Instance);

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
            capture!.WriteChunk(new string('x', 2 * 1024 * 1024));

        var file = Assert.Single(await store.ListAsync(itemId));
        var lines = await File.ReadAllLinesAsync(Path.Combine(_root, itemId.ToString(), file.FileName));
        Assert.Single(lines);
        Assert.StartsWith("[...truncated by ", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncationMarker_StaysWithinConfiguredFileCap()
    {
        var itemId = WorkItemId.New();
        var store = new AgentStreamStore(
            new AgentStreamsOptions { Path = _root, MaxFileSizeMb = 1 },
            NullLogger<AgentStreamStore>.Instance);
        var maxBytes = 1024 * 1024;
        var exactlyFillingLine = new string('x', maxBytes - 1) + "\n";

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
        {
            capture!.WriteChunk(exactlyFillingLine);
            capture.WriteChunk("{\"type\":\"after-cap\"}\n");
        }

        var file = Assert.Single(await store.ListAsync(itemId));
        var path = Path.Combine(_root, itemId.ToString(), file.FileName);
        Assert.True(new FileInfo(path).Length <= maxBytes);

        await using var stream = await store.OpenReadAsync(itemId, file.FileName);
        using var reader = new StreamReader(stream!);
        var contents = await reader.ReadToEndAsync();
        Assert.Contains("[...truncated by ", contents);
    }
}

public sealed class StreamBackpressureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codeybox-agent-streams-{Guid.NewGuid():N}");
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task BurstOfMoreThanPriorQueueLimit_IsPersistedLosslesslyBelowCap()
    {
        var itemId = WorkItemId.New();
        var store = new AgentStreamStore(new AgentStreamsOptions { Path = _root, MaxFileSizeMb = 1 }, NullLogger<AgentStreamStore>.Instance);

        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
        {
            for (var i = 0; i < 256; i++)
                capture!.WriteChunk($"{{\"i\":{i}}}\n");
        }

        var file = Assert.Single(await store.ListAsync(itemId));
        var lines = await File.ReadAllLinesAsync(Path.Combine(_root, itemId.ToString(), file.FileName));
        Assert.Equal(256, lines.Length);
        using (var first = JsonDocument.Parse(lines[0]))
        using (var last = JsonDocument.Parse(lines[^1]))
        {
            Assert.Equal(0, first.RootElement.GetProperty("i").GetInt32());
            Assert.Equal(255, last.RootElement.GetProperty("i").GetInt32());
            Assert.False(first.RootElement.TryGetProperty("created_at", out _));
            Assert.False(last.RootElement.TryGetProperty("created_at", out _));
        }
        Assert.DoesNotContain(lines, line => line.StartsWith("[...truncated by ", StringComparison.Ordinal));
    }
}

public sealed class MultiplePhasesPerWorkItemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codeybox-agent-streams-{Guid.NewGuid():N}");
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task WorkAuditsReworkAndMerge_ProduceExpectedFileSet()
    {
        var itemId = WorkItemId.New();
        var store = new AgentStreamStore(new AgentStreamsOptions { Path = _root }, NullLogger<AgentStreamStore>.Instance);
        var phases = new[] { ("work", 1), ("audit-llm-security:llm-review", 1), ("audit-llm-tests:llm-review", 2), ("rework", 2), ("merge", 1) };

        foreach (var (phase, iteration) in phases)
        {
            await using var capture = await store.BeginCaptureAsync(itemId, phase, iteration);
            capture!.WriteChunk("{\"type\":\"result\"}\n");
        }

        var files = await store.ListAsync(itemId);
        Assert.Equal(phases.Length, files.Count);
        foreach (var (phase, iteration) in phases)
            Assert.Contains(files, f => f.Phase == phase && f.Iteration == iteration);
    }
}

public sealed class RetryProducesNewFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codeybox-agent-streams-{Guid.NewGuid():N}");
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task SamePhaseAndIteration_DoesNotOverwritePriorAttempt()
    {
        var itemId = WorkItemId.New();
        var store = new AgentStreamStore(new AgentStreamsOptions { Path = _root }, NullLogger<AgentStreamStore>.Instance);

        await using (var capture = await store.BeginCaptureAsync(itemId, "audit-llm-security:llm-review", 3))
            capture!.WriteChunk("{\"attempt\":1}\n");
        await using (var capture = await store.BeginCaptureAsync(itemId, "audit-llm-security:llm-review", 3))
            capture!.WriteChunk("{\"attempt\":2}\n");

        var files = await store.ListAsync(itemId);
        Assert.Equal(2, files.Count);
        Assert.Equal(2, files.Select(f => f.FileName).Distinct(StringComparer.Ordinal).Count());
    }
}

public sealed class AgentStreamsDisabledTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codeybox-agent-streams-{Guid.NewGuid():N}");
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task BeginCapture_WhenDisabled_ReturnsNullAndDoesNotCreateFiles()
    {
        var store = new AgentStreamStore(
            new AgentStreamsOptions { Enabled = false, Path = _root },
            NullLogger<AgentStreamStore>.Instance);

        var capture = await store.BeginCaptureAsync(WorkItemId.New(), "work", 1);

        Assert.Null(capture);
        Assert.False(Directory.Exists(_root));
    }
}

public sealed class AgentStreamEndpointTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Endpoints_ListAndServeCapturedStreamFile()
    {
        var client = _factory.CreateClient();
        var created = await client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "stream endpoint test",
            prompt = "do work",
        });
        created.EnsureSuccessStatusCode();
        var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = createdJson.GetProperty("id").GetString()!;

        var streams = _factory.Services.GetRequiredService<IAgentStreamStore>();
        await using (var capture = await streams.BeginCaptureAsync(new WorkItemId(Guid.Parse(id)), "work", 1))
            capture!.WriteChunk("{\"type\":\"result\"}\n");

        var list = await client.GetFromJsonAsync<JsonElement>($"/workitems/{id}/agent-streams?includeLineCount=true");
        var first = list.EnumerateArray().Single();
        var fileName = first.GetProperty("fileName").GetString()!;
        Assert.Equal("work", first.GetProperty("phase").GetString());
        Assert.Equal(1, first.GetProperty("lineCount").GetInt64());

        var file = await client.GetAsync($"/workitems/{id}/agent-streams/{fileName}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/x-ndjson", file.Content.Headers.ContentType!.MediaType);
        var line = Assert.Single((await file.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var parsed = JsonDocument.Parse(line);
        Assert.Equal("result", parsed.RootElement.GetProperty("type").GetString());
        Assert.False(parsed.RootElement.TryGetProperty("created_at", out _));
    }
}
