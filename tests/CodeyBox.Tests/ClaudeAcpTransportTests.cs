using System.Text;
using System.Text.Json;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the transport-selection layer of <see cref="ClaudeSessionWorker"/>
/// and the <see cref="AcpClaudeTransport"/> observation/fallback machinery. The
/// ACP transport's wire round-trip (bridge script + claude --ide WebSocket) is
/// covered through a fake transport that injects scripted bridge output so the
/// worker integration is deterministic — the actual claude --ide handshake is
/// exercised in production E2E, not from a unit test.
/// </summary>
public sealed class ClaudeAcpTransportTests
{
    // ── Transport-selection / config switching ────────────────────────────────

    [Fact]
    public async Task Transport_DefaultsToPrint_AndUsesClaudePrintArgv()
    {
        var sandbox = new RecordingSandbox(StreamJsonOk("cli-default"));
        var worker = new ClaudeSessionWorker(BuildRunner());

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");

        var argv = sandbox.LastClaudeExec!.Argv.ToList();
        Assert.Contains("--print", argv);
        Assert.Equal("print", handle.Metadata![ClaudeSessionWorker.TransportMetadataKey]);
    }

    [Fact]
    public async Task Transport_AcpSelected_UsesAcpTransportInsteadOfPrint()
    {
        // The fake ACP transport never spawns claude; it returns a canned
        // observation. The print transport is therefore NEVER invoked.
        var sandbox = new RecordingSandbox(StreamJsonOk("cli-acp"));
        var fakeAcp = new FakeAcpTransport(new FakeAcpResult(
            SessionId: "acp-sess-1",
            AssistantText: "ok",
            CacheReadInputTokens: 4000,
            InputTokens: 500,
            OutputTokens: 80));

        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            options: new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Acp },
            acpTransport: fakeAcp);

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");

        // No claude exec ran; ACP was used.
        Assert.Empty(sandbox.AllClaudeExecs);
        Assert.Equal(1, fakeAcp.OpenCount);
        Assert.Equal(1, fakeAcp.Sessions.Single().TurnCount);
        Assert.Equal("acp", handle.Metadata![ClaudeSessionWorker.TransportMetadataKey]);
    }

    [Fact]
    public async Task Transport_HotReloadFromPrintToAcp_AppliesOnNextOpen()
    {
        // Same options instance flips at runtime — the second session opens
        // ACP because the options were mutated between OpenSession calls.
        var sandbox = new RecordingSandbox(StreamJsonOk("cli-hot"));
        var fakeAcp = new FakeAcpTransport(new FakeAcpResult("acp-hot", "ok"));
        var opts = new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Print };
        var worker = new ClaudeSessionWorker(BuildRunner(), options: opts, acpTransport: fakeAcp);

        var h1 = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(h1, "first");
        Assert.Equal(0, fakeAcp.OpenCount);

        opts.Transport = ClaudeSessionTransport.Acp; // hot reload
        var h2 = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(h2, "first");
        Assert.Equal(1, fakeAcp.OpenCount);
    }

    [Fact]
    public async Task Transport_PerAgentClassMemberOverride_BeatsGlobalDefault()
    {
        // Global default is print, but the per-class-member override flips
        // the resolved transport to ACP. The worker reads the metadata hint
        // from the options resolver — verified via ResolveTransport directly
        // since OpenSessionAsync stamps metadata after the resolver runs.
        var opts = new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Print };
        opts.TransportOverridesByAgentClassMember["claude-fast"] = ClaudeSessionTransport.Acp;
        var resolved = opts.ResolveTransport(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ClaudeSessionWorker.AgentClassMemberMetadataKey] = "claude-fast",
        });
        Assert.Equal(ClaudeSessionTransport.Acp, resolved);

        var resolvedDefault = opts.ResolveTransport(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ClaudeSessionWorker.AgentClassMemberMetadataKey] = "claude-slow",
        });
        Assert.Equal(ClaudeSessionTransport.Print, resolvedDefault);
        await Task.CompletedTask;
    }

    [Fact]
    public void Transport_PerProjectOverride_BeatsGlobalButLosesToMember()
    {
        var opts = new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Print };
        opts.TransportOverridesByProject["proj-1"] = ClaudeSessionTransport.Acp;
        opts.TransportOverridesByAgentClassMember["member-1"] = ClaudeSessionTransport.Print;

        var projectOnly = opts.ResolveTransport(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ClaudeSessionWorker.ProjectIdMetadataKey] = "proj-1",
        });
        Assert.Equal(ClaudeSessionTransport.Acp, projectOnly);

        // Member override wins over project override.
        var both = opts.ResolveTransport(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ClaudeSessionWorker.AgentClassMemberMetadataKey] = "member-1",
            [ClaudeSessionWorker.ProjectIdMetadataKey] = "proj-1",
        });
        Assert.Equal(ClaudeSessionTransport.Print, both);
    }

    // ── ACP turn round-trip ────────────────────────────────────────────────────

    [Fact]
    public async Task AcpTurn_CapturesSessionIdForNextTurnContinuation()
    {
        var sandbox = new RecordingSandbox();
        var fakeAcp = new FakeAcpTransport(
            new FakeAcpResult("acp-cont-1", "first done"),
            new FakeAcpResult("acp-cont-1", "second done"));
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            options: new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Acp },
            acpTransport: fakeAcp);

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");
        await worker.SendTurnAsync(handle, "second");

        var turns = fakeAcp.Sessions.Single().TurnRequests;
        Assert.Equal(2, turns.Count);
        Assert.Null(turns[0].CliResumeSessionId);
        Assert.Equal("acp-cont-1", turns[1].CliResumeSessionId);
    }

    [Fact]
    public async Task AcpTurn_EmitsPerTurnMetricsWithAcpTransportTag()
    {
        var sandbox = new RecordingSandbox();
        var fakeAcp = new FakeAcpTransport(new FakeAcpResult(
            SessionId: "acp-metrics",
            AssistantText: "ok",
            InputTokens: 200,
            CacheReadInputTokens: 8000,
            OutputTokens: 50));
        var sink = new RecordingMetricsSink();
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            metricsSink: sink,
            options: new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Acp },
            acpTransport: fakeAcp);

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");

        var rec = sink.Records.Single();
        Assert.Equal("acp", rec.Transport);
        Assert.Equal(200 + 8000, rec.InputTokens);
        Assert.Equal(8000, rec.CachedInputTokens);
        Assert.Equal(200, rec.FreshInputTokens);
    }

    // ── Permission / question auto-handling ───────────────────────────────────

    [Fact]
    public void AcpBridge_ConfigurationDefaults_AutoApproveAndAutoAnswer()
    {
        // The bridge script defaults to auto-approve permissions and
        // auto-answer questions so a headless ACP session never waits on a
        // human. The script is large; verify the explicit defaults are wired
        // (the operator could only opt out by writing the hello envelope with
        // those flags set to false, which CodeyBox never does).
        Assert.Contains("autoApprovePermissions: true", AcpBridgeScript.Source);
        Assert.Contains("autoAnswerQuestions: true", AcpBridgeScript.Source);
        Assert.Contains("<codeybox-question>", AcpBridgeScript.Source);
        Assert.Contains("session/request_permission", AcpBridgeScript.Source);
        Assert.Contains("session/request_input", AcpBridgeScript.Source);
    }

    [Fact]
    public void AcpBridge_ObservesPermissionGrantsAndQuestions()
    {
        var stdout = string.Join("\n", new[]
        {
            "{\"type\":\"bridge_started\",\"pid\":123}",
            "{\"type\":\"ready\",\"port\":40123}",
            "{\"type\":\"peer_connected\"}",
            "{\"type\":\"permission_auto_granted\",\"method\":\"session/request_permission\"}",
            "{\"type\":\"permission_auto_granted\",\"method\":\"session/request_permission\"}",
            "{\"type\":\"question_auto_answered\",\"method\":\"session/request_input\"}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"sessionId\":\"acp-x9\"}}}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"stopReason\":\"end_turn\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}}",
            "{\"type\":\"turn_complete\",\"stopReason\":\"end_turn\"}",
        });
        var obs = AcpClaudeTransport.AcpSession.ObserveBridgeOutput(stdout);
        Assert.Equal(2, obs.PermissionsAutoGranted);
        Assert.Equal(1, obs.QuestionsAutoAnswered);
        Assert.Equal("acp-x9", obs.SessionId);
        Assert.Equal("end_turn", obs.Complete?.StopReason);
        Assert.Null(obs.TurnError);
        Assert.Null(obs.Fatal);
    }

    [Fact]
    public void AcpBridge_FatalEnvelope_SurfacesAsObservationFatal()
    {
        var stdout =
            "{\"type\":\"bridge_started\",\"pid\":1}\n" +
            "{\"type\":\"fatal\",\"message\":\"lockfile_write_failed\",\"detail\":\"EACCES\"}\n";
        var obs = AcpClaudeTransport.AcpSession.ObserveBridgeOutput(stdout);
        Assert.Equal("lockfile_write_failed", obs.Fatal?.Message);
        Assert.Equal("EACCES", obs.Fatal?.Detail);
    }

    // ── Runtime fallback to print ─────────────────────────────────────────────

    [Fact]
    public async Task AcpTurn_TransportUnavailableException_DegradesToPrintAndContinues()
    {
        // First turn opens against an ACP transport whose SendTurn raises
        // unavailable; the worker must transparently swap to the print
        // transport for the SAME turn, stamp the degradation flag on the
        // handle, and the turn must complete successfully.
        var sandbox = new RecordingSandbox(StreamJsonOk("cli-fallback"));
        var degradedCalls = new List<(string SessionId, string Reason)>();
        var failingAcp = new FailingAcpTransport(
            new AcpTransportUnavailableException("bridge could not start"));
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            options: new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Acp },
            acpTransport: failingAcp,
            onTransportDegraded: (id, reason) => degradedCalls.Add((id, reason)));

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        var result = await worker.SendTurnAsync(handle, "first");

        Assert.True(result.Success);
        // Print transport ran — claude exec recorded.
        Assert.Single(sandbox.AllClaudeExecs);
        var argv = sandbox.LastClaudeExec!.Argv.ToList();
        Assert.Contains("--print", argv);

        // Degradation event surfaced exactly once for SendTurn (the open path
        // succeeded so there's no second hook call from that branch).
        Assert.Single(degradedCalls);
        Assert.Contains("bridge could not start", degradedCalls[0].Reason);

        var snapshot = worker.SnapshotPersistedHandle(handle);
        Assert.Equal("print", snapshot.Metadata![ClaudeSessionWorker.TransportMetadataKey]);
        Assert.Equal("true", snapshot.Metadata[ClaudeSessionWorker.AcpFallbackToPrintMetadataKey]);
    }

    [Fact]
    public async Task AcpOpen_FailsWithUnavailable_DegradesToPrintAtOpen()
    {
        var sandbox = new RecordingSandbox(StreamJsonOk("cli-open-fail"));
        var failingAcp = new FailingAcpTransport(
            new AcpTransportUnavailableException("write lockfile failed"));
        failingAcp.FailOnOpen = true;
        var degradedCalls = new List<(string, string)>();
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            options: new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Acp },
            acpTransport: failingAcp,
            onTransportDegraded: (id, reason) => degradedCalls.Add((id, reason)));

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);

        Assert.Equal("print", handle.Metadata![ClaudeSessionWorker.TransportMetadataKey]);
        Assert.Equal("true", handle.Metadata[ClaudeSessionWorker.AcpFallbackToPrintMetadataKey]);
        Assert.Single(degradedCalls);
    }

    [Fact]
    public async Task AcpUnregistered_AcpRequested_DegradesToPrintCleanly()
    {
        // No acpTransport supplied at all — operator config says acp but
        // there's nothing to call. Worker degrades to print and logs.
        var sandbox = new RecordingSandbox(StreamJsonOk("cli-no-acp"));
        var notes = new List<string>();
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            options: new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Acp },
            acpTransport: null,
            onTransportDegraded: (_, reason) => notes.Add(reason));

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");

        Assert.Equal("print", handle.Metadata![ClaudeSessionWorker.TransportMetadataKey]);
        Assert.Single(notes);
    }

    // ── Auditor / worker separate-session isolation ──────────────────────────

    [Fact]
    public async Task AuditorAndWorker_GetSeparateAcpSessions()
    {
        // Auditor and worker each call OpenSessionAsync. Each call results in
        // a separate transport session — the test asserts the fake ACP
        // transport opens twice and each gets its own turn queue.
        var sandbox = new RecordingSandbox();
        var fakeAcp = new FakeAcpTransport(
            new FakeAcpResult("acp-worker-sess", "worker reply"),
            new FakeAcpResult("acp-auditor-sess", "audit reply"));
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            options: new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Acp },
            acpTransport: fakeAcp);

        var wHandle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        var aHandle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(wHandle, "do work");
        await worker.SendTurnAsync(aHandle, "audit");

        Assert.Equal(2, fakeAcp.Sessions.Count);
        Assert.NotEqual(wHandle.SessionId, aHandle.SessionId);
        Assert.NotEqual(fakeAcp.Sessions[0], fakeAcp.Sessions[1]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ClaudeAgentRunner BuildRunner() =>
        new(new AgentDefaultsSnapshot(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = "claude-opus-4-7",
        }));

    private static string StreamJsonOk(string sessionId) =>
        "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + sessionId + "\",\"tools\":[]}\n" +
        "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_01\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-4-7\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}\n" +
        "{\"type\":\"result\",\"subtype\":\"success\",\"duration_ms\":50,\"num_turns\":1,\"result\":\"Done\",\"is_error\":false,\"session_id\":\"" + sessionId + "\",\"total_cost_usd\":0.001,\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}";

    private sealed class RecordingSandbox : ISandbox
    {
        private readonly Queue<string> _claudeStdouts;
        public List<SandboxExec> AllExecs { get; } = new();
        public List<SandboxExec> AllClaudeExecs { get; } = new();
        public SandboxExec? LastClaudeExec => AllClaudeExecs.Count == 0 ? null : AllClaudeExecs[^1];
        public string Id { get; } = "vm-" + Guid.NewGuid().ToString("N")[..8];

        public RecordingSandbox(params string[] claudeStdouts)
            => _claudeStdouts = new Queue<string>(claudeStdouts);

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            AllExecs.Add(exec);
            if (exec.Argv.Count == 0)
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (exec.Argv[0] == "claude")
            {
                AllClaudeExecs.Add(exec);
                var stdout = _claudeStdouts.Count > 0 ? _claudeStdouts.Dequeue() : "";
                exec.StdoutChunkCallback?.Invoke(stdout);
                return Task.FromResult(new SandboxExecResult(0, stdout, ""));
            }
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record FakeAcpResult(
        string SessionId,
        string AssistantText,
        int InputTokens = 0,
        int CacheReadInputTokens = 0,
        int OutputTokens = 0);

    private sealed class FakeAcpTransport : IClaudeTransport
    {
        private readonly Queue<FakeAcpResult> _results;
        public List<FakeAcpSession> Sessions { get; } = new();
        public int OpenCount { get; private set; }

        public FakeAcpTransport(params FakeAcpResult[] results)
            => _results = new Queue<FakeAcpResult>(results);

        public string Name => "acp";
        public ClaudeSessionTransport Transport => ClaudeSessionTransport.Acp;

        public Task<IClaudeTransportSession> OpenAsync(ClaudeTransportOpenRequest request, CancellationToken ct)
        {
            OpenCount++;
            var s = new FakeAcpSession(_results);
            Sessions.Add(s);
            return Task.FromResult<IClaudeTransportSession>(s);
        }
    }

    private sealed class FakeAcpSession : IClaudeTransportSession
    {
        private readonly Queue<FakeAcpResult> _results;
        public List<ClaudeTransportTurnRequest> TurnRequests { get; } = new();
        public int TurnCount => TurnRequests.Count;

        public FakeAcpSession(Queue<FakeAcpResult> results) => _results = results;

        public Task<ClaudeTransportTurnResult> SendTurnAsync(ClaudeTransportTurnRequest request, CancellationToken ct)
        {
            TurnRequests.Add(request);
            var canned = _results.Count > 0 ? _results.Dequeue() : new FakeAcpResult("acp-fallthrough", "");
            var streamJson = BuildShim(canned);
            var result = new AgentResult(true, "ok", streamJson, null);
            return Task.FromResult(new ClaudeTransportTurnResult(result, streamJson, canned.SessionId));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static string BuildShim(FakeAcpResult r)
        {
            return
                "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + r.SessionId + "\",\"tools\":[]}\n" +
                "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_a\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-4-7\",\"content\":[{\"type\":\"text\",\"text\":\"" + r.AssistantText + "\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":" + r.InputTokens + ",\"output_tokens\":" + r.OutputTokens + ",\"cache_read_input_tokens\":" + r.CacheReadInputTokens + ",\"cache_creation_input_tokens\":0}}}\n" +
                "{\"type\":\"result\",\"subtype\":\"success\",\"duration_ms\":50,\"num_turns\":1,\"result\":\"Done\",\"is_error\":false,\"session_id\":\"" + r.SessionId + "\",\"total_cost_usd\":0,\"usage\":{\"input_tokens\":" + r.InputTokens + ",\"output_tokens\":" + r.OutputTokens + ",\"cache_read_input_tokens\":" + r.CacheReadInputTokens + ",\"cache_creation_input_tokens\":0}}";
        }
    }

    private sealed class FailingAcpTransport : IClaudeTransport
    {
        private readonly AcpTransportUnavailableException _ex;
        public bool FailOnOpen { get; set; }

        public FailingAcpTransport(AcpTransportUnavailableException ex) => _ex = ex;
        public string Name => "acp";
        public ClaudeSessionTransport Transport => ClaudeSessionTransport.Acp;

        public Task<IClaudeTransportSession> OpenAsync(ClaudeTransportOpenRequest request, CancellationToken ct)
        {
            if (FailOnOpen) throw _ex;
            return Task.FromResult<IClaudeTransportSession>(new FailingSession(_ex));
        }

        private sealed class FailingSession : IClaudeTransportSession
        {
            private readonly AcpTransportUnavailableException _ex;
            public FailingSession(AcpTransportUnavailableException ex) => _ex = ex;
            public Task<ClaudeTransportTurnResult> SendTurnAsync(ClaudeTransportTurnRequest request, CancellationToken ct)
                => throw _ex;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingMetricsSink : IClaudeSessionMetricsSink
    {
        public List<ClaudeSessionTurnMetrics> Records { get; } = new();
        public void Record(ClaudeSessionTurnMetrics metrics) => Records.Add(metrics);
    }
}
