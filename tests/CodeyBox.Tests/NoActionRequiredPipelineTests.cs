using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage for the no-action-required terminal outcome: an agent
/// that exits cleanly with no diff but reports a structured determination
/// resolves the item terminally (reasoning preserved, no failure recorded,
/// no-changes breaker untouched), while an empty diff without a report keeps
/// the pre-existing no-changes failure path.
/// </summary>
[Collection("Pipeline integration")]
public sealed class NoActionRequiredPipelineTests : IDisposable
{
    private readonly string _workspace;
    private readonly TestSupport.AmbientGitConfigScope _gitConfigScope;

    public NoActionRequiredPipelineTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-noaction-").FullName;
        // The ambient harness may inject GIT_CONFIG_* (e.g.
        // safe.bareRepository=explicit) that changes bare-repo discovery and
        // breaks the LocalGitHost seed/clone plumbing these tests run through.
        // Clear it so the tests exercise plain git behaviour.
        _gitConfigScope = TestSupport.AmbientGitConfigScope.Clear();
    }

    public void Dispose()
    {
        _gitConfigScope.Dispose();
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task ReportedNoAction_ResolvesTerminally_PreservesReasoning()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        var availability = NewAvailability();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            webhookDispatcher: webhooks,
            availabilityRegistry: availability);

        const string reason = "No reset-TRIGGER endpoint exists anywhere in the API surface.";
        const string precondition = "a POST reset-trigger endpoint for the quota advisor";
        tp.Agent.BeforeWorkAsync = (sandbox, workingDirectory, ct) =>
            WriteReportAsync(sandbox, workingDirectory,
                """{"reason": """ + Json(reason) + """, "precondition": """ + Json(precondition) + "}",
                ct);
        tp.Agent.WorkResults.Enqueue(new AgentResult(
            Success: true,
            Summary: "investigated; precondition absent",
            Stdout: "No trigger endpoint found after a full surface scan.",
            Stderr: null));

        var item = NewItem("feature/no-action");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.NoActionRequired, final!.State);
        Assert.True(WorkItemStates.IsTerminal(final.State));
        // The determination reasoning must be retrievable from the item.
        Assert.Contains("no action required:", final.LastError);
        Assert.Contains(reason, final.LastError);
        Assert.Contains(precondition, final.LastError);
        // A resolution is not a failure: no failure kind, no Failed webhook.
        Assert.Null(final.FailureKind);
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "work_item.failed");
        // The operator-facing outcome is distinct from a failure.
        var resolved = Assert.Single(webhooks.Events, e => e.Event == "work_item.no_action_required");
        Assert.NotNull(resolved.WorkItem);
        Assert.Equal(WorkItemState.NoActionRequired.ToString(), resolved.WorkItem!.State.ToString());
        // Neither breaker mistakes the determination for an agent failure.
        Assert.True(availability.GetAvailability(AgentKind.Claude).Available);
        var snap = availability.Snapshot().SingleOrDefault(s => s.Agent == AgentKind.Claude);
        Assert.True(snap is null || snap.ConsecutiveNoChanges == 0);
    }

    [Fact]
    public async Task EmptyDiff_WithoutReport_StillFailsAndFeedsBreaker()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var availability = NewAvailability();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            availabilityRegistry: availability);

        tp.Agent.WorkResults.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: "No repository changes were necessary.",
            Stderr: null));

        var item = NewItem("feature/empty-diff");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Agent produced no changes to commit", final.LastError);
        var snap = availability.Snapshot().Single(s => s.Agent == AgentKind.Claude);
        Assert.Equal(1, snap.ConsecutiveNoChanges);
    }

    [Fact]
    public async Task MalformedReport_FallsBackToNoChangesFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var availability = NewAvailability();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            availabilityRegistry: availability);

        tp.Agent.BeforeWorkAsync = (sandbox, workingDirectory, ct) =>
            WriteReportAsync(sandbox, workingDirectory, "{not valid json", ct);
        tp.Agent.WorkResults.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: "done",
            Stderr: null));

        var item = NewItem("feature/bad-report");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Agent produced no changes to commit", final.LastError);
        var snap = availability.Snapshot().Single(s => s.Agent == AgentKind.Claude);
        Assert.Equal(1, snap.ConsecutiveNoChanges);
    }

    [Fact]
    public async Task ReportAlongsideRealChanges_IsIgnored()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            webhookDispatcher: webhooks);

        // Contradictory signals: real changes plus a report. The changes win;
        // the report must not hijack a productive run.
        tp.Agent.BeforeWorkAsync = (sandbox, workingDirectory, ct) =>
            WriteReportAsync(sandbox, workingDirectory,
                """{"reason": "stale report left alongside real work"}""",
                ct);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hello world\n"));

        var item = NewItem("feature/changes-win");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "work_item.no_action_required");
    }

    private static AgentAvailabilityRegistry NewAvailability() =>
        new(new AvailabilityOptions
        {
            FastFailThresholdSeconds = 10,
            MaxConsecutiveFastFails = 3,
        },
        TimeProvider.System,
        NullLogger<AgentAvailabilityRegistry>.Instance);

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "no-action test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        Agent = AgentKind.Claude,
        PushUpstream = false,
    };

    private static async Task WriteReportAsync(
        ISandbox sandbox, string workingDirectory, string contents, CancellationToken ct)
    {
        var mkdir = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["mkdir", "-p", $"{workingDirectory}/.codeybox"],
        }, ct);
        if (!mkdir.Success)
            throw new InvalidOperationException("test setup: mkdir .codeybox failed: " + mkdir.Stderr);
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/.codeybox/no-action-required.json"],
            Stdin = contents,
        }, ct);
        if (!write.Success)
            throw new InvalidOperationException("test setup: writing report file failed: " + write.Stderr);
    }

    private static string Json(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
