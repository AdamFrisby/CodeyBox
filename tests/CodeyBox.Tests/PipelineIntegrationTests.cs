using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

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
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

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
        Assert.Null(final.TransientRetryFrom);
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
    public async Task WorkPhaseTransientRetry_WithExistingWorkBranch_AutoPicksAudit()
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
        Assert.Null(parked.TransientRetryFrom);

        time.Advance(TimeSpan.FromSeconds(31));
        await RunTransientPeriodicSweepAsync(tp.RetryScheduler!);

        var resumed = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);
        Assert.Equal(WorkItemState.WorkComplete, resumed!.State);
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
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            transientRetryOptions: TransientRetryOptions(),
            retryTimeProvider: time);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "work complete\n"));
        tp.Agent.MergeResults.Enqueue(new AgentResult(
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
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed,
            mergeStrategy: [MergeStrategy.NoOp]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hi\n"));

        var item = NewItem("feature/hello");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("merge agent", final.LastError, StringComparison.OrdinalIgnoreCase);
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
