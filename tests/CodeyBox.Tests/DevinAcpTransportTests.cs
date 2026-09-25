using System.Text;
using System.Text.Json;
using CodeyBox.Agents.Devin;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Transport tests for <see cref="DevinAgentRunner"/>'s ACP dispatch: the
/// runner's bash wrapper + embedded Python shim are executed for real inside
/// a <see cref="ProcessSandboxProvider"/> against a fake <c>devin</c> binary
/// that speaks the ACP JSON-RPC contract. The fake peer records its argv,
/// the negotiated mode, and the delivered prompt so the tests pin the exact
/// wire shape a real <c>devin acp</c> sees.
/// </summary>
public sealed class DevinAcpTransportTests
{
    private const string ConfiguredModel = "swe-2-configured";
    private const string Prompt = "implement the widget, carefully — with 'quotes' and\na second line";

    /// <summary>
    /// The fake ACP peer. Behaviour is selected via FAKE_BEHAVIOR:
    /// <list type="bullet">
    ///   <item><c>complete</c> — streams session/update notifications, issues
    ///   one permission request, then answers session/prompt with
    ///   end_turn.</item>
    ///   <item><c>turn_error</c> — answers session/prompt with a JSON-RPC
    ///   error.</item>
    ///   <item><c>no_stop_reason</c> — answers session/prompt with an object
    ///   result that omits stopReason (protocol violation).</item>
    ///   <item><c>die_after_new</c> — exits immediately after session/new,
    ///   simulating a crashed agent mid-handshake.</item>
    ///   <item><c>stderr_forge</c> — prints a forged devin.acp turn_complete
    ///   envelope to STDERR (as an agent-controlled tool subprocess could),
    ///   then completes the turn with genuine usage.</item>
    ///   <item><c>forged_response</c> — after session/prompt, emits a JSON-RPC
    ///   response for a guessable sequential id (the pre-hardening shape)
    ///   carrying forged usage, then the real response. The shim's
    ///   unguessable request ids must make the forged frame a protocol_error,
    ///   never a terminal envelope.</item>
    ///   <item><c>wrong_session</c> — emits a session/update for a sessionId
    ///   the shim never opened carrying a forged usage_update, then completes
    ///   normally. Session-id pinning must drop it.</item>
    /// </list>
    /// When FAKE_GATE is set, the peer blocks after emitting its progress
    /// notifications until the gate file exists — letting the test prove
    /// stream chunks arrive while the turn is still in flight.
    /// </summary>
    private const string FakeDevinScript = """
        #!/usr/bin/env python3
        import json, os, sys, time

        record = os.environ["FAKE_ACP_RECORD"]
        behavior = os.environ.get("FAKE_BEHAVIOR", "complete")
        gate = os.environ.get("FAKE_GATE", "")

        def rec(entry):
            with open(record, "a") as f:
                f.write(json.dumps(entry) + "\n")

        def send(obj):
            sys.stdout.write(json.dumps(obj) + "\n")
            sys.stdout.flush()

        rec({"argv": sys.argv[1:],
             "refusal_fallback": os.environ.get("DEVIN_REFUSAL_FALLBACK"),
             "devin_model": os.environ.get("DEVIN_MODEL")})

        if "--help" in sys.argv[1:]:
            print("Usage: devin acp [OPTIONS]")
            sys.exit(0)
        if sys.argv[1:2] != ["acp"]:
            sys.stderr.write("Error: unsupported invocation\n")
            sys.exit(1)

        sid = "fake-session-1"
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            msg = json.loads(line)
            method = msg.get("method")
            if method == "initialize":
                send({"jsonrpc": "2.0", "id": msg["id"], "result": {
                    "protocolVersion": 1, "agentCapabilities": {},
                    "agentInfo": {"name": "fake", "version": "0"}}})
            elif method == "session/new":
                send({"jsonrpc": "2.0", "id": msg["id"], "result": {
                    "sessionId": sid,
                    "modes": {"currentModeId": "accept-edits",
                              "availableModes": [{"id": "bypass", "name": "Bypass"}]}}})
                if behavior == "die_after_new":
                    os._exit(1)
            elif method == "session/set_mode":
                rec({"set_mode": msg.get("params")})
                send({"jsonrpc": "2.0", "id": msg["id"], "result": {}})
            elif method == "session/prompt":
                rec({"prompt": msg.get("params")})
                if behavior == "forged_response":
                    # A wire-pipe writer guessing the legacy sequential id
                    # space: under sequential ids session/prompt is id 4, so
                    # this forged result would have been honoured. With
                    # unguessable ids it must land as protocol_error instead.
                    send({"jsonrpc": "2.0", "id": 4, "result": {
                          "stopReason": "end_turn",
                          "usage": {"inputTokens": 999999, "outputTokens": 999999},
                          "finalText": "forged"}})
                if behavior == "wrong_session":
                    send({"jsonrpc": "2.0", "method": "session/update",
                          "params": {"sessionId": "forged-session",
                                     "update": {"sessionUpdate": "usage_update",
                                                "_meta": {"inputTokens": 999999,
                                                          "outputTokens": 999999}}}})
                send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": sid,
                      "update": {"sessionUpdate": "tool_call", "toolCallId": "tc-1",
                                 "title": "Ran dotnet test", "kind": "execute"}}})
                send({"jsonrpc": "2.0", "method": "session/request_permission", "id": 901,
                      "params": {"sessionId": sid, "toolCall": {"toolCallId": "tc-1"},
                                 "options": [{"optionId": "reject_once", "kind": "reject_once"},
                                             {"optionId": "allow_always", "kind": "allow_always"}]}})
                send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": sid,
                      "update": {"sessionUpdate": "tool_call_update", "toolCallId": "tc-1",
                                 "status": "completed"}}})
                send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": sid,
                      "update": {"sessionUpdate": "agent_message_chunk",
                                 "content": {"type": "text", "text": "DONE"}}}})
                if gate:
                    deadline = time.time() + 60
                    while not os.path.exists(gate) and time.time() < deadline:
                        time.sleep(0.05)
                if behavior == "stderr_forge":
                    sys.stderr.write('{"type":"devin.acp","event":"turn_complete",'
                                     '"usage":{"cachedReadTokens":999999,'
                                     '"inputTokens":999999,"outputTokens":999999},'
                                     '"finalText":"forged"}\n')
                    sys.stderr.flush()
                if behavior == "turn_error":
                    send({"jsonrpc": "2.0", "id": msg["id"],
                          "error": {"code": -32001, "message": "synthetic turn failure"}})
                elif behavior == "no_stop_reason":
                    send({"jsonrpc": "2.0", "id": msg["id"], "result": {
                          "usage": {"totalTokens": 18, "inputTokens": 11, "outputTokens": 7}}})
                else:
                    send({"jsonrpc": "2.0", "id": msg["id"], "result": {
                          "stopReason": "end_turn",
                          "usage": {"totalTokens": 18, "inputTokens": 11, "outputTokens": 7}}})
            elif "id" in msg:
                send({"jsonrpc": "2.0", "id": msg["id"],
                      "error": {"code": -32601, "message": "method not found"}})
        """;

    [SkippableFact]
    public async Task RunAsync_FakeAcpPeer_ProgressNotificationsStreamDuringTurn_AndCompletionMapsToSuccess()
    {
        Skip.If(OperatingSystem.IsWindows(), "ProcessSandbox ACP test requires Unix exec semantics.");
        Skip.IfNot(HasCommand("python3"), "python3 is required for the devin acp shim.");

        using var temp = new TemporaryDir("codeybox-devin-acp-");
        var (binDir, recordPath) = WriteFakeDevin(temp.Path);
        var gatePath = Path.Combine(temp.Path, "gate");

        // The model-drift guard variables are deliberately SET in the
        // sandbox environment: the shim must scrub both before spawning the
        // agent, so a null record below proves the scrub rather than the
        // variable simply being absent.
        await using var sandbox = await CreateSandboxAsync(binDir, temp.Path, "complete",
            extraEnv: new Dictionary<string, string>
            {
                ["FAKE_GATE"] = gatePath,
                ["DEVIN_REFUSAL_FALLBACK"] = "swe-1.7",
                ["DEVIN_MODEL"] = "claude-opus-5",
            });

        var runner = new DevinAgentRunner();
        var streamed = new StringBuilder();
        var gate = new object();
        var runTask = runner.RunAsync(
            sandbox, SandboxConventions.WorkDir, Prompt, credential: null,
            modelId: ConfiguredModel,
            stdoutChunkCallback: chunk => { lock (gate) streamed.Append(chunk); });

        try
        {
            // The fake peer blocks on the gate AFTER emitting its progress
            // notifications, so observing a session_update chunk here proves the
            // stream advances while the turn is still in flight — the exact
            // signal the worker-progress watchdog consumes.
            var sawProgress = await WaitForAsync(() =>
            {
                lock (gate) return streamed.ToString().Contains("\"session_update\"", StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(20));
            Assert.True(sawProgress, "no session_update envelope streamed while the ACP turn was in flight");
            Assert.False(runTask.IsCompleted, "run must still be in flight when progress notifications stream");
        }
        finally
        {
            // Always release the fake peer — a failed assertion must not
            // leave it blocking on the gate inside the sandbox while the
            // in-flight run task is abandoned unobserved.
            File.WriteAllText(gatePath, "go");
        }
        var result = await runTask;

        Assert.True(result.Success, $"expected success; stderr={result.Stderr}");
        var streamText = streamed.ToString();
        Assert.Contains("\"turn_complete\"", streamText, StringComparison.Ordinal);
        Assert.Contains("\"session_update\"", streamText, StringComparison.Ordinal);
        Assert.True(
            streamText.IndexOf("\"session_update\"", StringComparison.Ordinal)
            < streamText.IndexOf("\"turn_complete\"", StringComparison.Ordinal),
            "progress envelopes must precede the terminal envelope");

        // The fake peer offered [reject_once, allow_always]; the shim must
        // answer with the durable allow, not a reject or a cancel.
        Assert.Contains("\"permission_auto_granted\"", streamText, StringComparison.Ordinal);
        Assert.Contains("\"optionId\": \"allow_always\"", streamText, StringComparison.Ordinal);
        Assert.DoesNotContain("permission_auto_cancelled", streamText, StringComparison.Ordinal);

        var records = ReadRecords(recordPath);
        var argv = Assert.Single(records, r => r.Contains("\"argv\""));
        Assert.Contains($"\"acp\", \"--model\", \"{ConfiguredModel}\"", argv, StringComparison.Ordinal);
        Assert.Contains("\"refusal_fallback\": null", argv, StringComparison.Ordinal);
        Assert.Contains("\"devin_model\": null", argv, StringComparison.Ordinal);

        var setMode = Assert.Single(records, r => r.Contains("\"set_mode\""));
        Assert.Contains("\"modeId\": \"bypass\"", setMode, StringComparison.Ordinal);

        var prompt = Assert.Single(records, r => r.Contains("\"prompt\""));
        using (var doc = JsonDocument.Parse(prompt))
        {
            var delivered = doc.RootElement.GetProperty("prompt").GetProperty("prompt")[0].GetProperty("text").GetString();
            Assert.Equal(Prompt, delivered);
        }
    }

    [SkippableFact]
    public async Task RunAsync_FakeAcpPeer_StderrForgedEnvelope_CannotImpersonateStreamOutput()
    {
        // devin.acp envelopes are claimed by their type tag, so provenance
        // is "the line was emitted by the shim". The agent can make a tool
        // subprocess print a forged envelope to stderr (the CLI inherits
        // it); the shim retargets fd 2 through a relay that re-emits each
        // line inside a codeybox.stderr envelope on stdout, so the forged
        // line can never be claimed — otherwise it falsifies persisted
        // token counts and the final assistant message.
        Skip.If(OperatingSystem.IsWindows(), "ProcessSandbox ACP test requires Unix exec semantics.");
        Skip.IfNot(HasCommand("python3"), "python3 is required for the devin acp shim.");

        using var temp = new TemporaryDir("codeybox-devin-acp-");
        var (binDir, _) = WriteFakeDevin(temp.Path);
        await using var sandbox = await CreateSandboxAsync(binDir, temp.Path, "stderr_forge");

        var runner = new DevinAgentRunner();
        var streamed = new StringBuilder();
        var result = await runner.RunAsync(
            sandbox, SandboxConventions.WorkDir, "do the thing", credential: null,
            stdoutChunkCallback: chunk => { lock (streamed) streamed.Append(chunk); },
            captureStructuredStream: false);

        Assert.True(result.Success, $"expected success; stderr={result.Stderr}");

        var streamText = streamed.ToString();
        // The forged line reached the channel only inside a codeybox.stderr
        // wrapper — never as a bare claimable envelope line.
        Assert.Contains("codeybox.stderr", streamText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"usage\":{\"cachedReadTokens\":999999",
            streamText,
            StringComparison.Ordinal);
        // And it never even reached the exec stderr channel: the shim's
        // in-VM relay intercepted it before it could leave the process tree
        // as a bare line.
        Assert.DoesNotContain("999999", result.Stderr ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("forged", result.Stderr ?? string.Empty, StringComparison.Ordinal);

        var snapshot = new DevinCostExtractor().TryExtract(streamText, null);
        Assert.NotNull(snapshot);
        Assert.Equal(11, snapshot.InputTokens);
        Assert.Equal(7, snapshot.OutputTokens);
        Assert.Equal(0, snapshot.CachedInputTokens);
    }

    [SkippableFact]
    public async Task DispatchScript_MergedStderrStream_StillCannotForgeEnvelope()
    {
        // Provenance is established at the emission point, not by transport
        // hygiene: run the real dispatch script with stderr merged into
        // stdout (`2>&1`, the shape the in-VM wrapper's log-file tee takes
        // when CODEYBOX_AGENT_LOG_FILE is set on a stale VM image). Even a
        // fully merged stream must carry the forged line only inside a
        // codeybox.stderr envelope — a bare devin.acp line sourced from
        // stderr must be impossible.
        Skip.If(OperatingSystem.IsWindows(), "dispatch-script test requires Unix exec semantics.");
        Skip.IfNot(HasCommand("python3"), "python3 is required for the devin acp shim.");
        Skip.IfNot(HasCommand("bash"), "bash is required for the dispatch script.");

        using var temp = new TemporaryDir("codeybox-devin-acp-");
        var (binDir, recordPath) = WriteFakeDevin(temp.Path);

        var script = DevinAgentRunner.BuildAcpDispatchScript(
            DevinAgentRunner.AcpShimArgs("devin", ConfiguredModel, DevinAgentRunner.FullAutonomyAcpMode));

        using var proc = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "bash",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        // `bash -c "$1" 2>&1` merges the dispatch's whole stderr surface into
        // stdout — the merged-stream shape the audit flagged.
        proc.StartInfo.ArgumentList.Add("-c");
        proc.StartInfo.ArgumentList.Add("bash -c \"$1\" 2>&1");
        proc.StartInfo.ArgumentList.Add("devin-acp-dispatch");
        proc.StartInfo.ArgumentList.Add(script);
        proc.StartInfo.Environment["PATH"] = $"{binDir}:/usr/bin:/bin";
        proc.StartInfo.Environment["FAKE_ACP_RECORD"] = recordPath;
        proc.StartInfo.Environment["FAKE_BEHAVIOR"] = "stderr_forge";
        proc.Start();
        await proc.StandardInput.WriteAsync(DevinAcpShim.BuildDispatchStdin("do the thing"));
        proc.StandardInput.Close();
        var merged = await proc.StandardOutput.ReadToEndAsync();
        var strayStderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.Equal(0, proc.ExitCode);
        Assert.Equal("", strayStderr);

        // The forged line survives only as escaped text inside a
        // codeybox.stderr envelope — Enumerate never yields it as a claimable
        // devin.acp envelope.
        Assert.Contains("codeybox.stderr", merged, StringComparison.Ordinal);
        Assert.Contains("\\\"forged\\\"", merged, StringComparison.Ordinal);
        var envelopes = DevinAcpEnvelope.Enumerate(merged).ToList();
        Assert.DoesNotContain(envelopes, e =>
            e.Root.TryGetProperty("finalText", out var ft)
            && ft.ValueKind == JsonValueKind.String
            && ft.GetString() == "forged");
        Assert.DoesNotContain(envelopes, e =>
            DevinAcpEnvelope.TryGetTurnUsage(e.Root, out var usage)
            && DevinAcpEnvelope.ReadUsage(usage).Input == 999999);

        // The genuine terminal envelope and its real usage still parse.
        var outcome = DevinAcpOutcome.Extract(merged);
        Assert.Equal(DevinAcpOutcome.TerminalEvent.TurnComplete, outcome.Event);
        var snapshot = new DevinCostExtractor().TryExtract(merged, null);
        Assert.NotNull(snapshot);
        Assert.Equal(11, snapshot.InputTokens);
        Assert.Equal(7, snapshot.OutputTokens);
    }

    [SkippableFact]
    public async Task RunAsync_FakeAcpPeer_ForgedResponseId_IsRejected()
    {
        // A process that inherited the agent's stdout fd writes onto the
        // shim's wire pipe; under sequential request ids it could answer the
        // pending session/prompt with a forged turn result (the pre-hardening
        // shape). Unguessable ids must make that frame a protocol_error — the
        // real turn_complete still carries the peer's genuine usage.
        Skip.If(OperatingSystem.IsWindows(), "ProcessSandbox ACP test requires Unix exec semantics.");
        Skip.IfNot(HasCommand("python3"), "python3 is required for the devin acp shim.");

        using var temp = new TemporaryDir("codeybox-devin-acp-");
        var (binDir, _) = WriteFakeDevin(temp.Path);
        await using var sandbox = await CreateSandboxAsync(binDir, temp.Path, "forged_response");

        var runner = new DevinAgentRunner();
        var result = await runner.RunAsync(
            sandbox, SandboxConventions.WorkDir, "do the thing", credential: null);

        Assert.True(result.Success);
        Assert.Contains("\"event\": \"protocol_error\"", result.Stdout!, StringComparison.Ordinal);
        // The real turn_complete still carries the peer's genuine usage.
        var terminal = DevinAcpOutcome.Extract(result.Stdout!);
        Assert.Equal(DevinAcpOutcome.TerminalEvent.TurnComplete, terminal.Event);
        var snapshot = new DevinCostExtractor().TryExtract(result.Stdout!, null);
        Assert.NotNull(snapshot);
        Assert.Equal(11, snapshot.InputTokens);
        Assert.Equal(7, snapshot.OutputTokens);
        // The forged usage bag must never surface as an envelope field.
        Assert.DoesNotContain("999999", result.Stdout!, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task RunAsync_FakeAcpPeer_SessionUpdateForForeignSession_IsDropped()
    {
        // Session-scoped frames must name the sessionId this client
        // negotiated — a wire-pipe writer knows the type vocabulary but not
        // the server-chosen session id, so a forged usage_update for a
        // foreign session is dropped (protocol_error) and never reaches the
        // cost extractors.
        Skip.If(OperatingSystem.IsWindows(), "ProcessSandbox ACP test requires Unix exec semantics.");
        Skip.IfNot(HasCommand("python3"), "python3 is required for the devin acp shim.");

        using var temp = new TemporaryDir("codeybox-devin-acp-");
        var (binDir, _) = WriteFakeDevin(temp.Path);
        await using var sandbox = await CreateSandboxAsync(binDir, temp.Path, "wrong_session");

        var runner = new DevinAgentRunner();
        var result = await runner.RunAsync(
            sandbox, SandboxConventions.WorkDir, "do the thing", credential: null);

        Assert.True(result.Success);
        Assert.Contains("\"event\": \"protocol_error\"", result.Stdout!, StringComparison.Ordinal);
        Assert.DoesNotContain("forged-session", result.Stdout!, StringComparison.Ordinal);
        Assert.DoesNotContain("999999", result.Stdout!, StringComparison.Ordinal);

        var snapshot = new DevinCostExtractor().TryExtract(result.Stdout!, null);
        Assert.NotNull(snapshot);
        Assert.Equal(11, snapshot.InputTokens);
        Assert.Equal(7, snapshot.OutputTokens);
    }

    [SkippableFact]
    public async Task RunAsync_FakeAcpPeer_TurnError_MapsToTypedFailure()
    {
        Skip.If(OperatingSystem.IsWindows(), "ProcessSandbox ACP test requires Unix exec semantics.");
        Skip.IfNot(HasCommand("python3"), "python3 is required for the devin acp shim.");

        using var temp = new TemporaryDir("codeybox-devin-acp-");
        var (binDir, _) = WriteFakeDevin(temp.Path);
        await using var sandbox = await CreateSandboxAsync(binDir, temp.Path, "turn_error");

        var runner = new DevinAgentRunner();
        var result = await runner.RunAsync(
            sandbox, SandboxConventions.WorkDir, "do the thing", credential: null);

        Assert.False(result.Success);
        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("turn error", result.TerminalDiagnostic, StringComparison.Ordinal);
        Assert.Contains("synthetic turn failure", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task RunAsync_FakeAcpPeer_PromptResultWithoutStopReason_MapsToTypedFailure()
    {
        // The shim's exit-0 contract is "a session/prompt response carrying
        // a stopReason". A spec-violating {"result":{}} must NOT emit
        // turn_complete — the shim reports fatal and the run fails typed
        // rather than PostProcessAcpResult trusting a false success.
        Skip.If(OperatingSystem.IsWindows(), "ProcessSandbox ACP test requires Unix exec semantics.");
        Skip.IfNot(HasCommand("python3"), "python3 is required for the devin acp shim.");

        using var temp = new TemporaryDir("codeybox-devin-acp-");
        var (binDir, _) = WriteFakeDevin(temp.Path);
        await using var sandbox = await CreateSandboxAsync(binDir, temp.Path, "no_stop_reason");

        var runner = new DevinAgentRunner();
        var result = await runner.RunAsync(
            sandbox, SandboxConventions.WorkDir, "do the thing", credential: null);

        Assert.False(result.Success);
        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("stopReason", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task RunAsync_FakeAcpPeer_DiesMidHandshake_MapsToTypedFailure()
    {
        Skip.If(OperatingSystem.IsWindows(), "ProcessSandbox ACP test requires Unix exec semantics.");
        Skip.IfNot(HasCommand("python3"), "python3 is required for the devin acp shim.");

        using var temp = new TemporaryDir("codeybox-devin-acp-");
        var (binDir, _) = WriteFakeDevin(temp.Path);
        await using var sandbox = await CreateSandboxAsync(binDir, temp.Path, "die_after_new");

        var runner = new DevinAgentRunner();
        var result = await runner.RunAsync(
            sandbox, SandboxConventions.WorkDir, "do the thing", credential: null);

        Assert.False(result.Success);
        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("fatal", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    private static (string BinDir, string RecordPath) WriteFakeDevin(string root)
    {
        var binDir = Path.Combine(root, "bin");
        var recordDir = Path.Combine(root, "records");
        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(recordDir);
        var devinPath = Path.Combine(binDir, "devin");
        File.WriteAllText(devinPath, FakeDevinScript);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(devinPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return (binDir, Path.Combine(recordDir, "record.jsonl"));
    }

    private static async Task<ISandbox> CreateSandboxAsync(
        string binDir, string root, string behavior, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = new Dictionary<string, string>
        {
            ["PATH"] = "/tools:/usr/bin:/bin",
            ["FAKE_ACP_RECORD"] = Path.Combine(root, "records", "record.jsonl"),
            ["FAKE_BEHAVIOR"] = behavior,
        };
        if (extraEnv is not null)
            foreach (var kv in extraEnv) env[kv.Key] = kv.Value;

        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        return await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts =
            [
                new SandboxMount { SandboxPath = "/tools", HostPath = binDir, ReadOnly = false },
                new SandboxMount { SandboxPath = SandboxConventions.WorkDir, Tmpfs = true },
            ],
            Environment = env,
            WorkingDirectory = SandboxConventions.WorkDir,
        });
    }

    private static IReadOnlyList<string> ReadRecords(string recordPath)
        => File.Exists(recordPath) ? File.ReadAllLines(recordPath) : [];

    private static bool HasCommand(string name)
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = name,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (proc is null) return false;
            proc.WaitForExit(10_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private sealed class TemporaryDir : IDisposable
    {
        public TemporaryDir(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
