using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PipelineRunner.EnsureHeadCarriesPromptRevisionTrailerAsync"/>
/// — the orchestrator-side pre-audit stamp that backstops agents that forget
/// to emit the <c>CodeyBox-Prompt-Revision</c> trailer on their commits.
///
/// <para>The deterministic <c>process:prompt-revision-trailer</c> auditor
/// fails when HEAD is missing the trailer or carries the wrong revision; in
/// practice agents intermittently skip the trailer on their final commit,
/// burning a whole multi-VM audit iteration on a purely mechanical mistake.
/// The orchestrator owns the dispatch revision and the work-item id outright,
/// so it stamps the trailer itself when the agent's final commit doesn't
/// carry it — except in the genuine stale-prompt case (dispatched revision
/// != item's current revision), where auto-stamping would mask the operator's
/// mid-iteration prompt edit.
/// </para>
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineRunnerPromptRevisionStampTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-pr-stamp-").FullName;

    public void Dispose()
    {
        CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace);
    }

    private static WorkItem NewItem(string branch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Prompt revision stamp test",
        Prompt = "write a file",
        State = WorkItemState.Queued,
        WorkBranch = branch,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromMinutes(5),
        MergeTimeout = TimeSpan.FromMinutes(5),
    };

    [Fact]
    public async Task AgentOmitsTrailerOnHead_OrchestratorStampsCorrectTrailer()
    {
        // Acceptance criterion: an honest commit whose HEAD lacks the trailer
        // no longer blocks the audit — the orchestrator stamps it pre-audit
        // and the auditor passes. We simulate "agent committed without
        // trailer" via BeforeWorkAsync (the agent commits its work itself
        // inside the sandbox), then the scripted WorkPlan rewrites the same
        // file with the same content so the orchestrator's add-and-commit
        // step finds no staged diff and skips its own trailer-bearing commit.
        // HEAD is therefore the agent's trailer-less commit at push time —
        // exactly the state the operator field-report flagged.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        const string fileName = "agent-forgot.txt";
        const string fileContent = "v1\n";

        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var path = $"{workingDirectory}/{fileName}";
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", path],
                Stdin = fileContent,
            }, ct);
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "add", "--", fileName],
            }, ct);
            // Commit with a message that deliberately omits the CodeyBox trailer.
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "commit", "-m", "agent commit without trailer"],
            }, ct);
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite(fileName, fileContent));

        var item = NewItem("feature/stamp-missing");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // Pipeline must still reach Done — the orchestrator stamp keeps the
        // audit gate happy without bouncing the work back to the agent.
        Assert.Equal(WorkItemState.Done, final!.State);

        var bareRepo = tp.GitHost.GetRepoPath(
            await tp.GitHost.EnsureRepositoryAsync(item.Id, seed));

        // HEAD on the work branch is the orchestrator's stamp commit; it
        // carries the dispatch revision in the prompt-revision trailer so the
        // process:prompt-revision-trailer auditor accepts it.
        var (codeTrailer, trailer, _) = await TestSupport.RunGit(bareRepo,
            "log", "-1",
            $"--pretty=format:%(trailers:key={CodeyBoxTrailers.PromptRevisionTrailerKey},valueonly=true,unfold=true)",
            item.WorkBranch!);
        Assert.Equal(0, codeTrailer);
        Assert.Equal("1", trailer.Trim());

        // The stamp must sit on top of the agent's trailer-less commit, not
        // replace it — the agent's work is preserved verbatim and the stamp
        // is an empty (no-tree-change) commit.
        var (codeLog, log, _) = await TestSupport.RunGit(bareRepo,
            "log", "--pretty=format:%s", item.WorkBranch!);
        Assert.Equal(0, codeLog);
        var subjects = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("agent commit without trailer", subjects);
        Assert.Contains("codeybox: stamp prompt-revision trailer", subjects);
    }

    [Fact]
    public async Task AgentEmitsTrailerCorrectly_OrchestratorDoesNotAddExtraStampCommit()
    {
        // The stamp is a fix-up, not a tax — when HEAD already carries the
        // correct trailer (which is the happy path: the orchestrator's own
        // add-and-commit step in RunAgentPhaseAsync stamps the trailer when
        // committing on the agent's behalf) the helper must short-circuit
        // without adding an empty commit. Otherwise every iteration would
        // accumulate a dead trailer-only commit and pollute the work branch.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("happy-path.txt", "x\n"));

        var item = NewItem("feature/stamp-happy-path");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var bareRepo = tp.GitHost.GetRepoPath(
            await tp.GitHost.EnsureRepositoryAsync(item.Id, seed));
        var (code, log, _) = await TestSupport.RunGit(bareRepo,
            "log", "--pretty=format:%s", item.WorkBranch!);
        Assert.Equal(0, code);
        var subjects = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("codeybox: stamp prompt-revision trailer", subjects);
    }

    [Fact]
    public async Task DispatchRevisionDiffersFromCurrent_NoStamp_AuditorStillFlagsMissingTrailer()
    {
        // Acceptance criterion: a genuine stale-prompt commit (dispatch
        // revision != current revision) still surfaces and is not masked.
        // We force this race by editing the prompt mid-flight (BeforeWorkAsync
        // bumps the item to revision 2 while the dispatched revision stays
        // at 1) and commit the agent's work WITHOUT a trailer. The
        // orchestrator's stamp helper must observe the divergence and skip
        // — the post-work audit must then surface the missing trailer.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Register the deterministic prompt-revision trailer auditor so the
        // post-work audit actually checks HEAD; without it, the pipeline
        // would Done-on-clean and the test could not observe the audit
        // outcome.
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [new PromptRevisionTrailerAuditor()],
            maxAuditIterations: 1);

        const string fileName = "stale.txt";
        const string fileContent = "v1\n";
        WorkItemId? editTarget = null;
        var promptEdited = false;

        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            // Mid-flight prompt update: the operator PUT lands while the agent
            // is running, bumping the item from revision 1 to revision 2.
            if (editTarget is not null && !promptEdited)
            {
                await tp.Store.TryReplacePromptAsync(
                    editTarget.Value, "edited mid-flight", DateTimeOffset.UtcNow, ct);
                promptEdited = true;
            }

            // Agent commits without a trailer.
            var path = $"{workingDirectory}/{fileName}";
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", path],
                Stdin = fileContent,
            }, ct);
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "add", "--", fileName],
            }, ct);
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "commit", "-m", "agent commit on stale prompt"],
            }, ct);
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite(fileName, fileContent));

        var item = NewItem("feature/stamp-skipped-stale");
        editTarget = item.Id;
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // The operator's mid-iteration prompt edit landed.
        Assert.Equal(2, final!.PromptRevision);

        // The orchestrator must NOT have papered over the stale-prompt case
        // with an auto-stamp: no stamp commit on the work branch.
        var bareRepo = tp.GitHost.GetRepoPath(
            await tp.GitHost.EnsureRepositoryAsync(item.Id, seed));
        var (codeLog, log, _) = await TestSupport.RunGit(bareRepo,
            "log", "--pretty=format:%s", item.WorkBranch!);
        Assert.Equal(0, codeLog);
        var subjects = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("codeybox: stamp prompt-revision trailer", subjects);

        // HEAD still carries no prompt-revision trailer, so the work-branch
        // tip is the genuine stale-prompt signal the auditor was designed
        // to catch.
        var (codeTrailer, trailer, _) = await TestSupport.RunGit(bareRepo,
            "log", "-1",
            $"--pretty=format:%(trailers:key={CodeyBoxTrailers.PromptRevisionTrailerKey},valueonly=true,unfold=true)",
            item.WorkBranch!);
        Assert.Equal(0, codeTrailer);
        Assert.Equal(string.Empty, trailer.Trim());

        // Pipeline did not advance past audit to Done — the auditor blocked
        // on the missing trailer just as it would have without the stamp
        // helper. We don't pin the exact terminal state since
        // maxAuditIterations=1 with a deterministic auditor can land in
        // a few non-Done states depending on rework wiring; the load-bearing
        // assertion is "not Done" plus the explicit no-stamp / no-trailer
        // checks above.
        Assert.NotEqual(WorkItemState.Done, final.State);
    }
}
