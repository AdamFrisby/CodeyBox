using System.Net;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP contract for the outbound-only phase protocol: the real
/// <see cref="HttpExecutorPhaseChannel"/> drives the real phase endpoints
/// (real routing, real bearer middleware, real broker) over HTTP. Proves
/// the wire shapes match on both sides — route paths, DTO names, the
/// two-call stage-out protocol, host binding, and the phase-failure vs
/// transport-failure status contract — through real HTTP, not stubs.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ExecutorPhaseHttpTests : IDisposable
{
    private const string ExecutorToken = "test-phase-http-bound-to-exec-1";
    private const string OtherHostToken = "test-phase-http-bound-to-exec-2";
    private const string UnboundToken = "test-phase-http-with-no-host-binding";

    private readonly ExecutorPhaseHttpFactory _factory = new();
    private readonly string _root = Directory.CreateTempSubdirectory("codeybox-phase-http-").FullName;
    private readonly string _apiKeyEnvVar = "CBXPH" + Guid.NewGuid().ToString("N").Replace("-", string.Empty);
    private readonly List<HttpClient> _clients = [];

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_apiKeyEnvVar, null);
        lock (_clients)
        {
            foreach (var client in _clients)
            {
                try { client.Dispose(); } catch { }
            }
            _clients.Clear();
        }
        _factory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── 1. full round trip over HTTP ────────────────────────────────────────

    [Fact]
    public async Task PhaseHttp_RoundTrip_PollDownloadStreamComplete()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await RegisterExecutorAsync(cts.Token);
        var broker = _factory.Services.GetRequiredService<ExecutorPhaseBroker>();
        var request = NewRequest(WorkItemId.New(), "work", 0);
        var repoDir = NewRepoDir("stage-me.txt", "stage-in content\n");

        var seen = new List<ExecutorStreamChunk>();
        var stageInTar = await NewStageInTarAsync(repoDir, cts.Token);
        var dispatch = broker.DispatchAsync("exec-1", request, stageInTar, "testroot",
            (chunk, _) => { lock (seen) seen.Add(chunk); return Task.CompletedTask; }, cts.Token);

        var channel = NewChannel();
        var pending = await channel.PollAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.NotNull(pending);
        Assert.Equal(request.WorkItemId, pending.Request.WorkItemId);
        Assert.Equal("work", pending.Request.Phase);
        Assert.Equal("testroot", pending.RepoRootName);

        var downloaded = Path.Combine(_root, "downloaded.tar");
        await channel.DownloadStageInAsync(pending.DispatchKey, downloaded, cts.Token);
        var extracted = Path.Combine(_root, "extracted");
        await ExecutorTarTransfer.ExtractTarToDirectoryAsync(downloaded, extracted, 10_000_000, 1000, "exec-1", cts.Token);
        Assert.Equal("stage-in content\n", await File.ReadAllTextAsync(
            Path.Combine(extracted, "testroot", "stage-me.txt"), cts.Token));

        await channel.PostChunkAsync(pending.DispatchKey, new ExecutorStreamChunk { Sequence = 0, Data = "hello " }, cts.Token);
        await channel.PostChunkAsync(pending.DispatchKey, new ExecutorStreamChunk { Sequence = 1, Data = "world" }, cts.Token);

        var stageOutDir = NewRepoDir("result.txt", "phase result\n");
        var stageOutTar = Path.Combine(_root, "stageout.tar");
        await ExecutorTarTransfer.WriteDirectoryToTarAsync(stageOutDir, "testroot", stageOutTar, 10_000_000, "exec-1", cts.Token);
        var expected = new ExecutorPhaseResult
        {
            Outcome = ExecutorPhaseOutcome.Succeeded,
            CommitSha = new string('a', 40),
            Findings = ["http finding"],
            Usage = new ExecutorPhaseUsage(7, 3, 0.0005m),
        };
        await channel.CompleteAsync(pending.DispatchKey, expected, stageOutTar, cts.Token);

        var completed = await dispatch.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
        Assert.Equal(expected.Outcome, completed.Result.Outcome);
        Assert.Equal(expected.CommitSha, completed.Result.CommitSha);
        Assert.Equal(expected.Findings, completed.Result.Findings);
        Assert.Equal(expected.Usage, completed.Result.Usage);
        lock (seen)
            Assert.Equal(["hello ", "world"], seen.Select(c => c.Data).ToList());

        // The dispatch is gone: nothing left to poll.
        Assert.Null(await channel.PollAsync(TimeSpan.Zero, cts.Token));
    }

    // ── 2. caller binding ───────────────────────────────────────────────────

    [Fact]
    public async Task PhaseHttp_Unauthenticated_IsUnauthorized()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("executors/exec-1/phase/next?waitSeconds=0");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PhaseHttp_UnboundToken_IsForbidden()
    {
        using var client = ClientWith(UnboundToken);
        var response = await client.GetAsync("executors/exec-1/phase/next?waitSeconds=0");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PhaseHttp_WrongHostToken_IsForbidden()
    {
        using var client = ClientWith(OtherHostToken);
        var response = await client.GetAsync("executors/exec-1/phase/next?waitSeconds=0");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── 3. bounds at ingress ────────────────────────────────────────────────

    [Fact]
    public async Task PhaseHttp_OversizedChunk_IsRejected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await RegisterExecutorAsync(cts.Token);
        var broker = _factory.Services.GetRequiredService<ExecutorPhaseBroker>();
        var dispatch = broker.DispatchAsync("exec-1", NewRequest(WorkItemId.New(), "work", 0),
            await NewStageInTarAsync(NewRepoDir("f.txt", "x\n"), cts.Token), "testroot", onChunk: null, cts.Token);
        var channel = NewChannel();
        var pending = await channel.PollAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.NotNull(pending);

        var tooBig = await Assert.ThrowsAsync<ExecutorPhaseTransportException>(() =>
            channel.PostChunkAsync(pending.DispatchKey,
                new ExecutorStreamChunk { Sequence = 0, Data = new string('x', 300_000) }, cts.Token));
        Assert.Contains("chunk", tooBig.Message, StringComparison.OrdinalIgnoreCase);

        await channel.FailAsync(pending.DispatchKey, "cleanup", cts.Token);
        await Assert.ThrowsAsync<ExecutorPhaseTransportException>(() =>
            dispatch.WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task PhaseHttp_OversizedStageOut_IsAPhaseFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await RegisterExecutorAsync(cts.Token);
        var broker = _factory.Services.GetRequiredService<ExecutorPhaseBroker>();
        // The factory caps archives at 256 KiB: 320 KiB of incompressible
        // bytes must fail as a phase failure (reachable host, hostile
        // payload), never as a transport failure.
        var dispatch = broker.DispatchAsync("exec-1", NewRequest(WorkItemId.New(), "work", 0),
            await NewStageInTarAsync(NewRepoDir("f.txt", "x\n"), cts.Token), "testroot", onChunk: null, cts.Token);
        var channel = NewChannel();
        var pending = await channel.PollAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.NotNull(pending);

        var big = Path.Combine(_root, "big.tar");
        var random = new Random(7);
        var bytes = new byte[320 * 1024];
        random.NextBytes(bytes);
        await File.WriteAllBytesAsync(big, bytes, cts.Token);
        var result = new ExecutorPhaseResult
        {
            Outcome = ExecutorPhaseOutcome.Succeeded,
            Findings = [],
            Usage = new ExecutorPhaseUsage(1, 1, 0m),
        };
        var upload = await Assert.ThrowsAsync<ExecutorPhaseException>(() =>
            channel.CompleteAsync(pending.DispatchKey, result, big, cts.Token));
        Assert.Contains("cap", upload.Message, StringComparison.OrdinalIgnoreCase);

        var phaseFailure = await Assert.ThrowsAsync<ExecutorPhaseException>(() =>
            dispatch.WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
        Assert.Contains("cap", phaseFailure.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 4. ingress ceilings and upload ownership ────────────────────────────

    [Fact]
    public async Task PhaseHttp_HugeFailMessage_IsRejectedAtIngress()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await RegisterExecutorAsync(cts.Token);
        var broker = _factory.Services.GetRequiredService<ExecutorPhaseBroker>();
        var dispatch = broker.DispatchAsync("exec-1", NewRequest(WorkItemId.New(), "work", 0),
            await NewStageInTarAsync(NewRepoDir("f.txt", "x\n"), cts.Token), "testroot", onChunk: null, cts.Token);
        var channel = NewChannel();
        var pending = await channel.PollAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.NotNull(pending);

        // 100 KiB far exceeds the fail-route ingress ceiling (~24 KiB) yet is
        // far below Kestrel's default body limit: an unbounded route would
        // buffer it fully and truncate to success, while the bounded ingress
        // rejects it before buffering.
        using var raw = ClientWith(ExecutorToken);
        var body = System.Text.Json.JsonSerializer.Serialize(
            new { dispatchKey = pending.DispatchKey, message = new string('y', 100 * 1024) });
        using var response = await raw.PostAsync("executors/exec-1/phase/fail",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The dispatch is still running: clean up through the normal path.
        await channel.FailAsync(pending.DispatchKey, "cleanup", cts.Token);
        await Assert.ThrowsAsync<ExecutorPhaseTransportException>(() =>
            dispatch.WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task PhaseHttp_HugeChunkBody_IsRejectedAtIngress()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await RegisterExecutorAsync(cts.Token);
        var broker = _factory.Services.GetRequiredService<ExecutorPhaseBroker>();
        var dispatch = broker.DispatchAsync("exec-1", NewRequest(WorkItemId.New(), "work", 0),
            await NewStageInTarAsync(NewRepoDir("f.txt", "x\n"), cts.Token), "testroot", onChunk: null, cts.Token);
        var channel = NewChannel();
        var pending = await channel.PollAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.NotNull(pending);

        // 2 MiB of chunk data exceeds the chunk-route ingress ceiling
        // (~272 KiB) yet is far below Kestrel's default body limit, so only
        // the bounded read rejects it before buffering.
        using var raw = ClientWith(ExecutorToken);
        var body = System.Text.Json.JsonSerializer.Serialize(
            new { dispatchKey = pending.DispatchKey, sequence = 0, data = new string('x', 2 * 1024 * 1024) });
        using var response = await raw.PostAsync("executors/exec-1/phase/chunk",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("chunk", await response.Content.ReadAsStringAsync(cts.Token), StringComparison.OrdinalIgnoreCase);

        await channel.FailAsync(pending.DispatchKey, "cleanup", cts.Token);
        await Assert.ThrowsAsync<ExecutorPhaseTransportException>(() =>
            dispatch.WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task PhaseHttp_StageOutUpload_ForUnknownKey_IsRejectedBeforeDiskWrite()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await RegisterExecutorAsync(cts.Token);
        var uploads = _factory.Services.GetRequiredService<ExecutorPhaseStageOutUploads>();
        Assert.Equal(0, uploads.CountForHost("exec-1"));

        // No dispatch owns this key: the upload must be rejected before a
        // single byte reaches orchestrator temp disk, leaving no registry entry.
        using var raw = ClientWith(ExecutorToken);
        var content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await raw.PostAsync(
            "executors/exec-1/phase/stageout?dispatchKey=" + Uri.EscapeDataString("no-such-dispatch"),
            content, cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, uploads.CountForHost("exec-1"));
    }

    [Fact]
    public void StageOutUploads_PerHostCap_RejectsNewKeysButAllowsReplacement()
    {
        using var uploads = new ExecutorPhaseStageOutUploads();
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var a = Path.Combine(_root, "cap-a.tar");
        var b = Path.Combine(_root, "cap-b.tar");
        var c = Path.Combine(_root, "cap-c.tar");
        var a2 = Path.Combine(_root, "cap-a2.tar");
        var foreign = Path.Combine(_root, "cap-foreign.tar");
        File.WriteAllBytes(a, [1]);
        File.WriteAllBytes(b, [2]);
        File.WriteAllBytes(c, [3]);
        File.WriteAllBytes(a2, [4]);
        File.WriteAllBytes(foreign, [5]);

        uploads.Put("exec-1", "key-a", a, expiry, maxPerHost: 2);
        uploads.Put("exec-1", "key-b", b, expiry, maxPerHost: 2);
        var over = Assert.Throws<ExecutorPhaseTransportException>(() =>
            uploads.Put("exec-1", "key-c", c, expiry, maxPerHost: 2));
        Assert.Contains("Too many", over.Message, StringComparison.Ordinal);

        // Replacing the same (host, key) never counts against the cap, and
        // deletes the orphaned file.
        uploads.Put("exec-1", "key-a", a2, expiry, maxPerHost: 2);
        Assert.False(File.Exists(a));
        Assert.Equal(2, uploads.CountForHost("exec-1"));

        // Another host's uploads are counted separately.
        uploads.Put("exec-2", "key-c", foreign, expiry, maxPerHost: 2);
        Assert.Equal(1, uploads.CountForHost("exec-2"));
    }

    [Fact]
    public async Task Broker_HasRunningDispatch_TracksPollOwnership()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = new ExecutorPhaseDispatchOptions
        {
            RemotePhaseLeaseTimeout = TimeSpan.FromMinutes(5),
            RemotePhasePollTimeout = TimeSpan.FromSeconds(5),
        };
        using var broker = new ExecutorPhaseBroker(() => options);
        var tar = Path.Combine(_root, "running-stagein.tar");
        await File.WriteAllBytesAsync(tar, [9], cts.Token);
        var request = NewRequest(WorkItemId.New(), "work", 0);
        var dispatch = broker.DispatchAsync("exec-1", request, tar, "testroot", onChunk: null, cts.Token);
        var key = ExecutorPhaseProxy.BuildDispatchKey(request);

        // Pending but not yet polled: no running dispatch to upload against.
        Assert.False(broker.HasRunningDispatch("exec-1", key));
        var pending = await broker.PollAsync("exec-1", TimeSpan.FromSeconds(10), cts.Token);
        Assert.NotNull(pending);
        Assert.True(broker.HasRunningDispatch("exec-1", key));
        Assert.False(broker.HasRunningDispatch("exec-1", "unknown-key"));
        Assert.False(broker.HasRunningDispatch("exec-2", key));

        await broker.FailAsync("exec-1", key, "cleanup", cts.Token);
        Assert.False(broker.HasRunningDispatch("exec-1", key));
        await Assert.ThrowsAsync<ExecutorPhaseTransportException>(() =>
            dispatch.WaitAsync(TimeSpan.FromSeconds(10), cts.Token));
    }

    // ── 5. fail paths ───────────────────────────────────────────────────────

    [Fact]
    public async Task PhaseHttp_Fail_SurfacesTransportFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await RegisterExecutorAsync(cts.Token);
        var broker = _factory.Services.GetRequiredService<ExecutorPhaseBroker>();
        var dispatch = broker.DispatchAsync("exec-1", NewRequest(WorkItemId.New(), "work", 0),
            await NewStageInTarAsync(NewRepoDir("f.txt", "x\n"), cts.Token), "testroot", onChunk: null, cts.Token);
        var channel = NewChannel();
        var pending = await channel.PollAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.NotNull(pending);

        await channel.FailAsync(pending.DispatchKey, "simulated sandbox loss", cts.Token);
        var thrown = await Assert.ThrowsAsync<ExecutorPhaseTransportException>(() =>
            dispatch.WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
        Assert.Equal("exec-1", thrown.HostId);
        Assert.Contains("simulated sandbox loss", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhaseHttp_FailPhase_SurfacesPhaseFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await RegisterExecutorAsync(cts.Token);
        var broker = _factory.Services.GetRequiredService<ExecutorPhaseBroker>();
        var dispatch = broker.DispatchAsync("exec-1", NewRequest(WorkItemId.New(), "work", 0),
            await NewStageInTarAsync(NewRepoDir("f.txt", "x\n"), cts.Token), "testroot", onChunk: null, cts.Token);
        var channel = NewChannel();
        var pending = await channel.PollAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.NotNull(pending);

        await channel.FailPhaseAsync(pending.DispatchKey, "unacceptable payload", cts.Token);
        var thrown = await Assert.ThrowsAsync<ExecutorPhaseException>(() =>
            dispatch.WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
        Assert.Contains("unacceptable payload", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhaseHttp_CompleteWithoutUpload_IsRejectedAsTransport()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await RegisterExecutorAsync(cts.Token);
        var broker = _factory.Services.GetRequiredService<ExecutorPhaseBroker>();
        var dispatch = broker.DispatchAsync("exec-1", NewRequest(WorkItemId.New(), "work", 0),
            await NewStageInTarAsync(NewRepoDir("f.txt", "x\n"), cts.Token), "testroot", onChunk: null, cts.Token);
        var channel = NewChannel();
        var pending = await channel.PollAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.NotNull(pending);

        // Completing with no stage-out upload is a protocol violation (the
        // host skipped a step), not a phase verdict — a transport failure.
        var result = new ExecutorPhaseResult
        {
            Outcome = ExecutorPhaseOutcome.Succeeded,
            Findings = [],
            Usage = new ExecutorPhaseUsage(1, 1, 0m),
        };
        await Assert.ThrowsAsync<ExecutorPhaseTransportException>(() =>
            channel.CompleteAsync(pending.DispatchKey, result, Path.Combine(_root, "missing.tar"), cts.Token));

        await channel.FailAsync(pending.DispatchKey, "cleanup", cts.Token);
        await Assert.ThrowsAsync<ExecutorPhaseTransportException>(() =>
            dispatch.WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private static ExecutorPhaseRequest NewRequest(WorkItemId item, string phase, int attempt) =>
        new() { WorkItemId = item.ToString(), Phase = phase, Attempt = attempt, RepositoryId = item.ToString(), PayloadJson = "{}" };

    private string NewRepoDir(string fileName, string content)
    {
        var dir = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        return dir;
    }

    private async Task<string> NewStageInTarAsync(string repoDir, CancellationToken ct)
    {
        var tar = Path.Combine(_root, "stagein-" + Guid.NewGuid().ToString("N") + ".tar");
        await ExecutorTarTransfer.WriteDirectoryToTarAsync(repoDir, "testroot", tar, 10_000_000, "exec-1", ct);
        return tar;
    }

    private async Task RegisterExecutorAsync(CancellationToken ct)
    {
        var registry = _factory.Services.GetRequiredService<IWorkerRegistry>();
        var now = DateTimeOffset.UtcNow;
        await registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = ExecutorRegistration.WorkerIdFor("exec-1"),
            HostName = "exec-1",
            ProcessId = 4242,
            StartedAt = now,
            LastHeartbeatAt = now,
            ExecutorHostId = "exec-1",
            MaxConcurrentSandboxes = 4,
            ExecutorNetworkProfiles = [],
            ExecutorCredentials = [],
            ExecutorCapabilities = [],
            Cordoned = false,
            Healthy = true,
        }, ct);
    }

    private HttpExecutorPhaseChannel NewChannel()
    {
        Environment.SetEnvironmentVariable(_apiKeyEnvVar, ExecutorToken);
        var client = _factory.CreateClient();
        lock (_clients) _clients.Add(client);
        var execOptions = new ExecutorOptions
        {
            HostId = "exec-1",
            OrchestratorBaseUrl = "http://localhost/",
            ApiKeyEnvVar = _apiKeyEnvVar,
            RequestTimeout = TimeSpan.FromSeconds(20),
        };
        return new HttpExecutorPhaseChannel(
            client,
            "exec-1",
            () => execOptions,
            _factory.Services.GetRequiredService<Func<ExecutorPhaseDispatchOptions>>());
    }

    private HttpClient ClientWith(string bearer)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private sealed class ExecutorPhaseHttpFactory : WebApplicationFactory<Program>
    {
        private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-phase-http-");
        private string _dbPath => _scratch.DbPath("phase-http.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitHubAppStorePath"] = Path.Combine(_scratch.DirectoryPath, "github-apps"),
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(_scratch.DirectoryPath, "test-git"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(_scratch.DirectoryPath, "test-log.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_scratch.DirectoryPath, "test-audit.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(_scratch.DirectoryPath, "test-agent-streams"),
                    ["CodeyBox:ExecutorPhaseDispatch:StageOutMaxArchiveBytes"] = (256 * 1024).ToString(),
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ApiKeyState>();
                services.AddSingleton(new ApiKeyState(Token: null, Disabled: false, Clients:
                [
                    new ResolvedApiClient(
                        "exec-1-client",
                        ExecutorToken,
                        new WorkInitiator { Issuer = "test", Subject = "exec-1", DisplayName = "exec-1" },
                        CanDelegateInitiator: false,
                        ExecutorHostId: "exec-1"),
                    new ResolvedApiClient(
                        "exec-2-client",
                        OtherHostToken,
                        new WorkInitiator { Issuer = "test", Subject = "exec-2", DisplayName = "exec-2" },
                        CanDelegateInitiator: false,
                        ExecutorHostId: "exec-2"),
                    new ResolvedApiClient(
                        "unbound-client",
                        UnboundToken,
                        new WorkInitiator { Issuer = "test", Subject = "service", DisplayName = "service" },
                        CanDelegateInitiator: false,
                        ExecutorHostId: null),
                ]));
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { File.Delete(_dbPath); } catch { }
                TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
                _scratch.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
