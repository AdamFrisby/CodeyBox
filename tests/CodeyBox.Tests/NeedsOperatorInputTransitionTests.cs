using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;
using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Upstream;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that a work item transitions to NeedsOperatorInput when the agent
/// emits <codeybox-question> blocks and AllowAgentQuestions=true, and that it
/// does NOT transition when AllowAgentQuestions=false or the store is absent.
/// </summary>
[Collection("Pipeline integration")]
public sealed class NeedsOperatorInputTransitionTests : IDisposable
{
    private readonly string _workspace;
    public NeedsOperatorInputTransitionTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-q-transition-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    private TestPipelineWithQuestions BuildWithQuestions(
        string seedRepoUrl,
        bool allowQuestions,
        IReadOnlyList<IAuditor>? auditors = null,
        IAgentStreamStore? agentStreams = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var questionStore = new SqliteWorkItemQuestionStore(stateDb);
        var queue = new InMemoryTaskQueue();
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var agent = new QuestionEmittingAgent();
        var registry = new AgentRegistry([agent]);

        var auditorList = auditors ?? (IReadOnlyList<IAuditor>)[];
        var auditTypes = auditorList.Count > 0 ? new[] { "scripted" } : Array.Empty<string>();

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            AllowAgentQuestions = allowQuestions,
            Audit = new ProjectAudit
            {
                MaxIterations = 3,
                AuditTypes = auditTypes,
            },
        };

        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog(auditorList));
        var upstreamFactory = new TestUpstreamFactory();
        var webhooks = new CapturingWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, upstreamFactory, composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            questionStore: allowQuestions ? questionStore : null,
            agentStreams: agentStreams,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new TestPipelineWithQuestions(pipeline, store, questionStore, agent, gitHost, gitRoot, webhooks);
    }

    [Fact]
    public async Task AgentEmitsQuestion_AllowQuestionsTrue_ParksAtNeedsOperatorInput()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = BuildWithQuestions(seed, allowQuestions: true);
        tp.Agent.QuestionToEmit = "q-001";

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Test",
            Prompt = "do something",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);

        var questions = await tp.QuestionStore.ListByWorkItemAsync(item.Id.ToString());
        Assert.Single(questions);
        Assert.Equal("q-001", questions[0].QuestionId);
        Assert.Equal("open", questions[0].State);
    }

    [Fact]
    public async Task WorkCompletionProgress_ClearsRecoveryAttemptsBeforeQuestionParking()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = BuildWithQuestions(seed, allowQuestions: true);
        tp.Agent.QuestionToEmit = "q-reset";

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Test",
            Prompt = "do something",
            RecoveryAttempts = 2,
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.Equal(0, final.RecoveryAttempts);
    }

    [Fact]
    public async Task AgentEmitsQuestion_WithStructuredStreamsEnabled_ParksAtNeedsOperatorInput()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var streamStore = new AgentStreamStore(
            new AgentStreamsOptions { Path = Path.Combine(_workspace, "streams") },
            NullLogger<AgentStreamStore>.Instance);
        using var tp = BuildWithQuestions(seed, allowQuestions: true, agentStreams: streamStore);
        tp.Agent.QuestionToEmit = "q-json";

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Test",
            Prompt = "do something",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);

        var questions = await tp.QuestionStore.ListByWorkItemAsync(item.Id.ToString());
        var question = Assert.Single(questions);
        Assert.Equal("q-json", question.QuestionId);
        Assert.Equal("open", question.State);

        var stream = Assert.Single(await streamStore.ListAsync(item.Id), f => f.Phase == "work");
        var path = Path.Combine(streamStore.Options.Path, item.Id.ToString(), stream.FileName);
        Assert.Contains("q-json", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AgentEmitsQuestion_AllowQuestionsFalse_ProceedsNormally()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = BuildWithQuestions(seed, allowQuestions: false);
        tp.Agent.QuestionToEmit = "q-001";

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Test",
            Prompt = "do something",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // Without AllowAgentQuestions the question block is ignored and the pipeline completes.
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task QuestionParsedWebhookFired_ForEachNewQuestion()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = BuildWithQuestions(seed, allowQuestions: true);
        tp.Agent.QuestionToEmit = "q-007";

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Test",
            Prompt = "do something",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var questionEvents = tp.Webhooks.Events
            .Where(e => e.Event == "work_item.question_asked")
            .ToList();
        Assert.Single(questionEvents);
    }

    [Fact]
    public async Task QuestionCap_ViaRealPipelineRunner_OnlyTenStored()
    {
        // Feeds 12 question blocks through the real PipelineRunner and verifies
        // the cap (MaxQuestionsPerWorkItem = 10) is enforced by TryParkForQuestionsAsync.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = BuildWithQuestions(seed, allowQuestions: true);
        tp.Agent.QuestionsToEmit = [.. Enumerable.Range(1, 12).Select(i => $"q-{i:D3}")];

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Cap test",
            Prompt = "do something",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var questions = await tp.QuestionStore.ListByWorkItemAsync(item.Id.ToString());
        Assert.Equal(10, questions.Count);
    }

    [Fact]
    public async Task ReworkPhaseEmitsQuestion_ParksAtNeedsOperatorInput()
    {
        // Work phase completes without a question; the auditor then generates a
        // blocking finding; the rework-phase agent emits a question — the item
        // must park at NeedsOperatorInput and NOT advance to AuditPassed.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = BuildWithQuestions(seed, allowQuestions: true, auditors: [new AlwaysFailAuditor()]);
        tp.Agent.QuestionToEmit = "q-rework";
        tp.Agent.EmitOnlyOnRework = true; // suppress question during work phase

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Test",
            Prompt = "do something",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);

        var questions = await tp.QuestionStore.ListByWorkItemAsync(item.Id.ToString());
        Assert.Single(questions);
        Assert.Equal("q-rework", questions[0].QuestionId);
        Assert.Equal("open", questions[0].State);
    }

    [Fact]
    public async Task ReworkCompletionProgress_ClearsRecoveryAttemptsBeforeQuestionParking()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = BuildWithQuestions(seed, allowQuestions: true, auditors: [new AlwaysFailAuditor()]);
        tp.Agent.QuestionToEmit = "q-rework-reset";
        tp.Agent.EmitOnlyOnRework = true;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Test",
            Prompt = "do something",
        };
        tp.Agent.AfterNonMergeCallAsync = async count =>
        {
            if (count != 2)
                return;

            var current = await tp.Store.GetAsync(item.Id);
            await tp.Store.UpdateAsync(current! with { RecoveryAttempts = 2 });
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.Equal(0, final.RecoveryAttempts);
    }

}

internal sealed class TestPipelineWithQuestions : IDisposable
{
    public PipelineRunner Pipeline { get; }
    public SqliteWorkItemStore Store { get; }
    public SqliteWorkItemQuestionStore QuestionStore { get; }
    public QuestionEmittingAgent Agent { get; }
    public LocalGitHost GitHost { get; }
    public string GitRoot { get; }
    public CapturingWebhookDispatcher Webhooks { get; }

    public TestPipelineWithQuestions(
        PipelineRunner pipeline,
        SqliteWorkItemStore store,
        SqliteWorkItemQuestionStore questionStore,
        QuestionEmittingAgent agent,
        LocalGitHost gitHost,
        string gitRoot,
        CapturingWebhookDispatcher webhooks)
    {
        Pipeline = pipeline;
        Store = store;
        QuestionStore = questionStore;
        Agent = agent;
        GitHost = gitHost;
        GitRoot = gitRoot;
        Webhooks = webhooks;
    }

    public void Dispose()
    {
        Store.Dispose();
        QuestionStore.Dispose();
    }
}

/// <summary>
/// Agent that writes a file (so the pipeline sees a commit) and optionally
/// emits a <codeybox-question> block in its stdout.
/// </summary>
internal sealed class QuestionEmittingAgent : IAgentRunner, IStructuredStreamAgentRunner
{
    public AgentKind Kind { get; } = AgentKind.Claude;
    public string? QuestionToEmit { get; set; }
    /// <summary>When set, overrides QuestionToEmit and emits all listed question IDs.</summary>
    public List<string> QuestionsToEmit { get; set; } = [];
    public string SeedRepoUrl { get; set; } = "";
    /// <summary>When true, only emit the question on rework (non-merge) calls after the first.</summary>
    public bool EmitOnlyOnRework { get; set; } = false;
    public Func<int, Task>? AfterNonMergeCallAsync { get; set; }
    private int _nonMergeCallCount;

    public Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default) =>
        Task.FromResult(true);

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox, string workingDirectory, string prompt,
        AgentCredential? credential, string? modelId = null, string? reasoningMode = null,
        CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
    {
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
        {
            // Simple real merge handling borrowed from ScriptedAgent.
            var m = System.Text.RegularExpressions.Regex.Match(prompt,
                @"merge branch `([^`]+)` into branch\s+`([^`]+)`",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) return new AgentResult(false, "no parse", null, null);
            var wb = m.Groups[1].Value;
            var rc = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "merge", "--no-ff", "-m", $"codeybox: merge {wb}", $"origin/{wb}"],
            }, ct);
            return rc.Success ? new AgentResult(true, "merged", null, null) : new AgentResult(false, "merge failed", rc.Stdout, rc.Stderr);
        }

        // Write a file so the pipeline sees an actual commit.
        var path = $"{workingDirectory}/question-test-{Guid.NewGuid():N}.txt";
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat > \"$0\"", path],
            Stdin = "content\n",
        }, ct);
        if (!write.Success) return new AgentResult(false, "write failed", write.Stdout, write.Stderr);

        _nonMergeCallCount++;
        if (AfterNonMergeCallAsync is not null)
            await AfterNonMergeCallAsync(_nonMergeCallCount);

        // Collect which question IDs to emit.
        var questionIds = QuestionsToEmit.Count > 0
            ? QuestionsToEmit
            : QuestionToEmit is not null ? [QuestionToEmit] : (IEnumerable<string>)[];

        var shouldEmit = questionIds.Any() && (!EmitOnlyOnRework || _nonMergeCallCount > 1);
        var plainText = shouldEmit
            ? string.Join("\n", questionIds.Select(id =>
                $"<codeybox-question id=\"{id}\">Should I use approach A or B? Default: A.</codeybox-question>"))
            : string.Empty;
        var stdout = captureStructuredStream
            ? System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "assistant",
                message = new
                {
                    role = "assistant",
                    content = new[] { new { type = "text", text = plainText } },
                },
            }) + "\n"
            : plainText;

        if (captureStructuredStream)
            stdoutChunkCallback?.Invoke(stdout);

        return new AgentResult(true, "ok", stdout, null);
    }
}

internal sealed class AlwaysFailAuditor : IAuditor
{
    public string Name => "always-fail";
    public string Kind => "diff-pattern";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        var finding = new AuditFinding("always-fail", AuditSeverity.Error, "Forced failure", "Test auditor always reports a blocking finding");
        return Task.FromResult(new AuditResult(false, [finding]));
    }
}
