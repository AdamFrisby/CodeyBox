using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Best-effort extractor for token counts from an agent CLI's captured stdout/stderr.
/// Implementations must never throw and must return null when the output does not
/// contain recognisable token counts (older CLI version, plain-text mode, etc.).
/// </summary>
public interface IAgentCostExtractor
{
    AgentKind Kind { get; }

    /// <summary>
    /// Attempts to extract token counts from captured CLI output.
    /// Returns null if the output doesn't contain token counts.
    /// Never throws.
    /// </summary>
    AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr);
}

/// <summary>
/// Token snapshot extracted from a single agent CLI invocation.
/// </summary>
public sealed record AgentCostSnapshot(
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    string? ModelId);

/// <summary>
/// Extracts token counts from Claude Code CLI output.
///
/// Tries two formats in order:
/// 1. NDJSON stream-json (--output-format stream-json): reads the final "result" event's
///    "usage" object and the "model" field from any "assistant" event.
/// 2. Human-readable footer: matches patterns like
///    "Total cost: $0.12 (12,345 input, 678 output, 5,000 cached tokens)"
///    or "Total input tokens: 12,345" / "Total output tokens: 678".
/// Returns null when neither format yields token counts.
/// </summary>
public sealed class ClaudeCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Claude;

    private static readonly Regex InputPattern = new(
        @"(\d[\d,]*)\s+input\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OutputPattern = new(
        @"(\d[\d,]*)\s+output\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CachedPattern = new(
        @"(\d[\d,]*)\s+cached(?:\s+input)?\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TotalInputPattern = new(
        @"(?:Total\s+)?[Ii]nput\s+tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TotalOutputPattern = new(
        @"(?:Total\s+)?[Oo]utput\s+tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TotalCachedPattern = new(
        @"[Cc]ache(?:d)?\s+(?:input\s+)?tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        if (string.IsNullOrWhiteSpace(agentStdout) && string.IsNullOrWhiteSpace(agentStderr))
            return null;

        // Strategy 1: NDJSON stream-json output
        var ndJson = TryParseNdJson(agentStdout);
        if (ndJson is not null) return ndJson;

        // Strategy 2: human-readable footer in either stream
        return TryParseHumanReadable(agentStdout) ?? TryParseHumanReadable(agentStderr);
    }

    private static AgentCostSnapshot? TryParseNdJson(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;

        var first = stdout.AsSpan().TrimStart();
        if (first.IsEmpty || first[0] != '{') return null;

        int inputTokens = 0, outputTokens = 0, cachedTokens = 0;
        string? modelId = null;
        bool foundUsage = false;

        foreach (var line in stdout.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString();

                if (type == "result" && root.TryGetProperty("usage", out var usage))
                {
                    inputTokens = usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv) ? itv : 0;
                    outputTokens = usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv) ? otv : 0;
                    cachedTokens = usage.TryGetProperty("cache_read_input_tokens", out var ct) && ct.TryGetInt32(out var ctv) ? ctv : 0;
                    foundUsage = true;
                }
                else if (type == "assistant" && modelId is null
                    && root.TryGetProperty("message", out var msg)
                    && msg.TryGetProperty("model", out var m))
                {
                    modelId = m.GetString();
                }
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }

        if (!foundUsage) return null;
        return new AgentCostSnapshot(inputTokens, cachedTokens, outputTokens, modelId);
    }

    private static AgentCostSnapshot? TryParseHumanReadable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Try compact form first: "12,345 input tokens, 678 output tokens, 5,000 cached tokens"
        var inputM = InputPattern.Match(text);
        var outputM = OutputPattern.Match(text);
        if (inputM.Success && outputM.Success)
        {
            var input = ParseTokenCount(inputM.Groups[1].Value);
            var output = ParseTokenCount(outputM.Groups[1].Value);
            var cachedM = CachedPattern.Match(text);
            var cached = cachedM.Success ? ParseTokenCount(cachedM.Groups[1].Value) : 0;
            if (input > 0 || output > 0)
                return new AgentCostSnapshot(input, cached, output, null);
        }

        // Try line-per-stat form: "Total input tokens: 12345"
        var tiM = TotalInputPattern.Match(text);
        var toM = TotalOutputPattern.Match(text);
        if (tiM.Success && toM.Success)
        {
            var input = ParseTokenCount(tiM.Groups[1].Value);
            var output = ParseTokenCount(toM.Groups[1].Value);
            var tcM = TotalCachedPattern.Match(text);
            var cached = tcM.Success ? ParseTokenCount(tcM.Groups[1].Value) : 0;
            if (input > 0 || output > 0)
                return new AgentCostSnapshot(input, cached, output, null);
        }

        return null;
    }

    private static int ParseTokenCount(string s)
    {
        var cleaned = s.Replace(",", "", StringComparison.Ordinal);
        return int.TryParse(cleaned, out var v) ? v : 0;
    }
}

/// <summary>
/// Extracts token counts from the OpenAI Codex CLI output.
///
/// Tries two formats:
/// 1. JSON output (--output-format json): looks for {"usage":{"prompt_tokens":N,"completion_tokens":M}}.
/// 2. Human-readable patterns: "Usage: N input, M output tokens".
/// </summary>
public sealed class CodexCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Codex;

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

        var jsonResult = TryParseJsonOutput(agentStdout) ?? TryParseJsonOutput(agentStderr);
        if (jsonResult is not null) return jsonResult;

        return TryParseHumanReadable(agentStdout) ?? TryParseHumanReadable(agentStderr);
    }

    private static AgentCostSnapshot? TryParseJsonOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Try to find a JSON object with a usage field anywhere in the output.
        var start = text.IndexOf("{\"usage\"", StringComparison.Ordinal);
        if (start < 0) start = text.IndexOf("\"usage\"", StringComparison.Ordinal);
        if (start < 0) return null;

        // Walk back to find the opening brace of the containing object.
        var objStart = start > 0 && text[start] != '{' ? text.LastIndexOf('{', start) : start;
        if (objStart < 0) objStart = start;

        foreach (var line in text.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains("usage", StringComparison.OrdinalIgnoreCase)) continue;
            if (!line.StartsWith('{') && !line.Contains("{")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var snapshot = ExtractFromDoc(doc.RootElement);
                if (snapshot is not null) return snapshot;
            }
            catch (JsonException) { }
        }

        // Try parsing the whole output as one JSON object.
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
        // {"usage":{"prompt_tokens":N,"completion_tokens":M,"total_tokens":T},"model":"..."}
        if (!root.TryGetProperty("usage", out var usage)) return null;

        int input = 0, output = 0;
        string? modelId = null;

        if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv)) input = ptv;
        else if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv)) input = itv;

        if (usage.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var ctv)) output = ctv;
        else if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv)) output = otv;

        if (root.TryGetProperty("model", out var m)) modelId = m.GetString();

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
            var input = ParseTokenCount(promptM.Groups[1].Value);
            var output = ParseTokenCount(completionM.Groups[1].Value);
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
                // {"promptTokenCount":N,"candidatesTokenCount":M,"totalTokenCount":T,"model":"..."}
                if (root.TryGetProperty("promptTokenCount", out var ptc) && ptc.TryGetInt32(out var input))
                {
                    var output = root.TryGetProperty("candidatesTokenCount", out var ctc) && ctc.TryGetInt32(out var ov)
                        ? ov : 0;
                    string? model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
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
