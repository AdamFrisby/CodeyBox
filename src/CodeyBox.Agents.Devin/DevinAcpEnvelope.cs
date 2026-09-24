using System.Text.Json;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// The single reader for the shim's <c>devin.acp</c> NDJSON envelope
/// contract (<c>Resources/devin-acp-client.py</c>): the line scan, the
/// envelope validation, the event-name vocabulary, the nested
/// <c>update</c>/<c>usage</c> shape navigation, and the usage-counter key
/// policy. Every consumer — <see cref="DevinAcpOutcome"/>,
/// <see cref="DevinCostExtractor"/>, <see cref="DevinStreamParser"/> —
/// reads through this type so a wire change lands in one place instead of
/// drifting across three scans (a snake_case usage object must feed the
/// cost row exactly as it feeds the stream summary).
/// </summary>
internal static class DevinAcpEnvelope
{
    /// <summary>The <c>type</c> tag stamped on every envelope the shim emits.</summary>
    internal const string EnvelopeType = "devin.acp";

    // Property names on the envelope root and inside the `update` object.
    // Centralised so no consumer re-literals the contract strings.
    internal const string EventPropertyName = "event";
    internal const string UpdatePropertyName = "update";
    internal const string SessionUpdatePropertyName = "sessionUpdate";
    internal const string MetaPropertyName = "_meta";
    internal const string UsagePropertyName = "usage";

    // Envelope `event` values the C# consumers dispatch on. The Python shim
    // owns the emitting vocabulary — keep these in lockstep with its
    // emit() calls (which is why the docstring lists every emitted name).
    internal const string EventSessionUpdate = "session_update";
    internal const string EventTurnComplete = "turn_complete";
    internal const string EventTurnError = "turn_error";
    internal const string EventFatal = "fatal";

    /// <summary>
    /// The <c>sessionUpdate</c> discriminator value marking a usage tick
    /// inside an <see cref="EventSessionUpdate"/> envelope's <c>update</c>
    /// object.
    /// </summary>
    internal const string UpdateKindUsageUpdate = "usage_update";

    // Devin-specific `usage_update` _meta counters (verified against devin
    // 3000.11.1); the ACP session/prompt `usage` object spells the same
    // counters without the vendor prefix.
    internal const string MetaInputTokens = "cognition.ai/inputTokens";
    internal const string MetaOutputTokens = "cognition.ai/outputTokens";
    internal const string MetaCachedReadTokens = "cognition.ai/cachedReadTokens";
    internal const string MetaCachedInputTokens = "cognition.ai/cached_input_tokens";

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
    /// tag is emitted only by CodeyBox's own devin shim, so claiming by it
    /// is unambiguous.
    /// </summary>
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
    /// Reads the <c>_meta</c> counter bag of a
    /// <see cref="EventSessionUpdate"/> envelope whose
    /// <c>update.sessionUpdate</c> discriminator is
    /// <see cref="UpdateKindUsageUpdate"/> — the nested-shape rule every
    /// usage consumer must agree on.
    /// </summary>
    internal static bool TryGetUsageUpdateMeta(JsonElement root, out JsonElement meta)
    {
        meta = default;
        return TryGetUpdate(root, out var update)
            && update.TryGetProperty(SessionUpdatePropertyName, out var sessionUpdate)
            && sessionUpdate.ValueKind == JsonValueKind.String
            && sessionUpdate.GetString() == UpdateKindUsageUpdate
            && update.TryGetProperty(MetaPropertyName, out meta)
            && meta.ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    /// Reads the <c>usage</c> totals object on a
    /// <see cref="EventTurnComplete"/> envelope — the ACP
    /// <c>session/prompt</c> result's usage bag.
    /// </summary>
    internal static bool TryGetTurnUsage(JsonElement root, out JsonElement usage)
    {
        usage = default;
        return root.TryGetProperty(UsagePropertyName, out usage)
            && usage.ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    /// Reads the per-turn token counters from a usage-shaped bag — either
    /// the <c>session/prompt</c> result's <c>usage</c> object (ACP
    /// camelCase) or a <c>usage_update</c> <c>_meta</c> bag (Devin's
    /// <c>cognition.ai/*</c> keys). Snake_case spellings are accepted in
    /// both so the cost row and the stream summary can never diverge on
    /// key naming when the agent's wire format shifts.
    /// </summary>
    internal static (int? Input, int? Output, int? CachedInput) ReadUsage(JsonElement usage)
        => (FirstInt(usage, "inputTokens", "input_tokens", MetaInputTokens),
            FirstInt(usage, "outputTokens", "output_tokens", MetaOutputTokens),
            FirstInt(usage, "cachedReadTokens", "cachedInputTokens", "cached_input_tokens",
                MetaCachedReadTokens, MetaCachedInputTokens));

    private static int? FirstInt(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty(name, out var value)
                && value.TryGetInt32(out var parsed))
            {
                return parsed;
            }
        }
        return null;
    }
}
