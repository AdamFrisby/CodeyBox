using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that PipelineRunner picks up .codeybox/suggestions.json after
/// the work phase: persists entries to ISuggestionStore, fires one
/// work_item.suggestion webhook per entry, and never commits the file.
///
/// Uses the real Process sandbox so git and shell commands run for real.
/// Requires git on PATH.
/// </summary>
[Collection("Pipeline integration")]
public sealed class WorkPhaseSuggestionPickupTests : IDisposable
{
    private readonly string _workspace;

    public WorkPhaseSuggestionPickupTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-suggestions-pickup-").FullName;

    public void Dispose() { CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace); }

    private const string SuggestionsJson = """
        {
          "suggestions": [
            {
              "title": "Add missing tests",
              "rationale": "No unit tests exist for the module",
              "category": "test-coverage",
              "severity": "notable",
              "estimatedEffort": "medium"
            }
          ]
        }
        """;

    [Fact]
    public async Task WorkPhase_SuggestionsJson_PersistedToStore()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var setup = BuildPipeline(_workspace, seed);

        var item = NewItem("feature/pickup-persist");
        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var suggestions = new List<Suggestion>();
        await foreach (var s in setup.SuggestionStore.ListAsync())
            suggestions.Add(s);

        Assert.Single(suggestions);
        var s0 = suggestions[0];
        Assert.Equal("Add missing tests", s0.Title);
        Assert.Equal("test-coverage", s0.Category);
        Assert.Equal("notable", s0.Severity);
        Assert.Equal("medium", s0.EstimatedEffort);
        Assert.Equal(item.Id.ToString(), s0.SourceWorkItemId);
        Assert.Equal("test-project", s0.ProjectId);
        Assert.Equal("open", s0.State);
    }

    [Fact]
    public async Task WorkPhase_SuggestionsJson_FiresOneWebhookPerSuggestion()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var setup = BuildPipeline(_workspace, seed);

        var item = NewItem("feature/pickup-webhook");
        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var suggestionEvents = setup.Webhooks.Events
            .Where(e => e.Event == "work_item.suggestion")
            .ToList();
        Assert.Single(suggestionEvents);
        Assert.Equal(item.Id, suggestionEvents[0].WorkItem!.Id);
    }

    [Fact]
    public async Task WorkPhase_SuggestionsJson_NotCommittedToWorkBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var setup = BuildPipeline(_workspace, seed);

        var item = NewItem("feature/pickup-nocommit");
        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Verify .codeybox/suggestions.json is absent from the work branch tree
        var barePath = Path.Combine(setup.GitRoot, item.Id + ".git");
        var (_, treeOutput, _) = await TestSupport.RunGit(
            barePath, "ls-tree", "-r", "feature/pickup-nocommit", "--name-only");
        Assert.DoesNotContain(".codeybox/suggestions.json", treeOutput);
    }

    [Fact]
    public async Task WorkPhase_AgentLogScratch_NotCommittedToWorkBranch()
    {
        // An agent that leaves a file under .codeybox/agent-logs/ (the orchestrator's
        // internal capture dir — where antigravity's UNREDACTED glog also lands) must
        // never have it committed to the work branch and pushed in the PR. The real
        // change (output.txt) is committed; the agent-log scratch is stripped.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var setup = BuildPipelineWith(new AgentLogScratchWritingAgent(), _workspace, seed);

        var item = NewItem("feature/agentlog-nocommit");
        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var barePath = Path.Combine(setup.GitRoot, item.Id + ".git");
        var (_, treeOutput, _) = await TestSupport.RunGit(
            barePath, "ls-tree", "-r", "feature/agentlog-nocommit", "--name-only");
        Assert.DoesNotContain(".codeybox/agent-logs", treeOutput);
        Assert.Contains("output.txt", treeOutput); // the real change still lands
    }

    [Fact]
    public async Task PreemptCheckpoint_AgentLogScratch_NotPushedToCheckpointRef()
    {
        // The preempt-checkpoint commit (PipelineRunner.CheckpointPreemptAsync) is
        // pushed to a remote ref and becomes the resumed work tree, so an unredacted
        // agy glog left under .codeybox/agent-logs/ leaks there exactly as it would
        // in the PR. This drives a host-shutdown preemption while the agent is still
        // running and asserts the checkpoint tree carries the real change but NOT the
        // agent-log scratch — pinning the second StripAgentLogScratchFromIndexAsync
        // call (deleting it must fail this test).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PreemptBlockingAgentLogAgent();
        var logger = new CapturingLogger<PipelineRunner>();
        using var setup = BuildPipelineWith(agent, _workspace, seed, logger: logger);

        var item = NewItem("feature/agentlog-preempt");
        await setup.Store.CreateAsync(item);

        using var hostShutdown = new CancellationTokenSource();
        var runTask = setup.Pipeline.RunAsync(item, CancellationToken.None, hostShutdown.Token);

        // Wait until the agent has written output.txt + the agent-log scratch into
        // the work tree, then signal host shutdown so the pipeline preempts and
        // checkpoints the (staged) tree.
        await agent.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await hostShutdown.CancelAsync();

        // The pipeline rethrows on host shutdown (mid-flight item left for recovery);
        // swallow it — the checkpoint ref push is what we assert on.
        try { await runTask; }
        catch (OperationCanceledException) { /* expected on host shutdown */ }

        var final = await setup.Store.GetAsync(item.Id);
        Assert.False(string.IsNullOrWhiteSpace(final!.PreemptCheckpoint),
            "expected a preempt checkpoint ref to be recorded\n" + string.Join(
                Environment.NewLine,
                logger.Entries.Select(entry => $"[{entry.Level}] {entry.Message}: {entry.Exception}")));
        var checkpointRef = AgentTurnCheckpointRef.Parse(final.PreemptCheckpoint!);
        Assert.Equal(item.Id, checkpointRef.WorkItemId);
        Assert.NotNull(final.AgentTurnResumeCheckpoint);

        var privateArchive = await ((IAgentTurnScratchpadStore)setup.Store).ReadAsync(
            item.Id,
            checkpointRef);
        Assert.NotNull(privateArchive);
        Assert.Equal(checkpointRef.ArchiveSha256, privateArchive.Sha256);

        var barePath = Path.Combine(setup.GitRoot, item.Id + ".git");
        var (exit, treeOutput, stderr) = await TestSupport.RunGitNoThrow(
            barePath, "ls-tree", "-r", checkpointRef.Value, "--name-only");
        Assert.True(exit == 0, $"ls-tree of checkpoint ref '{checkpointRef.Value}' failed: {stderr}");
        Assert.DoesNotContain(".codeybox/agent-logs", treeOutput);
        Assert.DoesNotContain(".codeybox/preempt-scratchpad", treeOutput);
        Assert.DoesNotContain(".codeybox/resume-scratchpad", treeOutput);
        Assert.Contains("output.txt", treeOutput); // the real change is checkpointed
    }

    private const string MergeSuggestionsJson = """
        {
          "suggestions": [
            {
              "title": "Refactor merge handler",
              "rationale": "Spotted during merge: the handler could be simplified.",
              "category": "refactor",
              "severity": "minor",
              "estimatedEffort": "small"
            }
          ]
        }
        """;

    [Fact]
    public async Task MergePhase_SuggestionsJson_PersistedToStore()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var setup = BuildPipelineWith(
            new MergeOnlySuggestionEmittingAgent(MergeSuggestionsJson), _workspace, seed, auditors: [auditor]);
        auditor.GitRoot = setup.GitRoot;

        var item = NewItem("feature/merge-pickup-persist");
        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var suggestions = new List<Suggestion>();
        await foreach (var s in setup.SuggestionStore.ListAsync())
            suggestions.Add(s);

        Assert.Single(suggestions);
        Assert.Equal("Refactor merge handler", suggestions[0].Title);
        Assert.Equal("refactor", suggestions[0].Category);
        Assert.Equal(item.Id.ToString(), suggestions[0].SourceWorkItemId);
        Assert.Equal("open", suggestions[0].State);
    }

    [Fact]
    public async Task MergePhase_SuggestionsJson_FiresOneWebhookPerSuggestion()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var setup = BuildPipelineWith(
            new MergeOnlySuggestionEmittingAgent(MergeSuggestionsJson), _workspace, seed, auditors: [auditor]);
        auditor.GitRoot = setup.GitRoot;

        var item = NewItem("feature/merge-pickup-webhook");
        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var suggestionEvents = setup.Webhooks.Events
            .Where(e => e.Event == "work_item.suggestion")
            .ToList();
        Assert.Single(suggestionEvents);
        Assert.Equal(item.Id, suggestionEvents[0].WorkItem!.Id);
    }

    [Fact]
    public async Task MergePhase_SuggestionsJson_NotCommittedToBaseBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var setup = BuildPipelineWith(
            new MergeOnlySuggestionEmittingAgent(MergeSuggestionsJson), _workspace, seed, auditors: [auditor]);
        auditor.GitRoot = setup.GitRoot;

        var item = NewItem("feature/merge-pickup-nocommit");
        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var barePath = Path.Combine(setup.GitRoot, item.Id + ".git");
        var (_, treeOutput, _) = await TestSupport.RunGit(
            barePath, "ls-tree", "-r", "main", "--name-only");
        Assert.DoesNotContain(".codeybox/suggestions.json", treeOutput);
    }

    // ── Build helpers ─────────────────────────────────────────────────────────

    private SuggestionTestSetup BuildPipeline(string workspace, string seedRepoUrl)
        => BuildPipelineWith(new SuggestionEmittingAgent(SuggestionsJson), workspace, seedRepoUrl);

    private static SuggestionTestSetup BuildPipelineWith(
        IAgentRunner agent,
        string workspace,
        string seedRepoUrl,
        IReadOnlyList<IAuditor>? auditors = null,
        ILogger<PipelineRunner>? logger = null)
    {
        var gitRoot = Path.Combine(workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var suggestionStore = new SqliteSuggestionStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var registry = new AgentRegistry([agent]);
        var webhooks = new CapturingWebhookDispatcher();
        var auditorList = auditors ?? [];
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit
            {
                MaxIterations = 1,
                AuditTypes = auditorList.Count > 0 ? ["scripted"] : [],
            },
        });
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog(auditorList));
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            logger ?? NullLogger<PipelineRunner>.Instance,
            suggestions: suggestionStore,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new SuggestionTestSetup(pipeline, store, suggestionStore, webhooks, gitRoot);
    }

    /// <summary>
    /// Tool auditor that advances <c>main</c>'s copy of a file during the audit
    /// phase, so a work branch touching the same file merges with a conflict —
    /// routing the merge phase through the agentic conflict resolver (the only
    /// merge path that now runs an agent in the merge sandbox, where the
    /// merge-phase suggestions.json pickup occurs).
    /// </summary>
    private sealed class MainAdvancingAuditor : IAuditor
    {
        private readonly string _workspace;
        private readonly string _path;
        private readonly string _content;

        public string? GitRoot { get; set; }
        public string Name => "advance-main";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public MainAdvancingAuditor(string workspace, string path, string content)
        {
            _workspace = workspace;
            _path = path;
            _content = content;
        }

        public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
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
            return new AuditResult(true, []);
        }
    }

    private static WorkItem NewItem(string branch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Test pickup",
        Prompt = "write output.txt",
        WorkBranch = branch,
    };
}

// ── Internal helpers ──────────────────────────────────────────────────────────

internal sealed class SuggestionTestSetup(
    PipelineRunner Pipeline,
    SqliteWorkItemStore Store,
    SqliteSuggestionStore SuggestionStore,
    CapturingWebhookDispatcher Webhooks,
    string GitRoot) : IDisposable
{
    public PipelineRunner Pipeline { get; } = Pipeline;
    public SqliteWorkItemStore Store { get; } = Store;
    public SqliteSuggestionStore SuggestionStore { get; } = SuggestionStore;
    public CapturingWebhookDispatcher Webhooks { get; } = Webhooks;
    public string GitRoot { get; } = GitRoot;

    public void Dispose()
    {
        Store.Dispose();
        SuggestionStore.Dispose();
    }
}

/// <summary>
/// Writes a regular file AND .codeybox/suggestions.json in the work phase.
/// Handles the merge phase without emitting suggestions (tests work-phase pickup only).
/// </summary>
internal sealed partial class SuggestionEmittingAgent : IAgentRunner
{
    private readonly string _suggestionsJson;

    public SuggestionEmittingAgent(string suggestionsJson) => _suggestionsJson = suggestionsJson;

    public AgentKind Kind => AgentKind.Claude;

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox, string workingDirectory, string prompt,
        AgentCredential? credential, string? modelId = null, string? reasoningMode = null, CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
    {
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            return await HandleMergeAsync(sandbox, workingDirectory, prompt, ct);

        // Write a regular file so there's a commit to push.
        // Pass path as a separate argv element so ProcessSandbox translates it.
        var r1 = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "echo 'hello from suggestion agent' > \"$0\"", $"{workingDirectory}/output.txt"],
        }, ct);
        if (!r1.Success)
            return new AgentResult(false, "failed to write output.txt", r1.Stdout, r1.Stderr);

        // Write the suggestions file — orchestrator reads and strips it; it must NOT be committed.
        // mkdir -p is needed; pass path as $0 so ProcessSandbox translates it.
        var r2 = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "mkdir -p \"$(dirname \"$0\")\" && cat > \"$0\"", $"{workingDirectory}/.codeybox/suggestions.json"],
            Stdin = _suggestionsJson,
        }, ct);
        if (!r2.Success)
            return new AgentResult(false, "failed to write suggestions.json", r2.Stdout, r2.Stderr);

        return new AgentResult(true, "ok", null, null);
    }

    private static async Task<AgentResult> HandleMergeAsync(
        ISandbox sandbox, string workingDirectory, string prompt, CancellationToken ct)
    {
        var m = MergePromptShape().Match(prompt);
        if (!m.Success)
            return new AgentResult(false, "could not parse merge prompt", null, null);

        var workBranch = m.Groups[1].Value;
        var baseBranch = m.Groups[2].Value;
        string[] argv = ["git", "-C", workingDirectory, "merge", "--no-ff",
            "-m", $"codeybox: merge {workBranch}", $"origin/{workBranch}"];
        var rc = await sandbox.ExecAsync(new SandboxExec { Argv = argv }, ct);
        _ = baseBranch;
        return rc.Success
            ? new AgentResult(true, "merged", null, null)
            : new AgentResult(false, $"merge failed: {string.Join(' ', argv)}", rc.Stdout, rc.Stderr);
    }

    [GeneratedRegex(@"merge branch `([^`]+)` into branch\s+`([^`]+)`",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex MergePromptShape();
}

/// <summary>
/// Writes a regular file AND a file under .codeybox/agent-logs/ in the work
/// phase — modelling the orchestrator's internal capture dir (base agent log and,
/// for antigravity, agy's unredacted glog). The orchestrator must strip the
/// agent-log scratch from the staged tree so it is never committed / pushed.
/// </summary>
internal sealed partial class AgentLogScratchWritingAgent : IAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox, string workingDirectory, string prompt,
        AgentCredential? credential, string? modelId = null, string? reasoningMode = null, CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
    {
        // Merge task: no-op success (this test only exercises the work phase).
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            return new AgentResult(true, "noop", null, null);

        var r1 = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "echo 'real change' > \"$0\"", $"{workingDirectory}/output.txt"],
        }, ct);
        if (!r1.Success)
            return new AgentResult(false, "failed to write output.txt", r1.Stdout, r1.Stderr);

        // Leave a file under .codeybox/agent-logs/ — mirrors the unredacted glog
        // path the orchestrator must keep out of the commit.
        var r2 = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "mkdir -p \"$(dirname \"$0\")\" && printf '%s\\n' 'access_token=ya29.secret' > \"$0\"",
                $"{workingDirectory}/.codeybox/agent-logs/run.agy.log"],
        }, ct);
        if (!r2.Success)
            return new AgentResult(false, "failed to write agent-log scratch", r2.Stdout, r2.Stderr);

        return new AgentResult(true, "ok", null, null);
    }
}

/// <summary>
/// Work-phase agent that writes the real change AND an unredacted-glog-shaped
/// file under .codeybox/agent-logs/, then blocks until its cancellation token
/// fires. This keeps the agent "running" so a host-shutdown signal routes the
/// pipeline through the preempt-checkpoint path (CheckpointPreemptAsync), which
/// must strip the agent-log scratch before committing/pushing the checkpoint ref.
/// </summary>
internal sealed class PreemptBlockingAgentLogAgent : IAgentRunner
{
    public TaskCompletionSource Ready { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AgentKind Kind => AgentKind.Claude;

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox, string workingDirectory, string prompt,
        AgentCredential? credential, string? modelId = null, string? reasoningMode = null, CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
    {
        var r1 = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "echo 'real change' > \"$0\"", $"{workingDirectory}/output.txt"],
        }, ct);
        if (!r1.Success)
            return new AgentResult(false, "failed to write output.txt", r1.Stdout, r1.Stderr);

        var r2 = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "mkdir -p \"$(dirname \"$0\")\" && printf '%s\\n' 'access_token=ya29.secret' > \"$0\"",
                $"{workingDirectory}/.codeybox/agent-logs/run.agy.log"],
        }, ct);
        if (!r2.Success)
            return new AgentResult(false, "failed to write agent-log scratch", r2.Stdout, r2.Stderr);

        // Signal the test that the tree is staged, then block until the pipeline
        // cancels us as part of the preempt drain.
        Ready.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return new AgentResult(true, "unreachable", null, null); // cancellation throws first
    }
}

/// <summary>
/// Writes README.md in the work phase so the merge conflicts with the
/// auditor-advanced main side. On the agentic conflict-resolver pass it
/// resolves the conflict AND writes .codeybox/suggestions.json into the merge
/// sandbox — exercising the merge-phase suggestion pickup path in
/// PipelineRunner (a clean merge runs host-side with no agent, so the resolver
/// path is the only merge agent run that can drop suggestions.json).
/// </summary>
internal sealed partial class MergeOnlySuggestionEmittingAgent : IAgentRunner
{
    private readonly string _suggestionsJson;

    public MergeOnlySuggestionEmittingAgent(string suggestionsJson) => _suggestionsJson = suggestionsJson;

    public AgentKind Kind => AgentKind.Claude;

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox, string workingDirectory, string prompt,
        AgentCredential? credential, string? modelId = null, string? reasoningMode = null, CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
    {
        if (prompt.StartsWith("# Conflict-resolution mode (in-sandbox agentic resolver)", StringComparison.Ordinal))
            return await HandleAgenticResolveAsync(sandbox, workingDirectory, ct);

        // Work phase: write README.md (no suggestions.json) so the merge-phase
        // tests get an unambiguous count of exactly one suggestion (the
        // merge-phase one) and so the merge genuinely conflicts with main.
        var r = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "echo 'work side' > \"$0\"", $"{workingDirectory}/README.md"],
        }, ct);
        return r.Success
            ? new AgentResult(true, "ok", null, null)
            : new AgentResult(false, "failed to write README.md", r.Stdout, r.Stderr);
    }

    private async Task<AgentResult> HandleAgenticResolveAsync(
        ISandbox sandbox, string workingDirectory, CancellationToken ct)
    {
        // Resolve the README conflict (keep both intents) and stage it so the
        // resolver's post-run verification finds no remaining conflicts.
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/README.md"],
            Stdin = "main side\nwork side\n",
        }, ct);
        if (!write.Success)
            return new AgentResult(false, "failed to resolve README.md", write.Stdout, write.Stderr);
        var add = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, "add", "--", "README.md"],
        }, ct);
        if (!add.Success)
            return new AgentResult(false, "failed to git add README.md", add.Stdout, add.Stderr);

        // Write suggestions.json to exercise the merge-phase pickup path.
        var r2 = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "mkdir -p \"$(dirname \"$0\")\" && cat > \"$0\"",
                $"{workingDirectory}/.codeybox/suggestions.json"],
            Stdin = _suggestionsJson,
        }, ct);
        if (!r2.Success)
            return new AgentResult(false, "failed to write suggestions.json during merge", r2.Stdout, r2.Stderr);

        return new AgentResult(true, "resolved with suggestions", null, null);
    }
}
