using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Field-by-field round-trip of the phase-execution seam request object: every
/// field the old positional phase signatures carried must survive the move.
/// No sandbox, git, or store involved.
/// </summary>
public sealed class AgentPhaseRequestTests
{
    [Theory]
    [InlineData(AgentPhaseKind.Work)]
    [InlineData(AgentPhaseKind.Rework)]
    [InlineData(AgentPhaseKind.Merge)]
    public void Request_RoundTripsEveryField(AgentPhaseKind phase)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Seam item",
            Prompt = "do the thing",
            WorkBranch = "feature/seam-roundtrip",
            ModelId = "model-7",
            ReasoningMode = "high",
            AgentTurnRecoveryLease = new SandboxRecoveryLease("prov", "sandbox-1", "tok-1"),
        };
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = "https://example.invalid/seed.git",
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
        };
        var runner = new StubAgentRunner(new AgentKind("stub-kind"));
        var auditor = new StubAuditor();

        var request = new AgentPhaseRequest
        {
            Item = item,
            Project = project,
            Phase = phase,
            RepositoryId = "repo-123",
            BaseBranch = "main",
            Branch = "feature/seam-roundtrip",
            Runner = runner,
            Prompt = "do the thing",
            NetworkProfile = "test-net",
            SandboxFlavor = SandboxProfileFlavor.Graphical,
            BuildPolicy = AgentPhaseBuildPolicy.DeferToAuditLoop,
            Iteration = 3,
            AuditorsForPreemptiveSelfReview = [auditor],
            ReworkNoDiffHandling = AgentPhaseReworkNoDiffHandling.AuditEmptyRework,
            ResumePreTurnCommitSha = "0123456789abcdef0123456789abcdef01234567",
            SuppressNoChangesBreaker = true,
        };

        Assert.Same(item, request.Item);
        Assert.Same(project, request.Project);
        Assert.Equal(phase, request.Phase);
        Assert.Equal("repo-123", request.RepositoryId);
        Assert.Equal("main", request.BaseBranch);
        Assert.Equal("feature/seam-roundtrip", request.Branch);
        Assert.Same(runner, request.Runner);
        Assert.Equal("do the thing", request.Prompt);
        Assert.Equal("test-net", request.NetworkProfile);
        Assert.Equal(SandboxProfileFlavor.Graphical, request.SandboxFlavor);
        Assert.Equal(AgentPhaseBuildPolicy.DeferToAuditLoop, request.BuildPolicy);
        Assert.Equal(3, request.Iteration);
        Assert.Same(auditor, Assert.Single(request.AuditorsForPreemptiveSelfReview!));
        Assert.Equal(AgentPhaseReworkNoDiffHandling.AuditEmptyRework, request.ReworkNoDiffHandling);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", request.ResumePreTurnCommitSha);
        Assert.True(request.SuppressNoChangesBreaker);

        // Resume state travels on the item itself.
        Assert.Equal("tok-1", request.Item.AgentTurnRecoveryLease!.Token);

        // The declared route is derived from the runner + item, so it can
        // never contradict the invocation the executor will perform.
        Assert.Equal(new AgentKind("stub-kind"), request.AgentRoute.Kind);
        Assert.Equal("model-7", request.AgentRoute.ModelId);
        Assert.Equal("high", request.AgentRoute.ReasoningMode);
    }

    [Fact]
    public void Request_DefaultsMatchLegacyCallConventions()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Seam item",
            Prompt = "do the thing",
            WorkBranch = "feature/seam-defaults",
        };
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = "https://example.invalid/seed.git",
        };
        var request = new AgentPhaseRequest
        {
            Item = item,
            Project = project,
            Phase = AgentPhaseKind.Merge,
            RepositoryId = "repo-123",
            BaseBranch = "main",
            Branch = "feature/seam-defaults",
            Runner = new StubAgentRunner(AgentKind.Claude),
        };

        Assert.Equal(SandboxProfileFlavor.Headless, request.SandboxFlavor);
        Assert.Equal(AgentPhaseReworkNoDiffHandling.TerminalError, request.ReworkNoDiffHandling);
        Assert.Null(request.Prompt);
        Assert.Null(request.BuildPolicy);
        Assert.Null(request.Iteration);
        Assert.Null(request.AuditorsForPreemptiveSelfReview);
        Assert.Null(request.ResumePreTurnCommitSha);
        Assert.False(request.SuppressNoChangesBreaker);

        var result = new AgentPhaseResult
        {
            Phase = AgentPhaseKind.Merge,
            Outcome = AgentPhaseOutcome.Completed,
        };
        Assert.Empty(result.Findings);
        Assert.Null(result.Usage);
        Assert.Null(result.ResultingCommitSha);
        Assert.Null(result.AgentStdout);
        Assert.Null(result.AgentStreamFileName);
    }

    private sealed class StubAgentRunner(AgentKind kind) : IAgentRunner
    {
        public AgentKind Kind { get; } = kind;

        public Task<AgentResult> RunAsync(
            ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential,
            string? modelId = null, string? reasoningMode = null, CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
            => throw new NotSupportedException("Round-trip test never runs the agent.");
    }

    private sealed class StubAuditor : IAuditor
    {
        public string Name => "stub-auditor";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => throw new NotSupportedException("Round-trip test never runs auditors.");
    }
}

/// <summary>
/// The phase-execution seam exercised end to end: substitution with a test
/// double (no sandbox provider), parity of work/rework/merge through the
/// interface against the real in-process implementation, and resume-state
/// honouring through the interface.
/// </summary>
[Collection("Pipeline integration")]
public sealed class AgentPhaseExecutorTests : IDisposable
{
    private readonly string _workspace;

    public AgentPhaseExecutorTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-agent-phase-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the temp workspace.
        }
    }

    [Fact]
    public async Task PipelineRuns_WorkAndMergeThroughDouble_WithNoSandboxProvider()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ThrowingSandboxProvider();
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]);
        var webhooks = new NullWebhookDispatcher();
        var project = SeamProject(seed);
        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
        var fake = new GitBackedFakeExecutor(gitHost, _workspace);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, new AgentRegistry([agent]), new StaticCredentialProvider(),
            new InMemoryPullRequestService(), projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions,
            phaseExecutor: fake);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = project.Id,
            Title = "Seam double item",
            Prompt = "do the thing",
            WorkBranch = "feature/seam-double",
        };
        await store.CreateAsync(item);
        await pipeline.RunAsync(item, CancellationToken.None);

        var final = await store.GetAsync(item.Id);
        Assert.True(final!.State == WorkItemState.Done,
            $"Expected Done but was {final!.State} (kind={final.FailureKind}): {final.LastError}");

        // Both sandbox-backed phases went through the double, in order.
        Assert.Equal(
            new[] { AgentPhaseKind.Work, AgentPhaseKind.Merge },
            fake.Requests.Select(r => r.Phase).ToList());
        var workRequest = fake.Requests[0];
        Assert.Equal(item.Id, workRequest.Item.Id);
        Assert.Equal("test-project", workRequest.Project.Id.Value);
        Assert.Equal("main", workRequest.BaseBranch);
        Assert.Equal("feature/seam-double", workRequest.Branch);
        Assert.False(string.IsNullOrWhiteSpace(workRequest.Prompt));
        Assert.Equal(AgentKind.Claude, workRequest.AgentRoute.Kind);
        var mergeRequest = fake.Requests[1];
        Assert.Equal(item.Id, mergeRequest.Item.Id);

        // No sandbox was ever provisioned and the registry agent never ran:
        // the double owns phase execution.
        Assert.Equal(0, sandboxes.CreateCalls);
        Assert.Empty(agent.WorkPrompts);

        // The fake merge produced a real two-parent merge commit on main.
        var bare = gitHost.GetRepoPath(fake.LastRepositoryId);
        var (_, parents, _) = await TestSupport.RunGit(bare, "rev-list", "--parents", "-n", "1", fake.LastMergeSha);
        Assert.Equal(3, parents.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task WorkReworkMerge_ThroughInterface_ProduceSameOutcomeShaAndState()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var setup = TestSupport.BuildPipeline(
            _workspace, seed, requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        var project = SeamProject(seed);
        var agent = setup.Agent;
        agent.WorkPlan.Enqueue(new FileWrite("output.txt", "v1"));
        agent.WorkPlan.Enqueue(new FileWrite("rework.txt", "v2"));
        agent.ResultStdout = "phase-stdout";

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = project.Id,
            Title = "Seam parity item",
            Prompt = "write output.txt",
            WorkBranch = "feature/seam-parity",
        };
        await setup.Store.CreateAsync(item);
        var repoId = await setup.GitHost.EnsureRepositoryAsync(item.Id, seed, "main");

        var workResult = await setup.Pipeline.PhaseExecutor.ExecuteAsync(new AgentPhaseRequest
        {
            Item = item,
            Project = project,
            Phase = AgentPhaseKind.Work,
            RepositoryId = repoId,
            BaseBranch = "main",
            Branch = item.WorkBranch,
            Runner = agent,
            Prompt = "write output.txt",
            BuildPolicy = AgentPhaseBuildPolicy.Terminal,
        }, CancellationToken.None, CancellationToken.None);

        Assert.Equal(AgentPhaseKind.Work, workResult.Phase);
        Assert.Equal(AgentPhaseOutcome.Completed, workResult.Outcome);
        Assert.Equal("phase-stdout", workResult.AgentStdout);
        var workTip = await setup.GitHost.ResolveCommitAsync(repoId, item.WorkBranch);
        Assert.Equal(workTip, workResult.ResultingCommitSha);
        Assert.Empty(workResult.Findings);
        Assert.Null(workResult.AgentStreamFileName);

        var reworkResult = await setup.Pipeline.PhaseExecutor.ExecuteAsync(new AgentPhaseRequest
        {
            Item = (await setup.Store.GetAsync(item.Id))!,
            Project = project,
            Phase = AgentPhaseKind.Rework,
            RepositoryId = repoId,
            BaseBranch = "main",
            Branch = item.WorkBranch,
            Runner = agent,
            Prompt = "address findings",
            BuildPolicy = AgentPhaseBuildPolicy.DeferToAuditLoop,
            Iteration = 1,
            ReworkNoDiffHandling = AgentPhaseReworkNoDiffHandling.AuditEmptyRework,
            SuppressNoChangesBreaker = true,
        }, CancellationToken.None, CancellationToken.None);

        Assert.Equal(AgentPhaseKind.Rework, reworkResult.Phase);
        Assert.Equal(AgentPhaseOutcome.Completed, reworkResult.Outcome);
        Assert.Equal("phase-stdout", reworkResult.AgentStdout);
        var reworkTip = await setup.GitHost.ResolveCommitAsync(repoId, item.WorkBranch);
        Assert.Equal(reworkTip, reworkResult.ResultingCommitSha);
        Assert.NotEqual(workTip, reworkTip);

        var bare = setup.GitHost.GetRepoPath(repoId);
        var (_, tree, _) = await TestSupport.RunGit(bare, "ls-tree", "-r", item.WorkBranch, "--name-only");
        Assert.Contains("output.txt", tree);
        Assert.Contains("rework.txt", tree);

        // Clean merge is host-side: the merge phase through the interface
        // produces the merge commit without invoking any agent.
        var mergeResult = await setup.Pipeline.PhaseExecutor.ExecuteAsync(new AgentPhaseRequest
        {
            Item = (await setup.Store.GetAsync(item.Id))!,
            Project = project,
            Phase = AgentPhaseKind.Merge,
            RepositoryId = repoId,
            BaseBranch = "main",
            Branch = item.WorkBranch,
            Runner = agent,
        }, CancellationToken.None, CancellationToken.None);

        Assert.Equal(AgentPhaseKind.Merge, mergeResult.Phase);
        Assert.Equal(AgentPhaseOutcome.Completed, mergeResult.Outcome);
        Assert.Null(mergeResult.AgentStdout);
        var baseTip = await setup.GitHost.ResolveCommitAsync(repoId, "main");
        Assert.Equal(baseTip, mergeResult.ResultingCommitSha);
        var (_, mergeParents, _) = await TestSupport.RunGit(bare, "rev-list", "--parents", "-n", "1", baseTip);
        Assert.Equal(3, mergeParents.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        // Only work + rework reached the agent; the clean merge did not.
        Assert.Equal(2, agent.WorkPrompts.Count);
    }

    [Fact]
    public async Task ResumeState_PreemptCheckpoint_HonouredThroughInterface()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var setup = TestSupport.BuildPipeline(
            _workspace, seed, requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        var project = SeamProject(seed);
        var agent = setup.Agent;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = project.Id,
            Title = "Seam resume item",
            Prompt = "write partial work",
            WorkBranch = "feature/seam-resume",
        };
        await setup.Store.CreateAsync(item);
        // RunAsync transitions the item to Working before dispatching the
        // phase; checkpoint creation requires it, so mirror that here.
        item = item with { State = WorkItemState.Working };
        await setup.Store.UpdateAsync(item);
        var repoId = await setup.GitHost.EnsureRepositoryAsync(item.Id, seed, "main");
        var baseTip = await setup.GitHost.ResolveCommitAsync(repoId, "main");

        var blocking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/partial.txt"],
                Stdin = "partial",
            }, ct);
            if (!write.Success)
                throw new InvalidOperationException("setup write failed: " + write.Stderr);
            blocking.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        using var hostShutdown = new CancellationTokenSource();
        var interrupted = setup.Pipeline.PhaseExecutor.ExecuteAsync(new AgentPhaseRequest
        {
            Item = item,
            Project = project,
            Phase = AgentPhaseKind.Work,
            RepositoryId = repoId,
            BaseBranch = "main",
            Branch = item.WorkBranch,
            Runner = agent,
            Prompt = "write partial work",
            BuildPolicy = AgentPhaseBuildPolicy.Terminal,
        }, CancellationToken.None, hostShutdown.Token);

        await blocking.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await hostShutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted);

        var checkpointed = await setup.Store.GetAsync(item.Id);
        Assert.False(string.IsNullOrWhiteSpace(checkpointed!.PreemptCheckpoint));
        Assert.NotNull(checkpointed.AgentTurnResumeCheckpoint);

        agent.BeforeWorkAsync = null;
        agent.WorkPlan.Enqueue(new FileWrite("resumed.txt", "done"));
        agent.ResultStdout = "resumed-stdout";

        var resumed = await setup.Pipeline.PhaseExecutor.ExecuteAsync(new AgentPhaseRequest
        {
            Item = checkpointed,
            Project = project,
            Phase = AgentPhaseKind.Work,
            RepositoryId = repoId,
            BaseBranch = "main",
            Branch = item.WorkBranch,
            Runner = agent,
            Prompt = "write partial work",
            BuildPolicy = AgentPhaseBuildPolicy.Terminal,
            ResumePreTurnCommitSha = baseTip,
        }, CancellationToken.None, CancellationToken.None);

        Assert.Equal(AgentPhaseOutcome.Completed, resumed.Outcome);
        Assert.Equal("resumed-stdout", resumed.AgentStdout);
        var tip = await setup.GitHost.ResolveCommitAsync(repoId, item.WorkBranch);
        Assert.Equal(tip, resumed.ResultingCommitSha);

        // The resumed turn restored the checkpointed tree (partial.txt) and
        // stacked its own commit (resumed.txt) on top.
        var bare = setup.GitHost.GetRepoPath(repoId);
        var (_, tree, _) = await TestSupport.RunGit(bare, "ls-tree", "-r", item.WorkBranch, "--name-only");
        Assert.Contains("partial.txt", tree);
        Assert.Contains("resumed.txt", tree);

        // The durable turn state was cleared once the resumed tree landed.
        var cleared = await setup.Store.GetAsync(item.Id);
        Assert.True(string.IsNullOrWhiteSpace(cleared!.PreemptCheckpoint));
        Assert.Null(cleared.AgentTurnResumeCheckpoint);
    }

    private static Project SeamProject(string seedRepoUrl) => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test Project",
        RepositoryUrl = seedRepoUrl,
        DefaultBaseBranch = "main",
        DefaultAgent = AgentKind.Claude,
        Audit = new ProjectAudit
        {
            MaxIterations = 1,
        },
    };

    private sealed class ThrowingSandboxProvider : ISandboxProvider
    {
        public int CreateCalls { get; private set; }
        public string Name => "throwing-test-provider";

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => Task.CompletedTask;

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            CreateCalls++;
            throw new InvalidOperationException(
                "No sandbox provider registered: phases must run through the injected executor double.");
        }
    }

    /// <summary>
    /// Executor double that performs minimal real git through the real git
    /// host (temp clones, no sandbox) so downstream pipeline steps observe a
    /// consistent repository.
    /// </summary>
    private sealed class GitBackedFakeExecutor(LocalGitHost gitHost, string workspace) : IAgentPhaseExecutor
    {
        public List<AgentPhaseRequest> Requests { get; } = [];
        public string LastRepositoryId { get; private set; } = string.Empty;
        public string LastMergeSha { get; private set; } = string.Empty;

        public async Task<AgentPhaseResult> ExecuteAsync(
            AgentPhaseRequest request, CancellationToken ct, CancellationToken hostShutdownToken)
        {
            Requests.Add(request);
            LastRepositoryId = request.RepositoryId;
            return request.Phase switch
            {
                AgentPhaseKind.Work => await ExecuteWorkAsync(request, ct),
                AgentPhaseKind.Merge => await ExecuteMergeAsync(request, ct),
                _ => throw new InvalidOperationException($"Unexpected phase {request.Phase} in double test."),
            };
        }

        private async Task<AgentPhaseResult> ExecuteWorkAsync(AgentPhaseRequest request, CancellationToken ct)
        {
            var clone = Directory.CreateTempSubdirectory("codeybox-fake-work-").FullName;
            try
            {
                var bare = gitHost.GetRepoPath(request.RepositoryId);
                await TestSupport.RunGit(workspace, "clone", bare, clone);
                await TestSupport.RunGit(clone, "config", "user.email", "fake@test.invalid");
                await TestSupport.RunGit(clone, "config", "user.name", "Fake");
                await TestSupport.RunGit(clone, "checkout", "-b", request.Branch, $"origin/{request.BaseBranch}");
                await TestSupport.RunGit(clone, "commit", "--allow-empty", "-m", "fake work");
                await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{request.Branch}");
                var sha = await gitHost.ResolveCommitAsync(request.RepositoryId, request.Branch, ct);
                return new AgentPhaseResult
                {
                    Phase = request.Phase,
                    Outcome = AgentPhaseOutcome.Completed,
                    ResultingCommitSha = sha,
                    AgentStdout = "fake-work-stdout",
                };
            }
            finally
            {
                try
                {
                    Directory.Delete(clone, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup of the temp clone.
                }
            }
        }

        private async Task<AgentPhaseResult> ExecuteMergeAsync(AgentPhaseRequest request, CancellationToken ct)
        {
            var clone = Directory.CreateTempSubdirectory("codeybox-fake-merge-").FullName;
            try
            {
                var bare = gitHost.GetRepoPath(request.RepositoryId);
                await TestSupport.RunGit(workspace, "clone", bare, clone);
                await TestSupport.RunGit(clone, "config", "user.email", "fake@test.invalid");
                await TestSupport.RunGit(clone, "config", "user.name", "Fake");
                await TestSupport.RunGit(clone, "checkout", request.BaseBranch);
                await TestSupport.RunGit(
                    clone, "merge", "--no-ff", "-m", $"fake merge {request.Branch}", $"origin/{request.Branch}");
                await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{request.BaseBranch}");
                var sha = await gitHost.ResolveCommitAsync(request.RepositoryId, request.BaseBranch, ct);
                LastMergeSha = sha;
                return new AgentPhaseResult
                {
                    Phase = request.Phase,
                    Outcome = AgentPhaseOutcome.Completed,
                    ResultingCommitSha = sha,
                    AgentStdout = "fake-merge-stdout",
                };
            }
            finally
            {
                try
                {
                    Directory.Delete(clone, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup of the temp clone.
                }
            }
        }
    }
}
