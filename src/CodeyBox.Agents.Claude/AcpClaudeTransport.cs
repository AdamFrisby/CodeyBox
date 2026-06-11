using System.Text;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Agent Client Protocol transport. Each turn runs the in-sandbox
/// <see cref="AcpBridgeScript"/> bridge under a single
/// <c>sandbox.ExecAsync</c>: the bridge stands up an IDE-shaped lockfile +
/// WebSocket, spawns <c>claude --ide</c> (interactive — OFF the
/// <c>--print</c> metered pool), proxies JSON-RPC 2.0 frames between the
/// in-sandbox WebSocket and its own stdio, and exits cleanly when the
/// <c>session/prompt</c> response carries a <c>stopReason</c>.
///
/// <para>Session continuity across turns. ACP sessions are identified by an
/// ID; the bridge surfaces the assigned id on the <c>session/new</c>
/// response and on subsequent <c>session/load</c> ACKs. The worker stamps it
/// on the persisted handle exactly the way it does for the print transport's
/// CLI session id — next turn passes it back so this transport sends
/// <c>session/load</c> instead of <c>session/new</c>. Cache warmth follows
/// the session id.</para>
///
/// <para>Failure → fallback. Any exception in <see cref="OpenAsync"/> or
/// <see cref="AcpSession.SendTurnAsync"/> that indicates the ACP path is
/// unusable (bridge failed to start, lockfile write failed, claude refused
/// the IDE handshake, deadline missed) surfaces as
/// <see cref="AcpTransportUnavailableException"/>. The worker catches it,
/// audit-logs the degradation, and replays the same turn through the
/// configured print fallback transport.</para>
/// </summary>
public sealed class AcpClaudeTransport : IClaudeTransport
{
    private readonly AgentNetworkToleranceSnapshot? _networkTolerance;

    public AcpClaudeTransport(AgentNetworkToleranceSnapshot? networkTolerance = null)
    {
        _networkTolerance = networkTolerance;
    }

    /// <summary>
    /// Effective claude binary the bridge spawns. Overrideable for tests.
    /// </summary>
    public string ClaudeBinary { get; init; } = ClaudeAgentRunner.DefaultBinary;

    /// <summary>
    /// Node binary used to host the bridge. Overrideable for tests / images
    /// that install Node under a non-default path.
    /// </summary>
    public string NodeBinary { get; init; } = "node";

    public string Name => "acp";
    public ClaudeSessionTransport Transport => ClaudeSessionTransport.Acp;

    internal int? BindApiTimeout()
    {
        if (_networkTolerance == null) return null;
        var dict = _networkTolerance.GetTolerance(ClaudeBinary);
        if (dict != null && dict.TryGetValue("ApiTimeoutMs", out var val) && int.TryParse(val, out var apiTimeoutMs))
        {
            return apiTimeoutMs;
        }
        return null;
    }

    public async Task<IClaudeTransportSession> OpenAsync(
        ClaudeTransportOpenRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        // Materialise the bridge script inside the sandbox up front. If this
        // fails for any reason (filesystem read-only, sandbox dead) the open
        // surfaces an AcpTransportUnavailableException so the worker can
        // degrade to the print transport on the very first turn.
        await MaterialiseBridgeAsync(request.Sandbox, ct).ConfigureAwait(false);

        return new AcpSession(this, request);
    }

    internal async Task MaterialiseBridgeAsync(ISandbox sandbox, CancellationToken ct)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(AcpBridgeScript.Source));
        var script =
            "set -eu\n" +
            "mkdir -p \"$HOME/.codeybox\"\n" +
            "chmod 700 \"$HOME/.codeybox\"\n" +
            "printf '%s' '" + encoded + "' | base64 -d > \"$HOME/.codeybox/claude-acp-bridge.cjs\"\n" +
            "chmod 700 \"$HOME/.codeybox/claude-acp-bridge.cjs\"\n";
        SandboxExecResult result;
        try
        {
            result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["bash", "-c", script],
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AcpTransportUnavailableException("failed to write the ACP bridge script", ex);
        }
        if (!result.Success)
        {
            throw new AcpTransportUnavailableException(
                $"failed to write the ACP bridge script: exit {result.ExitCode}, stderr={result.Stderr}");
        }
    }

    internal sealed class AcpSession : IClaudeTransportSession
    {
        private readonly AcpClaudeTransport _transport;
        private readonly ClaudeTransportOpenRequest _open;
        private int _turnIndex;
        private bool _disposed;

        public AcpSession(AcpClaudeTransport transport, ClaudeTransportOpenRequest open)
        {
            _transport = transport;
            _open = open;
        }

        public async Task<ClaudeTransportTurnResult> SendTurnAsync(
            ClaudeTransportTurnRequest request,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (_disposed)
                throw new ObjectDisposedException(nameof(AcpSession));

            // Preventive transcript sanitisation on resume turns. ACP's
            // session/load replays the persisted JSONL transcript exactly like
            // claude --resume does, so the same thinking-block immutability
            // 400 cluster can trigger here. Best-effort: a sanitiser failure
            // is folded into the result if the turn 400s reactively below.
            if (!string.IsNullOrEmpty(request.CliResumeSessionId))
                await ClaudeSessionSanitizer.SanitizeTranscriptsAsync(_open.Sandbox, ct).ConfigureAwait(false);

            var turnIndex = Interlocked.Increment(ref _turnIndex);
            var stdin = BuildStdin(request.Prompt, request.CliResumeSessionId);

            var stdoutBuf = new StringBuilder(4096);
            Action<string> aggregator = chunk =>
            {
                lock (stdoutBuf)
                {
                    stdoutBuf.Append(chunk);
                }
                request.StdoutChunkCallback?.Invoke(chunk);
            };

            var extraEnv = new Dictionary<string, string>();
            var apiTimeout = _transport.BindApiTimeout();
            if (apiTimeout.HasValue)
            {
                extraEnv["API_TIMEOUT_MS"] = apiTimeout.Value.ToString();
            }

            var exec = new SandboxExec
            {
                Argv = ["bash", "-lc", "exec " + EscapeForShell(_transport.NodeBinary) + " " + AcpBridgeScript.BridgeScriptPath],
                WorkingDirectory = _open.WorkingDirectory,
                Stdin = stdin,
                StdoutChunkCallback = aggregator,
                ExtraEnvironment = extraEnv.Count > 0 ? extraEnv : null
            };

            SandboxExecResult exec_result;
            try
            {
                exec_result = await _open.Sandbox.ExecAsync(exec, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new AcpTransportUnavailableException(
                    $"ACP bridge invocation failed on turn {turnIndex}", ex);
            }

            var combinedStdout = stdoutBuf.Length > 0
                ? stdoutBuf.ToString()
                : exec_result.Stdout ?? string.Empty;

            var observed = ObserveBridgeOutput(combinedStdout);

            if (observed.Fatal is { } fatal)
            {
                throw new AcpTransportUnavailableException(
                    $"ACP bridge reported fatal '{fatal.Message}': {fatal.Detail}");
            }

            // Treat bridge non-zero exit as transport-layer unavailability.
            // Exit 0 with a turn_error envelope is a TURN failure (surfaced to
            // the worker as a non-success AgentResult) and is recoverable via
            // the sanitiser; we do NOT degrade to print on every turn-level
            // upset.
            if (!exec_result.Success && observed.TurnError is null && observed.Complete is null)
            {
                throw new AcpTransportUnavailableException(
                    $"ACP bridge exited {exec_result.ExitCode} without reporting a turn outcome: stderr={exec_result.Stderr}");
            }

            var agentSuccess = observed.TurnError is null && observed.Complete is not null;
            var summary = agentSuccess
                ? "ok"
                : observed.TurnError is { } te
                    ? $"acp turn error: {te.Message ?? "unknown"}"
                    : $"acp turn timed out (no stopReason within {AcpBridgeScript.TurnTimeoutSeconds}s)";

            var stdoutForExtractor = observed.AssistantText.Length > 0
                ? BuildStreamJsonShimForExtractor(observed)
                : combinedStdout;

            var result = new AgentResult(
                Success: agentSuccess,
                Summary: summary,
                Stdout: stdoutForExtractor,
                Stderr: observed.Stderr ?? exec_result.Stderr);

            // Reactive thinking-block 400 recovery — the same safety net the
            // print transport's RunSessionTurnAsync runs. If the turn surfaced
            // an ACP error envelope carrying the thinking-block signature,
            // re-run the sanitiser and retry the turn once. A second failure
            // surfaces the original error so the work item observes it instead
            // of silently retrying forever.
            if (!result.Success && ClaudeSessionSanitizer.IsThinkingBlockFailure(result))
            {
                var sanitised = await ClaudeSessionSanitizer.SanitizeTranscriptsAsync(_open.Sandbox, ct)
                    .ConfigureAwait(false);
                if (sanitised is null)
                {
                    stdoutBuf.Clear();
                    SandboxExecResult retryResult;
                    try
                    {
                        retryResult = await _open.Sandbox.ExecAsync(exec, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        throw new AcpTransportUnavailableException(
                            $"ACP bridge re-invocation failed on retry of turn {turnIndex}", ex);
                    }
                    var retryStdout = stdoutBuf.Length > 0 ? stdoutBuf.ToString() : retryResult.Stdout ?? string.Empty;
                    var retryObserved = ObserveBridgeOutput(retryStdout);
                    var retrySuccess = retryObserved.TurnError is null && retryObserved.Complete is not null;
                    var retryShim = retryObserved.AssistantText.Length > 0
                        ? BuildStreamJsonShimForExtractor(retryObserved)
                        : retryStdout;
                    result = new AgentResult(
                        Success: retrySuccess,
                        Summary: retrySuccess ? "ok (post-sanitise retry)" : summary,
                        Stdout: retryShim,
                        Stderr: retryObserved.Stderr ?? retryResult.Stderr);
                    return new ClaudeTransportTurnResult(result, retryShim, retryObserved.SessionId ?? observed.SessionId);
                }
                else
                {
                    result = result with
                    {
                        Summary = $"{result.Summary}; sanitiser failed: {sanitised.Summary}",
                        Stderr = string.Concat(result.Stderr, "\n", sanitised.Stderr),
                    };
                }
            }

            return new ClaudeTransportTurnResult(result, stdoutForExtractor, observed.SessionId);
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }

        private string BuildStdin(string prompt, string? resumeSessionId)
        {
            var sb = new StringBuilder();
            var hello = new
            {
                type = "hello",
                autoApprovePermissions = true,
                autoAnswerQuestions = true,
                claudeBinary = _transport.ClaudeBinary,
                claudeArgs = BuildClaudeArgs(),
                workingDirectory = _open.WorkingDirectory,
                claudeEnv = BuildClaudeEnv(),
            };
            sb.Append(JsonSerializer.Serialize(hello)).Append('\n');
            sb.Append(JsonSerializer.Serialize(new
            {
                type = "acp_send",
                payload = new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = 1,
                        clientCapabilities = new
                        {
                            fs = new { readTextFile = false, writeTextFile = false },
                            terminal = false,
                        },
                    },
                },
            })).Append('\n');

            if (!string.IsNullOrEmpty(resumeSessionId))
            {
                sb.Append(JsonSerializer.Serialize(new
                {
                    type = "acp_send",
                    payload = new
                    {
                        jsonrpc = "2.0",
                        id = 2,
                        method = "session/load",
                        @params = new { sessionId = resumeSessionId, cwd = _open.WorkingDirectory },
                    },
                })).Append('\n');
            }
            else
            {
                sb.Append(JsonSerializer.Serialize(new
                {
                    type = "acp_send",
                    payload = new
                    {
                        jsonrpc = "2.0",
                        id = 2,
                        method = "session/new",
                        @params = new { cwd = _open.WorkingDirectory, mcpServers = Array.Empty<object>() },
                    },
                })).Append('\n');
            }

            sb.Append(JsonSerializer.Serialize(new
            {
                type = "acp_send",
                payload = new
                {
                    jsonrpc = "2.0",
                    id = 3,
                    method = "session/prompt",
                    @params = new
                    {
                        prompt = new[]
                        {
                            new { type = "text", text = prompt },
                        },
                    },
                },
            })).Append('\n');

            return sb.ToString();
        }

        private List<string> BuildClaudeArgs()
        {
            var args = new List<string>();
            var effectiveModel = _open.ModelId;
            if (!string.IsNullOrEmpty(effectiveModel))
            {
                args.Add("--model");
                args.Add(effectiveModel);
            }
            if (!string.IsNullOrEmpty(_open.ReasoningMode))
            {
                args.Add("--effort");
                args.Add(_open.ReasoningMode);
            }
            // We are headless. Disable any interactive permission prompt — the
            // bridge auto-grants ACP permission RPCs separately, this covers the
            // case where claude --ide still asks at startup.
            args.Add("--dangerously-skip-permissions");
            return args;
        }

        private Dictionary<string, string> BuildClaudeEnv()
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            // The credential pipeline already injects auth at sandbox boot; if the
            // credential carries extra env variables (e.g. CLAUDE_CODE_OAUTH_TOKEN),
            // surface them so the spawned claude inherits them too.
            if (_open.Credential?.EnvironmentVariables is { Count: > 0 } extra)
            {
                foreach (var (k, v) in extra)
                    env[k] = v;
            }
            return env;
        }

        private static string EscapeForShell(string value)
        {
            return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        }

        /// <summary>
        /// Reduces the bridge's line-delimited envelope stream into a
        /// turn-level observation: assistant text concatenated across stream
        /// updates, the assigned ACP session id, fatal / turn errors, and a
        /// stderr aggregate. Returns a non-null observation even when the
        /// bridge produced no recognisable output (every field is just empty
        /// / null in that case).
        /// </summary>
        internal static BridgeObservation ObserveBridgeOutput(string stdout)
        {
            var obs = new BridgeObservation();
            if (string.IsNullOrEmpty(stdout))
                return obs;

            var stderrBuf = new StringBuilder();
            var textBuf = new StringBuilder();
            foreach (var line in stdout.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                    continue;
                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(trimmed); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                        continue;
                    var envType = typeProp.GetString();
                    switch (envType)
                    {
                        case "fatal":
                            obs.Fatal = new BridgeFatal(
                                root.TryGetProperty("message", out var fm) ? fm.GetString() ?? "" : "",
                                root.TryGetProperty("detail", out var fd) ? fd.GetString() : null);
                            break;
                        case "ready":
                            obs.BridgeReady = true;
                            break;
                        case "peer_connected":
                            obs.PeerConnected = true;
                            break;
                        case "turn_complete":
                            obs.Complete = new BridgeTurnComplete(
                                root.TryGetProperty("stopReason", out var sr) ? sr.GetString() : null);
                            break;
                        case "turn_error":
                            if (root.TryGetProperty("error", out var errEl)
                                && errEl.ValueKind == JsonValueKind.Object)
                            {
                                obs.TurnError = new BridgeTurnError(
                                    errEl.TryGetProperty("message", out var em) ? em.GetString() : null,
                                    errEl.TryGetProperty("code", out var ec) && ec.ValueKind == JsonValueKind.Number
                                        ? ec.GetInt32() : null);
                            }
                            break;
                        case "turn_timeout":
                            obs.TimedOut = true;
                            break;
                        case "claude_stderr":
                            if (root.TryGetProperty("text", out var ct) && ct.ValueKind == JsonValueKind.String)
                                stderrBuf.Append(ct.GetString());
                            break;
                        case "claude_exit":
                            obs.ClaudeExited = true;
                            break;
                        case "permission_auto_granted":
                            obs.PermissionsAutoGranted++;
                            break;
                        case "question_auto_answered":
                            obs.QuestionsAutoAnswered++;
                            break;
                        case "acp_recv":
                            HandleAcpRecv(root, obs, textBuf);
                            break;
                    }
                }
            }
            obs.AssistantText = textBuf.ToString();
            obs.Stderr = stderrBuf.Length > 0 ? stderrBuf.ToString() : null;
            return obs;
        }

        private static void HandleAcpRecv(JsonElement root, BridgeObservation obs, StringBuilder textBuf)
        {
            if (!root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
                return;

            // session/new response shape (per agent-client-protocol): {result:{sessionId:"..."}}
            if (payload.TryGetProperty("result", out var resultEl)
                && resultEl.ValueKind == JsonValueKind.Object)
            {
                if (resultEl.TryGetProperty("sessionId", out var sid)
                    && sid.ValueKind == JsonValueKind.String
                    && ClaudeSessionWorker.IsValidAcpSessionId(sid.GetString()))
                    obs.SessionId ??= sid.GetString();
                if (resultEl.TryGetProperty("session_id", out var sid2)
                    && sid2.ValueKind == JsonValueKind.String
                    && ClaudeSessionWorker.IsValidAcpSessionId(sid2.GetString()))
                    obs.SessionId ??= sid2.GetString();
                if (resultEl.TryGetProperty("usage", out var usage)
                    && usage.ValueKind == JsonValueKind.Object)
                    AccumulateUsage(usage, obs);
                if (resultEl.TryGetProperty("modelId", out var mid)
                    && mid.ValueKind == JsonValueKind.String)
                    obs.ModelId ??= mid.GetString();
            }

            // session/update notification (streaming text chunks) shape:
            // {method:"session/update", params:{update:{sessionUpdate:"agent_message_chunk", content:{type:"text", text:"..."}}}}
            if (payload.TryGetProperty("method", out var methodEl)
                && methodEl.ValueKind == JsonValueKind.String
                && methodEl.GetString() == "session/update"
                && payload.TryGetProperty("params", out var paramsEl)
                && paramsEl.ValueKind == JsonValueKind.Object
                && paramsEl.TryGetProperty("update", out var updateEl)
                && updateEl.ValueKind == JsonValueKind.Object)
            {
                if (updateEl.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.Object
                    && contentEl.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    textBuf.Append(textEl.GetString());
                }
                if (updateEl.TryGetProperty("usage", out var u2)
                    && u2.ValueKind == JsonValueKind.Object)
                    AccumulateUsage(u2, obs);
            }
        }

        private static void AccumulateUsage(JsonElement usage, BridgeObservation obs)
        {
            obs.InputTokens += ReadInt(usage, "input_tokens");
            obs.OutputTokens += ReadInt(usage, "output_tokens");
            obs.CacheReadInputTokens += ReadInt(usage, "cache_read_input_tokens");
            obs.CacheCreationInputTokens += ReadInt(usage, "cache_creation_input_tokens");
        }

        private static int ReadInt(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
                return 0;
            return el.TryGetInt32(out var v) ? v : 0;
        }

        /// <summary>
        /// Re-encodes the ACP-observed turn as a stream-json blob shaped the
        /// way <see cref="ClaudeCostExtractor"/> already understands, so the
        /// existing metrics pipeline keeps working without an
        /// ACP-specific extractor. The shim emits a single <c>result</c>
        /// event carrying the totals and the session id we saw on
        /// <c>session/new</c>/<c>session/load</c>.
        /// </summary>
        private static string BuildStreamJsonShimForExtractor(BridgeObservation obs)
        {
            using var stream = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "system");
                writer.WriteString("subtype", "init");
                writer.WriteString("session_id", obs.SessionId ?? "(acp-unassigned)");
                writer.WriteStartArray("tools");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            stream.WriteByte((byte)'\n');
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "assistant");
                writer.WriteStartObject("message");
                writer.WriteString("id", "msg_acp");
                writer.WriteString("type", "message");
                writer.WriteString("role", "assistant");
                writer.WriteString("model", obs.ModelId ?? "claude-opus-4-7");
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", obs.AssistantText);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteString("stop_reason", obs.Complete?.StopReason ?? "end_turn");
                writer.WriteStartObject("usage");
                writer.WriteNumber("input_tokens", obs.InputTokens);
                writer.WriteNumber("output_tokens", obs.OutputTokens);
                writer.WriteNumber("cache_read_input_tokens", obs.CacheReadInputTokens);
                writer.WriteNumber("cache_creation_input_tokens", obs.CacheCreationInputTokens);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            stream.WriteByte((byte)'\n');
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "result");
                writer.WriteString("subtype", "success");
                writer.WriteNumber("duration_ms", 0);
                writer.WriteNumber("num_turns", 1);
                writer.WriteString("result", "Done");
                writer.WriteBoolean("is_error", false);
                writer.WriteString("session_id", obs.SessionId ?? "(acp-unassigned)");
                writer.WriteNumber("total_cost_usd", 0);
                writer.WriteStartObject("usage");
                writer.WriteNumber("input_tokens", obs.InputTokens);
                writer.WriteNumber("output_tokens", obs.OutputTokens);
                writer.WriteNumber("cache_read_input_tokens", obs.CacheReadInputTokens);
                writer.WriteNumber("cache_creation_input_tokens", obs.CacheCreationInputTokens);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    internal sealed class BridgeObservation
    {
        public bool BridgeReady { get; set; }
        public bool PeerConnected { get; set; }
        public bool ClaudeExited { get; set; }
        public bool TimedOut { get; set; }
        public BridgeFatal? Fatal { get; set; }
        public BridgeTurnError? TurnError { get; set; }
        public BridgeTurnComplete? Complete { get; set; }
        public string? SessionId { get; set; }
        public string AssistantText { get; set; } = "";
        public string? Stderr { get; set; }
        public string? ModelId { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheReadInputTokens { get; set; }
        public int CacheCreationInputTokens { get; set; }
        public int PermissionsAutoGranted { get; set; }
        public int QuestionsAutoAnswered { get; set; }
    }

    internal sealed record BridgeFatal(string Message, string? Detail);
    internal sealed record BridgeTurnError(string? Message, int? Code);
    internal sealed record BridgeTurnComplete(string? StopReason);
}
