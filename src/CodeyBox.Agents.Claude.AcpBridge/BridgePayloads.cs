using System.IO;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Agents.Claude.AcpBridge;

/// <summary>
/// Pure-function wire-format helpers for the bridge's protocol edges.
/// Extracted from <see cref="Bridge"/> so unit tests can pin every byte of
/// the lockfile schema, the auto-reply JSON-RPC envelopes, and the
/// classification of inbound ACP frames without standing up the full
/// orchestration class (which spawns claude --ide and a TCP listener).
///
/// <para>Behavioural drift inside any of these payloads breaks the
/// claude --ide / orchestrator contract at the wire level — wrong field
/// names in the lockfile mean claude never discovers the IDE; the wrong
/// JSON-RPC result envelope means a session/request_permission turn never
/// makes forward progress; missing snake_case stop_reason support means
/// some claude releases fail to be detected as turn-complete. Each shape
/// is therefore covered by a direct fixture in <c>AcpBridgeUnitTests</c>.
/// </para>
/// </summary>
internal static class BridgePayloads
{
    /// <summary>
    /// Result of classifying an inbound ACP frame from claude --ide. The
    /// router in <see cref="Bridge.OnIncomingFrame"/> reacts to each kind:
    /// auto-reply for permission/input requests, shutdown for stopReason /
    /// error, plain acp_recv pass-through for everything else.
    /// </summary>
    public enum FrameKind
    {
        /// <summary>Not valid JSON — discarded silently.</summary>
        Malformed,
        /// <summary>Plain ACP traffic; emit acp_recv and continue.</summary>
        Plain,
        /// <summary>session/request_permission (or permission/request) + auto-approve enabled.</summary>
        AutoPermission,
        /// <summary>session/request_input (or input/request) + auto-answer enabled.</summary>
        AutoInput,
        /// <summary>result.stopReason / result.stop_reason — turn finished.</summary>
        TurnComplete,
        /// <summary>error member present — bridge emits turn_error envelope and shuts down.</summary>
        TurnError,
    }

    /// <summary>
    /// Parse the lockfile schema claude --ide expects to find at
    /// <c>~/.claude/ide/&lt;port&gt;.lock</c>. The exact field names matter:
    /// claude reads <c>workspaceFolders</c> (camelCase), <c>authToken</c>,
    /// <c>transport</c>, <c>url</c>, <c>ideName</c>, <c>runningInWindows</c>,
    /// and <c>pid</c> — any rename and discovery silently fails.
    /// </summary>
    public static byte[] BuildLockfileBytes(int pid, string workingDirectory, string authToken, int port)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("pid", pid);
            w.WriteStartArray("workspaceFolders");
            w.WriteStringValue(workingDirectory);
            w.WriteEndArray();
            w.WriteString("ideName", "CodeyBox");
            w.WriteString("transport", "ws");
            w.WriteBoolean("runningInWindows", false);
            w.WriteString("authToken", authToken);
            w.WriteString("url", "ws://127.0.0.1:" + port);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// JSON-RPC 2.0 reply to <c>session/request_permission</c> /
    /// <c>permission/request</c> that grants the request once. Matches the
    /// shape the original Node bridge emitted: <c>result.outcome.outcome =
    /// "selected"</c>, <c>result.outcome.optionId = "allow_once"</c>. The
    /// inbound id (which may be a number or a string per JSON-RPC) is
    /// echoed back verbatim.
    /// </summary>
    public static string BuildPermissionReplyJson(string idJson)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            w.WriteRawValue(idJson, skipInputValidation: false);
            w.WriteStartObject("result");
            w.WriteStartObject("outcome");
            w.WriteString("outcome", "selected");
            w.WriteString("optionId", "allow_once");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// JSON-RPC 2.0 reply to <c>session/request_input</c> /
    /// <c>input/request</c> that returns the agreed sentinel value.
    /// The literal <c>&lt;codeybox-question&gt;</c> prefix is what
    /// CodeyBox session-worker tests pin to confirm the auto-answer
    /// path actually fired.
    /// </summary>
    public static string BuildInputReplyJson(string idJson)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            w.WriteRawValue(idJson, skipInputValidation: false);
            w.WriteStartObject("result");
            w.WriteString("value",
                "<codeybox-question>: agent asked a blocking question; default applied, continuing.");
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Classify an inbound ACP frame. Out parameters carry the raw id JSON
    /// (so callers can echo it back without re-encoding), the method name
    /// (for telemetry envelopes), the stop reason (for turn_complete), and
    /// the raw error subtree JSON (for turn_error).
    /// </summary>
    public static FrameKind ClassifyIncomingFrame(
        string text,
        BridgeConfig config,
        out string? idJson,
        out string? methodName,
        out string? stopReason,
        out string? errorJson)
    {
        idJson = null;
        methodName = null;
        stopReason = null;
        errorJson = null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return FrameKind.Malformed; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return FrameKind.Plain;

            if (root.TryGetProperty("method", out var methodEl)
                && methodEl.ValueKind == JsonValueKind.String)
            {
                methodName = methodEl.GetString();
                if (root.TryGetProperty("id", out var idEl))
                    idJson = idEl.GetRawText();

                if (config.AutoApprovePermissions
                    && (methodName == "session/request_permission" || methodName == "permission/request"))
                {
                    return FrameKind.AutoPermission;
                }
                if (config.AutoAnswerQuestions
                    && (methodName == "session/request_input" || methodName == "input/request"))
                {
                    return FrameKind.AutoInput;
                }
            }

            if (root.TryGetProperty("error", out var errEl))
            {
                errorJson = errEl.GetRawText();
                return FrameKind.TurnError;
            }
            if (root.TryGetProperty("result", out var resultEl)
                && resultEl.ValueKind == JsonValueKind.Object)
            {
                if (resultEl.TryGetProperty("stopReason", out var sr) && sr.ValueKind == JsonValueKind.String)
                    stopReason = sr.GetString();
                else if (resultEl.TryGetProperty("stop_reason", out var sr2) && sr2.ValueKind == JsonValueKind.String)
                    stopReason = sr2.GetString();
                if (stopReason is not null) return FrameKind.TurnComplete;
            }

            return FrameKind.Plain;
        }
    }
}
