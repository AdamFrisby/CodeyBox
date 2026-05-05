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

    private TestPipelineWithQuestions BuildWithQuestions(string seedRepoUrl, bool allowQuestions, IReadOnlyList<IAuditor>? auditors = null)
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

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, upstreamFactory, composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            questionStore: allowQuestions ? questionStore : null);

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
internal sealed class QuestionEmittingAgent : IAgentRunner
{
    public AgentKind Kind { get; } = AgentKind.Claude;
    public string? QuestionToEmit { get; set; }
    public string SeedRepoUrl { get; set; } = "";
    /// <summary>When true, only emit the question on rework (non-merge) calls after the first.</summary>
    public bool EmitOnlyOnRework { get; set; } = false;
    private int _nonMergeCallCount;

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox, string workingDirectory, string prompt,
        AgentCredential? credential, string? modelId = null, CancellationToken ct = default)
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

        // Emit the question in stdout if configured (and not deferred to rework).
        var emitQuestion = QuestionToEmit is not null && (!EmitOnlyOnRework || _nonMergeCallCount > 1);
        var stdout = emitQuestion
            ? $"<codeybox-question id=\"{QuestionToEmit}\">Should I use approach A or B? Default: A.</codeybox-question>"
            : string.Empty;

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
