using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Pi;

/// <summary>
/// Best-effort token-count extractor for pi's <c>--mode json</c> event stream.
///
/// <para>Every assistant <c>message_start</c> / <c>message_end</c> frame embeds
/// the provider-reported cumulative <c>message.usage</c> object
/// (<c>{input, output, cacheRead, cacheWrite, totalTokens, cost:{…}}</c> —
/// field names verified against pi 0.85.1's live error frames; the success
/// path carries the same object with non-zero counts). The extractor scans
/// every JSON line of stdout/stderr and keeps the LATEST usage object: usage
/// is cumulative per session, so the last frame is the run total. The dispatch
/// model id rides alongside on the same frames as <c>message.model</c> and is
/// recorded for per-model rate lookup.</para>
///
/// <para>No <see cref="DefaultPricing"/> is shipped: pi fronts 30+ providers
/// with unrelated per-token economics, so no single fallback rate is honest.
/// Per-model rates ship in <c>agent-pricing-defaults.json</c> or as operator
/// overrides under <c>CodeyBox:AgentPricing</c>, keyed <c>pi/&lt;model-id&gt;</c>
/// by the model id this extractor records. Unrated models cost $0 with a
/// startup warning (see <c>AgentCostCalculator.ValidateAtStartup</c>) —
/// mirroring the Cursor/Copilot subscription-path posture.</para>
/// </summary>
public sealed class PiCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Pi;

    public ModelRateConfig? DefaultPricing { get; } = null;

    // Cap on the model id recorded for cost attribution. Bounded so a
    // pathological provider response can't blow past the schema column
    // width on the downstream cost-summary table.
    private const int MaxModelIdLength = 128;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        try
        {
            var fromStdout = ScanStream(agentStdout);
            var fromStderr = ScanStream(agentStderr);
            // Prefer the stream with the larger reported total — stderr may
            // carry a truncated retry while stdout holds the full session.
            if (fromStdout is null) return fromStderr;
            if (fromStderr is null) return fromStdout;
            var stdoutTotal = fromStdout.InputTokens + fromStdout.CachedInputTokens + fromStdout.OutputTokens;
            var stderrTotal = fromStderr.InputTokens + fromStderr.CachedInputTokens + fromStderr.OutputTokens;
            return stderrTotal > stdoutTotal ? fromStderr : fromStdout;
        }
        catch (Exception)
        {
            // Contract: implementations must never throw.
            return null;
        }
    }

    private readonly record struct ScannedUsage(int Input, int Cached, int Output, string? ModelId)
    {
        public int Total => Input + Cached + Output;
    }

    private static AgentCostSnapshot? ScanStream(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        ScannedUsage? latest = null;
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
                var usage = ExtractUsage(doc.RootElement);
                if (usage is not null)
                    latest = usage;
            }
            catch (JsonException)
            {
                // Interleaved non-JSON chatter — keep scanning.
            }
        }

        if (latest is not { } found)
            return null;
        if (found.Input == 0 && found.Cached == 0 && found.Output == 0)
            return null;
        return new AgentCostSnapshot(found.Input, found.Cached, found.Output, found.ModelId);
    }

    private static ScannedUsage? ExtractUsage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        // Usage rides on the nested assistant message; the top-level
        // turn/agent frames repeat it via "message".
        var message = root;
        if (root.TryGetProperty("message", out var nested) && nested.ValueKind == JsonValueKind.Object)
            message = nested;

        if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var input = ReadNonNegative(usage, "input");
        var cached = ReadNonNegative(usage, "cacheRead");
        var output = ReadNonNegative(usage, "output");

        string? modelId = null;
        if (message.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
        {
            var raw = model.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
                modelId = raw!.Length > MaxModelIdLength ? raw[..MaxModelIdLength] : raw;
        }

        return new ScannedUsage(input, cached, output, modelId);
    }

    private static int ReadNonNegative(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) && n > 0)
            return n;
        return 0;
    }
}
