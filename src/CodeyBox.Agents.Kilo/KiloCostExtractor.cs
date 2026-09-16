using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Kilo;

/// <summary>
/// Best-effort token-count extractor for kilo's
/// <c>run --auto --format json</c> event stream.
///
/// <para>Every run ends with a <c>type: "step_finish"</c> frame whose nested
/// <c>part</c> carries <c>tokens {total, input, output, reasoning,
/// cache:{read, write}}</c> alongside <c>cost</c> (verified against
/// @kilocode/cli 7.7.2 live frames: the success frame's <c>total</c> equals
/// <c>input + cache.read + output + reasoning</c>, so <c>input</c> is fresh
/// input and <c>cache.read</c> the cached-input bucket). The extractor scans
/// every JSON line of stdout/stderr and keeps the LATEST usage object: usage
/// is cumulative per session, so the last frame is the run total.
/// <c>reasoning</c> and <c>cache.write</c> have no bucket in
/// <see cref="AgentCostSnapshot"/> and are ignored for token accounting —
/// same treatment as the other providers.</para>
///
/// <para>No dispatch model id rides the stream (unlike pi's
/// <c>message.model</c>), so the snapshot records a null model id and cost
/// attribution falls through to the AgentDefaults-derived rate
/// (<c>CodeyBox:AgentPricing:Rates:kilo[&lt;default -m id&gt;]</c> — see
/// <c>AgentCostCalculator.ResolveRate</c> step 3). No
/// <see cref="DefaultPricing"/> is shipped: kilo fronts hundreds of models
/// with unrelated per-token economics, so no single fallback rate is honest.
/// The shipped free-tier member bills $0 via the explicit zero-rate bucket
/// in <c>agent-pricing-defaults.json</c>; operators fronting paid models add
/// that model's OpenRouter list prices there (or under
/// <c>CodeyBox:AgentPricing</c>) keyed by the <c>-m</c> id form.
/// Unrated models cost $0 with a startup warning (see
/// <c>AgentCostCalculator.ValidateAtStartup</c>) — mirroring the
/// Cursor/Copilot subscription-path posture.</para>
/// </summary>
public sealed class KiloCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Kilo;

    public ModelRateConfig? DefaultPricing { get; } = null;

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

    private readonly record struct ScannedUsage(int Input, int Cached, int Output)
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
            // Pre-screen: usage only rides on step-finish lines mentioning
            // tokens, so the JSON parse below never runs on text chatter.
            // Uses Ordinal (not OrdinalIgnoreCase) — the wire field is
            // lowercase `tokens` and a case-insensitive match would also trip
            // on prose like `"Tokens: ..."` inside message text.
            if (!line.Contains("\"tokens\"", StringComparison.Ordinal))
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
        return new AgentCostSnapshot(found.Input, found.Cached, found.Output, ModelId: null);
    }

    private static ScannedUsage? ExtractUsage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!IsStringEqual(root, "type", "step_finish"))
            return null;
        if (!root.TryGetProperty("part", out var part) || part.ValueKind != JsonValueKind.Object)
            return null;
        if (!part.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            return null;

        var input = ReadNonNegative(tokens, "input");
        var output = ReadNonNegative(tokens, "output");
        var cached = 0;
        if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            cached = ReadNonNegative(cache, "read");

        return new ScannedUsage(input, cached, output);
    }

    private static bool IsStringEqual(JsonElement root, string name, string expected) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && string.Equals(prop.GetString(), expected, StringComparison.Ordinal);

    private static int ReadNonNegative(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) && n > 0)
            return n;
        return 0;
    }
}
