using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the executor phase-dispatch proxy: remote execution
/// equivalence, per-item repo staging and stage-back, stage-out bounds,
/// idempotent redelivery, transport-vs-agent failure classification, and
/// in-process fallback. Uses a real <see cref="LocalGitHost"/>, a real
/// <see cref="SqliteIdempotencyStore"/>, real tar archives and real git
/// commits; only the network hop to the executor is faked (a tar-based
/// loopback transport over a per-host directory).
/// </summary>
public sealed class ExecutorPhaseProxyTests : IDisposable
{
    private const string FixedDate = "2000-01-01T00:00:00+00:00";

    private readonly string _root = Directory.CreateTempSubdirectory("codeybox-exec-phase-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── verification 1: remote dispatch ≡ in-process ────────────────────────

    [Fact]
    public async Task RemoteDispatch_ReturnsOutcomeShaFindingsAndUsage_EquivalentToInProcess()
    {
        using var ctx = CreateContext(["exec-1"]);
        var item = WorkItemId.New();
        var twin = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);
        await SeedBareRepoAsync(ctx.Git, twin);

        var remote = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), CancellationToken.None);
        var inner = await ctx.Inner.ExecutePhaseAsync(NewRequest(twin, "work", 0), CancellationToken.None);

        Assert.Equal(ExecutorPhaseOutcome.Succeeded, remote.Outcome);
        AssertResultsEqual(inner, remote);
    }

    // ── verification 2: commits land in the bare repo; push path unchanged ──

    [Fact]
    public async Task RemoteCommit_LandsInBareRepo_AndPushPublishesItUnchanged()
    {
        using var ctx = CreateContext(["exec-1"]);
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);

        var result = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), CancellationToken.None);
        Assert.NotNull(result.CommitSha);

        var bare = ctx.Git.GetRepoPath(item.ToString());
        var log = await RunGitBareCapture(bare, "log", "--format=%H", "phase/work-0");
        Assert.Contains(result.CommitSha!, log.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var upstream = Path.Combine(_root, "upstream-" + Guid.NewGuid().ToString("N") + ".git");
        await RunGit(_root, "init", "--bare", upstream);
        await RunGitBare(bare, "push", upstream, "phase/work-0:refs/heads/published");
        var published = await RunGitBareCapture(upstream, "rev-parse", "refs/heads/published");
        Assert.Equal(result.CommitSha, published.Trim());
    }

    // ── verification 3: per-item scoping ────────────────────────────────────

    [Fact]
    public async Task Executor_ReceivesOnlyTheStagedRepoForItsItem()
    {
        using var ctx = CreateContext(["exec-1"]);
        var itemA = WorkItemId.New();
        var itemB = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, itemA);
        await SeedBareRepoAsync(ctx.Git, itemB);

        await ctx.Proxy.ExecutePhaseAsync(NewRequest(itemA, "work", 0), CancellationToken.None);

        var transport = ctx.Transports["exec-1"];
        Assert.Single(transport.StagedInPaths);
        Assert.Equal(Path.GetFullPath(ctx.Git.GetRepoPath(itemA.ToString())), transport.StagedInPaths[0]);
        Assert.DoesNotContain(Path.GetFullPath(ctx.Git.GetRepoPath(itemB.ToString())), transport.StagedInPaths);
        Assert.Single(Directory.GetFileSystemEntries(transport.ExecutorRoot));
    }

    // ── verification 4: stage-out bounds ────────────────────────────────────

    [Fact]
    public async Task StageBack_LargerThanMaxArchiveBytes_IsRejectedWithoutRepoWrite()
    {
        using var ctx = CreateContext(["exec-1"], maxArchiveBytes: 256 * 1024);
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);
        var bare = ctx.Git.GetRepoPath(item.ToString());
        var before = (await RunGitBareCapture(bare, "rev-parse", "phase/seed")).Trim();

        ctx.Handler.PlantUnpackedBytes = 1024 * 1024;
        var request = NewRequest(item, "work", 0);
        await Assert.ThrowsAsync<ExecutorPhaseException>(
            () => ctx.Proxy.ExecutePhaseAsync(request, CancellationToken.None));

        // The cap travels into the transport and aborts the transfer
        // mid-stream: fewer than the planted 1 MiB ever land on disk.
        Assert.Equal(256 * 1024, ctx.Transports["exec-1"].LastStageOutMaxBytes);
        Assert.True(
            ctx.Transports["exec-1"].StageOutBytesWritten <= 256 * 1024,
            $"Streaming stage-out wrote {ctx.Transports["exec-1"].StageOutBytesWritten} bytes past the 256 KiB cap.");
        Assert.Equal(before, (await RunGitBareCapture(bare, "rev-parse", "phase/seed")).Trim());
        Assert.Empty((await RunGitBareCapture(bare, "branch", "--list", "phase/work-0")).Trim());
        var lookup = await ctx.Store.LookupAsync(
            ExecutorPhaseProxy.BuildDispatchKey(request),
            ExecutorPhaseProxy.ComputeBodyHash(request),
            DateTimeOffset.UtcNow);
        Assert.Equal(IdempotencyLookupOutcome.Miss, lookup.Outcome);
    }

    [Fact]
    public async Task StageBack_MoreEntriesThanAllowed_IsRejectedWithoutRepoWrite()
    {
        using var ctx = CreateContext(["exec-1"], maxEntries: 16);
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);
        var bare = ctx.Git.GetRepoPath(item.ToString());
        var before = (await RunGitBareCapture(bare, "rev-parse", "phase/seed")).Trim();

        ctx.Handler.PlantFileCount = 40;
        await Assert.ThrowsAsync<ExecutorPhaseException>(
            () => ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), CancellationToken.None));

        Assert.Equal(before, (await RunGitBareCapture(bare, "rev-parse", "phase/seed")).Trim());
        Assert.Empty((await RunGitBareCapture(bare, "branch", "--list", "phase/work-0")).Trim());
    }

    [Fact]
    public async Task StageBack_InflatedDeclaredSize_IsRejectedWithoutRepoWrite()
    {
        using var ctx = CreateContext(["exec-1"]);
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);
        var bare = ctx.Git.GetRepoPath(item.ToString());
        var before = (await RunGitBareCapture(bare, "rev-parse", "phase/seed")).Trim();

        var rootName = Path.GetFileName(bare.TrimEnd(Path.DirectorySeparatorChar));
        ctx.Transports["exec-1"].CustomArchive = stream =>
        {
            WriteInflatedArchive(stream, rootName);
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<ExecutorPhaseException>(
            () => ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), CancellationToken.None));

        Assert.Equal(before, (await RunGitBareCapture(bare, "rev-parse", "phase/seed")).Trim());
        Assert.Empty((await RunGitBareCapture(bare, "branch", "--list", "phase/work-0")).Trim());
    }

    [Theory]
    [InlineData("traversal")]
    [InlineData("absolute")]
    [InlineData("wrong-root")]
    [InlineData("symlink")]
    public async Task StageBack_MaliciousEntry_IsRejectedWithoutRepoWrite(string kind)
    {
        using var ctx = CreateContext(["exec-1"]);
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);
        var bare = ctx.Git.GetRepoPath(item.ToString());
        var before = (await RunGitBareCapture(bare, "rev-parse", "phase/seed")).Trim();
        var rootName = Path.GetFileName(bare.TrimEnd(Path.DirectorySeparatorChar));

        var (entryName, typeFlag) = kind switch
        {
            "traversal" => ("../evil.txt", '0'),
            "absolute" => ("/tmp/evil.txt", '0'),
            "wrong-root" => ("other-root.git/evil.txt", '0'),
            "symlink" => (rootName + "/evil-link", '2'),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        ctx.Transports["exec-1"].CustomArchive = stream =>
        {
            WriteTarHeader(stream, entryName, typeFlag, 0);
            return Task.CompletedTask;
        };
        var request = NewRequest(item, "work", 0);
        await Assert.ThrowsAsync<ExecutorPhaseException>(
            () => ctx.Proxy.ExecutePhaseAsync(request, CancellationToken.None));

        Assert.Equal(before, (await RunGitBareCapture(bare, "rev-parse", "phase/seed")).Trim());
        Assert.Empty((await RunGitBareCapture(bare, "branch", "--list", "phase/work-0")).Trim());
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(bare)!, "evil.txt")), "Traversal entry escaped the staging root.");
        var lookup = await ctx.Store.LookupAsync(
            ExecutorPhaseProxy.BuildDispatchKey(request),
            ExecutorPhaseProxy.ComputeBodyHash(request),
            DateTimeOffset.UtcNow);
        Assert.Equal(IdempotencyLookupOutcome.Miss, lookup.Outcome);
    }

    // ── verification 5: idempotency ─────────────────────────────────────────

    [Fact]
    public async Task Redelivery_ReturnsOriginalResult_WithoutSecondSandbox()
    {
        using var ctx = CreateContext(["exec-1"]);
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);
        var request = NewRequest(item, "work", 0);

        var first = await ctx.Proxy.ExecutePhaseAsync(request, CancellationToken.None);
        var second = await ctx.Proxy.ExecutePhaseAsync(request, CancellationToken.None);

        AssertResultsEqual(first, second);
        Assert.Equal(1, ctx.Transports["exec-1"].RunPhaseCalls);
    }

    [Fact]
    public async Task SameKey_DifferentBody_IsRefusedAsConflictWithoutExecuting()
    {
        using var ctx = CreateContext(["exec-1"]);
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);

        await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0, "{\"v\":1}"), CancellationToken.None);
        await Assert.ThrowsAsync<ExecutorPhaseConflictException>(
            () => ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0, "{\"v\":2}"), CancellationToken.None));

        Assert.Equal(1, ctx.Transports["exec-1"].RunPhaseCalls);
    }

    // ── verification 6: transport vs agent failure ──────────────────────────

    [Fact]
    public async Task TransportFailure_PropagatesWithoutCachingOrRepoWrite()
    {
        using var ctx = CreateContext(["exec-1"]);
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);
        var bare = ctx.Git.GetRepoPath(item.ToString());
        var before = (await RunGitBareCapture(bare, "rev-parse", "phase/seed")).Trim();

        ctx.Transports["exec-1"].FailWith = new ExecutorPhaseTransportException("exec-1", "stage-in", "connection refused");
        var request = NewRequest(item, "work", 0);
        var thrown = await Assert.ThrowsAsync<ExecutorPhaseTransportException>(
            () => ctx.Proxy.ExecutePhaseAsync(request, CancellationToken.None));
        Assert.Equal("stage-in", thrown.Operation);

        Assert.Equal(before, (await RunGitBareCapture(bare, "rev-parse", "phase/seed")).Trim());
        Assert.Equal(0, ctx.InnerSpy.Calls);
        var lookup = await ctx.Store.LookupAsync(
            ExecutorPhaseProxy.BuildDispatchKey(request),
            ExecutorPhaseProxy.ComputeBodyHash(request),
            DateTimeOffset.UtcNow);
        Assert.Equal(IdempotencyLookupOutcome.Miss, lookup.Outcome);
    }

    [Fact]
    public async Task AgentFailure_IsReturnedAndCached_NotThrown()
    {
        using var ctx = CreateContext(["exec-1"]);
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);

        ctx.Handler.ForceAgentFailure = true;
        var request = NewRequest(item, "work", 0, "{\"v\":9}");
        var first = await ctx.Proxy.ExecutePhaseAsync(request, CancellationToken.None);
        Assert.Equal(ExecutorPhaseOutcome.AgentFailed, first.Outcome);

        var second = await ctx.Proxy.ExecutePhaseAsync(request, CancellationToken.None);
        AssertResultsEqual(first, second);
        Assert.Equal(1, ctx.Transports["exec-1"].RunPhaseCalls);
    }

    // ── verification 7: fallback ────────────────────────────────────────────

    [Fact]
    public async Task NoExecutorRegistered_FallsBackToInProcess_Unchanged()
    {
        using var ctx = CreateContext([]);
        var item = WorkItemId.New();
        var twin = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);
        await SeedBareRepoAsync(ctx.Git, twin);

        var fallback = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), CancellationToken.None);
        var direct = await ctx.Inner.ExecutePhaseAsync(NewRequest(twin, "work", 0), CancellationToken.None);

        AssertResultsEqual(direct, fallback);
        Assert.Equal(0, ctx.Factory.Resolves);
    }

    [Fact]
    public async Task CordonedExecutor_IsNeverSelected_FallsBackToInProcess()
    {
        using var ctx = CreateContext(["exec-1"], cordoned: true);
        var item = WorkItemId.New();
        await SeedBareRepoAsync(ctx.Git, item);

        var result = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), CancellationToken.None);

        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.CommitSha);
        Assert.Equal(0, ctx.Factory.Resolves);
        Assert.Equal(1, ctx.InnerSpy.Calls);
    }

    [Fact]
    public void DispatchOptions_Validate_RejectsBadBounds()
    {
        Assert.Throws<InvalidOperationException>(() => new ExecutorPhaseDispatchOptions { StageOutMaxArchiveBytes = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ExecutorPhaseDispatchOptions { StageOutMaxEntries = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ExecutorPhaseDispatchOptions { StageOutMaxExpansionRatio = 0.5 }.Validate());
        new ExecutorPhaseDispatchOptions().Validate();
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private static ExecutorPhaseRequest NewRequest(WorkItemId item, string phase, int attempt, string payload = "{}") =>
        new() { WorkItemId = item.ToString(), Phase = phase, Attempt = attempt, RepositoryId = item.ToString(), PayloadJson = payload };

    private static void AssertResultsEqual(ExecutorPhaseResult expected, ExecutorPhaseResult actual)
    {
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.CommitSha, actual.CommitSha);
        Assert.Equal(expected.Findings, actual.Findings);
        Assert.Equal(expected.Usage, actual.Usage);
        Assert.Equal(expected.ErrorMessage, actual.ErrorMessage);
    }

    private TestHarness CreateContext(string[] executors, long? maxArchiveBytes = null, int? maxEntries = null, bool cordoned = false)
    {
        var gitRoot = Path.Combine(_root, "git-" + Guid.NewGuid().ToString("N"));
        var git = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var dbPath = Path.Combine(_root, "idem-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteIdempotencyStore(dbPath);
        var registry = new FakeWorkerRegistry();
        var handler = new GitCommitPhaseHandler();
        var options = new ExecutorPhaseDispatchOptions();
        if (maxArchiveBytes is not null) options.StageOutMaxArchiveBytes = maxArchiveBytes.Value;
        if (maxEntries is not null) options.StageOutMaxEntries = maxEntries.Value;
        var factory = new FakeTransportFactory();
        var inner = new InProcessExecutorPhaseRunner(git, handler, () => options);
        var spy = new SpyRunner(inner);
        var proxy = new ExecutorPhaseProxy(registry, factory, git, store, spy, () => options);
        foreach (var host in executors)
        {
            registry.AddExecutor(host, cordoned);
            factory.AddHost(host, Path.Combine(_root, "executor-" + host + "-" + Guid.NewGuid().ToString("N")), handler);
        }
        return new TestHarness(git, store, handler, factory, inner, spy, proxy);
    }

    private async Task SeedBareRepoAsync(LocalGitHost git, WorkItemId item)
    {
        var repoId = await git.EnsureRepositoryAsync(item, seedFromUrl: null);
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

    private async Task RunGitBare(string bareRepo, params string[] args) =>
        await RunGit(_root, ["--git-dir", bareRepo, .. args]);

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

    /// <summary>
    /// Writes a tar whose file entry declares a 1 MiB payload while the
    /// archive itself carries almost no data: declared bytes dwarf
    /// archive-bytes × ratio, so the expansion-ratio guard must reject it.
    /// </summary>
    private static void WriteInflatedArchive(Stream stream, string rootName)
    {
        WriteTarHeader(stream, rootName + "/", '5', 0);
        WriteTarHeader(stream, rootName + "/payload.bin", '0', 1024 * 1024);
        stream.Write(new byte[1024]);
        stream.Write(new byte[1024]);
    }

    private static void WriteTarHeader(Stream stream, string name, char typeFlag, long size)
    {
        var block = new byte[512];
        var nameBytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(nameBytes, block, Math.Min(nameBytes.Length, 100));
        Encoding.ASCII.GetBytes("0000777\0").CopyTo(block, 100);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(block, 108);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(block, 116);
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0").CopyTo(block, 124);
        Encoding.ASCII.GetBytes(Convert.ToString(946684800L, 8).PadLeft(11, '0') + "\0").CopyTo(block, 136);
        for (var i = 148; i < 156; i++) block[i] = (byte)' ';
        block[156] = (byte)typeFlag;
        Encoding.ASCII.GetBytes("ustar\0" + "00").CopyTo(block, 257);
        long checksum = 0;
        foreach (var b in block) checksum += b;
        Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ").CopyTo(block, 148);
        stream.Write(block);
    }

    private sealed class TestHarness : IDisposable
    {
        public TestHarness(
            LocalGitHost git,
            SqliteIdempotencyStore store,
            GitCommitPhaseHandler handler,
            FakeTransportFactory factory,
            InProcessExecutorPhaseRunner inner,
            SpyRunner spy,
            ExecutorPhaseProxy proxy)
        {
            Git = git;
            Store = store;
            Handler = handler;
            Factory = factory;
            Inner = inner;
            InnerSpy = spy;
            Proxy = proxy;
        }

        public LocalGitHost Git { get; }
        public SqliteIdempotencyStore Store { get; }
        public GitCommitPhaseHandler Handler { get; }
        public FakeTransportFactory Factory { get; }
        public Dictionary<string, FakePhaseTransport> Transports => Factory.Transports;
        public InProcessExecutorPhaseRunner Inner { get; }
        public SpyRunner InnerSpy { get; }
        public ExecutorPhaseProxy Proxy { get; }

        public void Dispose() => Store.Dispose();
    }

    private sealed class SpyRunner : IExecutorPhaseRunner
    {
        private readonly IExecutorPhaseRunner _inner;

        public SpyRunner(IExecutorPhaseRunner inner) => _inner = inner;

        public int Calls { get; private set; }

        public Task<ExecutorPhaseResult> ExecutePhaseAsync(ExecutorPhaseRequest request, CancellationToken ct)
        {
            Calls++;
            return _inner.ExecutePhaseAsync(request, ct);
        }
    }

    private sealed class FakeWorkerRegistry : IWorkerRegistry
    {
        private readonly Dictionary<string, WorkerRegistration> _rows = new(StringComparer.Ordinal);

        public void AddExecutor(string hostId, bool cordoned = false)
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
                Cordoned = cordoned,
                Healthy = true,
            };
        }

        public Task RegisterAsync(WorkerRegistration registration, CancellationToken ct = default)
        {
            _rows[registration.WorkerId] = registration;
            return Task.CompletedTask;
        }

        public Task HeartbeatAsync(string workerId, string? currentWorkItemId, CancellationToken ct = default)
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
    /// Deterministic phase handler over real git: clones the given bare repo,
    /// commits one file on <c>phase/&lt;phase&gt;-&lt;attempt&gt;</c> with fixed
    /// identity and timestamps (so identical starting repos yield identical
    /// shas), pushes back, and returns the sha with fixed findings and usage.
    /// </summary>
    private sealed class GitCommitPhaseHandler : IExecutorPhaseHandler
    {
        public int PlantUnpackedBytes;
        public int PlantFileCount;
        public bool ForceAgentFailure;

        public async Task<ExecutorPhaseResult> ExecuteAsync(ExecutorPhaseRequest request, string repoPath, CancellationToken ct)
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

            var work = Path.Combine(Path.GetTempPath(), "codeybox-phasework-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var branch = $"phase/{request.Phase}-{request.Attempt}";
                await RunGit(work, "clone", repoPath, "w");
                var w = Path.Combine(work, "w");
                await RunGit(w, "config", "user.email", "t@t");
                await RunGit(w, "config", "user.name", "T");
                await RunGit(w, "checkout", "-B", branch, "origin/phase/seed");
                // Content deliberately excludes the work item id so identical
                // starting repos yield identical commits (and shas) whether
                // the phase runs remotely or in process.
                var content = $"phase={request.Phase} attempt={request.Attempt}\n";
                await File.WriteAllTextAsync(Path.Combine(w, "phase-output.txt"), content, ct);
                for (var i = 0; i < PlantFileCount; i++)
                    await File.WriteAllTextAsync(Path.Combine(w, $"planted-{i}.txt"), "x\n", ct);
                await RunGit(w, "add", "-A");
                await RunGit(w, "commit", "-m", $"phase {request.Phase} attempt {request.Attempt}");
                var sha = (await RunGitCapture(w, "rev-parse", "HEAD")).Trim();
                await RunGit(w, "push", "origin", $"{branch}:{branch}");
                if (PlantUnpackedBytes > 0)
                {
                    // Incompressible payload written straight into the staged
                    // copy (outside git, as hostile executor content would
                    // be): git packs would otherwise compress planted zeros
                    // and the archive would stay under the cap.
                    var random = new Random(42);
                    var bytes = new byte[PlantUnpackedBytes];
                    random.NextBytes(bytes);
                    await File.WriteAllBytesAsync(Path.Combine(repoPath, "planted-unpacked.bin"), bytes, ct);
                }
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

    /// <summary>
    /// Loopback executor transport: each host owns a directory standing in
    /// for its machine. Stage-in copies the single repo path there; the phase
    /// runs the shared handler against that copy; stage-out tars the copy
    /// back through real tar bytes so the proxy validates a real archive.
    /// </summary>
    private sealed class FakePhaseTransport : IExecutorPhaseTransport
    {
        private readonly GitCommitPhaseHandler _handler;

        public FakePhaseTransport(string hostId, string executorRoot, GitCommitPhaseHandler handler)
        {
            HostId = hostId;
            ExecutorRoot = executorRoot;
            _handler = handler;
            Directory.CreateDirectory(executorRoot);
        }

        public string HostId { get; }
        public string ExecutorRoot { get; }
        public List<string> StagedInPaths { get; } = [];
        public int RunPhaseCalls { get; private set; }
        public ExecutorPhaseTransportException? FailWith;
        public Func<Stream, Task>? CustomArchive;
        public long? LastStageOutMaxBytes { get; private set; }
        public long StageOutBytesWritten { get; private set; }

        public string? StagedCopy
        {
            get
            {
                var entries = Directory.GetFileSystemEntries(ExecutorRoot);
                return entries.Length == 1 ? entries[0] : null;
            }
        }

        public Task StageInAsync(string hostRepoPath, CancellationToken ct)
        {
            ThrowIfFailing();
            StagedInPaths.Add(Path.GetFullPath(hostRepoPath));
            var dest = Path.Combine(ExecutorRoot, Path.GetFileName(hostRepoPath.TrimEnd(Path.DirectorySeparatorChar)));
            CopyDirectory(hostRepoPath, dest);
            return Task.CompletedTask;
        }

        public Task<ExecutorPhaseResult> RunPhaseAsync(ExecutorPhaseRequest request, CancellationToken ct)
        {
            ThrowIfFailing();
            RunPhaseCalls++;
            var staged = StagedCopy ?? throw new InvalidOperationException("No staged repo on fake executor.");
            return _handler.ExecuteAsync(request, staged, ct);
        }

        public async Task StageOutToArchiveAsync(string hostArchivePath, long maxArchiveBytes, CancellationToken ct)
        {
            ThrowIfFailing();
            LastStageOutMaxBytes = maxArchiveBytes;
            StageOutBytesWritten = 0;
            await using var file = File.OpenWrite(hostArchivePath);
            await using var bounded = new BoundedStageOutStream(file, maxArchiveBytes, HostId, bytes => StageOutBytesWritten = bytes);
            try
            {
                if (CustomArchive is not null)
                {
                    await CustomArchive(bounded).ConfigureAwait(false);
                    await bounded.FlushAsync(ct).ConfigureAwait(false);
                    return;
                }
                var staged = StagedCopy ?? throw new InvalidOperationException("No staged repo on fake executor.");
                await WriteTarOfDirectoryAsync(staged, bounded, ct).ConfigureAwait(false);
            }
            catch
            {
                // The streaming cap was exceeded (or the payload was hostile):
                // leave no usable archive behind, mirroring the production
                // contract that an aborted stage-out is a phase failure.
                try { File.Delete(hostArchivePath); } catch { }
                throw;
            }
        }

        private void ThrowIfFailing()
        {
            if (FailWith is not null) throw FailWith;
        }

        private static void CopyDirectory(string source, string destination)
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            Directory.CreateDirectory(destination);
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }

        private static async Task WriteTarOfDirectoryAsync(string sourceDir, Stream destination, CancellationToken ct)
        {
            var rootName = Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar));
            await using var writer = new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true);
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var name = rootName + "/" + Path.GetRelativePath(sourceDir, dir).Replace('\\', '/');
                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, name), ct).ConfigureAwait(false);
            }
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var name = rootName + "/" + Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name);
                await using var data = File.OpenRead(file);
                entry.DataStream = data;
                await writer.WriteEntryAsync(entry, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Write-only wrapper that aborts the stage-out as soon as the
        /// configured archive cap is exceeded, so the fake mirrors the
        /// production contract: the cap is enforced while receiving, never
        /// by measuring a fully-buffered file afterwards. Exceeding the cap
        /// is an <see cref="ExecutorPhaseException"/> (phase failure: the
        /// host was reachable) rather than a transport failure.
        /// </summary>
        private sealed class BoundedStageOutStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _maxBytes;
            private readonly string _hostId;
            private readonly Action<long> _progress;
            private long _written;

            public BoundedStageOutStream(Stream inner, long maxBytes, string hostId, Action<long> progress)
            {
                _inner = inner;
                _maxBytes = maxBytes;
                _hostId = hostId;
                _progress = progress;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _written;
            public override long Position { get => _written; set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_written + count > _maxBytes)
                    throw new ExecutorPhaseException(
                        $"Staged-back archive from host '{_hostId}' exceeded configured StageOutMaxArchiveBytes={_maxBytes}.");
                _inner.Write(buffer, offset, count);
                _written += count;
                _progress(_written);
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (_written + count > _maxBytes)
                    throw new ExecutorPhaseException(
                        $"Staged-back archive from host '{_hostId}' exceeded configured StageOutMaxArchiveBytes={_maxBytes}.");
                await _inner.WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
                _written += count;
                _progress(_written);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            {
                if (_written + buffer.Length > _maxBytes)
                    throw new ExecutorPhaseException(
                        $"Staged-back archive from host '{_hostId}' exceeded configured StageOutMaxArchiveBytes={_maxBytes}.");
                await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
                _written += buffer.Length;
                _progress(_written);
            }

            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    private sealed class FakeTransportFactory : IExecutorPhaseTransportFactory
    {
        public readonly Dictionary<string, FakePhaseTransport> Transports = new(StringComparer.Ordinal);
        public int Resolves { get; private set; }

        public void AddHost(string hostId, string executorRoot, GitCommitPhaseHandler handler) =>
            Transports[hostId] = new FakePhaseTransport(hostId, executorRoot, handler);

        public Task<IExecutorPhaseTransport?> ResolveAsync(string hostId, CancellationToken ct)
        {
            Resolves++;
            return Task.FromResult<IExecutorPhaseTransport?>(Transports.GetValueOrDefault(hostId));
        }
    }
}
