using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Codex;

/// <summary>
/// Extracts token counts from the OpenAI Codex CLI output.
///
/// Tries two formats:
/// 1. JSON output (codex exec --json): looks for {"usage":{"input_tokens":N,"cached_input_tokens":C,"output_tokens":M}}
///    and the OpenAI usage shape {"usage":{"prompt_tokens":N,"prompt_tokens_details":{"cached_tokens":C},"completion_tokens":M}}.
/// 2. Human-readable patterns: "Usage: N input, M output tokens".
///
/// Codex/OpenAI prompt/input totals include cached tokens, so this extractor
/// stores <see cref="AgentCostSnapshot.InputTokens"/> as the fresh remainder
/// and records the cached subset separately.
/// </summary>
public sealed class CodexCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Codex;

    public ModelRateConfig? DefaultPricing { get; } = new()
    {
        InputPerMillion = 5.0,
        CachedInputPerMillion = 0.50,
        OutputPerMillion = 30.0,
    };

    private static readonly Regex UsagePromptPattern = new(
        @"[Pp]rompt\s+tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UsageCompletionPattern = new(
        @"[Cc]ompletion\s+tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UsageCachedPattern = new(
        @"[Cc]ache(?:d)?\s+(?:input\s+)?tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InputPattern = new(
        @"(\d[\d,]*)\s+input\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OutputPattern = new(
        @"(\d[\d,]*)\s+output\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CachedPattern = new(
        @"(\d[\d,]*)\s+cached(?:\s+input)?\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        if (string.IsNullOrWhiteSpace(agentStdout) && string.IsNullOrWhiteSpace(agentStderr))
            return null;

        var jsonResult = TryParseJsonOutput(agentStdout) ?? TryParseJsonOutput(agentStderr);
        if (jsonResult is not null) return jsonResult;

        return TryParseHumanReadable(agentStdout) ?? TryParseHumanReadable(agentStderr);
    }

    private static AgentCostSnapshot? TryParseJsonOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (text.IndexOf("usage", StringComparison.OrdinalIgnoreCase) < 0) return null;

        AgentCostSnapshot? lastLineSnapshot = null;

        foreach (var line in text.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains("usage", StringComparison.OrdinalIgnoreCase)) continue;
            if (!line.StartsWith('{') && !line.Contains("{")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var snapshot = ExtractFromDoc(doc.RootElement);
                if (snapshot is not null) lastLineSnapshot = snapshot;
            }
            catch (JsonException) { }
        }

        if (lastLineSnapshot is not null) return lastLineSnapshot;

        try
        {
            using var doc = JsonDocument.Parse(text.Trim());
            return ExtractFromDoc(doc.RootElement);
        }
        catch (JsonException) { }

        return null;
    }

    private static AgentCostSnapshot? ExtractFromDoc(JsonElement root)
    {
        var usage = CodexUsageParser.TryExtract(root);
        if (usage is null) return null;

        string? modelId = null;

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("model", out var m)
            && m.ValueKind == JsonValueKind.String)
        {
            var raw = m.GetString();
            modelId = raw is { Length: > 128 } ? raw[..128] : raw;
        }

        return new AgentCostSnapshot(
            usage.Value.InputTokens,
            usage.Value.CachedInputTokens,
            usage.Value.OutputTokens,
            modelId);
    }

    private static AgentCostSnapshot? TryParseHumanReadable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var promptM = UsagePromptPattern.Match(text);
        var completionM = UsageCompletionPattern.Match(text);
        if (promptM.Success && completionM.Success)
        {
            var cached = TryParseCachedTokens(text);
            var input = TokenUsageAccounting.FreshInputTokens(ParseTokenCount(promptM.Groups[1].Value), cached);
            var output = ParseTokenCount(completionM.Groups[1].Value);
            return new AgentCostSnapshot(input, cached, output, null);
        }

        var inputM = InputPattern.Match(text);
        var outputM = OutputPattern.Match(text);
        if (inputM.Success && outputM.Success)
        {
            var cached = TryParseCachedTokens(text);
            var input = TokenUsageAccounting.FreshInputTokens(ParseTokenCount(inputM.Groups[1].Value), cached);
            var output = ParseTokenCount(outputM.Groups[1].Value);
            if (input > 0 || cached > 0 || output > 0)
                return new AgentCostSnapshot(input, cached, output, null);
        }

        return null;
    }

    private static int TryParseCachedTokens(string text)
    {
        var label = UsageCachedPattern.Match(text);
        if (label.Success)
            return ParseTokenCount(label.Groups[1].Value);

        var compact = CachedPattern.Match(text);
        return compact.Success ? ParseTokenCount(compact.Groups[1].Value) : 0;
    }

    private static int ParseTokenCount(string s)
    {
        var cleaned = s.Replace(",", "", StringComparison.Ordinal);
        return int.TryParse(cleaned, out var v) ? v : 0;
    }
}
