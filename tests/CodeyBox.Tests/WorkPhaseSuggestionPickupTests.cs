using System.Diagnostics;
using System.Text.RegularExpressions;
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

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ } }

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
        using var setup = BuildPipelineWith(new MergeOnlySuggestionEmittingAgent(MergeSuggestionsJson), _workspace, seed);

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
        using var setup = BuildPipelineWith(new MergeOnlySuggestionEmittingAgent(MergeSuggestionsJson), _workspace, seed);

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
        using var setup = BuildPipelineWith(new MergeOnlySuggestionEmittingAgent(MergeSuggestionsJson), _workspace, seed);

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

    private static SuggestionTestSetup BuildPipelineWith(IAgentRunner agent, string workspace, string seedRepoUrl)
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
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
        });
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            suggestions: suggestionStore);

        return new SuggestionTestSetup(pipeline, store, suggestionStore, webhooks, gitRoot);
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
/// Writes a regular file in the work phase (no suggestions.json).
/// During the merge phase, performs the git merge AND writes .codeybox/suggestions.json
/// to exercise the merge-phase pickup path in PipelineRunner.
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
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            return await HandleMergeAsync(sandbox, workingDirectory, prompt, ct);

        // Work phase: write output.txt only — no suggestions.json so merge-phase tests
        // get an unambiguous count of exactly one suggestion (the merge-phase one).
        var r = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "echo 'hello from merge-only agent' > \"$0\"", $"{workingDirectory}/output.txt"],
        }, ct);
        return r.Success
            ? new AgentResult(true, "ok", null, null)
            : new AgentResult(false, "failed to write output.txt", r.Stdout, r.Stderr);
    }

    private async Task<AgentResult> HandleMergeAsync(
        ISandbox sandbox, string workingDirectory, string prompt, CancellationToken ct)
    {
        var m = MergePromptShape().Match(prompt);
        if (!m.Success)
            return new AgentResult(false, "could not parse merge prompt", null, null);

        var workBranch = m.Groups[1].Value;
        var baseBranch = m.Groups[2].Value;
        _ = baseBranch;

        string[] mergeArgv = ["git", "-C", workingDirectory, "merge", "--no-ff",
            "-m", $"codeybox: merge {workBranch}", $"origin/{workBranch}"];
        var rc = await sandbox.ExecAsync(new SandboxExec { Argv = mergeArgv }, ct);
        if (!rc.Success)
            return new AgentResult(false, $"merge failed: {string.Join(' ', mergeArgv)}", rc.Stdout, rc.Stderr);

        // Write suggestions.json to exercise the merge-phase pickup path in PipelineRunner.
        var r2 = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "mkdir -p \"$(dirname \"$0\")\" && cat > \"$0\"",
                $"{workingDirectory}/.codeybox/suggestions.json"],
            Stdin = _suggestionsJson,
        }, ct);
        if (!r2.Success)
            return new AgentResult(false, "failed to write suggestions.json during merge", r2.Stdout, r2.Stderr);

        return new AgentResult(true, "merged with suggestions", null, null);
    }

    [GeneratedRegex(@"merge branch `([^`]+)` into branch\s+`([^`]+)`",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex MergePromptShape();
}
