using System.Collections.Concurrent;
using System.Text.Json;
using CodeyBox.Agents.Claude;
using CodeyBox.Api;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="ClaudeSessionWorker"/>: multi-turn --resume,
/// VM stop between turns, restart-fallback, sanitisation on resume, and
/// per-turn cache_read vs fresh-input metrics.
/// </summary>
public sealed class ClaudeSessionWorkerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Stream-json fixture used as the FIRST turn's stdout. The system/init event
    // is what the worker scrapes for the Claude CLI session id; the trailing
    // result event is what ClaudeCostExtractor uses for cache/input/output
    // accounting (cache_read = 5000, fresh input = 1234, output = 678).
    private static string StreamJsonFirstTurn(string sessionId) =>
        "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + sessionId + "\",\"tools\":[]}\n" +
        "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_01\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-4-7\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":1234,\"output_tokens\":678,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}\n" +
        "{\"type\":\"result\",\"subtype\":\"success\",\"duration_ms\":1000,\"num_turns\":1,\"result\":\"Done\",\"is_error\":false,\"session_id\":\"" + sessionId + "\",\"total_cost_usd\":0.05,\"usage\":{\"input_tokens\":1234,\"output_tokens\":678,\"cache_read_input_tokens\":5000,\"cache_creation_input_tokens\":0}}";

    // SECOND turn: cache_read carries the bulk of the input — the savings the
    // session worker is supposed to expose. fresh input is 100, output is 50.
    private static string StreamJsonSecondTurn(string sessionId) =>
        "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + sessionId + "\",\"tools\":[]}\n" +
        "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_02\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-4-7\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}\n" +
        "{\"type\":\"result\",\"subtype\":\"success\",\"duration_ms\":900,\"num_turns\":1,\"result\":\"Done\",\"is_error\":false,\"session_id\":\"" + sessionId + "\",\"total_cost_usd\":0.005,\"usage\":{\"input_tokens\":100,\"output_tokens\":50,\"cache_read_input_tokens\":12000,\"cache_creation_input_tokens\":0}}";

    private static ClaudeAgentRunner BuildRunner() =>
        new(new AgentDefaultsSnapshot(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = "claude-opus-4-7",
        }));

    // ── Argv shape ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstTurn_OmitsResumeFlag_ButForcesStreamJson()
    {
        var sandbox = new ScriptedSandbox(StreamJsonFirstTurn("cli-sess-1"));
        var worker = new ClaudeSessionWorker(BuildRunner());

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first prompt");

        var argv = sandbox.AgentExec!.Argv.ToList();
        Assert.DoesNotContain("--resume", argv);
        Assert.Contains("--output-format", argv);
        Assert.Contains("stream-json", argv);
        Assert.Contains("--verbose", argv);
        Assert.Contains("--print", argv);
        Assert.Contains("--dangerously-skip-permissions", argv);
    }

    [Fact]
    public async Task SecondTurn_PassesResumeFlagWithCapturedCliSessionId()
    {
        var sandbox = new ScriptedSandbox(
            StreamJsonFirstTurn("cli-sess-42"),
            StreamJsonSecondTurn("cli-sess-42"));
        var worker = new ClaudeSessionWorker(BuildRunner());

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");
        await worker.SendTurnAsync(handle, "second");

        var secondArgv = sandbox.AllAgentExecs[1].Argv.ToList();
        var resumeIdx = secondArgv.IndexOf("--resume");
        Assert.True(resumeIdx >= 0, "Second turn must pass --resume");
        Assert.Equal("cli-sess-42", secondArgv[resumeIdx + 1]);
    }

    [Fact]
    public async Task SecondTurn_PrePopulatesSanitiserBeforeResume()
    {
        // The sanitiser runs preventively before each resume turn (it is the
        // same hook ClaudeAgentRunner.PrepareSandboxAsync invokes when resuming).
        // We assert at least one transcript-discovery bash exec happened
        // before the second --resume invocation.
        var sandbox = new ScriptedSandbox(
            StreamJsonFirstTurn("cli-sess-san"),
            StreamJsonSecondTurn("cli-sess-san"));
        var worker = new ClaudeSessionWorker(BuildRunner());

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");
        await worker.SendTurnAsync(handle, "second");

        // Find the index of the second `claude` exec and confirm at least one
        // bash transcript-listing exec ran before it.
        var execs = sandbox.AllExecs;
        var secondClaudeExecIndex = -1;
        var claudeCount = 0;
        for (var i = 0; i < execs.Count; i++)
        {
            if (execs[i].Argv.Count > 0 && execs[i].Argv[0] == "claude")
            {
                if (++claudeCount == 2)
                {
                    secondClaudeExecIndex = i;
                    break;
                }
            }
        }
        Assert.True(secondClaudeExecIndex > 0);
        var precedingBashIndices = Enumerable.Range(0, secondClaudeExecIndex)
            .Where(i => execs[i].Argv.Count > 0 && execs[i].Argv[0] == "bash")
            .ToList();
        Assert.NotEmpty(precedingBashIndices);
    }

    // ── Multi-turn lifecycle with VM stop ─────────────────────────────────────

    [Fact]
    public async Task SuspendThenResume_StopsAndResumesVmBetweenTurns()
    {
        var sandbox = new PreemptibleScriptedSandbox(
            StreamJsonFirstTurn("cli-sess-stop"),
            StreamJsonSecondTurn("cli-sess-stop"));
        var resumeHookCalled = 0;
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            sandboxResumeHook: (_, _) =>
            {
                Interlocked.Increment(ref resumeHookCalled);
                return Task.CompletedTask;
            },
            sandboxRefFactory: static sandbox => new AgentSessionSandboxRef(
                sandbox.Id,
                HotSwappableSandboxProvider.MultipassProviderId),
            sandboxResumeUnsupportedReason: AgentSessionSandboxRouting.GetMultipassResumeUnsupportedReason);

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");
        await worker.SuspendSessionAsync(handle);
        Assert.Equal(1, sandbox.StopCallCount);

        await worker.ResumeSessionAsync(handle);
        Assert.Equal(1, resumeHookCalled);

        await worker.SendTurnAsync(handle, "second");
        await worker.CloseSessionAsync(handle);

        Assert.Equal(1, sandbox.DisposeCount);
        Assert.Equal(2, sandbox.AllAgentExecs.Count);
        var secondArgv = sandbox.AllAgentExecs[1].Argv.ToList();
        Assert.Equal("cli-sess-stop", secondArgv[secondArgv.IndexOf("--resume") + 1]);
    }

    [Fact]
    public async Task IncusSession_ResumeUnsupported_FailsBeforeStopOrMultipassHook()
    {
        var sandbox = new PreemptibleScriptedSandbox(
            StreamJsonFirstTurn("cli-incus-running"),
            StreamJsonSecondTurn("cli-incus-running"));
        var multipassResumeCalls = 0;
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            sandboxResumeHook: (_, _) =>
            {
                Interlocked.Increment(ref multipassResumeCalls);
                return Task.CompletedTask;
            },
            sandboxRefFactory: static current => new AgentSessionSandboxRef(
                current.Id,
                HotSwappableSandboxProvider.IncusProviderId),
            sandboxResumeUnsupportedReason: AgentSessionSandboxRouting.GetMultipassResumeUnsupportedReason);

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");

        var suspendFailure = await Assert.ThrowsAsync<NotSupportedException>(
            () => worker.SuspendSessionAsync(handle));
        Assert.Contains("Incus", suspendFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, sandbox.StopCallCount);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => worker.ResumeSessionAsync(handle));
        Assert.Equal(0, multipassResumeCalls);

        // The failed suspension made no transport or sandbox state transition;
        // the still-running session remains usable until the caller chooses a
        // fresh-sandbox fallback policy.
        var second = await worker.SendTurnAsync(handle, "second");
        Assert.True(second.Success);
        Assert.Equal(2, sandbox.AllAgentExecs.Count);
        await worker.CloseSessionAsync(handle);
    }

    [Fact]
    public async Task TurnWhileSuspended_ThrowsClearError()
    {
        var sandbox = new PreemptibleScriptedSandbox(StreamJsonFirstTurn("cli-sess-blocked"));
        var worker = new ClaudeSessionWorker(BuildRunner());

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");
        await worker.SuspendSessionAsync(handle);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.SendTurnAsync(handle, "blocked"));
        Assert.Contains("suspended", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Cache observability ───────────────────────────────────────────────────

    [Fact]
    public async Task SendTurn_EmitsPerTurnCacheReadMetrics()
    {
        var sandbox = new ScriptedSandbox(
            StreamJsonFirstTurn("cli-metrics"),
            StreamJsonSecondTurn("cli-metrics"));
        var sink = new RecordingMetricsSink();
        var worker = new ClaudeSessionWorker(BuildRunner(), metricsSink: sink);

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");
        await worker.SendTurnAsync(handle, "second");

        Assert.Equal(2, sink.Records.Count);

        // First turn: input 1234 fresh + 5000 cache_read = 6234 total input;
        // fresh = total - cache_read; output 678; UsedResume = false.
        Assert.Equal("cli-metrics", sink.Records[0].CliSessionId);
        Assert.Equal(0, sink.Records[0].TurnIndex);
        Assert.Equal(1234 + 5000, sink.Records[0].InputTokens);
        Assert.Equal(5000, sink.Records[0].CachedInputTokens);
        Assert.Equal(1234, sink.Records[0].FreshInputTokens);
        Assert.Equal(678, sink.Records[0].OutputTokens);
        Assert.False(sink.Records[0].UsedResume);
        Assert.Equal("claude-opus-4-7", sink.Records[0].ModelId);

        // Second turn: cache_read carries 12000 of the input (the cache benefit),
        // fresh input is just 100; UsedResume = true.
        Assert.Equal(1, sink.Records[1].TurnIndex);
        Assert.Equal(100 + 12000, sink.Records[1].InputTokens);
        Assert.Equal(12000, sink.Records[1].CachedInputTokens);
        Assert.Equal(100, sink.Records[1].FreshInputTokens);
        Assert.Equal(50, sink.Records[1].OutputTokens);
        Assert.True(sink.Records[1].UsedResume);
    }

    [Fact]
    public async Task SendTurn_WithEmitTurnMetricsDisabled_SuppressesMetricEmission()
    {
        // CodeyBox:ClaudeSession:EmitTurnMetrics=false flips the documented
        // off-branch: the sink must receive zero records regardless of how
        // many turns run. Operators rely on this for A/B comparisons against
        // the one-shot path.
        var sandbox = new ScriptedSandbox(
            StreamJsonFirstTurn("cli-quiet"),
            StreamJsonSecondTurn("cli-quiet"));
        var sink = new RecordingMetricsSink();
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            metricsSink: sink,
            options: new ClaudeSessionWorkerOptions { EmitTurnMetrics = false });

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");
        await worker.SendTurnAsync(handle, "second");

        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task SendTurn_WithThrowingMetricsSink_DoesNotBreakTurn()
    {
        var sandbox = new ScriptedSandbox(StreamJsonFirstTurn("cli-throws"));
        var sink = new ThrowingMetricsSink();
        var worker = new ClaudeSessionWorker(BuildRunner(), metricsSink: sink);

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        var result = await worker.SendTurnAsync(handle, "first");

        Assert.True(result.Success);
    }

    [Fact]
    public async Task RefreshSessionCredential_UsesLatestCredentialOnNextTurn()
    {
        var sandbox = new ScriptedSandbox(
            StreamJsonFirstTurn("cli-refresh"),
            StreamJsonSecondTurn("cli-refresh"));
        var worker = new ClaudeSessionWorker(BuildRunner());
        var initial = ClaudeApiCredential("old-token");
        var refreshed = ClaudeApiCredential("new-token");

        var handle = await worker.OpenSessionAsync(sandbox, "/work", initial);
        await worker.SendTurnAsync(handle, "first");
        await worker.RefreshSessionCredentialAsync(handle, refreshed);
        await worker.SendTurnAsync(handle, "second");

        Assert.Equal("old-token", sandbox.AllAgentExecs[0].ExtraEnvironment!["ANTHROPIC_API_KEY"]);
        Assert.Equal("new-token", sandbox.AllAgentExecs[1].ExtraEnvironment!["ANTHROPIC_API_KEY"]);
    }

    // ── Sanitiser & 400 thinking-block recovery ───────────────────────────────

    [Fact]
    public async Task SendTurn_ThinkingBlock400_TriggersSanitiserAndRetry()
    {
        // Stream-json error envelope carrying the thinking-block signature
        // (matches ClaudeQuotaFailureDetector.ContainsThinkingBlockSignature).
        const string thinkingBlock400 =
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"cli-retry\",\"tools\":[]}\n" +
            "{\"type\":\"result\",\"subtype\":\"error\",\"is_error\":true,\"result\":\"messages.0.content.0: blocks in the latest assistant message cannot be modified\"}";
        var retrySuccess = StreamJsonFirstTurn("cli-retry");
        var sandbox = new RetryAfterSanitiseSandbox(thinkingBlock400, retrySuccess);
        var worker = new ClaudeSessionWorker(BuildRunner());

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        var result = await worker.SendTurnAsync(handle, "first");

        Assert.True(result.Success);
        Assert.Equal(2, sandbox.AgentInvocations);
        Assert.True(sandbox.SanitiserListInvocations >= 1);
    }

    // ── Restart recovery (persisted handle reattaches and continues) ──────────

    [Fact]
    public async Task PersistedHandle_ReattachesAndResumesWithSameCliSessionId()
    {
        var firstSandbox = new ScriptedSandbox(StreamJsonFirstTurn("cli-restart-99"));
        var workerA = new ClaudeSessionWorker(BuildRunner());

        var handle = await workerA.OpenSessionAsync(firstSandbox, "/work", credential: null);
        await workerA.SendTurnAsync(handle, "first");
        var persisted = workerA.SnapshotPersistedHandle(handle);

        // Round-trip the handle to confirm Metadata persists.
        var json = JsonSerializer.Serialize(persisted, JsonOptions);
        var revived = JsonSerializer.Deserialize<AgentSessionHandle>(json, JsonOptions)!;
        Assert.Equal("cli-restart-99", revived.Metadata![ClaudeSessionWorker.CliSessionIdMetadataKey]);

        // Fresh worker (simulated restart). Reattacher returns a NEW sandbox
        // (same name) seeded with the second turn fixture.
        var secondSandbox = new ScriptedSandbox(StreamJsonSecondTurn("cli-restart-99"));
        var resumeHookCalls = new List<string>();
        var workerB = new ClaudeSessionWorker(
            BuildRunner(),
            sandboxReattacher: (sandboxRef, _) =>
            {
                Assert.Equal(firstSandbox.Id, sandboxRef.Id);
                return Task.FromResult<ISandbox>(secondSandbox);
            },
            sandboxResumeHook: (sandboxRef, _) =>
            {
                resumeHookCalls.Add(sandboxRef.Id);
                return Task.CompletedTask;
            });

        await workerB.ResumeSessionAsync(revived);
        Assert.Single(resumeHookCalls);
        Assert.Equal(firstSandbox.Id, resumeHookCalls[0]);

        await workerB.SendTurnAsync(revived, "after restart");
        var secondArgv = secondSandbox.AgentExec!.Argv.ToList();
        Assert.Equal("cli-restart-99", secondArgv[secondArgv.IndexOf("--resume") + 1]);
    }

    [Fact]
    public async Task PersistedHandle_VmCannotResume_DegradesToFreshOneShot()
    {
        // The session worker must "degrade, never strand" when the configured
        // sandbox resume hook throws (VM gone, multipass start failed, etc.).
        // The next turn runs as a fresh claude --print (no --resume).
        var firstSandbox = new ScriptedSandbox(StreamJsonFirstTurn("cli-degrade"));
        var workerA = new ClaudeSessionWorker(BuildRunner());

        var handle = await workerA.OpenSessionAsync(firstSandbox, "/work", credential: null);
        await workerA.SendTurnAsync(handle, "first");
        var persisted = workerA.SnapshotPersistedHandle(handle);

        var secondSandbox = new ScriptedSandbox(StreamJsonFirstTurn("cli-fresh-after-degrade"));
        var workerB = new ClaudeSessionWorker(
            BuildRunner(),
            sandboxReattacher: (_, _) => Task.FromResult<ISandbox>(secondSandbox),
            sandboxResumeHook: static (_, _) => throw new InvalidOperationException("multipass start failed"));

        await workerB.ResumeSessionAsync(persisted);
        var result = await workerB.SendTurnAsync(persisted, "after degrade");

        Assert.True(result.Success);
        var argv = secondSandbox.AgentExec!.Argv.ToList();
        Assert.DoesNotContain("--resume", argv);

        // The fallback flag is exposed via the persisted handle so a subsequent
        // restart inherits the degraded state rather than retrying the resume.
        var persistedB = workerB.SnapshotPersistedHandle(persisted);
        Assert.Equal("true", persistedB.Metadata![ClaudeSessionWorker.FallbackMetadataKey]);
    }

    [Fact]
    public async Task PersistedHandle_WithFallbackMetadataKey_ReattachesInDegradedMode()
    {
        // A worker that previously degraded to fresh-one-shot mode persists
        // FallbackMetadataKey=true on the handle (see
        // PersistedHandle_VmCannotResume_DegradesToFreshOneShot). After an
        // orchestrator restart that re-attaches against a fresh worker, the
        // restored SessionState must inherit FallbackToFresh=true so the next
        // turn skips --resume and the worker does not try to re-execute the
        // failed resume hook.
        var sandbox = new ScriptedSandbox(StreamJsonFirstTurn("cli-stale-resume-target"));
        var resumeHookCalls = 0;
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            sandboxReattacher: (_, _) => Task.FromResult<ISandbox>(sandbox),
            sandboxResumeHook: (_, _) =>
            {
                resumeHookCalls++;
                return Task.CompletedTask;
            });

        var persisted = new AgentSessionHandle(
            AgentKind.Claude,
            "claude-session-degraded",
            new AgentSessionSandboxRef("vm-degraded"),
            "/work",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClaudeSessionWorker.CliSessionIdMetadataKey] = "cli-stale-resume-target",
                [ClaudeSessionWorker.FallbackMetadataKey] = "true",
            });

        var result = await worker.SendTurnAsync(persisted, "after restart in degraded mode");
        Assert.True(result.Success);

        // Fallback path: no --resume even though a CliSessionId is persisted.
        var argv = sandbox.AgentExec!.Argv.ToList();
        Assert.DoesNotContain("--resume", argv);

        // Snapshot reflects the inherited degraded state so subsequent restarts
        // keep honouring it.
        var snapshot = worker.SnapshotPersistedHandle(persisted);
        Assert.Equal("true", snapshot.Metadata![ClaudeSessionWorker.FallbackMetadataKey]);
    }

    [Fact]
    public async Task PersistedHandle_WithMalformedCliSessionId_IgnoresPersistedIdAndRunsFresh()
    {
        // The reattach branch validates the persisted CliSessionId via
        // IsValidCliSessionId before adopting it. A tampered or malformed
        // persisted value must NOT make it into argv as --resume; the next
        // turn falls back to fresh.
        var sandbox = new ScriptedSandbox(StreamJsonFirstTurn("cli-fresh-rebuild"));
        var worker = new ClaudeSessionWorker(
            BuildRunner(),
            sandboxReattacher: (_, _) => Task.FromResult<ISandbox>(sandbox));

        var persisted = new AgentSessionHandle(
            AgentKind.Claude,
            "claude-session-tampered",
            new AgentSessionSandboxRef("vm-tampered"),
            "/work",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // shell-metacharacter laced value — IsValidCliSessionId rejects.
                [ClaudeSessionWorker.CliSessionIdMetadataKey] = "; rm -rf /",
            });

        var result = await worker.SendTurnAsync(persisted, "after restart with bad id");
        Assert.True(result.Success);

        var argv = sandbox.AgentExec!.Argv.ToList();
        Assert.DoesNotContain("--resume", argv);
        Assert.DoesNotContain("; rm -rf /", argv);
    }

    [Fact]
    public async Task PersistedHandle_WithoutReattacher_RejectsCleanly()
    {
        var worker = new ClaudeSessionWorker(BuildRunner());
        var persisted = new AgentSessionHandle(
            AgentKind.Claude,
            "claude-session-orphan",
            new AgentSessionSandboxRef("vm-orphan"),
            "/work");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.SendTurnAsync(persisted, "after orphan"));
    }

    // ── Default-runner unchanged ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_OnSessionWorker_DelegatesToOneShotRunner()
    {
        // The session worker is a config-gated alternative; passing it through
        // RunAsync (i.e. the default IAgentRunner path) must behave exactly
        // like the one-shot ClaudeAgentRunner.
        var sandbox = new ScriptedSandbox(StreamJsonFirstTurn("cli-oneshot"));
        var worker = new ClaudeSessionWorker(BuildRunner());

        await worker.RunAsync(sandbox, "/work", "prompt", credential: null);

        // The one-shot path does NOT add --resume.
        var argv = sandbox.AgentExec!.Argv.ToList();
        Assert.DoesNotContain("--resume", argv);
        Assert.Contains("--print", argv);
        Assert.Contains("--dangerously-skip-permissions", argv);
    }

    // ── Argument validation ───────────────────────────────────────────────────

    [Fact]
    public async Task OpenSession_RejectsBlankWorkingDirectory()
    {
        var worker = new ClaudeSessionWorker(BuildRunner());
        await Assert.ThrowsAsync<ArgumentException>(
            () => worker.OpenSessionAsync(new ScriptedSandbox(), " ", credential: null));
    }

    [Fact]
    public async Task SendTurn_NullPrompt_Throws()
    {
        var worker = new ClaudeSessionWorker(BuildRunner());
        var handle = await worker.OpenSessionAsync(new ScriptedSandbox(), "/work", credential: null);
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => worker.SendTurnAsync(handle, null!));
    }

    [Fact]
    public void TryExtractCliSessionId_ParsesSystemInit()
    {
        var sessionId = ClaudeSessionWorker.TryExtractCliSessionId(StreamJsonFirstTurn("cli-extract"));
        Assert.Equal("cli-extract", sessionId);
    }

    [Fact]
    public void TryExtractCliSessionId_NoStream_ReturnsNull()
    {
        Assert.Null(ClaudeSessionWorker.TryExtractCliSessionId(null));
        Assert.Null(ClaudeSessionWorker.TryExtractCliSessionId(""));
        Assert.Null(ClaudeSessionWorker.TryExtractCliSessionId("not json"));
    }

    [Fact]
    public void TryExtractCliSessionId_RejectsShellMetacharacters()
    {
        // Even though the id flows into argv (no shell), the worker refuses
        // ids carrying anything outside [A-Za-z0-9_-] so a malformed stream
        // can't smuggle weird content into the persisted handle.
        const string malformed =
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"abc; rm -rf /\",\"tools\":[]}";
        Assert.Null(ClaudeSessionWorker.TryExtractCliSessionId(malformed));
    }

    [Fact]
    public void TryExtractCliSessionId_RejectsExcessivelyLongIds()
    {
        var huge = new string('a', 200);
        var line = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + huge + "\",\"tools\":[]}";
        Assert.Null(ClaudeSessionWorker.TryExtractCliSessionId(line));
    }

    [Fact]
    public async Task SuspendSession_NonPreemptibleSandbox_DoesNotThrow()
    {
        // Process / bubblewrap sandboxes don't implement IPreemptibleSandbox;
        // the worker must still let the orchestrator suspend (no VM to stop),
        // and a subsequent SendTurn must be rejected until ResumeSession is
        // called.
        var sandbox = new ScriptedSandbox(StreamJsonFirstTurn("cli-non-pre"));
        var worker = new ClaudeSessionWorker(BuildRunner());

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");
        await worker.SuspendSessionAsync(handle);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.SendTurnAsync(handle, "blocked"));

        await worker.ResumeSessionAsync(handle);
        await worker.SendTurnAsync(handle, "after");
        Assert.Equal(2, sandbox.AllAgentExecs.Count);
    }

    [Fact]
    public async Task CloseSession_AfterStopPreserve_DisablesPreserveBeforeDisposing()
    {
        var sandbox = new PreserveOnDisposeScriptedSandbox(StreamJsonFirstTurn("cli-preserve-close"));
        var worker = new ClaudeSessionWorker(BuildRunner());

        var handle = await worker.OpenSessionAsync(sandbox, "/work", credential: null);
        await worker.SendTurnAsync(handle, "first");
        await worker.SuspendSessionAsync(handle);
        await worker.CloseSessionAsync(handle);

        Assert.Equal(1, sandbox.StopCallCount);
        Assert.True(sandbox.DisablePreserveCalled);
        Assert.True(sandbox.Destroyed);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private static AgentCredential ClaudeApiCredential(string token) =>
        new(
            AgentKind.Claude,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ANTHROPIC_API_KEY"] = token,
            },
            new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// Sandbox that returns a scripted stdout for each successive `claude` exec
    /// (so the worker can capture session_id from stream-json), and reports
    /// success for every bash exec (so credential staging / transcript
    /// sanitisation pass through).
    /// </summary>
    private class ScriptedSandbox : ISandbox
    {
        private readonly Queue<string> _agentStdouts;
        private readonly List<SandboxExec> _allAgentExecs = [];
        public List<SandboxExec> AllExecs { get; } = [];
        public SandboxExec? AgentExec => _allAgentExecs.Count == 0 ? null : _allAgentExecs[^1];
        public IReadOnlyList<SandboxExec> AllAgentExecs => _allAgentExecs;
        public int DisposeCount { get; private set; }
        public string Id { get; } = "vm-" + Guid.NewGuid().ToString("N")[..8];

        public ScriptedSandbox(params string[] agentStdouts)
        {
            _agentStdouts = new Queue<string>(agentStdouts);
        }

        public virtual Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            AllExecs.Add(exec);
            if (exec.Argv.Count == 0)
                return Task.FromResult(new SandboxExecResult(0, "", ""));

            if (exec.Argv[0] == "claude")
            {
                _allAgentExecs.Add(exec);
                var stdout = _agentStdouts.Count > 0 ? _agentStdouts.Dequeue() : "";
                exec.StdoutChunkCallback?.Invoke(stdout);
                return Task.FromResult(new SandboxExecResult(0, stdout, ""));
            }

            // bash hooks — credential stage, transcript sanitiser file-list, etc.
            // The transcript-sanitiser list script honours an empty session_root
            // and exits 0 quietly; emulate that.
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public virtual ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PreemptibleScriptedSandbox : ScriptedSandbox, IPreemptibleSandbox
    {
        public int StopCallCount { get; private set; }
        public PreemptibleScriptedSandbox(params string[] agentStdouts) : base(agentStdouts) { }
        public Task StopAndPreserveAsync(CancellationToken ct = default)
        {
            StopCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class PreserveOnDisposeScriptedSandbox :
        ScriptedSandbox,
        IPreemptibleSandbox,
        IPreserveOnDisposeSandbox
    {
        private bool _preserveOnDispose;
        public int StopCallCount { get; private set; }
        public bool DisablePreserveCalled { get; private set; }
        public bool Destroyed { get; private set; }

        public PreserveOnDisposeScriptedSandbox(params string[] agentStdouts) : base(agentStdouts) { }

        public Task StopAndPreserveAsync(CancellationToken ct = default)
        {
            StopCallCount++;
            _preserveOnDispose = true;
            return Task.CompletedTask;
        }

        public void DisablePreserveOnDispose()
        {
            DisablePreserveCalled = true;
            _preserveOnDispose = false;
        }

        public override ValueTask DisposeAsync()
        {
            if (!_preserveOnDispose)
                Destroyed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Sandbox that returns a thinking-block 400 on the first agent invocation
    /// and a success stream on the second, after a sanitiser bash script ran.
    /// Used to assert the reactive sanitise-and-retry recovery path inside
    /// <see cref="ClaudeAgentRunner.RunSessionTurnAsync"/>.
    /// </summary>
    private sealed class RetryAfterSanitiseSandbox : ISandbox
    {
        private readonly string _failureStream;
        private readonly string _successStream;
        public int AgentInvocations { get; private set; }
        public int SanitiserListInvocations { get; private set; }
        public string Id => "vm-retry";

        public RetryAfterSanitiseSandbox(string failureStream, string successStream)
        {
            _failureStream = failureStream;
            _successStream = successStream;
        }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count == 0)
                return Task.FromResult(new SandboxExecResult(0, "", ""));

            if (exec.Argv[0] == "claude")
            {
                AgentInvocations++;
                if (AgentInvocations == 1)
                {
                    var failure = _failureStream;
                    exec.StdoutChunkCallback?.Invoke(failure);
                    // exit 1 with stream-json error envelope on stdout — same shape
                    // the CLI emits when the API rejects with 400.
                    return Task.FromResult(new SandboxExecResult(1, failure, ""));
                }
                exec.StdoutChunkCallback?.Invoke(_successStream);
                return Task.FromResult(new SandboxExecResult(0, _successStream, ""));
            }

            // The sanitiser's file-list bash script is detected here. It writes
            // its output to stdout listing the discovered transcripts; emitting
            // an empty list short-circuits per-file reads and the sanitiser
            // returns null (success).
            SanitiserListInvocations++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingMetricsSink : IClaudeSessionMetricsSink
    {
        public List<ClaudeSessionTurnMetrics> Records { get; } = [];
        public void Record(ClaudeSessionTurnMetrics metrics) => Records.Add(metrics);
    }

    private sealed class ThrowingMetricsSink : IClaudeSessionMetricsSink
    {
        public void Record(ClaudeSessionTurnMetrics metrics)
            => throw new InvalidOperationException("sink failed");
    }
}
