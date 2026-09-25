using System.Text;
using System.Text.Json;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// The single reader for the shim's <c>devin.acp</c> NDJSON envelope
/// contract (<c>Resources/devin-acp-client.py</c>): the line scan, the
/// envelope validation, the event-name vocabulary, and the nested
/// <c>update</c> shape navigation. Every consumer —
/// <see cref="DevinAcpOutcome"/>, <see cref="DevinStreamParser"/>,
/// <see cref="DevinAgentRunner"/>'s visible-text projection — reads through
/// this type so a wire change lands in one place instead of drifting
/// across scans.
///
/// <para>Envelope usage counters (<c>turn_complete.usage</c>,
/// <c>usage_update</c> <c>_meta</c>) are deliberately NOT read by any
/// consumer: they are agent-influenceable telemetry (see
/// <see cref="IsEnvelope"/>'s bounded claim) and promoting them into
/// <c>has_extracted_token_usage</c> rows would let a forged envelope settle
/// paid-quota escrow and skew burn estimates. They remain in the raw
/// capture for diagnostics only.</para>
/// </summary>
internal static class DevinAcpEnvelope
{
    /// <summary>The <c>type</c> tag stamped on every envelope the shim emits.</summary>
    internal const string EnvelopeType = "devin.acp";

    // Property names centralised for the multi-consumer vocabulary —
    // anything read by more than one consumer (event names, the
    // update/sessionUpdate navigation, the turn-complete text)
    // lives here; fields read by a single consumer stay literal at their
    // use site.
    internal const string EventPropertyName = "event";
    internal const string UpdatePropertyName = "update";
    internal const string SessionUpdatePropertyName = "sessionUpdate";
    internal const string FinalTextPropertyName = "finalText";

    // Envelope `event` values the C# consumers dispatch on. The Python shim
    // owns the emitting vocabulary — keep these in lockstep with its
    // emit() calls (which is why the docstring lists every emitted name).
    internal const string EventSessionUpdate = "session_update";
    internal const string EventTurnComplete = "turn_complete";
    internal const string EventTurnError = "turn_error";
    internal const string EventFatal = "fatal";

    /// <summary>
    /// The <c>sessionUpdate</c> discriminator values inside an
    /// <see cref="EventSessionUpdate"/> envelope's <c>update</c> object —
    /// the ACP notification kinds the stream parser folds into stream
    /// events.
    /// </summary>
    internal const string UpdateKindUsageUpdate = "usage_update";
    internal const string UpdateKindToolCall = "tool_call";
    internal const string UpdateKindToolCallUpdate = "tool_call_update";
    internal const string UpdateKindAgentMessageChunk = "agent_message_chunk";

    /// <summary>
    /// One parsed envelope: the <c>event</c> name plus a detached clone of
    /// the root element, so the value stays valid after enumeration moves
    /// on and the source <see cref="JsonDocument"/> is released.
    /// </summary>
    internal readonly record struct Envelope(string Event, JsonElement Root);

    /// <summary>
    /// Enumerates the <c>devin.acp</c> envelopes in a captured stdout blob.
    /// Never throws: blank lines, non-JSON lines, JSON that is not a
    /// devin.acp envelope, and lines truncated mid-write are all skipped —
    /// the capture may carry leading CLI noise or be cut by an output cap.
    /// </summary>
    internal static IEnumerable<Envelope> Enumerate(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            yield break;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '{')
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (IsEnvelope(root)
                    && root.TryGetProperty(EventPropertyName, out var eventEl)
                    && eventEl.ValueKind == JsonValueKind.String
                    && eventEl.GetString() is { } eventName)
                {
                    yield return new Envelope(eventName, root.Clone());
                }
            }
        }
    }

    /// <summary>
    /// True when <paramref name="root"/> carries the devin.acp type tag —
    /// the claim check the stream parser runs on each candidate line. The
    /// tag is emitted only by CodeyBox's own devin shim; the claim holds
    /// against the vectors it is designed to close — the shim folds its
    /// entire stderr surface (including tool-subprocess output) into
    /// <c>codeybox.stderr</c> envelopes, envelope-framed dispatches are
    /// pinned to the attached exec-pipe transport, and the exec wrapper
    /// never merges stderr into the stream under
    /// <c>CODEYBOX_STDOUT_ENVELOPE_FRAMED</c>.
    /// </summary>
    /// <remarks>
    /// Bounded claim: a same-uid (root-capable) process inside the sandbox
    /// can still write the exec stdout pipe through
    /// <c>/proc/&lt;pid&gt;/fd</c>, and a tool subprocess inheriting the
    /// agent's fd 1 writes onto the shim's ACP wire pipe — the shim narrows
    /// the wire pipe with unguessable request ids, session-id pinning, and
    /// scalar-only usage bags, but the exec-pipe leg has no in-VM fix.
    /// Consumers must therefore treat envelope payloads — usage counters,
    /// the terminal event, finalText — as agent-influenceable telemetry:
    /// good enough to keep the stream advancing and to surface the terminal
    /// outcome, never authoritative accounting or authorization evidence
    /// (the usage counters specifically are never promoted to extracted
    /// usage — see this type's summary).
    /// </remarks>
    internal static bool IsEnvelope(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("type", out var typeEl)
        && typeEl.ValueKind == JsonValueKind.String
        && typeEl.GetString() == EnvelopeType;

    /// <summary>
    /// Reads the envelope's <c>update</c> object — the ACP
    /// <c>session/update</c> params carried by an
    /// <see cref="EventSessionUpdate"/> envelope.
    /// </summary>
    internal static bool TryGetUpdate(JsonElement root, out JsonElement update)
    {
        update = default;
        return root.TryGetProperty(UpdatePropertyName, out update)
            && update.ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    /// Reads the assistant text of a <see cref="EventSessionUpdate"/>
    /// envelope whose update discriminator is
    /// <see cref="UpdateKindAgentMessageChunk"/> — the
    /// <c>update.content.text</c> chunk the shim later joins into
    /// <c>turn_complete.finalText</c>.
    /// </summary>
    internal static bool TryGetMessageChunkText(JsonElement root, out string? text)
    {
        text = null;
        if (!TryGetUpdate(root, out var update)
            || !update.TryGetProperty(SessionUpdatePropertyName, out var kind)
            || kind.ValueKind != JsonValueKind.String
            || kind.GetString() != UpdateKindAgentMessageChunk
            || !update.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("text", out var textEl)
            || textEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        text = textEl.GetString();
        return true;
    }

    /// <summary>
    /// Reads the <c>finalText</c> string of a
    /// <see cref="EventTurnComplete"/> envelope — the joined agent-message
    /// chunks the shim stamps at turn end.
    /// </summary>
    internal static bool TryGetFinalText(JsonElement root, out string? text)
    {
        text = null;
        if (!root.TryGetProperty(FinalTextPropertyName, out var el)
            || el.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        text = el.GetString();
        return true;
    }

    /// <summary>
    /// Projects a captured envelope stream back to the agent-visible answer
    /// text: the concatenated <c>agent_message_chunk</c> payloads (the same
    /// join the shim stamps as <c>turn_complete.finalText</c>), falling back
    /// to the last terminal envelope's <c>finalText</c> when the capture
    /// carries no chunks (e.g. a truncated stream that kept the terminal
    /// line). Returns null when no devin.acp envelope was observed so the
    /// caller feeds the raw stdout to its consumer unchanged — the same
    /// fallback contract as Claude's plan-artifact extractor.
    /// </summary>
    internal static string? ExtractAgentVisibleText(string? stdout)
    {
        var sawEnvelope = false;
        var chunks = new StringBuilder();
        string? terminalText = null;
        foreach (var envelope in Enumerate(stdout))
        {
            sawEnvelope = true;
            switch (envelope.Event)
            {
                case EventSessionUpdate:
                    if (TryGetMessageChunkText(envelope.Root, out var chunk))
                        chunks.Append(chunk);
                    break;
                case EventTurnComplete:
                    if (TryGetFinalText(envelope.Root, out var finalText))
                        terminalText = finalText;
                    break;
            }
        }
        if (!sawEnvelope)
            return null;
        return chunks.Length > 0 ? chunks.ToString() : terminalText ?? string.Empty;
    }

}
