using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Agents.Goose;

/// <summary>
/// Best-effort token-count extractor for goose's
/// <c>--output-format stream-json</c> event stream (verified against goose
/// 1.50.1 live frames).
///
/// <para>Every run ends with a single terminal totals frame:
/// <c>{"type":"complete","total_tokens":…,"input_tokens":…,
/// "output_tokens":…,"cache_read_input_tokens":…,
/// "cache_write_input_tokens":…,"cost_usd":…}</c>. The extractor scans every
/// JSON line of stdout/stderr and keeps the LATEST complete frame — a
/// retried run emits one totals frame per attempt, so the last frame is the
/// run total. The dispatch model id rides on per-chunk message frames as
/// <c>message.metadata.inference.requestedModel</c> (e.g.
/// <c>nvidia/nemotron-3.5-lightning:free</c>) and the latest seen value is
/// recorded for per-model rate lookup.</para>
///
/// <para>All-zero totals (the shape a failed run emits — verified: an
/// OpenRouter 401 completes with every counter at 0) yield null rather than
/// a zero-token snapshot, so failures never record rows that look like free
/// successful runs. No <see cref="DefaultPricing"/> is shipped: goose fronts
/// 30+ providers with unrelated per-token economics (and free-tier models at
/// $0), so no single fallback rate is honest. Per-model rates ship in
/// <c>agent-pricing-defaults.json</c> or as operator overrides under
/// <c>CodeyBox:AgentPricing</c>, keyed by the requested-model id this
/// extractor records. Unrated models cost $0 with a startup warning (see
/// <c>AgentCostCalculator.ValidateAtStartup</c>).</para>
/// </summary>
public sealed class GooseCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Goose;

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
            // Prefer the stream with the larger reported total — one stream
            // may carry a truncated retry while the other holds the full run.
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

    private readonly record struct ScannedUsage(int Input, int Cached, int Output, string? ModelId);

    private static AgentCostSnapshot? ScanStream(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        ScannedUsage? latest = null;
        string? latestModel = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('{'))
                continue;
            // Pre-screen: totals ride only on complete frames and the model
            // only on message frames, so the JSON parse below never runs on
            // the human session banner or tool-call chatter.
            var mayBeComplete = line.Contains("\"complete\"", StringComparison.Ordinal);
            var mayBeMessage = line.Contains("\"requestedModel\"", StringComparison.Ordinal);
            if (!mayBeComplete && !mayBeMessage)
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (mayBeComplete && ExtractTotals(root) is { } usage)
                    latest = usage with { ModelId = latestModel ?? usage.ModelId };
                if (mayBeMessage && ExtractRequestedModel(root) is { } model)
                {
                    latestModel = model;
                    if (latest.HasValue && latest.Value.ModelId is null)
                        latest = latest.Value with { ModelId = model };
                }
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

    private static ScannedUsage? ExtractTotals(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "complete", StringComparison.Ordinal))
            return null;
        if (!root.TryGetProperty("total_tokens", out var total)
            || total.ValueKind != JsonValueKind.Number)
            return null;

        var input = ReadNonNegative(root, "input_tokens");
        var cached = ReadNonNegative(root, "cache_read_input_tokens");
        var output = ReadNonNegative(root, "output_tokens");
        return new ScannedUsage(input, cached, output, ModelId: null);
    }

    private static string? ExtractRequestedModel(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return null;
        if (!message.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
            return null;
        if (!metadata.TryGetProperty("inference", out var inference) || inference.ValueKind != JsonValueKind.Object)
            return null;
        if (!inference.TryGetProperty("requestedModel", out var model) || model.ValueKind != JsonValueKind.String)
            return null;
        var raw = model.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return raw!.Length > MaxModelIdLength ? raw[..MaxModelIdLength] : raw;
    }

    private static int ReadNonNegative(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) && n > 0)
            return n;
        return 0;
    }
}
