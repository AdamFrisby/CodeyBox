using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Cross-host observability parity: a phase run on a poll-driven remote
/// executor (real <see cref="ExecutorPhaseBroker"/>, real
/// <see cref="PollingExecutorPhaseTransport"/>, real
/// <see cref="BrokerExecutorPhaseChannel"/>, real
/// <see cref="ExecutorPhasePollWorker"/> and real
/// <see cref="ExecutorHostPhaseRunner"/> — only the network hop itself is
/// in-process) leaves the same observable artefacts as the equivalent
/// colocated run: outcome/sha/findings/usage, stream capture files (same
/// naming, location semantics and content), live hub events, cost/usage
/// accounting rows, and failure classification with evidence. This suite is
/// the enforcement of the proxy's "same observable artefact" claim: any
/// divergence fails here.
/// </summary>
public sealed class ExecutorRemoteParityTests : IDisposable
{
    private const string FixedDate = "2000-01-01T00:00:00+00:00";

    private readonly string _root = Directory.CreateTempSubdirectory("codeybox-exec-parity-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── 1. colocated ≡ remote: outcome, sha, findings, usage ────────────────

    [Fact]
    public async Task ColocatedAndRemote_AgreeOnOutcomeCommitFindingsAndUsage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var remote = CreateContext(remoteOnly: true);
        using var local = CreateContext(remoteOnly: false);
        var remoteItem = WorkItemId.New();
        var localItem = WorkItemId.New();
        await SeedBareRepoAsync(remote.Git, remoteItem, cts.Token);
        await SeedBareRepoAsync(local.Git, localItem, cts.Token);
        remote.Handler.Script = _ => [(0L, "alpha\n"), (1L, "beta\n")];
        local.Handler.Script = _ => [(0L, "alpha\n"), (1L, "beta\n")];

        using var worker = RunPollWorker(remote, cts.Token);
        var remoteResult = await remote.Proxy.ExecutePhaseAsync(NewRequest(remoteItem, "work", 0), cts.Token);
        var localResult = await local.Proxy.ExecutePhaseAsync(NewRequest(localItem, "work", 0), cts.Token);

        Assert.Equal(ExecutorPhaseOutcome.Succeeded, remoteResult.Outcome);
        AssertResultsEqual(remoteResult, localResult);
    }

    // ── 2. colocated ≡ remote: capture files + live hub events ──────────────

    [Fact]
    public async Task ColocatedAndRemote_ProduceIdenticalStreamArtefacts()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var remote = CreateContext(remoteOnly: true);
        using var local = CreateContext(remoteOnly: false);
        var remoteItem = WorkItemId.New();
        var localItem = WorkItemId.New();
        await SeedBareRepoAsync(remote.Git, remoteItem, cts.Token);
        await SeedBareRepoAsync(local.Git, localItem, cts.Token);
        remote.Handler.Script = _ => [(0L, "alpha\n"), (1L, "beta\n")];
        local.Handler.Script = _ => [(0L, "alpha\n"), (1L, "beta\n")];

        using var worker = RunPollWorker(remote, cts.Token);
        await remote.Proxy.ExecutePhaseAsync(NewRequest(remoteItem, "work", 0), cts.Token);
        await local.Proxy.ExecutePhaseAsync(NewRequest(localItem, "work", 0), cts.Token);

        var remoteFile = Assert.Single(await remote.Streams.ListAsync(remoteItem, ct: cts.Token));
        var localFile = Assert.Single(await local.Streams.ListAsync(localItem, ct: cts.Token));

        // Same naming: phase/iteration prefix plus the store's random suffix.
        Assert.Equal("work", remoteFile.Phase);
        Assert.Equal(1, remoteFile.Iteration);
        Assert.Equal(localFile.Phase, remoteFile.Phase);
        Assert.Equal(localFile.Iteration, remoteFile.Iteration);
        Assert.Matches(@"^work-1-[0-9a-f]{6}\.jsonl$", remoteFile.FileName);
        Assert.Matches(@"^work-1-[0-9a-f]{6}\.jsonl$", localFile.FileName);

        // Same location semantics: one directory per work item under the
        // configured stream root, on the orchestrator — never on the
        // executing host.
        Assert.Equal(
            Path.Combine(remote.StreamsRoot, remoteItem.ToString()),
            Path.GetDirectoryName(Path.Combine(remote.StreamsRoot, remoteItem.ToString(), remoteFile.FileName)));
        Assert.Equal(
            Path.Combine(local.StreamsRoot, localItem.ToString()),
            Path.GetDirectoryName(Path.Combine(local.StreamsRoot, localItem.ToString(), localFile.FileName)));

        // Same content.
        var remoteLines = await File.ReadAllLinesAsync(
            Path.Combine(remote.StreamsRoot, remoteItem.ToString(), remoteFile.FileName), cts.Token);
        var localLines = await File.ReadAllLinesAsync(
            Path.Combine(local.StreamsRoot, localItem.ToString(), localFile.FileName), cts.Token);
        Assert.Equal(["alpha", "beta"], remoteLines);
        Assert.Equal(remoteLines, localLines);

        // Same live contract: subscribers saw the same chunks on the same hub.
        Assert.Equal(local.Broadcaster.Phases, remote.Broadcaster.Phases);
        Assert.Equal(["work", "work"], remote.Broadcaster.Phases);
        Assert.Equal(remote.Broadcaster.Text, local.Broadcaster.Text);
        Assert.Contains("alpha\n", remote.Broadcaster.Text);
        Assert.Contains("beta\n", remote.Broadcaster.Text);
    }

    // ── 3. colocated ≡ remote: cost/usage accounting ────────────────────────

    [Fact]
    public async Task ColocatedAndRemote_RecordIdenticalUsageRows()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var remote = CreateContext(remoteOnly: true);
        using var local = CreateContext(remoteOnly: false);
        var remoteItem = WorkItemId.New();
        var localItem = WorkItemId.New();
        await SeedBareRepoAsync(remote.Git, remoteItem, cts.Token);
        await SeedBareRepoAsync(local.Git, localItem, cts.Token);

        using var worker = RunPollWorker(remote, cts.Token);
        await remote.Proxy.ExecutePhaseAsync(NewRequest(remoteItem, "work", 0), cts.Token);
        await local.Proxy.ExecutePhaseAsync(NewRequest(localItem, "work", 0), cts.Token);

        var remoteCost = Assert.Single(remote.Costs.Rows);
        var localCost = Assert.Single(local.Costs.Rows);
        Assert.Equal(remoteItem.ToString(), remoteCost.WorkItemId);
        Assert.Equal("work", remoteCost.Phase);
        Assert.Equal(1, remoteCost.Iteration);
        Assert.True(remoteCost.HasExtractedTokenUsage);
        AssertUsageRowsEqual(remoteCost, localCost, "exec-1", "local");

        var remoteUsage = Assert.Single(remote.Usages.Events);
        var localUsage = Assert.Single(local.Usages.Events);
        Assert.Equal(remoteItem.ToString(), remoteUsage.WorkItemId);
        Assert.Equal(remoteUsage.InputTokens, localUsage.InputTokens);
        Assert.Equal(remoteUsage.OutputTokens, localUsage.OutputTokens);
        Assert.Equal(remoteUsage.CostMicroCents, localUsage.CostMicroCents);
        Assert.Equal(remoteUsage.AgentKind, localUsage.AgentKind);
        Assert.Equal("exec-1", remoteUsage.AgentInstanceId);
        Assert.Equal("local", localUsage.AgentInstanceId);
        Assert.Equal(remoteUsage.Phase, localUsage.Phase);

        // Timing attributes: a non-negative wall-clock window on both paths.
        Assert.True(remoteCost.StartedAt <= remoteCost.EndedAt);
        Assert.True(localCost.StartedAt <= localCost.EndedAt);
        Assert.True(remoteUsage.ElapsedMs >= 0);
        Assert.True(localUsage.ElapsedMs >= 0);
    }

    // ── 4. live streaming from the remote executor ──────────────────────────

    [Fact]
    public async Task RemoteStdout_StreamsIncrementally_NotAsATerminalFlush()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var remote = CreateContext(remoteOnly: true);
        var item = WorkItemId.New();
        await SeedBareRepoAsync(remote.Git, item, cts.Token);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.Handler.Script = _ => [(0L, "live-one\n")];
        remote.Handler.BeforeResult = async token =>
        {
            await gate.Task.WaitAsync(token).ConfigureAwait(false);
            await Task.Delay(50, token).ConfigureAwait(false);
        };

        using var worker = RunPollWorker(remote, cts.Token);
        var dispatch = remote.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), cts.Token);
        await remote.Broadcaster.FirstChunk.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);

        // The first chunk arrived while the remote phase was still running:
        // the handler is parked behind the test gate, so completion is
        // impossible yet — output streamed during execution, not as a
        // terminal flush after it.
        Assert.False(dispatch.IsCompleted);
        gate.TrySetResult();
        var result = await dispatch.WaitAsync(TimeSpan.FromSeconds(60), cts.Token);

        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
        var file = Assert.Single(await remote.Streams.ListAsync(item, ct: cts.Token));
        Assert.Contains("live-one", await File.ReadAllTextAsync(
            Path.Combine(remote.StreamsRoot, item.ToString(), file.FileName), cts.Token));
    }

    // ── 5. remote failure ≡ local failure ───────────────────────────────────

    [Fact]
    public async Task RemoteAgentFailure_MatchesLocalClassificationAndEvidence()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var remote = CreateContext(remoteOnly: true);
        using var local = CreateContext(remoteOnly: false);
        var remoteItem = WorkItemId.New();
        var localItem = WorkItemId.New();
        await SeedBareRepoAsync(remote.Git, remoteItem, cts.Token);
        await SeedBareRepoAsync(local.Git, localItem, cts.Token);
        remote.Handler.ForceAgentFailure = true;
        local.Handler.ForceAgentFailure = true;

        using var worker = RunPollWorker(remote, cts.Token);
        var remoteResult = await remote.Proxy.ExecutePhaseAsync(NewRequest(remoteItem, "work", 0), cts.Token);
        var localResult = await local.Proxy.ExecutePhaseAsync(NewRequest(localItem, "work", 0), cts.Token);

        // Same operator-facing classification (returned, not thrown) and the
        // same evidence — and both cached, so redelivery never reruns.
        Assert.Equal(ExecutorPhaseOutcome.AgentFailed, remoteResult.Outcome);
        AssertResultsEqual(remoteResult, localResult);
        var remoteCalls = remote.Handler.Calls;
        var replayed = await remote.Proxy.ExecutePhaseAsync(NewRequest(remoteItem, "work", 0), cts.Token);
        AssertResultsEqual(remoteResult, replayed);
        Assert.Equal(remoteCalls, remote.Handler.Calls);
    }

    [Fact]
    public async Task RemoteTransportFailure_MatchesLocalClassificationAndEvidence()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var remote = CreateContext(remoteOnly: true);
        using var local = CreateContext(remoteOnly: false);
        var remoteItem = WorkItemId.New();
        var localItem = WorkItemId.New();
        await SeedBareRepoAsync(remote.Git, remoteItem, cts.Token);
        await SeedBareRepoAsync(local.Git, localItem, cts.Token);
        remote.Handler.Throw = new InvalidOperationException("simulated sandbox loss");
        local.Handler.Throw = new InvalidOperationException("simulated sandbox loss");
        var remoteRequest = NewRequest(remoteItem, "work", 0);
        var localRequest = NewRequest(localItem, "work", 0);
        var remoteBare = remote.Git.GetRepoPath(remoteItem.ToString());
        var localBare = local.Git.GetRepoPath(localItem.ToString());
        var remoteBefore = (await RunGitBareCapture(remoteBare, "rev-parse", "phase/seed", cts.Token)).Trim();
        var localBefore = (await RunGitBareCapture(localBare, "rev-parse", "phase/seed", cts.Token)).Trim();

        using var worker = RunPollWorker(remote, cts.Token);
        var remoteThrown = await Assert.ThrowsAsync<ExecutorPhaseTransportException>(
            () => remote.Proxy.ExecutePhaseAsync(remoteRequest, cts.Token));
        var localThrown = await Assert.ThrowsAsync<ExecutorPhaseTransportException>(
            () => local.Proxy.ExecutePhaseAsync(localRequest, cts.Token));

        // Same classification (host-attributed transport failure on
        // run-phase) and the same evidence modulo host attribution: the
        // message is identical with the executing host's name swapped.
        Assert.Equal(localThrown.Operation, remoteThrown.Operation);
        Assert.Equal("run-phase", remoteThrown.Operation);
        Assert.Equal(
            localThrown.Message.Replace("'local'", "'exec-1'", StringComparison.Ordinal),
            remoteThrown.Message);
        Assert.Equal("exec-1", remoteThrown.HostId);
        Assert.Equal("local", localThrown.HostId);

        // Neither path wrote to its repo nor cached anything.
        Assert.Equal(remoteBefore, (await RunGitBareCapture(remoteBare, "rev-parse", "phase/seed", cts.Token)).Trim());
        Assert.Equal(localBefore, (await RunGitBareCapture(localBare, "rev-parse", "phase/seed", cts.Token)).Trim());
        Assert.Equal(IdempotencyLookupOutcome.Miss, (await remote.Store.LookupAsync(
            ExecutorPhaseProxy.BuildDispatchKey(remoteRequest),
            ExecutorPhaseProxy.ComputeBodyHash(remoteRequest),
            DateTimeOffset.UtcNow, cts.Token)).Outcome);
        Assert.Equal(IdempotencyLookupOutcome.Miss, (await local.Store.LookupAsync(
            ExecutorPhaseProxy.BuildDispatchKey(localRequest),
            ExecutorPhaseProxy.ComputeBodyHash(localRequest),
            DateTimeOffset.UtcNow, cts.Token)).Outcome);
    }

    // ── 6. usage recorder bounds ────────────────────────────────────────────

    [Fact]
    public async Task UsageRecorder_NullUsage_WritesNoRows()
    {
        using var ctx = CreateContext(remoteOnly: false);
        var recorder = new CostUsageRecorder(ctx.Costs, ctx.Usages, null, null, NullLogger.Instance);
        var request = NewRequest(WorkItemId.New(), "work", 0);
        var result = new ExecutorPhaseResult { Outcome = ExecutorPhaseOutcome.Succeeded, Usage = null!, Findings = [] };

        await recorder.TryRecordExecutorUsageAsync(request, result, "local", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.Empty(ctx.Costs.Rows);
        Assert.Empty(ctx.Usages.Events);
    }

    [Fact]
    public async Task UsageRecorder_HostileUsage_IsClampedNeverThrown()
    {
        using var ctx = CreateContext(remoteOnly: false);
        var recorder = new CostUsageRecorder(ctx.Costs, ctx.Usages, null, null, NullLogger.Instance);
        var request = NewRequest(WorkItemId.New(), "work", 0);
        var result = new ExecutorPhaseResult
        {
            Outcome = ExecutorPhaseOutcome.Succeeded,
            Usage = new ExecutorPhaseUsage(-5, -7, -1.5m),
            Findings = [],
        };

        await recorder.TryRecordExecutorUsageAsync(request, result, "exec-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var cost = Assert.Single(ctx.Costs.Rows);
        Assert.Equal(0, cost.InputTokens);
        Assert.Equal(0, cost.OutputTokens);
        Assert.Equal(0, cost.EstimatedUsd);
        var usage = Assert.Single(ctx.Usages.Events);
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.CostMicroCents);
    }

    [Fact]
    public async Task UsageRecorder_StoreFailure_NeverFailsThePhase()
    {
        using var ctx = CreateContext(remoteOnly: false);
        var recorder = new CostUsageRecorder(new ThrowingCostStore(), new ThrowingUsageStore(), null, null, NullLogger.Instance);
        var request = NewRequest(WorkItemId.New(), "work", 0);
        var result = new ExecutorPhaseResult
        {
            Outcome = ExecutorPhaseOutcome.Succeeded,
            Usage = new ExecutorPhaseUsage(10, 5, 0.001m),
            Findings = [],
        };

        await recorder.TryRecordExecutorUsageAsync(request, result, "local", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void DispatchOptions_Validate_RejectsBadRemoteKnobs()
    {
        Assert.Throws<InvalidOperationException>(() => new ExecutorPhaseDispatchOptions { RemotePhasePollTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ExecutorPhaseDispatchOptions { RemotePhaseLeaseTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ExecutorPhaseDispatchOptions { MaxRemoteStreamChunkChars = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ExecutorPhaseDispatchOptions { MaxPendingRemoteDispatchesPerHost = 0 }.Validate());
        new ExecutorPhaseDispatchOptions().Validate();
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private static ExecutorPhaseRequest NewRequest(WorkItemId item, string phase, int attempt) =>
        new() { WorkItemId = item.ToString(), Phase = phase, Attempt = attempt, RepositoryId = item.ToString(), PayloadJson = "{}" };

    private static void AssertResultsEqual(ExecutorPhaseResult expected, ExecutorPhaseResult actual)
    {
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.CommitSha, actual.CommitSha);
        Assert.Equal(expected.Findings, actual.Findings);
        Assert.Equal(expected.Usage, actual.Usage);
        Assert.Equal(expected.ErrorMessage, actual.ErrorMessage);
    }

    private static void AssertUsageRowsEqual(WorkItemCost expected, WorkItemCost actual, string expectedHost, string actualHost)
    {
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.Iteration, actual.Iteration);
        Assert.Equal(expected.AgentKind, actual.AgentKind);
        // Cost attribution names the host that actually ran the phase —
        // identical accounting, per-host attribution.
        Assert.Equal(expectedHost, expected.AgentInstanceId);
        Assert.Equal(actualHost, actual.AgentInstanceId);
        Assert.Equal(expected.InputTokens, actual.InputTokens);
        Assert.Equal(expected.CachedInputTokens, actual.CachedInputTokens);
        Assert.Equal(expected.OutputTokens, actual.OutputTokens);
        Assert.Equal(expected.EstimatedUsd, actual.EstimatedUsd);
        Assert.Equal(expected.ModelId, actual.ModelId);
        Assert.Equal(expected.HasExtractedTokenUsage, actual.HasExtractedTokenUsage);
    }

    private ParityContext CreateContext(bool remoteOnly)
    {
        var gitRoot = Path.Combine(_root, "git-" + Guid.NewGuid().ToString("N"));
        var git = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var dbPath = Path.Combine(_root, "idem-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteIdempotencyStore(dbPath);
        var registry = new ParityWorkerRegistry();
        var options = new ExecutorPhaseDispatchOptions { RemotePhasePollTimeout = TimeSpan.FromSeconds(2) };
        var handler = new StreamingCommitHandler();

        var colocatedOptions = new ColocatedExecutorOptions
        {
            StagingRoot = Path.Combine(_root, "local-stage-" + Guid.NewGuid().ToString("N")),
            MaxConcurrentSandboxes = remoteOnly ? 0 : null,
        };
        var local = new ColocatedExecutorHost(
            new ParityNoopSandboxProvider(),
            () => colocatedOptions,
            () => options,
            handler);

        var broker = new ExecutorPhaseBroker(() => options);
        var remoteStaging = Path.Combine(_root, "remote-stage-" + Guid.NewGuid().ToString("N"));
        var remoteOptions = new ExecutorOptions
        {
            HostId = "exec-1",
            OrchestratorBaseUrl = "http://127.0.0.1:9/",
            PhaseStagingRoot = remoteStaging,
        };
        var runner = new ExecutorHostPhaseRunner(
            new ParityNoopSandboxProvider(),
            new ExecutorSandboxTracker(),
            handler,
            () => remoteOptions,
            () => options);
        var channel = new BrokerExecutorPhaseChannel(broker, "exec-1", () => options);
        // The poll worker and its runner share one staging root — as the
        // executor host process wires them — so the leaf the worker extracts
        // is the leaf the runner resolves.
        var worker = new ExecutorPhasePollWorker(
            channel, "exec-1", runner, remoteStaging, () => options);
        var remoteFactory = new ParityPollingTransportFactory(broker, () => options);

        var streamsRoot = Path.Combine(_root, "streams-" + Guid.NewGuid().ToString("N"));
        var streams = new AgentStreamStore(
            new AgentStreamsOptions { Enabled = true, Path = streamsRoot, MaxFileSizeMb = 32 },
            NullLogger<AgentStreamStore>.Instance);
        var broadcaster = new ParityRecordingBroadcaster();
        var costs = new CapturingCostStore();
        var usages = new CapturingUsageStore();
        var recorder = new CostUsageRecorder(costs, usages, null, null, NullLogger.Instance);
        if (remoteOnly)
            registry.AddExecutor("exec-1");
        var proxy = new ExecutorPhaseProxy(
            registry,
            new ColocatedExecutorTransportFactory(local, remoteFactory),
            git, store, local, () => options,
            streamStore: streams, broadcaster: broadcaster, usageRecorder: recorder);
        return new ParityContext(git, store, registry, handler, options, broker, worker, streams, streamsRoot, broadcaster, costs, usages, proxy);
    }

    private static PollWorkerLease RunPollWorker(ParityContext ctx, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = ctx.PollWorker.RunAsync(cts.Token);
        return new PollWorkerLease(cts, task);
    }

    private sealed class PollWorkerLease(CancellationTokenSource cts, Task task) : IDisposable
    {
        public void Dispose()
        {
            try { cts.Cancel(); } catch { }
            try { task.Wait(TimeSpan.FromSeconds(15)); } catch { }
            cts.Dispose();
        }
    }

    private async Task SeedBareRepoAsync(LocalGitHost git, WorkItemId item, CancellationToken ct)
    {
        var repoId = await git.EnsureRepositoryAsync(item, seedFromUrl: null, ct);
        var bare = git.GetRepoPath(repoId);
        var clone = Path.Combine(_root, "seedclone-" + Guid.NewGuid().ToString("N"));
        await RunGit(_root, ct, "clone", bare, clone);
        await RunGit(clone, ct, "config", "user.email", "t@t");
        await RunGit(clone, ct, "config", "user.name", "T");
        await File.WriteAllTextAsync(Path.Combine(clone, "README.md"), "seed\n", ct);
        await RunGit(clone, ct, "add", "README.md");
        await RunGit(clone, ct, "commit", "-m", "seed");
        await RunGit(clone, ct, "branch", "-M", "phase/seed");
        await RunGit(clone, ct, "push", "origin", "phase/seed");
    }

    private async Task<string> RunGitBareCapture(string bareRepo, string arg1, string arg2, CancellationToken ct)
    {
        using var p = Process.Start(GitPsi(_root, ["--git-dir", bareRepo, arg1, arg2]))!;
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {arg1} {arg2} failed");
        return stdout;
    }

    private static async Task RunGit(string cwd, CancellationToken ct, params string[] args)
    {
        using var p = Process.Start(GitPsi(cwd, args))!;
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }

    private static ProcessStartInfo GitPsi(string cwd, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_AUTHOR_NAME"] = "CodeyBox Test";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "test@codeybox.invalid";
        psi.Environment["GIT_COMMITTER_NAME"] = "CodeyBox Test";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "test@codeybox.invalid";
        psi.Environment["GIT_AUTHOR_DATE"] = FixedDate;
        psi.Environment["GIT_COMMITTER_DATE"] = FixedDate;
        return psi;
    }

    private sealed record ParityContext(
        LocalGitHost Git,
        SqliteIdempotencyStore Store,
        ParityWorkerRegistry Registry,
        StreamingCommitHandler Handler,
        ExecutorPhaseDispatchOptions Options,
        ExecutorPhaseBroker Broker,
        ExecutorPhasePollWorker PollWorker,
        AgentStreamStore Streams,
        string StreamsRoot,
        ParityRecordingBroadcaster Broadcaster,
        CapturingCostStore Costs,
        CapturingUsageStore Usages,
        ExecutorPhaseProxy Proxy) : IDisposable
    {
        public void Dispose()
        {
            Broker.Dispose();
            Store.Dispose();
        }
    }

    private sealed class ParityPollingTransportFactory(
        ExecutorPhaseBroker broker,
        Func<ExecutorPhaseDispatchOptions> options) : IExecutorPhaseTransportFactory
    {
        public Task<IExecutorPhaseTransport?> ResolveAsync(string hostId, CancellationToken ct) =>
            Task.FromResult<IExecutorPhaseTransport?>(
                string.Equals(hostId?.Trim(), "exec-1", StringComparison.Ordinal)
                    ? new PollingExecutorPhaseTransport("exec-1", broker, options)
                    : null);
    }

    private sealed class ParityWorkerRegistry : IWorkerRegistry
    {
        private readonly Dictionary<string, WorkerRegistration> _rows = new(StringComparer.Ordinal);

        public void AddExecutor(string hostId, int? capacity = 4)
        {
            var now = DateTimeOffset.UtcNow;
            _rows[ExecutorRegistration.WorkerIdFor(hostId)] = new WorkerRegistration
            {
                WorkerId = ExecutorRegistration.WorkerIdFor(hostId),
                HostName = hostId,
                ProcessId = 4242,
                StartedAt = now,
                LastHeartbeatAt = now,
                ExecutorHostId = hostId,
                MaxConcurrentSandboxes = capacity,
                ExecutorNetworkProfiles = [],
                ExecutorCredentials = [],
                ExecutorCapabilities = [],
                Cordoned = false,
                Healthy = true,
            };
        }

        public Task RegisterAsync(WorkerRegistration registration, CancellationToken ct = default)
        {
            _rows[registration.WorkerId] = registration;
            return Task.CompletedTask;
        }

        public Task HeartbeatAsync(string workerId, string? currentWorkItemId, CancellationToken ct = default, int? executorActivePhases = null)
        {
            if (_rows.TryGetValue(workerId, out var row))
                _rows[workerId] = row with { LastHeartbeatAt = DateTimeOffset.UtcNow, CurrentWorkItemId = currentWorkItemId };
            return Task.CompletedTask;
        }

        public Task DeregisterAsync(string workerId, CancellationToken ct = default)
        {
            _rows.Remove(workerId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkerRegistration>> ListAsync(CancellationToken ct = default)
        {
            IReadOnlyList<WorkerRegistration> snapshot = [.. _rows.Values];
            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<WorkerRegistration>> ClaimDeadWorkersAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkerRegistration>>([]);

        public Task<WorkerRegistration?> TryClaimDeadWorkerAsync(string workerId, DateTimeOffset cutoff, CancellationToken ct = default) =>
            Task.FromResult<WorkerRegistration?>(null);

        public Task<WorkerRegistration?> TryClaimWorkerAsync(string workerId, CancellationToken ct = default) =>
            Task.FromResult<WorkerRegistration?>(null);
    }

    private sealed class ParityRecordingBroadcaster : IStdoutBroadcaster
    {
        private readonly object _lock = new();
        private readonly List<(string Phase, string Chunk)> _chunks = [];

        public TaskCompletionSource FirstChunk { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Phases
        {
            get { lock (_lock) { return [.. _chunks.Select(c => c.Phase)]; } }
        }

        public string Text
        {
            get { lock (_lock) { return string.Concat(_chunks.Select(c => c.Chunk)); } }
        }

        public void BroadcastChunk(WorkItemId workItemId, string phase, string chunk)
        {
            lock (_lock) { _chunks.Add((phase, chunk)); }
            FirstChunk.TrySetResult();
        }

        public Task CompleteAsync(WorkItemId workItemId) => Task.CompletedTask;

        public string? GetTail(WorkItemId workItemId)
        {
            lock (_lock)
                return _chunks.Count == 0 ? null : string.Concat(_chunks.Select(c => c.Chunk));
        }
    }

    private sealed class CapturingCostStore : IWorkItemCostStore
    {
        public List<WorkItemCost> Rows { get; } = [];

        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default)
        {
            lock (Rows) Rows.Add(cost);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
        {
            lock (Rows) return Task.FromResult<IReadOnlyList<WorkItemCost>>([.. Rows.Where(r => r.WorkItemId == workItemId)]);
        }

        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<decimal> SumEstimatedUsdAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingUsageStore : IAgentUsageStore
    {
        public List<AgentUsageEvent> Events { get; } = [];

        public Task RecordAsync(AgentUsageEvent usage, CancellationToken ct = default)
        {
            lock (Events) Events.Add(usage);
            return Task.CompletedTask;
        }

        public Task<AgentUsageWindowAggregate> SumWindowAsync(string agentKind, string? modelId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingCostStore : IWorkItemCostStore
    {
        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated cost store failure.");

        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<decimal> SumEstimatedUsdAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingUsageStore : IAgentUsageStore
    {
        public Task RecordAsync(AgentUsageEvent usage, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated usage store failure.");

        public Task<AgentUsageWindowAggregate> SumWindowAsync(string agentKind, string? modelId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ParityNoopSandbox : ISandbox
    {
        public string Id => "parity-noop-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, "", ""));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ParityNoopSandboxProvider : ISandboxProvider
    {
        public string Name => "parity-noop";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            Task.FromResult<ISandbox>(new ParityNoopSandbox());

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Deterministic streaming phase handler: emits a scripted chunk stream
    /// (with sequence numbers, through the streaming callback when present),
    /// then commits phase content derived only from the phase and attempt —
    /// so colocated and remote runs of identical seeds produce identical
    /// commits — and returns a fixed usage report.
    /// </summary>
    private sealed class StreamingCommitHandler : IStreamingExecutorPhaseHandler
    {
        public Func<ExecutorPhaseRequest, List<(long Sequence, string Data)>>? Script;
        public Func<CancellationToken, Task>? BeforeResult;
        public bool ForceAgentFailure;
        public Exception? Throw;
        public int Calls;

        public Task<ExecutorPhaseResult> ExecuteAsync(
            ExecutorPhaseRequest request,
            string repoPath,
            ISandbox sandbox,
            CancellationToken ct) =>
            ExecuteAsync(request, repoPath, sandbox, onChunk: null, ct);

        public async Task<ExecutorPhaseResult> ExecuteAsync(
            ExecutorPhaseRequest request,
            string repoPath,
            ISandbox sandbox,
            Func<ExecutorStreamChunk, CancellationToken, Task>? onChunk,
            CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Throw is not null)
                throw Throw;
            if (Script is not null && onChunk is not null)
            {
                foreach (var (sequence, data) in Script(request))
                {
                    ct.ThrowIfCancellationRequested();
                    await onChunk(new ExecutorStreamChunk { Sequence = sequence, Data = data }, ct).ConfigureAwait(false);
                }
            }
            if (BeforeResult is not null)
                await BeforeResult(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (ForceAgentFailure)
            {
                return new ExecutorPhaseResult
                {
                    Outcome = ExecutorPhaseOutcome.AgentFailed,
                    Usage = new ExecutorPhaseUsage(10, 5, 0.001m),
                    Findings = ["agent could not complete the phase"],
                    ErrorMessage = "simulated agent failure",
                };
            }

            var work = Path.Combine(Path.GetTempPath(), "codeybox-paritywork-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var branch = $"phase/{request.Phase}-{request.Attempt}";
                await RunGit(work, "clone", repoPath, "w");
                var w = Path.Combine(work, "w");
                await RunGit(w, "config", "user.email", "t@t");
                await RunGit(w, "config", "user.name", "T");
                await RunGit(w, "checkout", "-B", branch, "origin/phase/seed");
                var content = $"phase={request.Phase} attempt={request.Attempt}\n";
                await File.WriteAllTextAsync(Path.Combine(w, "phase-output.txt"), content, ct);
                await RunGit(w, "add", "-A");
                await RunGit(w, "commit", "-m", $"phase {request.Phase} attempt {request.Attempt}");
                var sha = (await RunGitCapture(w, "rev-parse", "HEAD")).Trim();
                await RunGit(w, "push", "origin", $"{branch}:{branch}");
                return new ExecutorPhaseResult
                {
                    Outcome = ExecutorPhaseOutcome.Succeeded,
                    CommitSha = sha,
                    Findings = [$"finding for {request.Phase}"],
                    Usage = new ExecutorPhaseUsage(100, 50, 0.002m),
                };
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        private static async Task RunGit(string cwd, params string[] args)
        {
            using var p = Process.Start(GitPsi(cwd, args))!;
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }

        private static async Task<string> RunGitCapture(string cwd, params string[] args)
        {
            using var p = Process.Start(GitPsi(cwd, args))!;
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
            return stdout;
        }
    }
}
