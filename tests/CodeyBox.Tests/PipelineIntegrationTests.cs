using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end pipeline test using the Process sandbox + a project + a
/// scripted agent that handles both the work and merge phases. Exercises:
///   - project resolution from in-memory repo
///   - work phase: agent writes a file → commit → push workBranch
///   - merge phase: agent runs `git merge --no-ff origin/&lt;workBranch&gt;` →
///     orchestrator verifies + pushes baseBranch
///   - final state Done with merged history in the bare repo
///
/// Requires git on PATH.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineIntegrationTests : IDisposable
{
    private readonly string _workspace;
    public PipelineIntegrationTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-pipeline-").FullName;
    public void Dispose() { CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace); }

    [Fact]
    public async Task EndToEnd_RunsWorkAndAgentMergePhases()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hello world\n"));

        var item = NewItem("feature/hello");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, blob, _) = await TestSupport.RunGit(barePath, "show", "main:hello.txt");
        Assert.Equal("hello world\n", blob);

        var (_, branches, _) = await TestSupport.RunGit(barePath, "branch", "--list");
        Assert.Contains("feature/hello", branches);
    }

    [Fact]
    public async Task WorkPhaseCommit_CarriesCodeyBoxAttributionTrailers()
    {
        // Persistent attribution: the orchestrator-emitted commit must carry
        // CodeyBox-WorkItem + CodeyBox-Agent trailers so `git log --grep` can
        // identify which agent produced the change without consulting the DB.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("attribution.txt", "marker\n"));

        var item = NewItem("feature/attribution");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Equal(WorkItemState.Done, (await tp.Store.GetAsync(item.Id))!.State);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, message, _) = await TestSupport.RunGit(barePath, "log", "-1", "--format=%B", $"{item.WorkBranch}");

        Assert.Contains($"CodeyBox-WorkItem: {item.Id}", message);
        Assert.Contains("CodeyBox-Agent: ", message);
        Assert.Contains(CodeyBoxTrailers.CoAuthoredBy, message);
        // No fallbacks occurred — that trailer must be absent.
        Assert.DoesNotContain("CodeyBox-Fallbacks:", message);
    }

    [Fact]
    public async Task WorkPhasePickup_WithExistingBareRepoWorkBranch_ResetsPriorAttempt()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("fresh.txt", "fresh run\n"));

        var item = NewItem("feature/retry") with { RecoveryAttempts = 1 };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var baseTip = await RevParseAsync(barePath, "main");
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "stale.txt", "prior attempt\n", "prior attempt");
        var staleTip = await RevParseAsync(barePath, item.WorkBranch!);

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        Assert.NotEqual(staleTip, await RevParseAsync(barePath, item.WorkBranch!));
        Assert.Equal(baseTip, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
        Assert.NotEqual(0, (await TestSupport.RunGitNoThrow(barePath, "show", $"{item.WorkBranch}:stale.txt")).code);
        Assert.NotEqual(0, (await TestSupport.RunGitNoThrow(barePath, "show", "main:stale.txt")).code);
        var (_, freshOnWorkBranch, _) = await TestSupport.RunGit(barePath, "show", $"{item.WorkBranch}:fresh.txt");
        var (_, freshOnMain, _) = await TestSupport.RunGit(barePath, "show", "main:fresh.txt");

        Assert.Equal("fresh run\n", freshOnWorkBranch);
        Assert.Equal("fresh run\n", freshOnMain);
    }

    [Fact]
    public async Task WorkPhasePush_WithExistingBareRepoWorkBranch_StartsFromExistingTip()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "fresh run\n"));

        var item = NewItem("feature/retry-conflict");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "prior attempt\n", "prior conflicting attempt");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var (_, readmeOnWorkBranch, _) = await TestSupport.RunGit(barePath, "show", $"{item.WorkBranch}:README.md");
        var (_, readmeOnMain, _) = await TestSupport.RunGit(barePath, "show", "main:README.md");
        Assert.Equal("fresh run\n", readmeOnWorkBranch);
        Assert.Equal("fresh run\n", readmeOnMain);
    }

    [Fact]
    public async Task RetryPickup_RefreshesExistingBareRepoBeforeSandboxClone()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        var item = NewItem("feature/retry-refresh");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var staleMain = await RevParseAsync(barePath, "main");

        await CommitToSeedAsync(seed, "dependency.txt", "dependency landed\n", "dependency landed");
        var latestMain = await RevParseAsync(seed, "main");
        Assert.NotEqual(staleMain, latestMain);

        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var observed = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh", "-c",
                    "git -C \"$1\" rev-parse origin/main > \"$1/observed-origin-main.txt\"",
                    "sh",
                    workingDirectory,
                ],
            }, ct);
            if (!observed.Success)
                throw new InvalidOperationException($"failed to capture sandbox origin/main: {observed.Stderr}");
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "agent saw refreshed main\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var (_, observedOriginMain, _) = await TestSupport.RunGit(barePath, "show", "main:observed-origin-main.txt");
        var (_, dependency, _) = await TestSupport.RunGit(barePath, "show", "main:dependency.txt");
        Assert.Equal(latestMain + "\n", observedOriginMain);
        Assert.Equal("dependency landed\n", dependency);
    }

    [Fact]
    public async Task RetryPickup_UnconfiguredBaseBranchRefreshesUpstreamDefaultBeforeSandboxClone()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await TestSupport.RunGit(seed, "checkout", "-b", "develop");
        await CommitToSeedAsync(seed, "develop.txt", "develop\n", "create develop");
        using var tp = TestSupport.BuildPipeline(_workspace, seed, defaultBaseBranch: null);

        var item = NewItem("feature/retry-refresh-default") with { BaseBranch = null };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, "develop");
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var staleDevelop = await RevParseAsync(barePath, "develop");

        await CommitToSeedAsync(seed, "dependency.txt", "dependency landed\n", "dependency landed");
        var latestDevelop = await RevParseAsync(seed, "develop");
        Assert.NotEqual(staleDevelop, latestDevelop);

        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var observed = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh", "-c",
                    "git -C \"$1\" rev-parse origin/develop > \"$1/observed-origin-develop.txt\"",
                    "sh",
                    workingDirectory,
                ],
            }, ct);
            if (!observed.Success)
                throw new InvalidOperationException($"failed to capture sandbox origin/develop: {observed.Stderr}");
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "agent saw refreshed develop\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var (_, observedOriginDevelop, _) = await TestSupport.RunGit(barePath, "show", "develop:observed-origin-develop.txt");
        var (_, dependency, _) = await TestSupport.RunGit(barePath, "show", "develop:dependency.txt");
        Assert.Equal(latestDevelop + "\n", observedOriginDevelop);
        Assert.Equal("dependency landed\n", dependency);
    }

    [Fact]
    public async Task InitialWorkPhase_ChecksOutWorkBranchFromConfiguredBaseBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await TestSupport.RunGit(seed, "checkout", "-b", "develop");
        await CommitToSeedAsync(seed, "develop-only.txt", "develop base\n", "develop base");
        await TestSupport.RunGit(seed, "checkout", "main");

        using var tp = TestSupport.BuildPipeline(_workspace, seed, defaultBaseBranch: "develop");
        var item = NewItem("feature/develop-work") with { BaseBranch = null };
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var observed = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh", "-c",
                    "git -C \"$1\" rev-parse HEAD > \"$1/observed-head.txt\" && git -C \"$1\" show HEAD:develop-only.txt > \"$1/observed-develop-file.txt\"",
                    "sh",
                    workingDirectory,
                ],
            }, ct);
            if (!observed.Success)
                throw new InvalidOperationException($"failed to capture initial checkout state: {observed.Stderr}");
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "agent started on develop\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var developTip = await RevParseAsync(seed, "develop");
        var (_, observedHead, _) = await TestSupport.RunGit(barePath, "show", "develop:observed-head.txt");
        var (_, observedDevelopFile, _) = await TestSupport.RunGit(barePath, "show", "develop:observed-develop-file.txt");
        Assert.Equal(developTip + "\n", observedHead);
        Assert.Equal("develop base\n", observedDevelopFile);
    }

    [Fact]
    public async Task AgentNoChange_FailsWorkItem()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        // No WorkPlan entries → ScriptedAgent throws → pipeline catches and fails.

        var item = NewItem("feature/x");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
    }

    [Fact]
    public async Task WorkPhaseTransientAgentFailure_ParksWaitingForTransientRetry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var time = new ManualTimeProvider();
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            webhookDispatcher: webhooks,
            transientRetryOptions: TransientRetryOptions(),
            retryTimeProvider: time);
        const string Secret = "ghp_XYZabc789012345678901234567890";
        tp.Agent.WorkResults.Enqueue(new AgentResult(
            Success: false,
            Summary: $"agent transport failed {Secret}",
            Stdout: null,
            Stderr: "request timed out while reading agent stream"));

        var item = NewItem("feature/transient-transport");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, final!.State);
        Assert.DoesNotContain(final.State, WorkItemDependencies.TerminalStates);
        Assert.Equal("transient", final.FailureKind);
        Assert.Equal(RetryFromPolicy.Work, final.TransientRetryFrom);
        Assert.Equal(
            AgentTurnResumePhase.Work,
            Assert.IsType<AgentTurnResumeCheckpoint>(final.AgentTurnResumeCheckpoint).Phase);
        Assert.Equal(time.GetUtcNow(), final.TransientRetryFirstFailedAt);
        Assert.Equal(time.GetUtcNow().AddSeconds(30), final.NextTransientRetryAt);
        Assert.Equal(0, final.TransientRetryAttempts);
        Assert.DoesNotContain(Secret, final.LastError, StringComparison.Ordinal);
        Assert.Contains("***", final.LastError, StringComparison.Ordinal);

        var waiting = Assert.Single(webhooks.Events, e => e.Event == "work_item.waiting_for_transient_retry");
        Assert.Equal(item.Id, waiting.WorkItem?.Id);
        using var details = JsonDocument.Parse(JsonSerializer.Serialize(waiting.Details));
        var root = details.RootElement;
        Assert.Equal("work", root.GetProperty("phase").GetString());
        Assert.Equal("claude", root.GetProperty("agent").GetString());
        Assert.Equal(final.NextTransientRetryAt, root.GetProperty("nextRetryAt").GetDateTimeOffset());
        Assert.Equal(final.TransientRetryAttempts, root.GetProperty("attempts").GetInt32());
        var reason = root.GetProperty("reason").GetString();
        Assert.NotNull(reason);
        Assert.DoesNotContain(Secret, reason, StringComparison.Ordinal);
        Assert.Contains("***", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkPhaseInfrastructureLoss_RetainsThenConvertsSandboxBeforeResumedDispatch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var provider = new RetainableProcessSandboxProvider();
        var involvement = new InMemoryAgentInvolvementStore();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: provider,
            involvement: involvement);

        var outageInjected = false;
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            if (outageInjected)
                return;

            var partialWrite = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/partial.txt"],
                Stdin = "partial work survived\n",
            }, ct);
            Assert.True(partialWrite.Success, partialWrite.Stderr);
            outageInjected = true;
            provider.SetExecutionAvailable(false);
        };
        tp.Agent.WorkResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "sandbox transport disappeared",
            Stdout: null,
            Stderr: null)
        {
            ExecutionUnavailable = true,
        });
        tp.Agent.WorkPlan.Enqueue(new FileWrite("completed.txt", "resumed work completed\n"));

        var item = NewItem("feature/retained-infrastructure-recovery");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var retained = (await tp.Store.GetAsync(item.Id))!;
        Assert.Equal(WorkItemState.Failed, retained.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, retained.FailureKind);
        Assert.NotNull(retained.AgentTurnRecoveryLease);
        Assert.Null(retained.PreemptCheckpoint);
        Assert.Null(retained.AgentTurnResumeCheckpoint?.DispatchClaimId);
        Assert.Equal(1, provider.RetentionCount);
        Assert.Equal(0, provider.AdoptionCount);

        var retrier = new WorkItemRetrier(
            tp.Store,
            tp.Queue,
            tp.GitHost,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkItemRetrier>.Instance);
        var firstRetry = await retrier.RetryAsync(retained, trigger: "test-infrastructure-restored");
        Assert.True(firstRetry.Success, firstRetry.Error);
        Assert.Equal(item.Id, await tp.Queue.DequeueAsync(CancellationToken.None));
        var conversionPickup = (await tp.Store.GetAsync(item.Id))!;
        await tp.Pipeline.RunAsync(conversionPickup, CancellationToken.None);

        var checkpointed = (await tp.Store.GetAsync(item.Id))!;
        Assert.True(
            checkpointed.State == WorkItemState.Working,
            $"Expected automatic checkpoint continuation; state={checkpointed.State}, failure={checkpointed.FailureKind}, error={checkpointed.LastError}");
        Assert.Null(checkpointed.FailureKind);
        Assert.Null(checkpointed.AgentTurnRecoveryLease);
        Assert.NotNull(checkpointed.PreemptCheckpoint);
        Assert.Equal(0, Assert.IsType<AgentTurnResumeCheckpoint>(
            checkpointed.AgentTurnResumeCheckpoint).AttemptCount);
        Assert.Equal(1, provider.AdoptionCount);
        Assert.Equal(1, provider.RetainedSandboxDisposalCount);
        Assert.Single(tp.Agent.WorkPrompts);
        var afterConversionInvolvement = await involvement.ListByWorkItemAsync(item.Id);
        Assert.Single(afterConversionInvolvement);
        Assert.Equal(
            AgentInvolvementOutcomes.FailureInfrastructure,
            afterConversionInvolvement[0].Outcome);

        Assert.Equal(item.Id, await tp.Queue.DequeueAsync(CancellationToken.None));
        await tp.Pipeline.RunAsync((await tp.Store.GetAsync(item.Id))!, CancellationToken.None);

        var final = (await tp.Store.GetAsync(item.Id))!;
        Assert.Equal(WorkItemState.Done, final.State);
        Assert.Null(final.AgentTurnRecoveryLease);
        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.AgentTurnResumeCheckpoint);
        Assert.Equal(2, tp.Agent.WorkPrompts.Count);
        var finalInvolvement = await involvement.ListByWorkItemAsync(item.Id);
        Assert.True(finalInvolvement.Count > afterConversionInvolvement.Count);
        Assert.Contains(finalInvolvement, static row => row.Outcome == AgentInvolvementOutcomes.Success);
        Assert.DoesNotContain(
            finalInvolvement,
            static row => row.Outcome == AgentInvolvementOutcomes.FailureAgent);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, partial, _) = await TestSupport.RunGit(barePath, "show", "main:partial.txt");
        var (_, completed, _) = await TestSupport.RunGit(barePath, "show", "main:completed.txt");
        Assert.Equal("partial work survived\n", partial);
        Assert.Equal("resumed work completed\n", completed);
    }

    [Fact]
    public async Task RetainedSandboxConversion_WhenExecutionIsStillUnavailable_ReleasesPreparationClaim()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var provider = new RetainableProcessSandboxProvider
        {
            RestoreExecutionOnAdoption = false,
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: provider);

        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var partialWrite = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/partial.txt"],
                Stdin = "still recoverable\n",
            }, ct);
            Assert.True(partialWrite.Success, partialWrite.Stderr);
            provider.SetExecutionAvailable(false);
        };
        tp.Agent.WorkResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "sandbox transport disappeared",
            Stdout: null,
            Stderr: null)
        {
            ExecutionUnavailable = true,
        });

        var item = NewItem("feature/retained-conversion-retry");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        var retained = (await tp.Store.GetAsync(item.Id))!;
        var originalLease = Assert.IsType<SandboxRecoveryLease>(retained.AgentTurnRecoveryLease);

        var retrier = new WorkItemRetrier(
            tp.Store,
            tp.Queue,
            tp.GitHost,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkItemRetrier>.Instance);
        var retry = await retrier.RetryAsync(retained, trigger: "test-provider-still-down");
        Assert.True(retry.Success, retry.Error);
        Assert.Equal(item.Id, await tp.Queue.DequeueAsync(CancellationToken.None));
        await tp.Pipeline.RunAsync((await tp.Store.GetAsync(item.Id))!, CancellationToken.None);

        var failedConversion = (await tp.Store.GetAsync(item.Id))!;
        Assert.Equal(WorkItemState.Failed, failedConversion.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, failedConversion.FailureKind);
        Assert.Equal(originalLease, failedConversion.AgentTurnRecoveryLease);
        Assert.Null(failedConversion.PreemptCheckpoint);
        var checkpoint = Assert.IsType<AgentTurnResumeCheckpoint>(
            failedConversion.AgentTurnResumeCheckpoint);
        Assert.Null(checkpoint.DispatchClaimId);
        Assert.Null(checkpoint.DispatchClaimStage);
        Assert.Equal(0, checkpoint.AttemptCount);
        Assert.Equal(0, provider.RetainedSandboxDisposalCount);
    }

    [Fact]
    public async Task RetainedSandboxAdoption_WhenProviderIsStillUnavailable_PreservesLeaseAndReleasesPreparationClaim()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var provider = new RetainableProcessSandboxProvider();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: provider);

        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var partialWrite = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/partial.txt"],
                Stdin = "still recoverable before adoption\n",
            }, ct);
            Assert.True(partialWrite.Success, partialWrite.Stderr);
            provider.SetExecutionAvailable(false);
        };
        tp.Agent.WorkResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "sandbox transport disappeared",
            Stdout: null,
            Stderr: null)
        {
            ExecutionUnavailable = true,
        });

        var item = NewItem("feature/retained-adoption-retry");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        var retained = (await tp.Store.GetAsync(item.Id))!;
        var originalLease = Assert.IsType<SandboxRecoveryLease>(retained.AgentTurnRecoveryLease);

        provider.FailBeforeAdoptionSandboxReturned = true;
        var retrier = new WorkItemRetrier(
            tp.Store,
            tp.Queue,
            tp.GitHost,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkItemRetrier>.Instance);
        var retry = await retrier.RetryAsync(retained, trigger: "test-provider-still-down-before-adoption");
        Assert.True(retry.Success, retry.Error);
        Assert.Equal(item.Id, await tp.Queue.DequeueAsync(CancellationToken.None));
        await tp.Pipeline.RunAsync((await tp.Store.GetAsync(item.Id))!, CancellationToken.None);

        var failedAdoption = (await tp.Store.GetAsync(item.Id))!;
        Assert.Equal(WorkItemState.Failed, failedAdoption.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, failedAdoption.FailureKind);
        Assert.Equal(originalLease, failedAdoption.AgentTurnRecoveryLease);
        Assert.Null(failedAdoption.PreemptCheckpoint);
        var checkpoint = Assert.IsType<AgentTurnResumeCheckpoint>(
            failedAdoption.AgentTurnResumeCheckpoint);
        Assert.Null(checkpoint.DispatchClaimId);
        Assert.Null(checkpoint.DispatchClaimStage);
        Assert.Equal(0, checkpoint.AttemptCount);
        Assert.Equal(1, provider.AdoptionCount);
        Assert.Equal(0, provider.RetainedSandboxDisposalCount);
    }

    [Fact]
    public async Task WorkPhaseTransientAgentFailure_WithOperatorCancellation_CancelsInsteadOfSchedulingRetry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var time = new ManualTimeProvider();
        var webhooks = new CapturingWebhookDispatcher();
        using var cancellations = new CancellationRegistry(CancellationToken.None);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            webhookDispatcher: webhooks,
            transientRetryOptions: TransientRetryOptions(),
            retryTimeProvider: time,
            cancellationRegistry: cancellations);
        tp.Agent.WorkResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent transport failed",
            Stdout: null,
            Stderr: "request timed out while reading agent stream"));

        var item = NewItem("feature/transient-operator-cancel");
        await tp.Store.CreateAsync(item);
        using var registration = cancellations.Register(item.Id);
        Assert.True(cancellations.Cancel(item.Id));
        Assert.Equal(CancellationRequestKind.Operator, cancellations.GetRequestKind(item.Id));

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Cancelled, final!.State);
        Assert.Equal(WorkItemCancellationReason.OperatorRequested, final.CancellationReason);
        Assert.Equal(CancellationSources.Operator, final.CancellationSource);
        Assert.NotEqual("transient", final.FailureKind);
        Assert.Null(final.NextTransientRetryAt);
        Assert.Equal(0, final.TransientRetryAttempts);
        Assert.Contains(webhooks.Events, e => e.Event == "work_item.cancelled" && e.WorkItem?.Id == item.Id);
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "work_item.waiting_for_transient_retry");
    }

    [Fact]
    public async Task WaitingForTransientRetryTransition_CurrentNeedsOperatorInput_DoesNotOverwrite()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var time = new ManualTimeProvider();
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            webhookDispatcher: webhooks,
            transientRetryOptions: TransientRetryOptions(),
            retryTimeProvider: time);

        var stale = NewItem("feature/transient-operator-race") with
        {
            State = WorkItemState.Working,
            LastError = "stale transient snapshot",
        };
        var needsOperatorInput = stale.With(
            WorkItemState.NeedsOperatorInput,
            "operator answer required");
        await tp.Store.CreateAsync(needsOperatorInput);

        var method = typeof(PipelineRunner).GetMethod(
            "TransitionWaitingForTransientRetryAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            binder: null,
            types: [typeof(WorkItem), typeof(string), typeof(Project), typeof(string), typeof(AgentKind?)],
            modifiers: null);
        Assert.NotNull(method);

        await (Task)method!.Invoke(
            tp.Pipeline,
            [stale, "Transport channel closed", null, "work", AgentKind.Claude])!;

        var final = await tp.Store.GetAsync(stale.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.Equal("operator answer required", final.LastError);
        Assert.Null(final.FailureKind);
        Assert.Null(final.NextTransientRetryAt);
        Assert.Null(final.TransientRetryFirstFailedAt);
        Assert.Equal(0, final.TransientRetryAttempts);
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "work_item.waiting_for_transient_retry");
    }

    [Fact]
    public async Task WorkPhaseTransientRetry_WithExistingWorkBranch_UsesDurableWorkCheckpoint()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var time = new ManualTimeProvider();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            transientRetryOptions: TransientRetryOptions(),
            retryTimeProvider: time);

        var item = NewItem("feature/transient-prior-work");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(
            barePath,
            item.WorkBranch!,
            "prior.txt",
            "prior progress\n",
            "prior progress");
        tp.Agent.WorkResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent transport failed",
            Stdout: null,
            Stderr: "request timed out while reading agent stream"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, parked!.State);
        Assert.Equal(RetryFromPolicy.Work, parked.TransientRetryFrom);
        Assert.Equal(
            AgentTurnResumePhase.Work,
            Assert.IsType<AgentTurnResumeCheckpoint>(parked.AgentTurnResumeCheckpoint).Phase);

        time.Advance(TimeSpan.FromSeconds(31));
        await RunTransientPeriodicSweepAsync(tp.RetryScheduler!);

        var resumed = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);
        Assert.Equal(WorkItemState.Working, resumed!.State);
        Assert.Equal(1, resumed.TransientRetryAttempts);
        Assert.Equal(item.Id, await tp.Queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WorkPhaseTransientAgentFailure_AtRetryCap_PublishesFailedNotWaiting()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var time = new ManualTimeProvider();
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            webhookDispatcher: webhooks,
            transientRetryOptions: TransientRetryOptions() with { MaxAutoRetriesPerWorkItem = 0 },
            retryTimeProvider: time);
        tp.Agent.WorkResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent transport failed",
            Stdout: null,
            Stderr: "request timed out while reading agent stream"));

        var item = NewItem("feature/transient-cap");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("transient-exhausted", final.FailureKind);
        Assert.Null(final.NextTransientRetryAt);
        Assert.Contains("attempts=0; max=0", final.LastError);
        Assert.Contains(webhooks.Events, e => e.Event == "work_item.failed" && e.WorkItem?.Id == item.Id);
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "work_item.waiting_for_transient_retry");
    }

    [Fact]
    public async Task MergePhaseTransientAgentFailure_ParksWaitingForTransientRetry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var time = new ManualTimeProvider();
        // A clean merge runs host-side with no agent, so a merge-phase agent
        // transport failure can only arise on the agentic conflict-resolver
        // path. Induce a README conflict so the resolver agent runs and returns
        // a transport-channel failure, which must be classified transient.
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            transientRetryOptions: TransientRetryOptions(),
            retryTimeProvider: time);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));
        tp.Agent.AgenticConflictResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "merge transport failed",
            Stdout: null,
            Stderr: "Transport channel closed"));

        var item = NewItem("feature/transient-merge");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, final!.State);
        Assert.DoesNotContain(final.State, WorkItemDependencies.TerminalStates);
        Assert.Equal("transient", final.FailureKind);
        Assert.Equal("merge", final.TransientRetryFrom);
        Assert.Equal(time.GetUtcNow(), final.TransientRetryFirstFailedAt);
        Assert.Equal(time.GetUtcNow().AddSeconds(30), final.NextTransientRetryAt);
        Assert.Equal(0, final.TransientRetryAttempts);
    }

    [Fact]
    public async Task ConflictMergeVerificationFailure_WithTransportText_DoesNotParkTransientRetry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var time = new ManualTimeProvider();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            transientRetryOptions: TransientRetryOptions(),
            retryTimeProvider: time);

        var item = NewItem("feature/conflict-verification") with
        {
            State = WorkItemState.WorkComplete,
            ConflictReworkAttempts = 1,
        };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(
            barePath,
            item.WorkBranch!,
            "README.md",
            "work branch change\n",
            "work changes readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");

        tp.Agent.AgenticConflictResults.Enqueue(new AgentResult(
            Success: true,
            Summary: "resolver thought it resolved",
            Stdout: "resolver stdout",
            Stderr: "Transport channel closed after harmless reconnect"));
        tp.Agent.AgenticConflictResults.Enqueue(new AgentResult(
            Success: true,
            Summary: "resolver still thought it resolved",
            Stdout: "resolver stdout",
            Stderr: "Transport channel closed after harmless reconnect"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.NotEqual("transient", final.FailureKind);
        Assert.Null(final.NextTransientRetryAt);
        Assert.Equal(0, final.TransientRetryAttempts);
    }

    [Fact]
    public async Task WorkBranchEqualsBaseBranch_FailsBeforeSandbox()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("x", "y"));

        var item = NewItem("main");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("must differ from baseBranch", final.LastError);
    }

    [Fact]
    public async Task MergeAgentDoesNothing_PipelineFailsVerification()
    {
        // A clean merge is now completed host-side and cannot "do nothing" — it
        // is deterministic git plumbing. The merge agent only runs on a genuine
        // conflict, via the agentic resolver. Reproduce the "merge work not
        // actually done" failure there: induce a README conflict but supply no
        // resolution plan, so the resolver agent produces no clean resolution.
        // The pipeline must FAIL (not silently pass) when the conflict is left
        // unresolved.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));
        // No ConflictResolutionPlan / AgenticConflictResults enqueued: the
        // resolver runs out of plan entries and fails to resolve.

        var item = NewItem("feature/hello");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotEqual(WorkItemState.Done, final!.State);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final.State);
    }

    private sealed class RetainableProcessSandboxProvider : ISandboxProvider
    {
        private readonly ProcessSandboxProvider _inner = new(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessSandboxProvider>.Instance);
        private readonly object _gate = new();
        private RetainedSandbox? _retained;
        private volatile bool _executionAvailable = true;

        public string Name => "retainable-process";
        public int RetentionCount { get; private set; }
        public int AdoptionCount { get; private set; }
        public int RetainedSandboxDisposalCount { get; private set; }
        public bool RestoreExecutionOnAdoption { get; set; } = true;
        public bool FailBeforeAdoptionSandboxReturned { get; set; }

        public void SetExecutionAvailable(bool available) => _executionAvailable = available;

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (spec.RecoveryLease is { } requestedLease)
            {
                RetainedSandbox retained;
                lock (_gate)
                {
                    retained = _retained
                        ?? throw new InvalidOperationException("No retained test sandbox exists for adoption.");
                    if (retained.Lease != requestedLease)
                        throw new InvalidDataException("Retained test sandbox lease does not match exactly.");
                    AdoptionCount++;
                }

                if (FailBeforeAdoptionSandboxReturned)
                    throw new SandboxExecutionUnavailableException(255);

                if (RestoreExecutionOnAdoption)
                    _executionAvailable = true;
                return new RetainableProcessSandbox(
                    this,
                    retained.Sandbox,
                    preserveOnDispose: true,
                    retained.ExpectedOrigin);
            }

            var sandbox = await _inner.CreateAsync(spec, ct);
            return new RetainableProcessSandbox(
                this,
                sandbox,
                preserveOnDispose: false,
                expectedOrigin: null);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                IReadOnlyList<ManagedSandboxInfo> result = _retained is null
                    ? []
                    :
                    [
                        new ManagedSandboxInfo(
                            _retained.Sandbox.Id,
                            CreatedAt: null,
                            DiskBytes: null,
                            IsTrackedActive: false,
                            LifecycleProviderId: Name),
                    ];
                return Task.FromResult(result);
            }
        }

        public async Task DisposeLeakedAsync(string name, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ISandbox? sandbox = null;
            lock (_gate)
            {
                if (_retained is { } retained
                    && string.Equals(retained.Sandbox.Id, name, StringComparison.Ordinal))
                {
                    sandbox = retained.Sandbox;
                    _retained = null;
                }
            }

            if (sandbox is not null)
                await sandbox.DisposeAsync();
        }

        private SandboxRecoveryLease Retain(RetainableProcessSandbox owner)
        {
            owner.EnablePreserveOnDispose();
            lock (_gate)
            {
                if (_retained is { } retained)
                {
                    if (!ReferenceEquals(retained.Sandbox, owner.InnerSandbox))
                        throw new InvalidOperationException("A different test sandbox is already retained.");
                    return retained.Lease;
                }

                var lease = new SandboxRecoveryLease(
                    Name,
                    owner.Id,
                    Guid.NewGuid().ToString("N"));
                _retained = new RetainedSandbox(owner.InnerSandbox, lease, owner.ExpectedOrigin);
                RetentionCount++;
                return lease;
            }
        }

        private async ValueTask DisposeInnerAsync(ISandbox sandbox)
        {
            lock (_gate)
            {
                if (_retained is { } retained
                    && ReferenceEquals(retained.Sandbox, sandbox))
                {
                    _retained = null;
                    RetainedSandboxDisposalCount++;
                }
            }

            await sandbox.DisposeAsync();
        }

        private sealed record RetainedSandbox(
            ISandbox Sandbox,
            SandboxRecoveryLease Lease,
            string? ExpectedOrigin);

        private sealed class RetainableProcessSandbox :
            IPreemptibleSandbox,
            IPreserveOnDisposeSandbox,
            IProviderOwnedSandbox,
            ISandboxDecorator
        {
            private readonly RetainableProcessSandboxProvider _owner;
            private bool _preserveOnDispose;
            private int _disposed;
            private string? _expectedOrigin;

            public RetainableProcessSandbox(
                RetainableProcessSandboxProvider owner,
                ISandbox innerSandbox,
                bool preserveOnDispose,
                string? expectedOrigin)
            {
                _owner = owner;
                InnerSandbox = innerSandbox;
                _preserveOnDispose = preserveOnDispose;
                _expectedOrigin = expectedOrigin;
            }

            public string Id => InnerSandbox.Id;
            public string ProviderId => _owner.Name;
            public ISandbox InnerSandbox { get; }
            public string? ExpectedOrigin => _expectedOrigin;
            public SandboxAgentOutputTransportKind AgentOutputTransportKind =>
                InnerSandbox.AgentOutputTransportKind;
            public SandboxBatchLaunchMode BatchLaunchMode => InnerSandbox.BatchLaunchMode;

            public async Task<SandboxExecResult> ExecAsync(
                SandboxExec exec,
                CancellationToken ct = default)
            {
                if (!_owner._executionAvailable)
                {
                    return new SandboxExecResult(
                        ExitCode: 255,
                        Stdout: string.Empty,
                        Stderr: "simulated sandbox transport outage",
                        ExecutionUnavailable: true);
                }

                var result = await InnerSandbox.ExecAsync(exec, ct);
                if (result.Success
                    && exec.Argv.Count >= 3
                    && string.Equals(exec.Argv[0], "git", StringComparison.Ordinal)
                    && string.Equals(exec.Argv[1], "clone", StringComparison.Ordinal))
                {
                    _expectedOrigin = exec.Argv[2];
                }
                if (result.Success
                    && _expectedOrigin is not null
                    && exec.Argv.Count >= 6
                    && string.Equals(exec.Argv[0], "git", StringComparison.Ordinal)
                    && string.Equals(exec.Argv[3], "remote", StringComparison.Ordinal)
                    && string.Equals(exec.Argv[4], "get-url", StringComparison.Ordinal)
                    && string.Equals(exec.Argv[5], "origin", StringComparison.Ordinal))
                {
                    return result with { Stdout = _expectedOrigin + "\n" };
                }

                return result;
            }

            public Task SyncStateToHostAsync(CancellationToken ct = default) =>
                InnerSandbox.SyncStateToHostAsync(ct);

            public Task StopAndPreserveAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                _ = _owner.Retain(this);
                return Task.CompletedTask;
            }

            public Task<SandboxRecoveryLease?> RetainForInfrastructureRecoveryAsync(
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<SandboxRecoveryLease?>(_owner.Retain(this));
            }

            public void DisablePreserveOnDispose() => _preserveOnDispose = false;
            public void EnablePreserveOnDispose() => _preserveOnDispose = true;

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0 || _preserveOnDispose)
                    return;
                await _owner.DisposeInnerAsync(InnerSandbox);
            }
        }
    }

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

    [Fact]
    public async Task TwoWorkItems_DoNotShareBareRepoVisibility()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-iso-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalGitHost>.Instance);

        var idA = WorkItemId.New();
        var idB = WorkItemId.New();
        var repoA = await gitHost.EnsureRepositoryAsync(idA, seed);
        var repoB = await gitHost.EnsureRepositoryAsync(idB, seed);
        Assert.NotEqual(repoA, repoB);

        var access = gitHost.GetSandboxAccess(repoA);
        Assert.Single(access.Mounts);
        Assert.Equal(LocalGitHost.SandboxRepoMountPath, access.Mounts[0].SandboxPath);
        Assert.Contains(repoA, access.Mounts[0].HostPath!);
        Assert.DoesNotContain(repoB, access.Mounts[0].HostPath!);
    }

    private async Task CommitToBareBranchAsync(
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(_workspace, "stale-branch-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", branch);

        var path = Path.Combine(clone, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        await TestSupport.RunGit(clone, "add", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(clone, "push", "origin", $"{branch}:{branch}");
    }

    private static async Task CommitToSeedAsync(string repoPath, string path, string content, string message)
    {
        await TestSupport.RunGit(repoPath, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(repoPath, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repoPath, path), content);
        await TestSupport.RunGit(repoPath, "add", path);
        await TestSupport.RunGit(repoPath, "commit", "-m", message);
    }

    private static async Task<string> RevParseAsync(string repoPath, string rev)
    {
        var (_, stdout, _) = await TestSupport.RunGit(repoPath, "rev-parse", rev);
        return stdout.Trim();
    }

    private static async Task RunTransientPeriodicSweepAsync(TransientRetryScheduler scheduler)
    {
        var method = typeof(TransientRetryScheduler).GetMethod(
            "RunTransientPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(scheduler, [CancellationToken.None])!;
    }

    private static AutoRetryOnTransientFailureOptions TransientRetryOptions() => new()
    {
        Enabled = true,
        BaseDelay = TimeSpan.FromSeconds(30),
        MaxDelay = TimeSpan.FromMinutes(15),
        Multiplier = 2,
        MaxAutoRetriesPerWorkItem = 5,
        MaxElapsedTime = TimeSpan.FromHours(1),
        JitterMode = TransientRetryJitterMode.None,
    };

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };
}
