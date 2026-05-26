using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Best-effort token-count extractor for opencode CLI output.
///
/// <para>Tries the two common shapes the OpenAI-compatible / Anthropic-compatible
/// model wrappers opencode fronts emit at end-of-run: a JSON
/// <c>{"usage": { "prompt_tokens": N, "completion_tokens": M }}</c> blob,
/// and the human-readable "Usage: N input, M output tokens" line. opencode's
/// own canonical final-summary shape has not been verified, so the
/// human-readable fallback is intentionally generous.</para>
///
/// <para>No <see cref="DefaultPricing"/> is shipped: opencode fronts many
/// providers with very different per-token economics (DeepSeek vs Anthropic
/// vs OpenAI). Operators set per-model pricing under
/// <c>CodeyBox:AgentPricing</c> in appsettings keyed by the model id the
/// extractor records, matching the cost-reporting flow for the other
/// agents.</para>
/// </summary>
public sealed class OpencodeCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Opencode;

    public ModelRateConfig? DefaultPricing { get; } = null;

    private static readonly Regex UsagePromptPattern = new(
        @"[Pp]rompt\s+tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UsageCompletionPattern = new(
        @"[Cc]ompletion\s+tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InputPattern = new(
        @"(\d[\d,]*)\s+input\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OutputPattern = new(
        @"(\d[\d,]*)\s+output\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        if (string.IsNullOrWhiteSpace(agentStdout) && string.IsNullOrWhiteSpace(agentStderr))
            return null;

        var json = TryParseJson(agentStdout) ?? TryParseJson(agentStderr);
        if (json is not null) return json;

        return TryParseHumanReadable(agentStdout) ?? TryParseHumanReadable(agentStderr);
    }

    private static AgentCostSnapshot? TryParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.IndexOf("usage", StringComparison.OrdinalIgnoreCase) < 0) return null;

        foreach (var line in text.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains("usage", StringComparison.OrdinalIgnoreCase)) continue;
            if (!line.StartsWith('{') && !line.Contains('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var snapshot = ExtractFromDoc(doc.RootElement);
                if (snapshot is not null) return snapshot;
            }
            catch (JsonException) { }
        }

        try
        {
            using var doc = JsonDocument.Parse(text.Trim());
            return ExtractFromDoc(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AgentCostSnapshot? ExtractFromDoc(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)) return null;

        int input = 0, output = 0;
        string? modelId = null;

        if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv)) input = ptv;
        else if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv)) input = itv;

        if (usage.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var ctv)) output = ctv;
        else if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv)) output = otv;

        if (root.TryGetProperty("model", out var m))
        {
            var raw = m.GetString();
            modelId = raw is { Length: > 128 } ? raw[..128] : raw;
        }

        if (input == 0 && output == 0) return null;
        return new AgentCostSnapshot(input, 0, output, modelId);
    }

    private static AgentCostSnapshot? TryParseHumanReadable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var promptM = UsagePromptPattern.Match(text);
        var completionM = UsageCompletionPattern.Match(text);
        if (promptM.Success && completionM.Success)
        {
            return new AgentCostSnapshot(
                ParseTokenCount(promptM.Groups[1].Value),
                0,
                ParseTokenCount(completionM.Groups[1].Value),
                null);
        }

        var inputM = InputPattern.Match(text);
        var outputM = OutputPattern.Match(text);
        if (inputM.Success && outputM.Success)
        {
            var input = ParseTokenCount(inputM.Groups[1].Value);
            var output = ParseTokenCount(outputM.Groups[1].Value);
            if (input > 0 || output > 0)
                return new AgentCostSnapshot(input, 0, output, null);
        }

        return null;
    }

    private static int ParseTokenCount(string s)
    {
        var cleaned = s.Replace(",", "", StringComparison.Ordinal);
        return int.TryParse(cleaned, out var v) ? v : 0;
    }
}
