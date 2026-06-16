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
    public async Task AcpTurn_TransportUnavailableAfterSuccessfulTurn_DoesNotPassAcpIdToPrint()
    {
        // Regression: previously SendTurnAsync built turnRequest with the
        // captured ACP session id BEFORE the transport call, and reused the
        // same turnRequest in the post-degrade retry. The print transport
        // then received the ACP UUID as --resume, which claude --print does
        // not know — the second turn would silently fail. The fix rebuilds
        // turnRequest after DegradeToPrintAsync clears CapturedSessionId.
        var sandbox = new RecordingSandbox(StreamJsonOk("cli-fallback-2"));
        var fakeAcp = new FakeAcpTransport(
            new FakeAcpResult("acp-cont-id", "first ok"),
            new FakeAcpResult(
                SessionId: "ignored",
                AssistantText: "",
                ThrowUnavailable: new AcpTransportUnavailableException("bridge died mid-session")));
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            options: new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Acp },
            acpTransport: fakeAcp);

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        var first = await worker.SendTurnAsync(handle, "first");
        Assert.True(first.Success);
        // ACP id was captured.
        Assert.Equal("acp-cont-id",
            worker.SnapshotPersistedHandle(handle).Metadata![ClaudeSessionWorker.CliSessionIdMetadataKey]);

        // Second turn: ACP throws unavailable. Worker degrades and retries via
        // the print transport. The print invocation MUST NOT receive
        // --resume acp-cont-id (claude --print does not know that id).
        var second = await worker.SendTurnAsync(handle, "second");
        Assert.True(second.Success);

        Assert.Single(sandbox.AllClaudeExecs);
        var argv = sandbox.LastClaudeExec!.Argv.ToList();
        Assert.Contains("--print", argv);
        Assert.DoesNotContain("--resume", argv);
        Assert.DoesNotContain("acp-cont-id", argv);

        // Persisted handle reflects the degrade. The print transport captured
        // its own CLI session id ("cli-fallback-2") on the retry — the stale
        // ACP id ("acp-cont-id") must not survive on the snapshot.
        var snapshot = worker.SnapshotPersistedHandle(handle);
        Assert.Equal("print", snapshot.Metadata![ClaudeSessionWorker.TransportMetadataKey]);
        Assert.Equal("true", snapshot.Metadata[ClaudeSessionWorker.AcpFallbackToPrintMetadataKey]);
        if (snapshot.Metadata.TryGetValue(ClaudeSessionWorker.CliSessionIdMetadataKey, out var persistedId))
            Assert.NotEqual("acp-cont-id", persistedId);
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

    // ── OpenSessionAsync overload — overrides actually fire at open time ─────

    [Fact]
    public async Task OpenSessionAsync_WithProjectAndMember_StampsScopeMetadata_AndAppliesMemberOverride()
    {
        // Per-class-member override flips the resolved transport when the
        // operator supplied the scope at open time. The new overload also
        // stamps both keys onto the handle so reattach observes the same
        // scope after restart.
        var sandbox = new RecordingSandbox(StreamJsonOk("cli-scope"));
        var fakeAcp = new FakeAcpTransport(new FakeAcpResult("acp-scope", "ok"));
        var opts = new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Print };
        opts.TransportOverridesByAgentClassMember["claude-fast"] = ClaudeSessionTransport.Acp;
        opts.TransportOverridesByProject["proj-A"] = ClaudeSessionTransport.Print;
        var worker = new ClaudeSessionWorker(BuildRunner(), options: opts, acpTransport: fakeAcp);

        var handle = await worker.OpenSessionAsync(
            sandbox,
            workingDirectory: "/work",
            credential: null,
            modelId: null,
            reasoningMode: null,
            projectId: "proj-A",
            agentClassMember: "claude-fast");

        // Member override wins → ACP opens for this session.
        Assert.Equal(1, fakeAcp.OpenCount);
        Assert.Equal("acp", handle.Metadata![ClaudeSessionWorker.TransportMetadataKey]);
        Assert.Equal("proj-A", handle.Metadata[ClaudeSessionWorker.ProjectIdMetadataKey]);
        Assert.Equal("claude-fast", handle.Metadata[ClaudeSessionWorker.AgentClassMemberMetadataKey]);
    }

    [Fact]
    public async Task OpenSessionAsync_PerProjectOverride_AppliesWithoutMemberHint()
    {
        var sandbox = new RecordingSandbox(StreamJsonOk("cli-proj"));
        var fakeAcp = new FakeAcpTransport(new FakeAcpResult("acp-proj", "ok"));
        var opts = new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Print };
        opts.TransportOverridesByProject["proj-acp"] = ClaudeSessionTransport.Acp;
        var worker = new ClaudeSessionWorker(BuildRunner(), options: opts, acpTransport: fakeAcp);

        var handle = await worker.OpenSessionAsync(
            sandbox,
            workingDirectory: "/work",
            credential: null,
            modelId: null,
            reasoningMode: null,
            projectId: "proj-acp",
            agentClassMember: null);

        Assert.Equal(1, fakeAcp.OpenCount);
        Assert.Equal("acp", handle.Metadata![ClaudeSessionWorker.TransportMetadataKey]);
        Assert.Equal("proj-acp", handle.Metadata[ClaudeSessionWorker.ProjectIdMetadataKey]);
        Assert.False(handle.Metadata.ContainsKey(ClaudeSessionWorker.AgentClassMemberMetadataKey));
    }

    [Fact]
    public async Task OpenSessionAsync_NoScope_FallsThroughToGlobalDefault()
    {
        // No project / member supplied → global default wins (print).
        var sandbox = new RecordingSandbox(StreamJsonOk("cli-noscope"));
        var fakeAcp = new FakeAcpTransport();
        var opts = new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Print };
        opts.TransportOverridesByAgentClassMember["claude-fast"] = ClaudeSessionTransport.Acp;
        var worker = new ClaudeSessionWorker(BuildRunner(), options: opts, acpTransport: fakeAcp);

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");

        Assert.Equal(0, fakeAcp.OpenCount);
        Assert.Equal("print", handle.Metadata![ClaudeSessionWorker.TransportMetadataKey]);
        Assert.False(handle.Metadata.ContainsKey(ClaudeSessionWorker.AgentClassMemberMetadataKey));
        Assert.False(handle.Metadata.ContainsKey(ClaudeSessionWorker.ProjectIdMetadataKey));
    }

    // ── Post-restart reattach honours persisted transport metadata ────────────

    [Fact]
    public async Task Reattach_PersistedAcpFallbackMetadata_InheritsPrintTransport()
    {
        // Simulates orchestrator restart: a handle whose prior run degraded
        // ACP→print carries AcpFallbackToPrintMetadataKey=true. On reattach
        // ResolveStateAsync must inherit the print transport rather than
        // retrying ACP on every turn. The ACP transport stays registered to
        // prove the worker explicitly skips it when the metadata says
        // "degraded".
        var sandbox = new RecordingSandbox(StreamJsonOk("cli-reattach-degraded"));
        var fakeAcp = new FakeAcpTransport(new FakeAcpResult("acp-should-not-be-used", "nope"));
        var opts = new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Acp };
        var degradedCalls = new List<string>();
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            sandboxReattacher: (_, _) => Task.FromResult<ISandbox>(sandbox),
            options: opts,
            acpTransport: fakeAcp,
            onTransportDegraded: (_, reason) => degradedCalls.Add(reason));

        var persisted = new AgentSessionHandle(
            AgentKind.Claude,
            "claude-session-reattach-degraded",
            new AgentSessionSandboxRef("vm-reattach-degraded"),
            "/work",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClaudeSessionWorker.AcpFallbackToPrintMetadataKey] = "true",
            });

        var result = await worker.SendTurnAsync(persisted, "after restart in degraded mode");
        Assert.True(result.Success);

        // ACP transport was never opened.
        Assert.Equal(0, fakeAcp.OpenCount);
        Assert.Empty(degradedCalls);
        // Print transport ran exactly once.
        Assert.Single(sandbox.AllClaudeExecs);
        Assert.Contains("--print", sandbox.LastClaudeExec!.Argv);

        // Persisted handle continues to advertise degraded state.
        var snapshot = worker.SnapshotPersistedHandle(persisted);
        Assert.Equal("print", snapshot.Metadata![ClaudeSessionWorker.TransportMetadataKey]);
        Assert.Equal("true", snapshot.Metadata[ClaudeSessionWorker.AcpFallbackToPrintMetadataKey]);
    }

    [Fact]
    public async Task Reattach_PersistedAgentClassMemberMetadata_ReResolvesViaOverride()
    {
        // Reattach replays per-class-member override resolution against the
        // persisted metadata. Global default is Print; the persisted member
        // points at the Acp override, so the reattach opens the ACP transport.
        var sandbox = new RecordingSandbox(StreamJsonOk("cli-should-not-be-used"));
        var fakeAcp = new FakeAcpTransport(new FakeAcpResult("acp-reattach", "ok"));
        var opts = new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Print };
        opts.TransportOverridesByAgentClassMember["claude-fast"] = ClaudeSessionTransport.Acp;
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            sandboxReattacher: (_, _) => Task.FromResult<ISandbox>(sandbox),
            options: opts,
            acpTransport: fakeAcp);

        var persisted = new AgentSessionHandle(
            AgentKind.Claude,
            "claude-session-reattach-member",
            new AgentSessionSandboxRef("vm-reattach-member"),
            "/work",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClaudeSessionWorker.AgentClassMemberMetadataKey] = "claude-fast",
            });

        var result = await worker.SendTurnAsync(persisted, "after restart");
        Assert.True(result.Success);

        // ACP transport was used (per-member override won); print never ran.
        Assert.Equal(1, fakeAcp.OpenCount);
        Assert.Empty(sandbox.AllClaudeExecs);

        var snapshot = worker.SnapshotPersistedHandle(persisted);
        Assert.Equal("acp", snapshot.Metadata![ClaudeSessionWorker.TransportMetadataKey]);
    }

    [Fact]
    public async Task Reattach_PersistedProjectMetadata_ReResolvesViaProjectOverride()
    {
        // Same as the per-member case but for ProjectIdMetadataKey. The
        // persisted project id picks the Acp override even though the global
        // default is Print.
        var sandbox = new RecordingSandbox(StreamJsonOk("cli-should-not-be-used"));
        var fakeAcp = new FakeAcpTransport(new FakeAcpResult("acp-reattach-proj", "ok"));
        var opts = new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Print };
        opts.TransportOverridesByProject["proj-acp"] = ClaudeSessionTransport.Acp;
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            sandboxReattacher: (_, _) => Task.FromResult<ISandbox>(sandbox),
            options: opts,
            acpTransport: fakeAcp);

        var persisted = new AgentSessionHandle(
            AgentKind.Claude,
            "claude-session-reattach-proj",
            new AgentSessionSandboxRef("vm-reattach-proj"),
            "/work",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClaudeSessionWorker.ProjectIdMetadataKey] = "proj-acp",
            });

        var result = await worker.SendTurnAsync(persisted, "after restart");
        Assert.True(result.Success);

        Assert.Equal(1, fakeAcp.OpenCount);
        Assert.Empty(sandbox.AllClaudeExecs);
        Assert.Equal("acp",
            worker.SnapshotPersistedHandle(persisted).Metadata![ClaudeSessionWorker.TransportMetadataKey]);
    }

    // ── Real AcpClaudeTransport — bridge materialisation + turn round-trip ───

    [Fact]
    public async Task AcpClaudeTransport_OpenAsync_WritesBridgeScript_ViaBase64Pipe()
    {
        var sandbox = new BridgeSandbox();
        var transport = new AcpClaudeTransport { NodeBinary = "node" };
        var request = new ClaudeTransportOpenRequest(
            sandbox, "/work", Credential: null, ModelId: null, ReasoningMode: null,
            LocalSessionId: "local-1");

        await using var session = await transport.OpenAsync(request, CancellationToken.None);

        var materialise = sandbox.AllExecs.Single();
        Assert.Equal("bash", materialise.Argv[0]);
        Assert.Equal("-c", materialise.Argv[1]);
        Assert.Contains("base64 -d > \"$HOME/.codeybox/claude-acp-bridge.cjs\"", materialise.Argv[2]);
        // The encoded payload is the bridge script verbatim.
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(AcpBridgeScript.Source));
        Assert.Contains(encoded, materialise.Argv[2]);
    }

    [Fact]
    public async Task AcpClaudeTransport_OpenAsync_SandboxFailure_RaisesUnavailable()
    {
        var sandbox = new BridgeSandbox { FailMaterialise = true };
        var transport = new AcpClaudeTransport();
        var request = new ClaudeTransportOpenRequest(
            sandbox, "/work", Credential: null, ModelId: null, ReasoningMode: null,
            LocalSessionId: "local-x");

        await Assert.ThrowsAsync<AcpTransportUnavailableException>(
            () => transport.OpenAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task AcpClaudeTransport_SendTurn_ShipsAcpEnvelopesAsStdin_AndCapturesSessionId()
    {
        // First turn: no resume → bridge sees initialize + session/new + session/prompt.
        // Bridge replies with a session id; transport captures it.
        var sandbox = new BridgeSandbox();
        sandbox.NextBridgeOutput(new[]
        {
            "{\"type\":\"ready\",\"port\":40123}",
            "{\"type\":\"peer_connected\"}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"sessionId\":\"acp-real-1\"}}}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"stopReason\":\"end_turn\",\"usage\":{\"input_tokens\":120,\"output_tokens\":30,\"cache_read_input_tokens\":4500,\"cache_creation_input_tokens\":0}}}}",
            "{\"type\":\"turn_complete\",\"stopReason\":\"end_turn\"}",
        });

        var transport = new AcpClaudeTransport { NodeBinary = "node" };
        var open = new ClaudeTransportOpenRequest(
            sandbox, "/work", Credential: null, ModelId: null, ReasoningMode: null,
            LocalSessionId: "local-real");
        await using var session = await transport.OpenAsync(open, CancellationToken.None);

        var turn = await session.SendTurnAsync(
            new ClaudeTransportTurnRequest("hello", CliResumeSessionId: null, StdoutChunkCallback: null),
            CancellationToken.None);

        Assert.True(turn.Result.Success);
        Assert.Equal("acp-real-1", turn.CapturedCliSessionId);

        var bridgeExec = sandbox.BridgeExecs.Single();
        Assert.Contains("$HOME/.codeybox/claude-acp-bridge.cjs", bridgeExec.Argv[2]);
        Assert.Equal(SandboxAgentOutputTransportPreference.ExecPipe, bridgeExec.AgentOutputTransport);

        // Stdin frames the full envelope sequence: hello → initialize → session/new → session/prompt.
        var stdin = bridgeExec.Stdin!;
        Assert.Contains("\"type\":\"hello\"", stdin);
        Assert.Contains("\"autoApprovePermissions\":true", stdin);
        Assert.Contains("\"autoAnswerQuestions\":true", stdin);
        Assert.Contains("\"method\":\"initialize\"", stdin);
        Assert.Contains("\"method\":\"session/new\"", stdin);
        Assert.Contains("\"method\":\"session/prompt\"", stdin);
        Assert.DoesNotContain("\"method\":\"session/load\"", stdin);
    }

    [Fact]
    public async Task AcpClaudeTransport_ApiTimeoutExtendsBridgeTurnTimeout()
    {
        const int expectedTurnTimeoutSeconds = 1230;
        var sandbox = new BridgeSandbox();
        sandbox.NextBridgeOutput(new[]
        {
            "{\"type\":\"ready\",\"port\":40123}",
            "{\"type\":\"peer_connected\"}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"sessionId\":\"acp-timeout-1\"}}}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"stopReason\":\"end_turn\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}}",
            "{\"type\":\"turn_complete\",\"stopReason\":\"end_turn\"}",
        });
        var snapshot = new AgentNetworkToleranceSnapshot(
            new Dictionary<string, AgentNetworkToleranceOptions?>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = new AgentNetworkToleranceOptions { ApiTimeoutMs = 1_200_000 },
            });
        var transport = new AcpClaudeTransport(snapshot);
        var open = new ClaudeTransportOpenRequest(
            sandbox, "/work", Credential: null, ModelId: null, ReasoningMode: null,
            LocalSessionId: "local-timeout");
        await using var session = await transport.OpenAsync(open, CancellationToken.None);

        var turn = await session.SendTurnAsync(
            new ClaudeTransportTurnRequest("hello", CliResumeSessionId: null, StdoutChunkCallback: null),
            CancellationToken.None);

        Assert.True(turn.Result.Success);
        var bridgeExec = sandbox.BridgeExecs.Single();
        Assert.Equal("1200000", bridgeExec.ExtraEnvironment!["API_TIMEOUT_MS"]);

        using var hello = JsonDocument.Parse(bridgeExec.Stdin!.Split('\n')[0]);
        var root = hello.RootElement;
        Assert.Equal("1200000", root.GetProperty("claudeEnv").GetProperty("API_TIMEOUT_MS").GetString());
        Assert.Equal(expectedTurnTimeoutSeconds, root.GetProperty("turnTimeoutSeconds").GetInt32());
        Assert.True(root.GetProperty("turnTimeoutSeconds").GetInt32() > AcpBridgeScript.TurnTimeoutSeconds);
    }

    [Fact]
    public async Task AcpClaudeTransport_ApiTimeoutUnsetKeepsDefaultBridgeTurnTimeout()
    {
        const int expectedTurnTimeoutSeconds = 900;
        var sandbox = new BridgeSandbox();
        sandbox.NextBridgeOutput(new[]
        {
            "{\"type\":\"ready\",\"port\":40123}",
            "{\"type\":\"peer_connected\"}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"sessionId\":\"acp-timeout-default\"}}}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"stopReason\":\"end_turn\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}}",
            "{\"type\":\"turn_complete\",\"stopReason\":\"end_turn\"}",
        });
        var transport = new AcpClaudeTransport();
        var open = new ClaudeTransportOpenRequest(
            sandbox, "/work", Credential: null, ModelId: null, ReasoningMode: null,
            LocalSessionId: "local-timeout-default");
        await using var session = await transport.OpenAsync(open, CancellationToken.None);

        var turn = await session.SendTurnAsync(
            new ClaudeTransportTurnRequest("hello", CliResumeSessionId: null, StdoutChunkCallback: null),
            CancellationToken.None);

        Assert.True(turn.Result.Success);
        var bridgeExec = sandbox.BridgeExecs.Single();
        Assert.True(bridgeExec.ExtraEnvironment is null || !bridgeExec.ExtraEnvironment.ContainsKey("API_TIMEOUT_MS"));

        using var hello = JsonDocument.Parse(bridgeExec.Stdin!.Split('\n')[0]);
        var root = hello.RootElement;
        Assert.False(root.GetProperty("claudeEnv").TryGetProperty("API_TIMEOUT_MS", out _));
        Assert.Equal(expectedTurnTimeoutSeconds, root.GetProperty("turnTimeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task AcpClaudeTransport_SendTurn_WithResume_IssuesSessionLoad()
    {
        var sandbox = new BridgeSandbox();
        sandbox.NextBridgeOutput(new[]
        {
            "{\"type\":\"peer_connected\"}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"stopReason\":\"end_turn\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}}",
            "{\"type\":\"turn_complete\",\"stopReason\":\"end_turn\"}",
        });

        var transport = new AcpClaudeTransport();
        var open = new ClaudeTransportOpenRequest(
            sandbox, "/work", Credential: null, ModelId: null, ReasoningMode: null,
            LocalSessionId: "local-resume");
        await using var session = await transport.OpenAsync(open, CancellationToken.None);

        var turn = await session.SendTurnAsync(
            new ClaudeTransportTurnRequest("next", CliResumeSessionId: "acp-prior", StdoutChunkCallback: null),
            CancellationToken.None);

        Assert.True(turn.Result.Success);

        // Sanitiser ran (resume turn). The discovery script fingerprint
        // contains the session_root marker the sanitiser is the only producer of.
        Assert.Contains(sandbox.AllExecs, e =>
            e.Argv.Count >= 3 && e.Argv[2].Contains("session_root", StringComparison.Ordinal));

        var bridgeExec = sandbox.BridgeExecs.Single();
        var stdin = bridgeExec.Stdin!;
        Assert.Contains("\"method\":\"session/load\"", stdin);
        Assert.Contains("\"sessionId\":\"acp-prior\"", stdin);
        Assert.DoesNotContain("\"method\":\"session/new\"", stdin);
    }

    [Fact]
    public async Task AcpClaudeTransport_BuildsRealShim_AndMetricsExtractCacheReadFromIt()
    {
        // End-to-end through the worker so the real BuildStreamJsonShimForExtractor
        // is what the metrics sink observes — a JSON property-name regression
        // in the production shim breaks this test, unlike the FakeAcpSession path.
        var sandbox = new BridgeSandbox();
        sandbox.NextBridgeOutput(new[]
        {
            "{\"type\":\"peer_connected\"}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"sessionId\":\"acp-shim-1\"}}}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"acp-shim-1\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"ok\"}}}}}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"stopReason\":\"end_turn\",\"usage\":{\"input_tokens\":250,\"output_tokens\":70,\"cache_read_input_tokens\":9000,\"cache_creation_input_tokens\":0}}}}",
            "{\"type\":\"turn_complete\",\"stopReason\":\"end_turn\"}",
        });

        var sink = new RecordingMetricsSink();
        var realAcp = new AcpClaudeTransport();
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            metricsSink: sink,
            options: new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Acp },
            acpTransport: realAcp);

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        var result = await worker.SendTurnAsync(handle, "first");

        Assert.True(result.Success);
        var rec = sink.Records.Single();
        Assert.Equal("acp", rec.Transport);
        // input_tokens (fresh) + cache_read_input_tokens make up the total input bucket.
        Assert.Equal(250 + 9000, rec.InputTokens);
        Assert.Equal(9000, rec.CachedInputTokens);
        Assert.Equal(250, rec.FreshInputTokens);
        Assert.Equal(70, rec.OutputTokens);
    }

    [Fact]
    public async Task AcpClaudeTransport_CacheCreationTokens_SurfaceOnTurnMetric()
    {
        // ACP cache-warmth verification needs cache_creation read separately from
        // cache_read. Drive the real shim through a turn that reports
        // cache_creation_input_tokens=4321 and assert the metric exposes that
        // exact value on the new field (folded into FreshInputTokens via the
        // billable bucket per the existing extractor contract).
        var sandbox = new BridgeSandbox();
        sandbox.NextBridgeOutput(new[]
        {
            "{\"type\":\"peer_connected\"}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"sessionId\":\"acp-cc-1\"}}}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"acp-cc-1\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"ok\"}}}}}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"stopReason\":\"end_turn\",\"usage\":{\"input_tokens\":120,\"output_tokens\":30,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":4321}}}}",
            "{\"type\":\"turn_complete\",\"stopReason\":\"end_turn\"}",
        });

        var sink = new RecordingMetricsSink();
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            metricsSink: sink,
            options: new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Acp },
            acpTransport: new AcpClaudeTransport());

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        var result = await worker.SendTurnAsync(handle, "first");

        Assert.True(result.Success);
        var rec = sink.Records.Single();
        Assert.Equal(4321, rec.CacheCreationInputTokens);
        Assert.Equal(0, rec.CachedInputTokens);
        // The billable input bucket is fresh (120) + cache_creation (4321).
        Assert.Equal(120 + 4321, rec.FreshInputTokens);
    }

    [Fact]
    public async Task AcpClaudeTransport_FatalEnvelope_SurfacesAsTransportUnavailable()
    {
        var sandbox = new BridgeSandbox();
        sandbox.NextBridgeOutput(new[]
        {
            "{\"type\":\"fatal\",\"message\":\"lockfile_write_failed\",\"detail\":\"EACCES\"}",
        });
        sandbox.BridgeExitCode = 2;

        var transport = new AcpClaudeTransport();
        var open = new ClaudeTransportOpenRequest(
            sandbox, "/work", Credential: null, ModelId: null, ReasoningMode: null,
            LocalSessionId: "local-fatal");
        await using var session = await transport.OpenAsync(open, CancellationToken.None);

        await Assert.ThrowsAsync<AcpTransportUnavailableException>(() =>
            session.SendTurnAsync(
                new ClaudeTransportTurnRequest("hi", CliResumeSessionId: null, StdoutChunkCallback: null),
                CancellationToken.None));
    }

    [Fact]
    public async Task AcpClaudeTransport_ThinkingBlock400_ReSanitisesAndRetries_Succeeds()
    {
        // Reactive recovery path (lines 164-206 of AcpClaudeTransport):
        // turn returns a thinking-block 400 error → sanitiser runs → retry
        // succeeds → final result is success.
        var sandbox = new BridgeSandbox();
        // First bridge call: turn_error carrying the thinking-block signature.
        sandbox.NextBridgeOutput(new[]
        {
            "{\"type\":\"peer_connected\"}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":3,\"error\":{\"code\":-32000,\"message\":\"messages.0.content.0: blocks in the latest assistant message cannot be modified\"}}}",
            "{\"type\":\"turn_error\",\"error\":{\"code\":-32000,\"message\":\"messages.0.content.0: blocks in the latest assistant message cannot be modified\"}}",
        });
        // Second bridge call (after sanitiser): turn completes successfully.
        sandbox.NextBridgeOutput(new[]
        {
            "{\"type\":\"peer_connected\"}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"sessionId\":\"acp-recovered\"}}}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"stopReason\":\"end_turn\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}}",
            "{\"type\":\"turn_complete\",\"stopReason\":\"end_turn\"}",
        });

        var transport = new AcpClaudeTransport();
        var open = new ClaudeTransportOpenRequest(
            sandbox, "/work", Credential: null, ModelId: null, ReasoningMode: null,
            LocalSessionId: "local-retry");
        await using var session = await transport.OpenAsync(open, CancellationToken.None);

        var turn = await session.SendTurnAsync(
            new ClaudeTransportTurnRequest("hi", CliResumeSessionId: null, StdoutChunkCallback: null),
            CancellationToken.None);

        Assert.True(turn.Result.Success);
        Assert.Equal("acp-recovered", turn.CapturedCliSessionId);
        Assert.Contains("post-sanitise retry", turn.Result.Summary);
        Assert.Equal(2, sandbox.BridgeExecs.Count);
    }

    [Fact]
    public async Task AcpClaudeTransport_ThinkingBlock400_SanitiserFails_PropagatesFailure()
    {
        // Sanitiser write-back failure → result keeps the original failure
        // with the sanitiser failure annotation in the summary; we do NOT
        // attempt the retry exec.
        var sandbox = new BridgeSandbox { SanitiserListsFile = "/home/test/.claude/projects/p/s.jsonl", SanitiserFailWrite = true };
        sandbox.NextBridgeOutput(new[]
        {
            "{\"type\":\"peer_connected\"}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":3,\"error\":{\"code\":-32000,\"message\":\"messages.0.content.0: blocks in the latest assistant message cannot be modified\"}}}",
            "{\"type\":\"turn_error\",\"error\":{\"code\":-32000,\"message\":\"messages.0.content.0: blocks in the latest assistant message cannot be modified\"}}",
        });

        var transport = new AcpClaudeTransport();
        var open = new ClaudeTransportOpenRequest(
            sandbox, "/work", Credential: null, ModelId: null, ReasoningMode: null,
            LocalSessionId: "local-saniterr");
        await using var session = await transport.OpenAsync(open, CancellationToken.None);

        var turn = await session.SendTurnAsync(
            new ClaudeTransportTurnRequest("hi", CliResumeSessionId: null, StdoutChunkCallback: null),
            CancellationToken.None);

        Assert.False(turn.Result.Success);
        Assert.Contains("sanitiser failed", turn.Result.Summary);
        Assert.Single(sandbox.BridgeExecs); // No retry was attempted.
    }

    [Fact]
    public void AcpClaudeTransport_BuildStreamJsonShimForExtractor_RoundTripsThroughCostExtractor()
    {
        // Direct check of the production shim shape so a property-name drift
        // (e.g. cache_read_input_tokens → cacheReadInputTokens) is caught
        // even when the worker integration test isn't run.
        var stdout = string.Join("\n", new[]
        {
            "{\"type\":\"peer_connected\"}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"sessionId\":\"acp-extract\"}}}",
            "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"stopReason\":\"end_turn\",\"usage\":{\"input_tokens\":11,\"output_tokens\":22,\"cache_read_input_tokens\":7777,\"cache_creation_input_tokens\":0}}}}",
            "{\"type\":\"turn_complete\",\"stopReason\":\"end_turn\"}",
        });
        var obs = AcpClaudeTransport.AcpSession.ObserveBridgeOutput(stdout);
        var shim = InvokeBuildStreamJsonShim(obs);

        var extractor = new ClaudeCostExtractor();
        var snap = extractor.TryExtract(shim, agentStderr: null);
        Assert.NotNull(snap);
        // AgentCostSnapshot.InputTokens carries the non-cached billable bucket
        // (fresh + cache_creation); cache_read lives on CachedInputTokens.
        Assert.Equal(11, snap!.InputTokens);
        Assert.Equal(7777, snap.CachedInputTokens);
        Assert.Equal(22, snap.OutputTokens);
    }

    private static string InvokeBuildStreamJsonShim(AcpClaudeTransport.BridgeObservation obs)
    {
        var mi = typeof(AcpClaudeTransport.AcpSession).GetMethod(
            "BuildStreamJsonShimForExtractor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("BuildStreamJsonShimForExtractor not found");
        return (string)mi.Invoke(null, new object[] { obs })!;
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
        int OutputTokens = 0,
        AcpTransportUnavailableException? ThrowUnavailable = null);

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
            if (canned.ThrowUnavailable is not null)
                throw canned.ThrowUnavailable;
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

    /// <summary>
    /// Sandbox stub that classifies each ExecAsync by argv shape so the real
    /// <see cref="AcpClaudeTransport"/> can drive a full SendTurn round-trip:
    /// the bridge invocation receives queued envelopes via StdoutChunkCallback,
    /// the bridge-script materialise call succeeds, and the
    /// <see cref="ClaudeSessionSanitizer"/> discovery script returns either an
    /// empty file list (default) or a single file path (for the sanitiser
    /// failure test).
    /// </summary>
    private sealed class BridgeSandbox : ISandbox
    {
        private readonly Queue<string[]> _bridgeOutputs = new();
        public List<SandboxExec> AllExecs { get; } = new();
        public List<SandboxExec> BridgeExecs { get; } = new();
        public string Id { get; } = "vm-" + Guid.NewGuid().ToString("N")[..8];

        public bool FailMaterialise { get; set; }
        public int BridgeExitCode { get; set; }
        public string? SanitiserListsFile { get; set; }
        public bool SanitiserFailWrite { get; set; }

        public void NextBridgeOutput(string[] envelopes) => _bridgeOutputs.Enqueue(envelopes);

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            AllExecs.Add(exec);
            if (exec.Argv.Count == 0)
                return Task.FromResult(new SandboxExecResult(0, "", ""));

            // Bridge materialise: `bash -c "set -eu\n...base64 -d > ..."`.
            if (IsBash(exec, "-c") && exec.Argv[2].Contains("claude-acp-bridge.cjs", StringComparison.Ordinal)
                && exec.Argv[2].Contains("base64 -d", StringComparison.Ordinal))
            {
                if (FailMaterialise)
                    return Task.FromResult(new SandboxExecResult(1, "", "permission denied"));
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }

            // Sanitiser discovery: `bash -c "...session_root..."` with no Stdin.
            if (IsBash(exec, "-c") && exec.Argv[2].Contains("session_root", StringComparison.Ordinal))
            {
                return Task.FromResult(new SandboxExecResult(0, SanitiserListsFile ?? "", ""));
            }

            // Sanitiser read: `bash -c "cat -- \"$1\" ..." _ <path>`.
            if (IsBash(exec, "-c") && exec.Argv[2].Contains("cat -- ", StringComparison.Ordinal) && exec.Stdin is null)
            {
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }

            // Sanitiser write: `bash -c "cat > \"$1\"" _ <path>` with Stdin.
            if (IsBash(exec, "-c") && exec.Argv[2].Contains("cat > ", StringComparison.Ordinal))
            {
                if (SanitiserFailWrite)
                    return Task.FromResult(new SandboxExecResult(1, "", "no space left on device"));
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }

            // Bridge invocation: `bash -lc "exec '<node>' $HOME/.codeybox/...cjs"`.
            if (IsBash(exec, "-lc") && exec.Argv[2].Contains("claude-acp-bridge.cjs", StringComparison.Ordinal))
            {
                BridgeExecs.Add(exec);
                var envelopes = _bridgeOutputs.Count > 0
                    ? _bridgeOutputs.Dequeue()
                    : Array.Empty<string>();
                var stdout = string.Join("\n", envelopes) + (envelopes.Length > 0 ? "\n" : "");
                exec.StdoutChunkCallback?.Invoke(stdout);
                return Task.FromResult(new SandboxExecResult(BridgeExitCode, stdout, ""));
            }

            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        private static bool IsBash(SandboxExec exec, string flag)
            => exec.Argv.Count >= 3 && exec.Argv[0] == "bash" && exec.Argv[1] == flag;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
