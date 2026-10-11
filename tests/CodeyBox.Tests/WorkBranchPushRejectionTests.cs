using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the same-branch push-rejection contract: when an agent turn's push
/// is refused as non-fast-forward against its own work branch, the host
/// rebases the new commits onto the current tip and pushes without an agent
/// (clean case: the item proceeds); a genuine content conflict routes into
/// the existing conflict-rework machine (or a resumable merge-conflict park
/// when routing is unavailable) and never fails the item terminally. Also
/// covers work-phase staging: a workspace trailing the bare-repo tip is
/// corrected to the tip before the agent runs. Requires git on PATH.
/// </summary>
[Collection("Pipeline integration")]
public sealed class WorkBranchPushRejectionTests : IDisposable
{
    private readonly string _workspace;

    public WorkBranchPushRejectionTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-push-reject-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // Best-effort test teardown.
        }
    }

    [Fact]
    public async Task WorkTurn_MidTurnRivalAdvance_ReconcilesAndProceeds()
    {
        string? rivalTip = null;
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]);
        agent.BeforeWorkAsync = (_, _, _) => AdvanceCurrentScopeAsync(
            "rival.txt", "rival change\n", "rival advance",
            sha => rivalTip = sha);
        agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "agent change\n"));
        await using var tracker = TrackingSandboxProvider.ForProcessSandbox();
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace),
            sandboxProvider: tracker,
            agentOverride: agent);

        var item = NewItem();
        _rivalScope = (setup.GitRoot, item.Id, item.WorkBranch!);
        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.NotNull(rivalTip);
        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var barePath = BarePathFor(setup, item);
        var (_, tree, _) = await TestSupport.RunGit(barePath, "ls-tree", "-r", item.WorkBranch!, "--name-only");
        Assert.Contains("agent.txt", tree);
        Assert.Contains("rival.txt", tree);
        // The rival history is retained: its tip is an ancestor of the
        // reconciled branch tip (a force-push/clobber would still show both
        // files but would not preserve this ancestry).
        var (code, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "merge-base", "--is-ancestor", rivalTip!, item.WorkBranch!);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task WorkTurn_MidTurnConflictingAdvance_RoutesToConflictRework()
    {
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]);
        agent.BeforeWorkAsync = (_, _, _) => AdvanceCurrentScopeAsync(
            "agent.txt", "rival change\n", "rival advance");
        agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "agent change\n"));
        await using var tracker = TrackingSandboxProvider.ForProcessSandbox();
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace),
            sandboxProvider: tracker,
            agentOverride: agent,
            staleBaseReworkOptions: new StalePullRequestSweeperOptions
            {
                RouteToConflictRework = true,
                MaxReworkAttempts = 2,
            });

        var item = NewItem();
        _rivalScope = (setup.GitRoot, item.Id, item.WorkBranch!);
        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.ReworkingForConflict, final!.State);
        Assert.Equal(1, final.ConflictReworkAttempts);
        Assert.Contains("conflict", final.MergeRetryReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var queue = Assert.IsType<InMemoryTaskQueue>(setup.Queue);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WorkTurn_ConflictingAdvanceWithoutRouter_ParksResumably()
    {
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]);
        agent.BeforeWorkAsync = (_, _, _) => AdvanceCurrentScopeAsync(
            "agent.txt", "rival change\n", "rival advance");
        agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "agent change\n"));
        await using var tracker = TrackingSandboxProvider.ForProcessSandbox();
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace),
            sandboxProvider: tracker,
            agentOverride: agent);

        var item = NewItem();
        _rivalScope = (setup.GitRoot, item.Id, item.WorkBranch!);
        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.NotEqual(WorkItemState.Failed, final.State);
        Assert.Contains("conflict", final.LastError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResumeAfterBranchAdvanced_PreservesCheckpointWork()
    {
        // The restart/recovery path must not stage a stale baseline: the
        // first turn is preempted after writing its file, then the branch
        // advances without it (as if the bare repo moved while the worker was
        // gone). The resume resolves the turn's true base as the fork point
        // of the tip and the checkpoint source, keeps the checkpointed work,
        // and reconciles the completed turn onto the advanced tip.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]);
        agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            calls++;
            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "echo 'real change' > \"$0\"", $"{workingDirectory}/output.txt"],
            }, ct);
            if (!write.Success)
                throw new InvalidOperationException($"failed to write output.txt: {write.Stderr}");
            if (calls == 1)
            {
                ready.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        agent.WorkResults.Enqueue(new AgentResult(true, "resumed ok", "resumed ok", null));
        await using var tracker = TrackingSandboxProvider.ForProcessSandbox();
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace),
            sandboxProvider: tracker,
            agentOverride: agent);

        var item = NewItem();
        await setup.Store.CreateAsync(item);
        var checkpointRef = await PreemptFirstTurnAsync(setup, item, ready);
        Assert.False(string.IsNullOrWhiteSpace(checkpointRef));

        // The branch advances past the checkpoint's pre-turn commit without
        // the checkpointed tree: the branch did not exist when the turn
        // started, so it is created at the base tip and advanced.
        _rivalScope = (setup.GitRoot, item.Id, item.WorkBranch!);
        await AdvanceCurrentScopeAsync("rival.txt", "rival change\n", "rival advance");

        var resumeEntry = await setup.Store.GetAsync(item.Id)
            ?? throw new InvalidOperationException("work item disappeared after preempt");
        await setup.Pipeline.RunAsync(resumeEntry, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.True(final!.State == WorkItemState.Done, $"state={final.State} failureKind={final.FailureKind} error={final.LastError}");
        Assert.Equal(WorkItemState.Done, final.State);
        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.AgentTurnResumeCheckpoint);

        var barePath = BarePathFor(setup, item);
        var (_, tree, _) = await TestSupport.RunGit(barePath, "ls-tree", "-r", item.WorkBranch!, "--name-only");
        Assert.Contains("output.txt", tree);
        Assert.Contains("rival.txt", tree);
    }

    [Fact]
    public async Task StagingGuard_CorrectsBehindTipCheckoutToCurrentTip()
    {
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace));
        var repoId = "guard-" + Guid.NewGuid().ToString("N")[..8];
        var branch = "feature/guard-" + Guid.NewGuid().ToString("N")[..8];
        var barePath = setup.GitHost.GetRepoPath(repoId);

        var sandboxDir = Path.Combine(_workspace, "guard-sandbox");
        var writerDir = Path.Combine(_workspace, "guard-writer");
        await TestSupport.RunGit(_workspace, "init", "--bare", "-q", barePath);
        await TestSupport.RunGit(_workspace, "clone", "-q", barePath, sandboxDir);
        await TestSupport.RunGit(_workspace, "clone", "-q", barePath, writerDir);
        foreach (var dir in new[] { sandboxDir, writerDir })
        {
            await TestSupport.RunGit(dir, "config", "user.email", "guard@example.invalid");
            await TestSupport.RunGit(dir, "config", "user.name", "Guard");
        }
        await File.WriteAllTextAsync(Path.Combine(sandboxDir, "seed.txt"), "seed\n");
        await TestSupport.RunGit(sandboxDir, "add", "seed.txt");
        await TestSupport.RunGit(sandboxDir, "commit", "-q", "-m", "initial");
        await TestSupport.RunGit(sandboxDir, "push", "-q", "origin", "HEAD:main");
        await TestSupport.RunGit(sandboxDir, "fetch", "-q", "origin");
        await TestSupport.RunGit(sandboxDir, "checkout", "-q", "-B", branch, "origin/main");

        // Advance the bare branch past the sandbox checkout: the staged
        // workspace now trails the current tip.
        await TestSupport.RunGit(writerDir, "fetch", "-q", "origin");
        await TestSupport.RunGit(writerDir, "checkout", "-q", "-B", branch, "origin/main");
        await File.WriteAllTextAsync(Path.Combine(writerDir, "ahead.txt"), "ahead\n");
        await TestSupport.RunGit(writerDir, "add", "ahead.txt");
        await TestSupport.RunGit(writerDir, "commit", "-q", "-m", "advance");
        await TestSupport.RunGit(writerDir, "push", "-q", "origin", $"HEAD:{branch}");
        var (_, tip, _) = await TestSupport.RunGit(writerDir, "rev-parse", "HEAD");
        tip = tip.Trim();

        await using var sandbox = new LocalGitSandbox("guard", sandboxDir);
        var proof = await setup.Pipeline.EnsureSandboxAtCurrentWorkBranchTipAsync(
            sandbox, repoId, branch, "main", AgentKind.Claude, "work", CancellationToken.None);

        Assert.Equal(tip, proof.HostTip);
        Assert.Equal(tip, proof.SandboxHead);
        var (_, head, _) = await TestSupport.RunGit(sandboxDir, "rev-parse", "HEAD");
        Assert.Equal(tip, head.Trim());
    }

    [Fact]
    public void StagedHeadAssertion_MismatchTrips()
    {
        var ex = Assert.Throws<AgentInfrastructureFailureException>(() =>
            PipelineRunner.VerifyStagedHeadMatchesTip(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "feature/x",
                AgentKind.Claude,
                "work"));
        Assert.Contains("stale base", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StagedHeadAssertion_MatchPasses()
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        PipelineRunner.VerifyStagedHeadMatchesTip(sha, sha, "feature/x", AgentKind.Claude, "work");
    }

    [Theory]
    [InlineData("--upload-pack=evil")]
    [InlineData("-nasty")]
    [InlineData("feature..evil")]
    public async Task StagingGuard_RejectsUnsafeBranchNameBeforeGit(string branch)
    {
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace));
        var sandboxDir = Path.Combine(_workspace, "guard-reject-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandboxDir);
        await using var sandbox = new LocalGitSandbox("guard-reject", sandboxDir);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            setup.Pipeline.EnsureSandboxAtCurrentWorkBranchTipAsync(
                sandbox, "repo", branch, "main", AgentKind.Claude, "work", CancellationToken.None));
    }

    [Theory]
    [InlineData("--upload-pack=evil")]
    [InlineData("-nasty")]
    public async Task StagingGuard_RejectsUnsafeBaseBranchNameBeforeGit(string baseBranch)
    {
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace));
        var sandboxDir = Path.Combine(_workspace, "guard-reject-base-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandboxDir);
        await using var sandbox = new LocalGitSandbox("guard-reject-base", sandboxDir);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            setup.Pipeline.EnsureSandboxAtCurrentWorkBranchTipAsync(
                sandbox, "repo", "feature/safe", baseBranch, AgentKind.Claude, "work", CancellationToken.None));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("not-a-ref", null)]
    public void CheckpointSourceSha_UnknownRef_ReturnsNull(string? checkpointRef, string? expected) =>
        Assert.Equal(expected, PipelineRunner.CheckpointSourceSha(checkpointRef));

    [Fact]
    public void CheckpointSourceSha_CanonicalRef_ReturnsSource()
    {
        var checkpointRef = "refs/heads/codeybox/preempt/"
            + new string('1', 32) + "/" + new string('a', 40) + "-" + new string('b', 64);
        Assert.Equal(new string('a', 40), PipelineRunner.CheckpointSourceSha(checkpointRef));
    }

    // Set before each run whenever BeforeWorkAsync needs bare-repo
    // coordinates (the setup's GitRoot is only known after BuildPipeline).
    private (string BareGitRoot, WorkItemId ItemId, string Branch) _rivalScope;

    private async Task AdvanceCurrentScopeAsync(
        string file, string content, string message, Action<string>? onTip = null)
    {
        var (gitRoot, itemId, branch) = _rivalScope;
        var barePath = Path.Combine(gitRoot, itemId + ".git");
        var clone = Path.Combine(
            _workspace, "rival-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", "-q", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "rival@example.invalid");
        await TestSupport.RunGit(clone, "config", "user.name", "Rival Writer");
        await TestSupport.RunGit(clone, "fetch", "-q", "origin");
        // The branch may not exist yet (a turn that never published); start
        // it at the base tip in that case, mirroring a landed turn's advance.
        var (showCode, showOut, _) = await TestSupport.RunGitNoThrow(clone, "rev-parse", "--verify", $"origin/{branch}");
        var start = showCode == 0 ? $"origin/{branch}" : "origin/main";
        _ = showOut;
        await TestSupport.RunGit(clone, "checkout", "-q", "-B", branch, start);
        await File.WriteAllTextAsync(Path.Combine(clone, file), content);
        await TestSupport.RunGit(clone, "add", file);
        await TestSupport.RunGit(clone, "commit", "-q", "-m", message);
        await TestSupport.RunGit(clone, "push", "-q", "origin", $"HEAD:{branch}");
        if (onTip is not null)
        {
            var (_, sha, _) = await TestSupport.RunGit(clone, "rev-parse", "HEAD");
            onTip(sha.Trim());
        }
    }

    /// <summary>
    /// Runs the first turn until the agent holds its tree, preempts via host
    /// shutdown, and returns the recorded checkpoint ref.
    /// </summary>
    private static async Task<string> PreemptFirstTurnAsync(
        TestPipeline setup, WorkItem item, TaskCompletionSource ready)
    {
        using var hostShutdown = new CancellationTokenSource();
        var runTask = setup.Pipeline.RunAsync(item, CancellationToken.None, hostShutdown.Token);

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await hostShutdown.CancelAsync();

        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on host shutdown.
        }

        var afterPreempt = await setup.Store.GetAsync(item.Id)
            ?? throw new InvalidOperationException("work item disappeared during preempt");
        Assert.False(
            string.IsNullOrWhiteSpace(afterPreempt.PreemptCheckpoint),
            "expected a preempt checkpoint ref to be recorded");
        Assert.NotNull(afterPreempt.AgentTurnResumeCheckpoint);
        return AgentTurnCheckpointRef.Parse(afterPreempt.PreemptCheckpoint!).Value;
    }

    private static string BarePathFor(TestPipeline setup, WorkItem item) =>
        Path.Combine(setup.GitRoot, item.Id + ".git");

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Test push rejection",
        Prompt = "write the file",
        WorkBranch = "feature/push-reject-" + Guid.NewGuid().ToString("N")[..8],
    };

    /// <summary>
    /// Minimal <see cref="ISandbox"/> that executes git argv against a local
    /// directory, mapping the sandbox work dir onto it. Used to prove the
    /// staging guard against a real behind-tip checkout without timing games.
    /// </summary>
    private sealed class LocalGitSandbox(string id, string workDir) : ISandbox
    {
        public string Id => id;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var argv = exec.Argv.Select(a =>
                string.Equals(a, SandboxConventions.WorkDir, StringComparison.Ordinal) ? workDir : a).ToArray();
            if (argv.Length == 0 || argv[0] != "git")
                throw new InvalidOperationException("test sandbox only supports git commands");
            var psi = new ProcessStartInfo
            {
                FileName = argv[0],
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in argv[1..])
                psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("could not start git");
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new SandboxExecResult(process.ExitCode, stdout, stderr);
        }
    }
}
