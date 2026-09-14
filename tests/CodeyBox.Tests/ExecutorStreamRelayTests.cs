using System.Formats.Tar;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the executor stream relay: a phase run on a remote
/// executor produces a captured stream on the orchestrator at the same path
/// and key as the equivalent local phase, live subscribers receive chunks
/// during execution, over-limit producers are truncated with the same marker
/// a local producer receives, relay failure never changes the phase outcome,
/// and sequence gaps are recorded instead of silently omitted. Uses a real
/// <see cref="AgentStreamStore"/>, a real <see cref="ExecutorPhaseProxy"/>,
/// a real <see cref="LocalGitHost"/>, a real
/// <see cref="SqliteIdempotencyStore"/> and real tar archives; only the
/// network hop to the executor is faked.
/// </summary>
public sealed class ExecutorStreamRelayTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codeybox-exec-relay-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── verification 1: same artefact, same path and key ────────────────────

    [Fact]
    public async Task RemotePhase_ProducesCapturedStream_AtSamePathAndKeyAsLocalPhase()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var ctx = CreateContext();
        var item = WorkItemId.New();
        await ctx.Git.EnsureRepositoryAsync(item, seedFromUrl: null, cts.Token);
        ctx.Transport.Script = _ => [(0, "alpha\n"), (1, "beta\n")];

        var result = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), cts.Token);

        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
        var file = Assert.Single(await ctx.Streams.ListAsync(item, ct: cts.Token));
        Assert.Equal("work", file.Phase);
        Assert.Equal(1, file.Iteration);
        Assert.Matches(@"^work-1-[0-9a-f]{6}\.jsonl$", file.FileName);
        Assert.Equal(Path.Combine(ctx.StreamsRoot, item.ToString()), Path.GetDirectoryName(StreamPath(ctx, item, file.FileName)));
        var lines = await File.ReadAllLinesAsync(StreamPath(ctx, item, file.FileName), cts.Token);
        Assert.Equal(["alpha", "beta"], lines);

        // The equivalent local phase keys its artefact identically: the same
        // work-item directory layout and the same phase/iteration file prefix.
        var twin = WorkItemId.New();
        await using (var local = await ctx.Streams.BeginCaptureAsync(twin, "work", 1, cts.Token))
        {
            Assert.NotNull(local);
            local!.WriteChunk("alpha\nbeta\n");
        }

        var twinFile = Assert.Single(await ctx.Streams.ListAsync(twin, ct: cts.Token));
        Assert.Equal(Path.Combine(ctx.StreamsRoot, twin.ToString()), Path.GetDirectoryName(StreamPath(ctx, twin, twinFile.FileName)));
        Assert.StartsWith("work-1-", twinFile.FileName);
        var twinLines = await File.ReadAllLinesAsync(StreamPath(ctx, twin, twinFile.FileName), cts.Token);
        Assert.Equal(lines, twinLines);

        // Live subscribers saw the same chunks through the existing contract.
        Assert.Equal(["work", "work"], ctx.Broadcaster.Phases);
        Assert.Contains("alpha\n", ctx.Broadcaster.Text);
        Assert.Contains("beta\n", ctx.Broadcaster.Text);
    }

    // ── verification 2: live delivery during execution ──────────────────────

    [Fact]
    public async Task LiveSubscriber_ReceivesChunks_DuringExecution_NotOnlyAtCompletion()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var ctx = CreateContext();
        var item = WorkItemId.New();
        await ctx.Git.EnsureRepositoryAsync(item, seedFromUrl: null, cts.Token);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Transport.Script = _ => [(0, "live-one\n")];
        ctx.Transport.BeforeReturn = async token =>
        {
            await gate.Task.WaitAsync(token).ConfigureAwait(false);
            await Task.Delay(50, token).ConfigureAwait(false);
        };

        var dispatch = ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), cts.Token);
        await ctx.Broadcaster.FirstChunk.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        // The first chunk arrived while the phase was still running: the
        // transport is parked behind the test gate, so completion is
        // impossible yet.
        Assert.False(dispatch.IsCompleted);
        gate.TrySetResult();
        var result = await dispatch.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
        var file = Assert.Single(await ctx.Streams.ListAsync(item, ct: cts.Token));
        Assert.Contains("live-one", await File.ReadAllTextAsync(StreamPath(ctx, item, file.FileName), cts.Token));
    }

    // ── verification 3: over-limit truncation marker parity ─────────────────

    [Fact]
    public async Task RemoteProducer_BeyondBufferingLimits_IsTruncatedWithSameMarkerAsLocal()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var ctx = CreateContext(maxFileSizeMb: 1);
        var item = WorkItemId.New();
        await ctx.Git.EnsureRepositoryAsync(item, seedFromUrl: null, cts.Token);

        var script = new List<(long, string)>();
        for (var i = 0; i < 12; i++)
            script.Add((i, BuildBlock(1000)));
        ctx.Transport.Script = _ => script;

        var result = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), cts.Token);

        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
        var file = Assert.Single(await ctx.Streams.ListAsync(item, ct: cts.Token));
        var path = StreamPath(ctx, item, file.FileName);
        Assert.True(new FileInfo(path).Length <= 1024 * 1024);
        var remoteLines = await File.ReadAllLinesAsync(path, cts.Token);
        var remoteMarker = Assert.Single(remoteLines, l => l.StartsWith("[...truncated by ", StringComparison.Ordinal));

        var twin = WorkItemId.New();
        await using (var local = await ctx.Streams.BeginCaptureAsync(twin, "work", 1, cts.Token))
        {
            Assert.NotNull(local);
            for (var i = 0; i < 12; i++)
                local!.WriteChunk(BuildBlock(1000));
        }

        var twinFile = Assert.Single(await ctx.Streams.ListAsync(twin, ct: cts.Token));
        var twinLines = await File.ReadAllLinesAsync(StreamPath(ctx, twin, twinFile.FileName), cts.Token);
        var localMarker = Assert.Single(twinLines, l => l.StartsWith("[...truncated by ", StringComparison.Ordinal));
        Assert.Equal(localMarker, remoteMarker);
    }

    // ── verification 4: relay failure is observability-only ─────────────────

    [Fact]
    public async Task RelayBroadcastFailure_LeavesPhaseOutcomeUnchanged_AndRetainsPartialStream()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var ctx = CreateContext();
        ctx.Broadcaster.ShouldThrow = _ => true;
        var item = WorkItemId.New();
        await ctx.Git.EnsureRepositoryAsync(item, seedFromUrl: null, cts.Token);
        ctx.Transport.Script = _ => [(0, "kept-one\n"), (1, "kept-two\n")];

        var result = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), cts.Token);

        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
        var file = Assert.Single(await ctx.Streams.ListAsync(item, ct: cts.Token));
        var lines = await File.ReadAllLinesAsync(StreamPath(ctx, item, file.FileName), cts.Token);
        Assert.Equal(["kept-one", "kept-two"], lines);
    }

    [Fact]
    public async Task RelayCaptureFailure_LeavesPhaseOutcomeUnchanged_AndStillBroadcastsLive()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var ctx = CreateContext(streamStore: new ThrowingStreamStore());
        var item = WorkItemId.New();
        await ctx.Git.EnsureRepositoryAsync(item, seedFromUrl: null, cts.Token);
        ctx.Transport.Script = _ => [(0, "live-despite-capture-failure\n")];

        var result = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), cts.Token);

        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
        Assert.Contains("live-despite-capture-failure\n", ctx.Broadcaster.Text);
    }

    // ── verification 5: gaps are recorded, never silent ─────────────────────

    [Fact]
    public async Task InducedGapInRelayedSequence_IsRecordedInCapturedStream()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var ctx = CreateContext();
        var item = WorkItemId.New();
        await ctx.Git.EnsureRepositoryAsync(item, seedFromUrl: null, cts.Token);
        ctx.Transport.Script = _ => [(0, "first\n"), (2, "third\n")];

        var result = await ctx.Proxy.ExecutePhaseAsync(NewRequest(item, "work", 0), cts.Token);

        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
        var file = Assert.Single(await ctx.Streams.ListAsync(item, ct: cts.Token));
        var lines = await File.ReadAllLinesAsync(StreamPath(ctx, item, file.FileName), cts.Token);
        Assert.Equal(3, lines.Length);
        Assert.Equal("first", lines[0]);
        Assert.Contains("stream gap", lines[1], StringComparison.Ordinal);
        Assert.Contains("expected seq 1", lines[1], StringComparison.Ordinal);
        Assert.Contains("received seq 2", lines[1], StringComparison.Ordinal);
        Assert.Equal("third", lines[2]);
        Assert.Contains("stream gap", ctx.Broadcaster.Text, StringComparison.Ordinal);
    }

    private static string BuildBlock(int lines)
    {
        var sb = new StringBuilder(lines * 101);
        for (var i = 0; i < lines; i++)
            sb.Append('x', 100).Append('\n');
        return sb.ToString();
    }

    private static string StreamPath(RelayContext ctx, WorkItemId item, string fileName) =>
        Path.Combine(ctx.StreamsRoot, item.ToString(), fileName);

    private static ExecutorPhaseRequest NewRequest(WorkItemId item, string phase, int attempt) =>
        new() { WorkItemId = item.ToString(), Phase = phase, Attempt = attempt, RepositoryId = item.ToString(), PayloadJson = "{}" };

    private RelayContext CreateContext(int maxFileSizeMb = 32, IAgentStreamStore? streamStore = null)
    {
        var gitRoot = Path.Combine(_root, "git-" + Guid.NewGuid().ToString("N"));
        var git = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var dbPath = Path.Combine(_root, "idem-" + Guid.NewGuid().ToString("N") + ".db");
        var idempotency = new SqliteIdempotencyStore(dbPath);
        var registry = new FakeWorkerRegistry();
        var options = new ExecutorPhaseDispatchOptions();
        var transport = new FakeStreamingTransport("exec-1", Path.Combine(_root, "executor-" + Guid.NewGuid().ToString("N")));
        var factory = new FakeTransportFactory(transport);
        var inner = new InProcessExecutorPhaseRunner(git, new UncalledHandler(), () => options);
        var streamsRoot = Path.Combine(_root, "streams-" + Guid.NewGuid().ToString("N"));
        var streams = new AgentStreamStore(
            new AgentStreamsOptions { Enabled = true, Path = streamsRoot, MaxFileSizeMb = maxFileSizeMb },
            NullLogger<AgentStreamStore>.Instance);
        var broadcaster = new RecordingBroadcaster();
        registry.AddExecutor("exec-1");
        var proxy = new ExecutorPhaseProxy(
            registry, factory, git, idempotency, inner, () => options,
            streamStore: streamStore ?? streams, broadcaster: broadcaster);
        return new RelayContext(git, idempotency, options, transport, streams, streamsRoot, broadcaster, proxy);
    }

    private sealed record RelayContext(
        LocalGitHost Git,
        SqliteIdempotencyStore Idempotency,
        ExecutorPhaseDispatchOptions Options,
        FakeStreamingTransport Transport,
        AgentStreamStore Streams,
        string StreamsRoot,
        RecordingBroadcaster Broadcaster,
        ExecutorPhaseProxy Proxy) : IDisposable
    {
        public void Dispose() => Idempotency.Dispose();
    }

    private sealed class UncalledHandler : IExecutorPhaseHandler
    {
        public Task<ExecutorPhaseResult> ExecuteAsync(ExecutorPhaseRequest request, string repoPath, CancellationToken ct) =>
            throw new InvalidOperationException("Fallback runner must not run while an executor is registered.");
    }

    private sealed class RecordingBroadcaster : IStdoutBroadcaster
    {
        private readonly object _lock = new();
        private readonly List<(string Phase, string Chunk)> _chunks = [];

        public TaskCompletionSource FirstChunk { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<int, bool>? ShouldThrow;

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
            lock (_lock)
            {
                if (ShouldThrow?.Invoke(_chunks.Count) == true)
                    throw new InvalidOperationException("Simulated broadcast failure.");
                _chunks.Add((phase, chunk));
            }
            FirstChunk.TrySetResult();
        }

        public Task CompleteAsync(WorkItemId workItemId) => Task.CompletedTask;

        public string? GetTail(WorkItemId workItemId)
        {
            lock (_lock)
                return _chunks.Count == 0 ? null : string.Concat(_chunks.Select(c => c.Chunk));
        }
    }

    private sealed class ThrowingStreamStore : IAgentStreamStore
    {
        public AgentStreamsOptions Options => new() { Enabled = true, Path = Path.GetTempPath() };

        public Task<AgentStreamCapture?> BeginCaptureAsync(WorkItemId workItemId, string phase, int iteration, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated stream store failure.");

        public Task<IReadOnlyList<AgentStreamFile>> ListAsync(WorkItemId workItemId, int limit = 100, bool includeLineCount = false, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AgentStreamFile?> GetAsync(WorkItemId workItemId, string fileName, bool includeLineCount = false, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Stream?> OpenReadAsync(WorkItemId workItemId, string fileName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default) =>
            throw new NotSupportedException();
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
                Cordoned = false,
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

    private sealed class FakeTransportFactory : IExecutorPhaseTransportFactory
    {
        private readonly FakeStreamingTransport _transport;

        public FakeTransportFactory(FakeStreamingTransport transport) => _transport = transport;

        public Task<IExecutorPhaseTransport?> ResolveAsync(string hostId, CancellationToken ct) =>
            Task.FromResult<IExecutorPhaseTransport?>(_transport);
    }

    /// <summary>
    /// Loopback executor transport with a scripted sequenced chunk stream:
    /// stage-in copies the single repo path over, the phase emits its script
    /// through the streaming callback before returning a canned success, and
    /// stage-out tars the copy back through real tar bytes.
    /// </summary>
    private sealed class FakeStreamingTransport : IStreamingExecutorPhaseTransport
    {
        public FakeStreamingTransport(string hostId, string executorRoot)
        {
            HostId = hostId;
            ExecutorRoot = executorRoot;
            Directory.CreateDirectory(executorRoot);
        }

        public string HostId { get; }
        public string ExecutorRoot { get; }
        public Func<ExecutorPhaseRequest, List<(long Sequence, string Data)>>? Script;
        public Func<CancellationToken, Task>? BeforeReturn;
        public int RunPhaseCalls { get; private set; }

        public string StagedCopy
        {
            get
            {
                var entries = Directory.GetFileSystemEntries(ExecutorRoot);
                return entries.Length == 1 ? entries[0] : throw new InvalidOperationException("No staged repo on fake executor.");
            }
        }

        public Task StageInAsync(string hostRepoPath, CancellationToken ct)
        {
            var dest = Path.Combine(ExecutorRoot, Path.GetFileName(hostRepoPath.TrimEnd(Path.DirectorySeparatorChar)));
            CopyDirectory(hostRepoPath, dest);
            return Task.CompletedTask;
        }

        public Task<ExecutorPhaseResult> RunPhaseAsync(ExecutorPhaseRequest request, CancellationToken ct) =>
            RunPhaseAsync(request, onChunk: null, ct);

        public async Task<ExecutorPhaseResult> RunPhaseAsync(
            ExecutorPhaseRequest request,
            Func<ExecutorStreamChunk, CancellationToken, Task>? onChunk,
            CancellationToken ct)
        {
            RunPhaseCalls++;
            if (Script is not null)
            {
                foreach (var (sequence, data) in Script(request))
                {
                    ct.ThrowIfCancellationRequested();
                    if (onChunk is not null)
                        await onChunk(new ExecutorStreamChunk { Sequence = sequence, Data = data }, ct).ConfigureAwait(false);
                }
            }
            if (BeforeReturn is not null)
                await BeforeReturn(ct).ConfigureAwait(false);
            return new ExecutorPhaseResult
            {
                Outcome = ExecutorPhaseOutcome.Succeeded,
                Findings = [$"finding for {request.Phase}"],
                Usage = new ExecutorPhaseUsage(100, 50, 0.002m),
            };
        }

        public async Task StageOutToArchiveAsync(string hostArchivePath, long maxArchiveBytes, CancellationToken ct)
        {
            await using var file = File.OpenWrite(hostArchivePath);
            await WriteTarOfDirectoryAsync(StagedCopy, file, ct).ConfigureAwait(false);
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
    }
}
