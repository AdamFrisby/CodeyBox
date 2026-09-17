using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Qwen;

/// <summary>
/// Best-effort token-count extractor for qwen's structured output.
///
/// <para>Every assistant frame embeds the provider-reported
/// <c>message.usage</c> object (<c>{input_tokens, output_tokens,
/// cache_read_input_tokens, total_tokens}</c> — field names verified
/// against qwen 0.24.0 live frames) alongside the dispatch model as
/// <c>message.model</c>, and the terminal <c>result</c> frame repeats the
/// run total in its own <c>usage</c> object plus a per-model breakdown in
/// <c>stats.models.&lt;id&gt;.tokens {prompt, candidates, total,
/// cached}</c>. The extractor scans every JSON line of stdout/stderr and
/// keeps the LATEST usage object: usage grows across turns (verified: the
/// result frame's totals exceed the intermediate assistant frame's), so the
/// last frame is the run total. The <c>stats.models</c> breakdown supplies
/// the model id when no <c>message.model</c> was seen. The id is recorded
/// verbatim (e.g. <c>nvidia/nemotron-3.5-lightning:free</c>) for per-model
/// rate lookup.</para>
///
/// <para>No <see cref="DefaultPricing"/> is shipped: qwen fronts many
/// providers with unrelated per-token economics (native Qwen, OpenAI-,
/// Anthropic-, and Gemini-compatible endpoints), so no single fallback rate
/// is honest. Per-model rates ship in <c>agent-pricing-defaults.json</c> or
/// as operator overrides under <c>CodeyBox:AgentPricing</c>, keyed by the
/// verbatim id this extractor records (the shipped free-tier member bills
/// $0 via an explicit zero-rate bucket). Unrated models cost $0 with a
/// startup warning (see <c>AgentCostCalculator.ValidateAtStartup</c>) —
/// mirroring the Cursor/Copilot subscription-path posture. Error runs
/// report all-zero usage and yield null (unknown), never a zero that looks
/// like data.</para>
/// </summary>
public sealed class QwenCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Qwen;

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

    private static AgentCostSnapshot? ScanStream(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        int input = 0, output = 0, cached = 0;
        string? model = null;
        var sawUsage = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('{'))
                continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                // The usage object rides at the top level (result frames),
                // under message (assistant frames), or inside stats.models
                // (per-model breakdown on the result frame).
                if (TryReadUsage(root, out var topInput, out var topOutput, out var topCached))
                {
                    input = topInput;
                    output = topOutput;
                    cached = topCached;
                    sawUsage = true;
                }

                if (root.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.Object)
                {
                    if (TryReadUsage(message, out var msgInput, out var msgOutput, out var msgCached))
                    {
                        input = msgInput;
                        output = msgOutput;
                        cached = msgCached;
                        sawUsage = true;
                    }

                    var messageModel = ReadString(message, "model");
                    if (!string.IsNullOrWhiteSpace(messageModel))
                        model = TruncateModelId(messageModel);
                }

                if (model is null
                    && root.TryGetProperty("stats", out var stats)
                    && stats.ValueKind == JsonValueKind.Object
                    && stats.TryGetProperty("models", out var models)
                    && models.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in models.EnumerateObject())
                    {
                        model = TruncateModelId(entry.Name);
                        break;
                    }
                }
            }
        }

        if (!sawUsage)
            return null;

        // Error runs report all-zero usage; there is nothing to attribute.
        // Return null (unknown) rather than a zero that looks like data.
        if (input <= 0 && output <= 0 && cached <= 0)
            return null;

        return new AgentCostSnapshot(input, cached, output, model);
    }

    private static bool TryReadUsage(JsonElement node, out int input, out int output, out int cached)
    {
        input = 0;
        output = 0;
        cached = 0;
        if (!node.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        input = ReadCounter(usage, "input_tokens");
        output = ReadCounter(usage, "output_tokens");
        cached = ReadCounter(usage, "cache_read_input_tokens");
        return true;
    }

    private static int ReadCounter(JsonElement node, string property)
    {
        if (node.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var counter)
            && counter > 0)
        {
            return counter;
        }

        return 0;
    }

    private static string? ReadString(JsonElement node, string property)
        => node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string TruncateModelId(string model)
        => model.Length > 128 ? model[..128] : model;
}
