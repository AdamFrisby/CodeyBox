using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging;
using MergeClock = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the merge-result build gate (merge queue): the exact merged
/// tree is built with the required build before the base branch advances.
/// A clean textual merge of two individually-green branches that fails to
/// compile routes to conflict rework with the build errors in the brief
/// (never lands, never terminally fails on that signal); a green merged
/// tree lands as before; a base that moves mid-verification forces
/// re-verification so no unverified tree ever lands.
/// </summary>
[Collection("Pipeline integration")]
public sealed class MergeResultBuildVerificationTests : IDisposable
{
    internal const string VerifyBranchPrefix = "codeybox/merge-verify/";

    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-merge-verify-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "merge verification",
        Prompt = "Change a file",
        BaseBranch = "main",
        WorkBranch = workBranch,
    };

    private static bool IsVerifyBranch(string branch) =>
        branch.StartsWith(VerifyBranchPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Two sides that each build on their own base but fail when combined:
    /// the broken merged tree is refused, the item takes exactly one
    /// conflict-rework turn briefed with the build errors, repairs the
    /// combination, and lands green. The base never observes the broken tree.
    /// </summary>
    [Fact]
    public async Task SemanticConflict_RoutesToReworkWithBuildErrors_ThenLandsRepaired()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace, "seed-merge-verify-semantic");
        var verifier = new DelegateBuildVerifier();
        var auditor = new SiblingAdvancingAuditor(_workspace);
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed, auditors: [auditor], requiredBuildVerifier: verifier);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "broken\n"));
        tp.Agent.ConflictReworkPlan.Enqueue(async (sandbox, workDir, ct) =>
        {
            await LandingGit.WriteFileAsync(sandbox, workDir, "work.txt", "fixed\n", ct);
            await LandingGit.RunAsync(sandbox, "git", "-C", workDir, "add", "work.txt");
            await LandingGit.RunAsync(sandbox, "git", "-C", workDir, "commit", "-m", "repair semantic conflict");
            return new AgentResult(true, "repaired", null, null);
        });

        var item = NewItem("codeybox/merge-verify-semantic-" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);
        var repoId = item.Id.ToString();
        verifier.OnVerifyAsync = async (request, ct) =>
        {
            if (!IsVerifyBranch(request.WorkBranch))
                return RequiredBuildVerificationResult.Passed(0, "work-branch head builds");
            var files = await tp.GitHost.ListFilesAsync(repoId, request.WorkBranch, null, ct);
            var hasSibling = files.Contains("sibling.txt", StringComparer.Ordinal);
            var work = files.Contains("work.txt", StringComparer.Ordinal)
                ? await tp.GitHost.ReadTextFileAsync(repoId, request.WorkBranch, "work.txt", ct)
                : null;
            if (hasSibling && string.Equals(work, "broken\n", StringComparison.Ordinal))
                return RequiredBuildVerificationResult.Failed(
                    1,
                    "error CS0108: 'work' declares a duplicate symbol also defined by sibling work; the combined tree does not compile");
            return RequiredBuildVerificationResult.Passed(0, "merged tree builds");
        };

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, final.ConflictReworkAttempts);
        var brief = Assert.Single(tp.Agent.ConflictReworkPrompts);
        Assert.Contains("CS0108", brief, StringComparison.Ordinal);
        var mergeVerifies = verifier.VerificationRequests.Where(r => IsVerifyBranch(r.WorkBranch)).ToList();
        Assert.Equal(2, mergeVerifies.Count);

        var barePath = Path.Combine(tp.GitRoot, repoId + ".git");
        var (_, workContent, _) = await TestSupport.RunGit(barePath, "show", "main:work.txt");
        Assert.Equal("fixed\n", workContent);
        var (_, sibling, _) = await TestSupport.RunGit(barePath, "show", "main:sibling.txt");
        Assert.Equal("sibling\n", sibling);
        var (refCode, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "show-ref", "--verify", $"refs/heads/{VerifyBranchPrefix}{item.Id}");
        Assert.NotEqual(0, refCode);
    }

    /// <summary>
    /// A clean merge result that builds lands exactly as before: no rework,
    /// no landing retries, and the short-lived verification branch is
    /// removed from the host repo.
    /// </summary>
    [Fact]
    public async Task CleanMergeResultThatBuilds_LandsAsBefore()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace, "seed-merge-verify-clean");
        var verifier = new DelegateBuildVerifier();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed, requiredBuildVerifier: verifier);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("feature.txt", "feature\n"));

        var item = NewItem("codeybox/merge-verify-clean-" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(0, final.ConflictReworkAttempts);
        Assert.Equal(0, final.MergeAttempts);
        var mergeVerifies = verifier.VerificationRequests.Where(r => IsVerifyBranch(r.WorkBranch)).ToList();
        Assert.Single(mergeVerifies);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, feature, _) = await TestSupport.RunGit(barePath, "show", "main:feature.txt");
        Assert.Equal("feature\n", feature);
        var (refCode, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "show-ref", "--verify", $"refs/heads/{VerifyBranchPrefix}{item.Id}");
        Assert.NotEqual(0, refCode);
    }

    /// <summary>
    /// A base that moves while the merged tree is being verified forces
    /// re-verification: the stale merge is never landed, the merge is
    /// recomposed against the fresh base, verified again, and only then
    /// landed — so the landed commit's first parent is the moved base tip.
    /// </summary>
    [Fact]
    public async Task BaseMovingDuringVerification_ReverifiesAndNeverLandsStaleTree()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace, "seed-merge-verify-race");
        var verifier = new DelegateBuildVerifier();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed, requiredBuildVerifier: verifier);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("feature.txt", "feature\n"));

        var item = NewItem("codeybox/merge-verify-race-" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);
        var repoId = item.Id.ToString();
        var barePath = Path.Combine(tp.GitRoot, repoId + ".git");
        string? staleMergeSha = null;
        string? movedBaseTip = null;
        var moved = false;
        verifier.OnVerifyAsync = async (request, ct) =>
        {
            _ = ct;
            if (!IsVerifyBranch(request.WorkBranch))
                return RequiredBuildVerificationResult.Passed(0, "work-branch head builds");
            if (!moved)
            {
                moved = true;
                var (_, tip, _) = await TestSupport.RunGit(barePath, "rev-parse", "refs/heads/main");
                var (_, tree, _) = await TestSupport.RunGit(barePath, "rev-parse", "main^{tree}");
                var (_, advanced, _) = await TestSupport.RunGit(
                    barePath,
                    "-c", "user.name=T",
                    "-c", "user.email=t@l",
                    "commit-tree", tree.Trim(), "-p", tip.Trim(), "-m", "out-of-band advance during verification");
                movedBaseTip = advanced.Trim();
                await TestSupport.RunGit(barePath, "update-ref", "refs/heads/main", movedBaseTip, tip.Trim());
                var (_, mergeSha, _) = await TestSupport.RunGit(barePath, "rev-parse", request.WorkBranch);
                staleMergeSha = mergeSha.Trim();
            }
            return RequiredBuildVerificationResult.Passed(0, "merged tree builds");
        };

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(0, final.ConflictReworkAttempts);
        var mergeVerifies = verifier.VerificationRequests.Where(r => IsVerifyBranch(r.WorkBranch)).ToList();
        Assert.Equal(2, mergeVerifies.Count);
        Assert.NotNull(staleMergeSha);
        Assert.NotNull(movedBaseTip);

        var (_, landed, _) = await TestSupport.RunGit(barePath, "rev-parse", "refs/heads/main");
        Assert.NotEqual(staleMergeSha, landed.Trim());
        var (_, firstParent, _) = await TestSupport.RunGit(barePath, "rev-parse", "main^1");
        Assert.Equal(movedBaseTip, firstParent.Trim());
    }

    /// <summary>
    /// With the gate disabled the merge lands without invoking the verifier
    /// at all — the pre-gate behaviour is preserved verbatim.
    /// </summary>
    [Fact]
    public async Task VerificationDisabled_LandsWithoutVerifying()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace, "seed-merge-verify-off");
        var verifier = new DelegateBuildVerifier
        {
            ProbeResult = RequiredBuildProbeResult.NotApplicable,
            OnVerifyAsync = (_, _) => Task.FromResult(RequiredBuildVerificationResult.Skipped),
        };
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            MergeResultBuildVerificationEnabled = false,
        });
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed, requiredBuildVerifier: verifier, pipelineTuning: tuning);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("feature.txt", "feature\n"));

        var item = NewItem("codeybox/merge-verify-off-" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.DoesNotContain(
            verifier.VerificationRequests,
            r => IsVerifyBranch(r.WorkBranch));

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, feature, _) = await TestSupport.RunGit(barePath, "show", "main:feature.txt");
        Assert.Equal("feature\n", feature);
    }

    /// <summary>
    /// A merge-result verification that times out is infrastructure, not a
    /// semantic verdict: it is retried a bounded number of times without
    /// consuming the guard-rework budget (which exists for real semantic
    /// failures), and only parks once the infra budget is spent — with a
    /// message naming both timeout settings and a per-attempt duration in
    /// the structured log so a too-tight budget is visible. The timeout
    /// budget runs on the pipeline clock, so the fake clock drives it
    /// deterministically.
    /// </summary>
    [Fact]
    public async Task VerificationTimeout_RetriesBoundedInfraAttempts_ThenParksWithoutConsumingGuardBudget()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace, "seed-merge-verify-timeout");
        var verifier = new DelegateBuildVerifier();
        var clock = new MergeClock(DateTimeOffset.UtcNow);
        const int infraMaxAttempts = 2;
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            MergeResultBuildVerificationTimeout = TimeSpan.FromMilliseconds(200),
            MergeResultBuildVerificationProvisioningAllowance = TimeSpan.Zero,
            MergeResultBuildVerificationInfraMaxAttempts = infraMaxAttempts,
        });
        var pipelineOptions = new PipelineOptions
        {
            SandboxImageReference = "ignored",
            AgentAllowedHosts = [],
            // A generous inner budget so the floor (required + allowance)
            // cannot clamp the 200 ms merge budget under test.
            RequiredBuildVerificationTimeout = TimeSpan.FromMilliseconds(100),
        };
        var logs = new ListLogger<PipelineRunner>();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            requiredBuildVerifier: verifier,
            pipelineTuning: tuning,
            pipelineOptions: pipelineOptions,
            pipelineTimeProvider: clock,
            logger: logs);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("feature.txt", "feature\n"));

        var item = NewItem("codeybox/merge-verify-timeout-" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);
        verifier.OnVerifyAsync = async (request, ct) =>
        {
            if (!IsVerifyBranch(request.WorkBranch))
                return RequiredBuildVerificationResult.Passed(0, "work-branch head builds");
            // Hang until the merge-result budget cancels us: the attempt must
            // end in a timeout, never in a build verdict.
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return RequiredBuildVerificationResult.Passed(0, "unreachable");
        };

        await RunWithAdvancingClockAsync(clock, tp.Pipeline.RunAsync(item, CancellationToken.None));

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, final.FailureKind);
        // Bounded infra retries: exactly one verification per attempt.
        var mergeVerifies = verifier.VerificationRequests.Where(r => IsVerifyBranch(r.WorkBranch)).ToList();
        Assert.Equal(infraMaxAttempts, mergeVerifies.Count);
        // The guard-rework budget is untouched: a timeout is infrastructure,
        // so no conflict-rework turn ever ran.
        Assert.Equal(0, final.ConflictReworkAttempts);
        Assert.Empty(tp.Agent.ConflictReworkPrompts);
        // The park message names both settings so the operator can act.
        Assert.Contains("RequiredBuildVerificationTimeout", final.LastError, StringComparison.Ordinal);
        Assert.Contains("MergeResultBuildVerificationTimeout", final.LastError, StringComparison.Ordinal);
        // Every attempt reported its duration in the structured log.
        var attemptLogs = logs.Lines
            .Where(line => line.Message.Contains("Merge-result build verification attempt", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(infraMaxAttempts, attemptLogs.Count);
        Assert.All(attemptLogs, line => Assert.Contains("timed out after", line.Message, StringComparison.Ordinal));
    }

    private static async Task RunWithAdvancingClockAsync(MergeClock clock, Task pipelineTask)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(pipelineTask);
        // Advance the fake clock in lockstep with real time (10 ms fake per
        // ~10 ms real delay) — never faster, so no other pipeline budget
        // fires spuriously early while the merge-result budget under test
        // elapses deterministically.
        while (!pipelineTask.IsCompleted)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            await Task.Delay(10);
        }

        await pipelineTask;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class DelegateBuildVerifier : IRequiredBuildVerifier
    {
        public RequiredBuildProbeResult ProbeResult { get; set; } = RequiredBuildProbeResult.Applies;
        public Func<RequiredBuildVerificationRequest, CancellationToken, Task<RequiredBuildVerificationResult>>? OnVerifyAsync;
        public int ProbeCalls { get; private set; }
        public int VerifyCalls { get; private set; }
        public List<RequiredBuildVerificationRequest> VerificationRequests { get; } = [];

        public Task<RequiredBuildProbeResult> ProbeAsync(RequiredBuildProbeRequest request, CancellationToken ct)
        {
            _ = request;
            _ = ct;
            ProbeCalls++;
            return Task.FromResult(ProbeResult);
        }

        public Task<RequiredBuildVerificationResult> VerifyAsync(
            RequiredBuildVerificationRequest request, CancellationToken ct)
        {
            VerificationRequests.Add(request);
            VerifyCalls++;
            return OnVerifyAsync is null
                ? Task.FromResult(RequiredBuildVerificationResult.Passed(0, "ok"))
                : OnVerifyAsync(request, ct);
        }
    }

    private sealed class SiblingAdvancingAuditor(string workspace) : IAuditor
    {
        public string? GitRoot { get; set; }
        public string Name => "advance-sibling";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public async Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
            if (GitRoot is null)
                throw new InvalidOperationException("GitRoot must be assigned before the auditor runs.");

            var barePath = Path.Combine(GitRoot, context.WorkItemId + ".git");
            var clone = Path.Combine(workspace, "advance-sibling-" + Guid.NewGuid().ToString("N")[..8]);
            await TestSupport.RunGit(workspace, "clone", barePath, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", context.BaseBranch);
            await File.WriteAllTextAsync(Path.Combine(clone, "sibling.txt"), "sibling\n");
            await TestSupport.RunGit(clone, "add", "sibling.txt");
            await TestSupport.RunGit(clone, "commit", "-m", "land sibling work during audit");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{context.BaseBranch}");
            return new AuditResult(true, []);
        }
    }
}
