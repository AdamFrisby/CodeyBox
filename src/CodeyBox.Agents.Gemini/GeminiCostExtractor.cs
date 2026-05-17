using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Gemini;

/// <summary>
/// Extracts token counts from Gemini CLI output (@google/gemini-cli).
///
/// Gemini CLI emits usage metadata in text like:
///   "Prompt tokens: 12345"
///   "Candidates tokens: 678"
/// or in a JSON summary block.
/// </summary>
public sealed class GeminiCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Gemini;

    public ModelRateConfig? DefaultPricing { get; } = new()
    {
        InputPerMillion = 7.0,
        CachedInputPerMillion = 0.70,
        OutputPerMillion = 21.0,
    };

    private static readonly Regex PromptPattern = new(
        @"[Pp]rompt\s+tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CandidatesPattern = new(
        @"[Cc]andidates?\s+tokens?[:\s]+(\d[\d,]*)",
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

        var jsonResult = TryParseJson(agentStdout) ?? TryParseJson(agentStderr);
        if (jsonResult is not null) return jsonResult;

        return TryParseHumanReadable(agentStdout) ?? TryParseHumanReadable(agentStderr);
    }

    private static AgentCostSnapshot? TryParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var line in text.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            if (!line.Contains("token", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("promptTokenCount", out var ptc) && ptc.TryGetInt32(out var input))
                {
                    var output = root.TryGetProperty("candidatesTokenCount", out var ctc) && ctc.TryGetInt32(out var ov)
                        ? ov : 0;
                    string? raw = root.TryGetProperty("model", out var m) ? m.GetString() : null;
                    string? model = raw is { Length: > 128 } ? raw[..128] : raw;
                    return new AgentCostSnapshot(input, 0, output, model);
                }
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static AgentCostSnapshot? TryParseHumanReadable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var promptM = PromptPattern.Match(text);
        var candidatesM = CandidatesPattern.Match(text);
        if (promptM.Success && candidatesM.Success)
        {
            var input = ParseTokenCount(promptM.Groups[1].Value);
            var output = ParseTokenCount(candidatesM.Groups[1].Value);
            return new AgentCostSnapshot(input, 0, output, null);
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
