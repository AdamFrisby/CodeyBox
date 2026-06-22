using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Orchestrator.Knobs;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class PlanningPipelineTests : IDisposable
{
    private readonly string _workspace;

    public PlanningPipelineTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-planning-pipeline-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task PlanOn_RunsPlanningReviewThenImplementation_AndPersistsPlan()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/plan-on") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Contains("PLAN:", final.PlanArtifact, StringComparison.Ordinal);
        Assert.NotNull(final.PlanGeneratedAt);
        Assert.NotNull(final.PlanReviewedAt);
        Assert.Equal("Placeholder plan review approved.", final.PlanReviewSummary);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(1, agent.WorkCalls);
        Assert.Contains("## Approved plan", agent.LastWorkPrompt, StringComparison.Ordinal);
        Assert.Contains("PLAN:", agent.LastWorkPrompt, StringComparison.Ordinal);

        var events = setup.Webhooks.Events.Select(e => e.Event).ToArray();
        Assert.Contains("work_item.planning", events);
        Assert.Contains("work_item.plan_review", events);
        Assert.Contains("work_item.plan_approved", events);

        var barePath = Path.Combine(setup.GitRoot, item.Id + ".git");
        var (_, treeOutput, _) = await TestSupport.RunGit(
            barePath, "ls-tree", "-r", "feature/plan-on", "--name-only");
        Assert.Contains("output.txt", treeOutput);
        Assert.DoesNotContain("planning-scratch.txt", treeOutput);
    }

    [Fact]
    public async Task PlanOff_Default_DoesNotRunPlanningPhase()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/plan-off");

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.PlanArtifact);
        Assert.Equal(0, agent.PlanningCalls);
        Assert.Equal(1, agent.WorkCalls);
        Assert.DoesNotContain("## Approved plan", agent.LastWorkPrompt, StringComparison.Ordinal);
    }

    private static PlanningPipelineSetup BuildPipeline(PlanningAwareAgent agent, string workspace, string seedRepoUrl)
    {
        var gitRoot = Path.Combine(workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
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
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions,
            knobRegistry: new KnobRegistry([new PlanKnob()]));

        return new PlanningPipelineSetup(pipeline, store, webhooks, gitRoot);
    }

    private static WorkItem NewItem(string branch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Planning test",
        Prompt = "write output.txt",
        WorkBranch = branch,
    };
}

internal sealed class PlanningPipelineSetup(
    PipelineRunner Pipeline,
    SqliteWorkItemStore Store,
    CapturingWebhookDispatcher Webhooks,
    string GitRoot) : IDisposable
{
    public PipelineRunner Pipeline { get; } = Pipeline;
    public SqliteWorkItemStore Store { get; } = Store;
    public CapturingWebhookDispatcher Webhooks { get; } = Webhooks;
    public string GitRoot { get; } = GitRoot;

    public void Dispose() => Store.Dispose();
}

internal sealed partial class PlanningAwareAgent : IAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public int PlanningCalls { get; private set; }
    public int WorkCalls { get; private set; }
    public string LastWorkPrompt { get; private set; } = string.Empty;

    public async Task<AgentResult> RunAsync(
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
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            return await HandleMergeAsync(sandbox, workingDirectory, prompt, ct);

        if (prompt.Contains("planning-only phase", StringComparison.Ordinal))
        {
            PlanningCalls++;
            var scratch = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "echo 'discard me' > \"$0\"", $"{workingDirectory}/planning-scratch.txt"],
            }, ct);
            if (!scratch.Success)
                return new AgentResult(false, "failed to write planning scratch", scratch.Stdout, scratch.Stderr);

            const string plan = """
                PLAN:
                Approach: make the smallest output file change.
                Files/areas to change: output.txt.
                Test/E2E strategy: pipeline integration verifies final branch.
                Risks: none for this fixture.
                Satisfies task: creates output.txt.
                """;
            stdoutChunkCallback?.Invoke(plan);
            return new AgentResult(true, "planned", plan, null);
        }

        WorkCalls++;
        LastWorkPrompt = prompt;
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "echo 'implemented from plan' > \"$0\"", $"{workingDirectory}/output.txt"],
        }, ct);
        return write.Success
            ? new AgentResult(true, "worked", null, null)
            : new AgentResult(false, "failed to write output.txt", write.Stdout, write.Stderr);
    }

    private static async Task<AgentResult> HandleMergeAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        CancellationToken ct)
    {
        var match = MergePromptShape().Match(prompt);
        if (!match.Success)
            return new AgentResult(false, "could not parse merge prompt", null, null);

        var workBranch = match.Groups[1].Value;
        var merge = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "git", "-C", workingDirectory, "merge", "--no-ff",
                "-m", $"codeybox: merge {workBranch}", $"origin/{workBranch}",
            ],
        }, ct);
        return merge.Success
            ? new AgentResult(true, "merged", null, null)
            : new AgentResult(false, "merge failed", merge.Stdout, merge.Stderr);
    }

    [GeneratedRegex(@"merge branch `([^`]+)` into branch\s+`([^`]+)`",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex MergePromptShape();
}
