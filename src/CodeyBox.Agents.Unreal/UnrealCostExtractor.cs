using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Unreal;

/// <summary>
/// Token usage extractor for Unreal Labs' unreal-agent structured output.
/// Scans session JSONL events for <c>model_response</c> frames carrying <c>Data.Response.Usage</c>.
/// </summary>
public sealed class UnrealCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Unreal;

    public ModelRateConfig? DefaultPricing => null;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        try
        {
            var fromStdout = ScanStream(agentStdout);
            var fromStderr = ScanStream(agentStderr);
            if (fromStdout is null) return fromStderr;
            if (fromStderr is null) return fromStdout;
            var stdoutTotal = fromStdout.InputTokens + fromStdout.CachedInputTokens + fromStdout.OutputTokens;
            var stderrTotal = fromStderr.InputTokens + fromStderr.CachedInputTokens + fromStderr.OutputTokens;
            return stderrTotal > stdoutTotal ? fromStderr : fromStdout;
        }
        catch
        {
            // Contract: implementations must never throw.
            return null;
        }
    }

    private static AgentCostSnapshot? ScanStream(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var input = 0;
        var output = 0;
        var cached = 0;
        var sawUsage = false;

        var reader = new StringReader(text);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '{')
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                // Unreal session item format: {"Kind":"model_response","Data":{"Response":{"Usage":{...}}}}
                if (root.TryGetProperty("Kind", out var kindProp)
                    && string.Equals(kindProp.GetString(), "model_response", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("Data", out var dataProp)
                    && dataProp.ValueKind == JsonValueKind.Object
                    && dataProp.TryGetProperty("Response", out var respProp)
                    && respProp.ValueKind == JsonValueKind.Object
                    && respProp.TryGetProperty("Usage", out var usageProp)
                    && usageProp.ValueKind == JsonValueKind.Object)
                {
                    if (TryReadTokens(usageProp, out var inTokens, out var outTokens, out var cachedTokens))
                    {
                        input = inTokens;
                        output = outTokens;
                        cached = cachedTokens;
                        sawUsage = true;
                    }
                }
            }
            catch (JsonException)
            {
                // Non-JSON or broken line, continue scanning
            }
        }

        if (!sawUsage || (input == 0 && output == 0 && cached == 0))
            return null;

        return new AgentCostSnapshot(
            InputTokens: input,
            CachedInputTokens: cached,
            OutputTokens: output,
            ModelId: null);
    }

    private static bool TryReadTokens(JsonElement usage, out int input, out int output, out int cached)
    {
        input = 0;
        output = 0;
        cached = 0;

        if (usage.TryGetProperty("InputTokens", out var inProp) && inProp.TryGetInt32(out var inVal))
            input = inVal;
        else if (usage.TryGetProperty("input_tokens", out var inSnake) && inSnake.TryGetInt32(out var inSnakeVal))
            input = inSnakeVal;

        if (usage.TryGetProperty("OutputTokens", out var outProp) && outProp.TryGetInt32(out var outVal))
            output = outVal;
        else if (usage.TryGetProperty("output_tokens", out var outSnake) && outSnake.TryGetInt32(out var outSnakeVal))
            output = outSnakeVal;

        if (usage.TryGetProperty("CachedInputTokens", out var cProp) && cProp.TryGetInt32(out var cVal))
            cached = cVal;
        else if (usage.TryGetProperty("cached_input_tokens", out var cSnake) && cSnake.TryGetInt32(out var cSnakeVal))
            cached = cSnakeVal;

        return input > 0 || output > 0 || cached > 0;
    }
}
