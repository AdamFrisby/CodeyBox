using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Autohand;

/// <summary>
/// Best-effort token extraction from autohand's
/// <c>--output-format stream-json</c> output.
///
/// <para><b>Unknown, not zero.</b> Bare headless streams (verified against
/// autohand-cli 0.9.7) carry no machine-readable usage frame — only
/// <c>tool_start</c>/<c>tool_end</c>/<c>file_modified</c>/<c>result</c>/
/// <c>error</c> events. The human footer prints a rounded total
/// (<c>12.5k tokens used</c>) with no input/output split and no model-rate
/// basis, so parsing it into a snapshot would fabricate data. This extractor
/// therefore returns null for real output (callers record no cost row —
/// unknown) and only recognises the documented
/// <c>messageType: "usage"</c> frame (<c>promptTokens</c>/
/// <c>completionTokens</c>) should a build emit it. Never throws.</para>
/// </summary>
public sealed class AutohandCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Autohand;

    /// <summary>
    /// No built-in fallback rate: without a token snapshot there is nothing
    /// to price, and a fallback would book a cost that was never measured.
    /// </summary>
    public ModelRateConfig? DefaultPricing => null;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        try
        {
            return TryExtractCore(agentStdout, agentStderr);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AgentCostSnapshot? TryExtractCore(string? agentStdout, string? agentStderr)
    {
        long input = 0;
        long output = 0;
        var sawUsage = false;

        foreach (var text in new[] { agentStdout, agentStderr })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || !line.StartsWith('{'))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (TryReadUsageFrame(doc.RootElement, out var frameInput, out var frameOutput))
                    {
                        input += frameInput;
                        output += frameOutput;
                        sawUsage = true;
                    }
                }
                catch (JsonException)
                {
                    // Half-written or interleaved chatter — keep scanning.
                }
            }
        }

        if (!sawUsage)
            return null;

        return new AgentCostSnapshot(
            SaturatingToInt(input),
            CachedInputTokens: 0,
            SaturatingToInt(output),
            ModelId: null);
    }

    private static bool TryReadUsageFrame(JsonElement root, out long input, out long output)
    {
        input = 0;
        output = 0;
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        if (!IsStringEqual(root, "type", "message"))
            return false;
        if (!IsStringEqual(root, "messageType", "usage"))
            return false;

        var parsedInput = ReadNonNegative(root, "promptTokens", "prompt_tokens", "input_tokens");
        var parsedOutput = ReadNonNegative(root, "completionTokens", "completion_tokens", "output_tokens");
        if (parsedInput is null && parsedOutput is null)
            return false;

        input = parsedInput ?? 0;
        output = parsedOutput ?? 0;
        return true;
    }

    private static bool IsStringEqual(JsonElement root, string name, string expected) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && string.Equals(prop.GetString(), expected, StringComparison.OrdinalIgnoreCase);

    private static long? ReadNonNegative(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop)
                && prop.ValueKind == JsonValueKind.Number
                && prop.TryGetInt64(out var value)
                && value >= 0)
            {
                return value;
            }
        }

        return null;
    }

    private static int SaturatingToInt(long value) =>
        value > int.MaxValue ? int.MaxValue : (int)value;
}
