using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Cmd;

/// <summary>
/// Best-effort token-count extractor for cmd's
/// <c>-p --output-format json</c> event stream.
///
/// <para>Per-turn frames (<c>model_request_end</c>, <c>turn_end</c>) carry
/// per-turn <c>usage {inputTokens, outputTokens, cacheReadTokens,
/// cacheWriteTokens}</c>, while the terminal frames
/// (<c>run_end.result.usage</c> and the <c>type: "result"</c> line) carry
/// the run total (verified live against command-code 1.54.2: the three
/// turn usages of a write run sum exactly to the result-line total). The
/// extractor scans every JSON line of stdout/stderr and keeps the LATEST
/// usage object: the terminal frames sort last, so the run total wins, and
/// on a truncated capture the last complete frame is still the best
/// available estimate. <c>cacheWriteTokens</c> has no bucket in
/// <see cref="AgentCostSnapshot"/> and is ignored for token accounting —
/// same treatment as the other providers.</para>
///
/// <para>The dispatch model id rides <c>model_request_start.model</c> in
/// full <c>openrouter/…</c> form (unlike omp, which strips the qualifier),
/// so the snapshot records it verbatim and pricing resolves per model
/// against the same qualified key. No <see cref="DefaultPricing"/> is
/// shipped: cmd fronts 150+ providers with unrelated per-token economics,
/// so no single fallback rate is honest. The shipped free-tier member bills
/// $0 via the explicit zero-rate bucket in
/// <c>agent-pricing-defaults.json</c>; operators fronting paid models add
/// that model's OpenRouter list prices there (or under
/// <c>CodeyBox:AgentPricing</c>) keyed by the qualified dispatch id. An
/// all-zero usage frame (model/key failures) yields null — unknown, never
/// a zero that looks like data.</para>
/// </summary>
public sealed class CmdCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Cmd;

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
        string? modelId = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('{'))
                continue;
            // Pre-screen: usage only rides on frames mentioning usage, and
            // the model only on model_request_start, so the JSON parse below
            // never runs on thinking-delta chatter. Uses Ordinal (not
            // OrdinalIgnoreCase) — the wire fields are camelCase and a
            // case-insensitive match would also trip on prose inside message
            // text.
            if (!line.Contains("\"usage\"", StringComparison.Ordinal)
                && !line.Contains("\"model_request_start\"", StringComparison.Ordinal))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (TryReadModelId(root) is { } model)
                    modelId ??= model;
                if (ExtractUsage(root) is { } usage)
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
        return new AgentCostSnapshot(found.Input, found.Cached, found.Output, ModelId: modelId);
    }

    private static string? TryReadModelId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty("event", out var inner) || inner.ValueKind != JsonValueKind.Object)
            return null;
        if (!IsStringEqual(inner, "type", "model_request_start"))
            return null;
        if (inner.TryGetProperty("model", out var model)
            && model.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(model.GetString()))
        {
            return model.GetString();
        }

        return null;
    }

    private static ScannedUsage? ExtractUsage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        // Usage rides three envelopes: the terminal result line
        // ({"type":"result","usage":{…}}), run_end
        // ({"type":"event","event":{"type":"run_end","result":{"usage":{…}}}}),
        // and the per-turn model_request_end/turn_end events. Only frames
        // carrying a usage object participate.
        JsonElement usage;
        if (IsStringEqual(root, "type", "result"))
        {
            if (!root.TryGetProperty("usage", out usage) || usage.ValueKind != JsonValueKind.Object)
                return null;
        }
        else if (root.TryGetProperty("event", out var inner) && inner.ValueKind == JsonValueKind.Object)
        {
            if (inner.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("usage", out var runUsage)
                && runUsage.ValueKind == JsonValueKind.Object)
            {
                usage = runUsage;
            }
            else if (!inner.TryGetProperty("usage", out var eventUsage)
                || eventUsage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            else
            {
                usage = eventUsage;
            }
        }
        else
        {
            return null;
        }

        var input = ReadNonNegative(usage, "inputTokens");
        var output = ReadNonNegative(usage, "outputTokens");
        var cached = ReadNonNegative(usage, "cacheReadTokens");

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
