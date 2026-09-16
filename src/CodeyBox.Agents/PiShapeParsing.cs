using System.Text.Json;

namespace CodeyBox.Agents;

/// <summary>
/// Shared parsing for the pi-family <c>--mode json</c> event stream: one JSON
/// object per stdout line, with the session header
/// <c>{"type":"session","version":3,…,"cwd":…}</c>, underscore-style lifecycle
/// verbs (<c>agent_start</c>, <c>turn_start</c>, <c>message_start</c>,
/// <c>message_update</c>, <c>message_end</c>, <c>turn_end</c>,
/// <c>agent_end</c>), cumulative provider-reported usage
/// <c>usage:{input, output, cacheRead, cacheWrite, totalTokens}</c> on the
/// assistant message frames, the dispatch model id as <c>message.model</c>,
/// and terminal failures as <c>stopReason:"error"</c> with
/// <c>errorMessage</c>.
///
/// <para>Owned here — not duplicated per agent — because two registered agents
/// speak this exact wire shape: <c>pi</c> (npm
/// <c>@earendil-works/pi-coding-agent</c>) and <c>prime-agent</c> (which
/// derives from the same codebase and emits byte-identical event vocabulary,
/// verified against prime-agent 0.9.5 live frames). A second copy in either
/// agent library would let usage/model/error recognition silently diverge.
/// Only one of the two stream parsers claims the shape on the wire (the
/// other delegates attribution to the orchestrator's work-item/cost-row
/// resolution) — see the parser classes for the claim policy.</para>
/// </summary>
public static class PiShapeParsing
{
    /// <summary>
    /// Cap on the model id recorded for cost attribution. Bounded so a
    /// pathological provider response can't blow past the schema column
    /// width on the downstream cost-summary table.
    /// </summary>
    public const int MaxModelIdLength = 128;

    /// <summary>
    /// The underscore-style lifecycle verbs shared by every pi-family
    /// emitter. Used for documentation and test pinning; each parser defines
    /// its own claim policy (only one parser may claim a shared shape).
    /// </summary>
    public static readonly HashSet<string> LifecycleTypes = new(StringComparer.Ordinal)
    {
        "session",
        "agent_start",
        "agent_end",
        "agent_settled",
        "turn_start",
        "turn_end",
        "message_start",
        "message_update",
        "message_end",
        "queue_update",
        "compaction_start",
        "compaction_end",
    };

    /// <summary>
    /// Unwraps the nested assistant message envelope: usage, model, and the
    /// terminal error ride on <c>message</c> for the top-level turn/agent
    /// frames, and directly on the root for <c>message_*</c> frames. Returns
    /// the root itself when no object <c>message</c> property exists.
    /// </summary>
    public static JsonElement MessageEnvelope(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("message", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            return nested;
        }

        return root;
    }

    /// <summary>
    /// Reads one non-negative pi-named usage counter (<c>input</c>,
    /// <c>output</c>, <c>cacheRead</c>) from a <c>usage</c> object. Returns
    /// null when the property is absent, non-numeric, or negative — callers
    /// decide whether absent means "keep the base parse" (stream parser) or
    /// "count zero" (cost extractor).
    /// </summary>
    public static int? TryReadUsageCounter(JsonElement usage, string name)
    {
        if (usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty(name, out var value)
            && value.TryGetInt32(out var n)
            && n >= 0)
        {
            return n;
        }

        return null;
    }

    /// <summary>
    /// Reads the dispatch model id from a message envelope. Returns null when
    /// absent or blank; over-long ids are truncated to
    /// <see cref="MaxModelIdLength"/>.
    /// </summary>
    public static string? TryReadModel(JsonElement message)
    {
        if (message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("model", out var model)
            && model.ValueKind == JsonValueKind.String)
        {
            var raw = (model.GetString() ?? string.Empty).Trim();
            if (raw.Length == 0)
                return null;
            return raw.Length > MaxModelIdLength ? raw[..MaxModelIdLength] : raw;
        }

        return null;
    }

    /// <summary>
    /// Scans captured output for pi-family usage frames and returns the
    /// LATEST (cumulative-per-session, so last is the run total) as
    /// <c>(Input, Cached, Output, ModelId)</c>. Returns null when no frame
    /// carries a positive count — error runs report all-zero usage and there
    /// is nothing to attribute; null is unknown, never a zero that looks
    /// like data. Never throws: malformed lines are skipped so a
    /// half-written stream still yields whatever usage it contains.
    /// </summary>
    public static (int Input, int Cached, int Output, string? ModelId)? ScanLatestUsage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        (int Input, int Cached, int Output, string? ModelId)? latest = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('{'))
                continue;
            // Pre-screen: usage only rides on lines mentioning it, so the
            // JSON parse below never runs on tool-call chatter. Uses
            // Ordinal (not OrdinalIgnoreCase) — the wire field is lowercase
            // `usage` and a case-insensitive match would also trip on prose
            // like `"Usage: ..."` inside message text.
            if (!line.Contains("\"usage\"", StringComparison.Ordinal))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var message = MessageEnvelope(doc.RootElement);
                if (!message.TryGetProperty("usage", out var usage)
                    || usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var input = PositiveOrZero(TryReadUsageCounter(usage, "input"));
                var cached = PositiveOrZero(TryReadUsageCounter(usage, "cacheRead"));
                var output = PositiveOrZero(TryReadUsageCounter(usage, "output"));
                if (input == 0 && cached == 0 && output == 0)
                    continue;
                latest = (input, cached, output, TryReadModel(message));
            }
            catch (JsonException)
            {
                // Interleaved non-JSON chatter — keep scanning.
            }
        }

        return latest;
    }

    private static int PositiveOrZero(int? value)
        => value is > 0 ? value.Value : 0;
}
