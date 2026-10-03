using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Xunit.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the colocated executor: a local deployment with no
/// executor configuration still executes phases through the executor path;
/// the colocated host is identified as <c>"local"</c>; proxy-through-
/// colocated returns the same states, artefacts and failure classifications
/// as running the executor host runner directly (the before/after comparison
/// for the removed in-process fallback); a failing remote fails over to the
/// colocated host; and option misconfiguration fails fast. Uses a real
/// <see cref="LocalGitHost"/>, a real <see cref="SqliteIdempotencyStore"/>
/// and real git commits; only agent work is faked (a deterministic
/// git-commit handler with fixed identity and timestamps).
/// </summary>
public sealed class ColocatedExecutorTests : IDisposable
{
    private const string FixedDate = "2000-01-01T00:00:00+00:00";

    private readonly string _root = Directory.CreateTempSubdirectory("codeybox-colocated-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── zero-config local execution ─────────────────────────────────────────

    [Fact]
    public async Task NoExecutorConfigured_ExecutesThroughColocatedHost()
    {
        using var ctx = CreateContext();
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);

        var result = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), CancellationToken.None);

        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.CommitSha);
        Assert.Equal(["local"], ctx.ResolvedHosts);
        var bare = ctx.Git.GetRepoPath(item.ToString());
        var log = await RunGitBareCapture(bare, "log", "--format=%H", "phase/work-0");
        Assert.Contains(result.CommitSha!, log.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void ColocatedHost_IsIdentifiedAsLocal_WithSaneDefaults()
    {
        Assert.Equal("local", ColocatedExecutorHost.HostId);
        using var ctx = CreateContext();
        var registration = ctx.Local.GetRegistration();
        Assert.Equal("local", registration.HostId);
        Assert.False(registration.Cordoned);
        Assert.True(registration.Healthy);
        Assert.Null(registration.MaxConcurrentSandboxes);
        Assert.Empty(registration.AllowedNetworkProfiles);
        Assert.Equal(["*"], registration.DeclaredCredentials);
        Assert.True(ctx.Local.HasRunner);
    }

    [Fact]
    public async Task TransportFactory_ResolvesLocalOnly_WhenNoRemoteChained()
    {
        using var ctx = CreateContext();
        var factory = new ColocatedExecutorTransportFactory(ctx.Local);
        Assert.NotNull(await factory.ResolveAsync("local", CancellationToken.None));
        Assert.Null(await factory.ResolveAsync("exec-9", CancellationToken.None));
    }

    // ── before/after: same states, artefacts, failure classifications ───────

    [Fact]
    public async Task BeforeAfter_SuccessEquivalentToDirectRunner()
    {
        using var ctx = CreateContext();
        var item = WorkItemId.New();
        var twin = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);
        await SeedBareRepoAsync(ctx.Git, twin);
        StageDirectRepo(ctx.Git, twin, ctx.DirectStagingRoot);

        var viaProxy = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), CancellationToken.None);
        var direct = await ctx.Runner.ExecutePhaseAsync(NewRequest(twin, "work", 0), CancellationToken.None);

        AssertResultsEqual(direct, viaProxy);
        var viaBare = ctx.Git.GetRepoPath(item.ToString());
        var viaLog = await RunGitBareCapture(viaBare, "log", "--format=%H", "phase/work-0");
        Assert.Contains(viaProxy.CommitSha!, viaLog.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        // The direct runner commits into its staged copy (like any remote
        // host); the proxy stages that copy back over the orchestrator repo.
        var stagedLeaf = ExecutorPhaseExecution.ResolveStagedRepoPath(ctx.DirectStagingRoot, twin.ToString());
        var directLog = await RunGitBareCapture(stagedLeaf, "log", "--format=%H", "phase/work-0");
        Assert.Contains(direct.CommitSha!, directLog.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task BeforeAfter_AgentFailureReturnedNotThrown_ByBothPaths()
    {
        using var ctx = CreateContext();
        ctx.Handler.ForceAgentFailure = true;
        var item = WorkItemId.New();
        var twin = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);
        await SeedBareRepoAsync(ctx.Git, twin);
        StageDirectRepo(ctx.Git, twin, ctx.DirectStagingRoot);

        var viaProxy = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), CancellationToken.None);
        var direct = await ctx.Runner.ExecutePhaseAsync(NewRequest(twin, "work", 0), CancellationToken.None);

        Assert.Equal(ExecutorPhaseOutcome.AgentFailed, viaProxy.Outcome);
        AssertResultsEqual(direct, viaProxy);
    }

    [Fact]
    public async Task BeforeAfter_OversizedPayloadRejected_OnBothPaths()
    {
        using var ctx = CreateContext();
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);
        var request = NewRequest(item, "work", 0) with { PayloadJson = new string('x', 2 * 1024 * 1024) };

        // Request-envelope validation rejects the dispatch before any host
        // is touched on either path: same classification, nothing staged.
        await Assert.ThrowsAsync<ArgumentException>(
            () => ctx.Proxy.ExecutePhaseAsync(request, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => ctx.Runner.ExecutePhaseAsync(request, CancellationToken.None));
    }

    // ── failover and loud failures ──────────────────────────────────────────

    [Fact]
    public async Task RemoteTransportFailure_FailsOverToColocatedHost()
    {
        using var ctx = CreateContext();
        ctx.Registry.AddExecutor("exec-1");
        ctx.Remotes.AddHost("exec-1");
        ctx.Remotes.FailWith("exec-1", new ExecutorPhaseTransportException("exec-1", "stage-in", "connection refused"));
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);

        var result = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), CancellationToken.None);

        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.CommitSha);
    }

    [Fact]
    public async Task RemoteHostIdCollidingWithLocal_FailsDispatchLoudly()
    {
        using var ctx = CreateContext();
        ctx.Registry.AddExecutor("local");
        ctx.Remotes.AddHost("local");
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), CancellationToken.None));
    }

    [Fact]
    public async Task NoHandlerComposed_DispatchFailsLoudly_NothingCached()
    {
        using var ctx = CreateContext(withHandler: false);
        Assert.False(ctx.Local.HasRunner);
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);
        var request = NewRequest(item, "work", 0);

        // The colocated host cannot compose a transport without a handler:
        // resolution fails and the dispatch throws instead of silently
        // running anywhere else.
        await Assert.ThrowsAsync<ExecutorPhaseTransportException>(
            () => ctx.Proxy.ExecutePhaseAsync(request, CancellationToken.None));
        var lookup = await ctx.Store.LookupAsync(
            ExecutorPhaseProxy.BuildDispatchKey(request),
            ExecutorPhaseProxy.ComputeBodyHash(request),
            DateTimeOffset.UtcNow);
        Assert.Equal(IdempotencyLookupOutcome.Miss, lookup.Outcome);
    }

    [Fact]
    public void ColocatedOptions_Validate_RejectsBadBounds()
    {
        Assert.Throws<InvalidOperationException>(() => new ColocatedExecutorOptions { MaxConcurrentSandboxes = -1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ColocatedExecutorOptions { StagingRoot = "relative/path" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ColocatedExecutorOptions { MaxCachedPhaseResults = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ColocatedExecutorOptions { PhaseResultCacheTtl = TimeSpan.Zero }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ColocatedExecutorOptions { DeclaredCredentials = [""] }.Validate());
        new ColocatedExecutorOptions().Validate();
    }

    // ── local overhead measurement ──────────────────────────────────────────

    private readonly ITestOutputHelper _output;

    public ColocatedExecutorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task LocalOverhead_ProxyThroughColocated_VersusDirectRunner()
    {
        using var ctx = CreateContext();
        const int iterations = 3;
        var proxyMs = new List<long>(iterations);
        var directMs = new List<long>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var item = WorkItemId.New();
            var twin = WorkItemId.New();
            await SeedBareRepoAsync(ctx.Git, item);
            await SeedBareRepoAsync(ctx.Git, twin);
            StageDirectRepo(ctx.Git, twin, ctx.DirectStagingRoot);

            var sw = Stopwatch.StartNew();
            var viaProxy = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), CancellationToken.None);
            sw.Stop();
            proxyMs.Add(sw.ElapsedMilliseconds);
            Assert.Equal(ExecutorPhaseOutcome.Succeeded, viaProxy.Outcome);

            sw.Restart();
            var direct = await ctx.Runner.ExecutePhaseAsync(NewRequest(twin, "work", 0), CancellationToken.None);
            sw.Stop();
            directMs.Add(sw.ElapsedMilliseconds);
            Assert.Equal(ExecutorPhaseOutcome.Succeeded, direct.Outcome);
        }
        _output.WriteLine(
            "Local phase overhead: proxy-through-colocated ms=[{0}] median={1}; direct-runner ms=[{2}] median={3}; added median overhead={4}ms",
            string.Join(",", proxyMs), Median(proxyMs),
            string.Join(",", directMs), Median(directMs),
            Median(proxyMs) - Median(directMs));
    }

    private static long Median(List<long> values)
    {
        var ordered = values.OrderBy(v => v).ToList();
        return ordered[ordered.Count / 2];
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private static void AssertResultsEqual(ExecutorPhaseResult expected, ExecutorPhaseResult actual)
    {
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.CommitSha, actual.CommitSha);
        Assert.Equal(expected.Findings, actual.Findings);
        Assert.Equal(expected.Usage, actual.Usage);
        Assert.Equal(expected.ErrorMessage, actual.ErrorMessage);
    }

    private static ExecutorPhaseRequest NewRequest(WorkItemId item, string phase, int attempt) =>
        new() { WorkItemId = item.ToString(), Phase = phase, Attempt = attempt, RepositoryId = item.ToString(), PayloadJson = "{}" };

    private TestHarness CreateContext(bool withHandler = true)
    {
        var gitRoot = Path.Combine(_root, "git-" + Guid.NewGuid().ToString("N"));
        var git = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var dbPath = Path.Combine(_root, "idem-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteIdempotencyStore(dbPath);
        var registry = new FakeWorkerRegistry();
        var handler = new DeterministicCommitHandler();
        var dispatchOptions = new ExecutorPhaseDispatchOptions();
        var colocatedOptions = new ColocatedExecutorOptions
        {
            StagingRoot = Path.Combine(_root, "local-stage-" + Guid.NewGuid().ToString("N")),
        };
        var sandboxes = new NoopSandboxProvider();
        var local = new ColocatedExecutorHost(
            sandboxes,
            () => colocatedOptions,
            () => dispatchOptions,
            withHandler ? handler : null);
        var remotes = new RemoteStubFactory();
        var transports = new RecordingFactory(new ColocatedExecutorTransportFactory(local, remotes));
        var proxy = new ExecutorPhaseProxy(registry, transports, git, store, local, () => dispatchOptions);
        var runnerOptions = new ExecutorOptions
        {
            HostId = "direct-test",
            OrchestratorBaseUrl = "http://localhost/",
            PhaseStagingRoot = Path.Combine(_root, "direct-stage-" + Guid.NewGuid().ToString("N")),
        };
        var runner = new ExecutorHostPhaseRunner(
            sandboxes,
            new ExecutorSandboxTracker(),
            handler,
            () => runnerOptions,
            () => dispatchOptions);
        return new TestHarness(git, store, registry, handler, local, runner, runnerOptions.PhaseStagingRoot, remotes, transports, proxy);
    }

    private sealed class TestHarness(
        LocalGitHost git,
        SqliteIdempotencyStore store,
        FakeWorkerRegistry registry,
        DeterministicCommitHandler handler,
        ColocatedExecutorHost local,
        ExecutorHostPhaseRunner runner,
        string directStagingRoot,
        RemoteStubFactory remotes,
        RecordingFactory transports,
        ExecutorPhaseProxy proxy) : IDisposable
    {
        public LocalGitHost Git { get; } = git;
        public SqliteIdempotencyStore Store { get; } = store;
        public FakeWorkerRegistry Registry { get; } = registry;
        public DeterministicCommitHandler Handler { get; } = handler;
        public ColocatedExecutorHost Local { get; } = local;
        public ExecutorHostPhaseRunner Runner { get; } = runner;
        public string DirectStagingRoot { get; } = directStagingRoot;
        public RemoteStubFactory Remotes { get; } = remotes;
        public IReadOnlyList<string> ResolvedHosts => transports.ResolvedHosts;
        public ExecutorPhaseProxy Proxy { get; } = proxy;

        public void Dispose() => Store.Dispose();
    }

    private sealed class RecordingFactory(IExecutorPhaseTransportFactory inner) : IExecutorPhaseTransportFactory
    {
        private readonly List<string> _resolved = [];

        public IReadOnlyList<string> ResolvedHosts
        {
            get { lock (_resolved) return [.. _resolved]; }
        }

        public async Task<IExecutorPhaseTransport?> ResolveAsync(string hostId, CancellationToken ct)
        {
            lock (_resolved) _resolved.Add(hostId);
            return await inner.ResolveAsync(hostId, ct).ConfigureAwait(false);
        }
    }

    private sealed class RemoteStubFactory : IExecutorPhaseTransportFactory
    {
        private readonly Dictionary<string, ExecutorPhaseTransportException> _failures = new(StringComparer.Ordinal);

        public void AddHost(string hostId) => _failures[hostId] = null!;

        public void FailWith(string hostId, ExecutorPhaseTransportException failure) => _failures[hostId] = failure;

        public Task<IExecutorPhaseTransport?> ResolveAsync(string hostId, CancellationToken ct)
        {
            if (!_failures.ContainsKey(hostId))
                return Task.FromResult<IExecutorPhaseTransport?>(null);
            return Task.FromResult<IExecutorPhaseTransport?>(new FailingTransport(hostId, _failures[hostId]));
        }

        private sealed class FailingTransport(string hostId, ExecutorPhaseTransportException? failure) : IExecutorPhaseTransport
        {
            public string HostId { get; } = hostId;

            public Task StageInAsync(string hostRepoPath, CancellationToken ct) =>
                failure is not null ? Task.FromException(failure) : Task.CompletedTask;

            public Task<ExecutorPhaseResult> RunPhaseAsync(ExecutorPhaseRequest request, CancellationToken ct) =>
                failure is not null
                    ? Task.FromException<ExecutorPhaseResult>(failure)
                    : Task.FromResult(new ExecutorPhaseResult
                    {
                        Outcome = ExecutorPhaseOutcome.Succeeded,
                        Usage = new ExecutorPhaseUsage(1, 1, 0m),
                        Findings = [],
                    });

            public Task StageOutToArchiveAsync(string hostArchivePath, long maxArchiveBytes, CancellationToken ct) =>
                Task.CompletedTask;
        }
    }

    private sealed class FakeWorkerRegistry : IWorkerRegistry
    {
        private readonly Dictionary<string, WorkerRegistration> _rows = new(StringComparer.Ordinal);

        public void AddExecutor(string hostId)
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
                MaxConcurrentSandboxes = 4,
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

    /// <summary>
    /// Deterministic phase handler over real git: clones the staged bare
    /// repo, commits one file on <c>phase/&lt;phase&gt;-&lt;attempt&gt;</c>
    /// with fixed identity and timestamps (so identical starting repos yield
    /// identical shas on either execution path), pushes back, and returns
    /// the sha with fixed findings and usage.
    /// </summary>
    private sealed class DeterministicCommitHandler : IExecutorPhaseHandler
    {
        public bool ForceAgentFailure;

        public async Task<ExecutorPhaseResult> ExecuteAsync(ExecutorPhaseRequest request, string repoPath, ISandbox sandbox, CancellationToken ct)
        {
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

            var work = Path.Combine(Path.GetTempPath(), "codeybox-colocated-work-" + Guid.NewGuid().ToString("N"));
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
    }

    private sealed class NoopSandbox : ISandbox
    {
        public static readonly NoopSandbox Instance = new();

        public string Id => "noop-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, "", ""));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopSandboxProvider : ISandboxProvider
    {
        public string Name => "noop";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            Task.FromResult<ISandbox>(NoopSandbox.Instance);

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Stages a bare repo into a bare executor-side runner's staging root,
    /// mirroring what the delivery plane does over the transport. The direct
    /// runner — like any remote host — refuses to run when its staged copy
    /// is absent.
    /// </summary>
    private static void StageDirectRepo(LocalGitHost git, WorkItemId item, string stagingRoot)
    {
        var bare = git.GetRepoPath(item.ToString());
        var leaf = ExecutorPhaseExecution.ResolveStagedRepoPath(stagingRoot, item.ToString());
        if (Directory.Exists(leaf))
            Directory.Delete(leaf, recursive: true);
        Directory.CreateDirectory(leaf);
        foreach (var dir in Directory.GetDirectories(bare, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(leaf, Path.GetRelativePath(bare, dir)));
        foreach (var file in Directory.GetFiles(bare, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(leaf, Path.GetRelativePath(bare, file)), overwrite: true);
    }

    private async Task SeedBareRepoAsync(LocalGitHost git, WorkItemId item)
    {        var repoId = await git.EnsureRepositoryAsync(item, seedFromUrl: null);
        var bare = git.GetRepoPath(repoId);
        var clone = Path.Combine(_root, "seedclone-" + Guid.NewGuid().ToString("N"));
        await RunGit(_root, "clone", bare, clone);
        await RunGit(clone, "config", "user.email", "t@t");
        await RunGit(clone, "config", "user.name", "T");
        await File.WriteAllTextAsync(Path.Combine(clone, "README.md"), "seed\n");
        await RunGit(clone, "add", "README.md");
        await RunGit(clone, "commit", "-m", "seed");
        await RunGit(clone, "branch", "-M", "phase/seed");
        await RunGit(clone, "push", "origin", "phase/seed");
    }

    private async Task<string> RunGitBareCapture(string bareRepo, params string[] args) =>
        await RunGitCapture(_root, ["--git-dir", bareRepo, .. args]);

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
}
