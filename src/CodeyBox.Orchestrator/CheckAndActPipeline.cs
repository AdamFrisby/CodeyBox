using System.Text;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Pure helpers for the check-and-act pipeline path: building the
/// agent-facing prompt that wraps the operator's question with the
/// verdict-protocol scaffold, and parsing the resulting structured JSON
/// verdict back out of agent stdout. Split from <see cref="PipelineRunner"/>
/// so the prompt shape and the parser are unit-testable without spinning
/// up sandboxes, repositories, or agents.
/// </summary>
internal static class CheckAndActPipeline
{
    /// <summary>Sentinel that marks the START of the agent's JSON verdict block.</summary>
    public const string StartSentinel = "<<<CODEYBOX_VERDICT>>>";

    /// <summary>Sentinel that marks the END of the agent's JSON verdict block.</summary>
    public const string EndSentinel = "<<<END_VERDICT>>>";

    public const string CompletionSystemBlock = """
        You are a no-tools code review completion for CodeyBox check-and-act.
        Evaluate only the supplied repository context and the specific yes/no question.
        Do not claim to have searched files or run commands. Do not suggest code changes unless the evidence requires the actionable answer.
        Return only the required verdict envelope.
        """;

    /// <summary>
    /// Builds the agent prompt for a check-and-act item: prepends the
    /// verdict-protocol scaffolding (sentinels, JSON schema, rules) to the
    /// operator's <see cref="CheckAndActSpec.Question"/>. The agent is told
    /// it must NOT commit, push, or modify the repo — this is a read-only
    /// audit and the only "output" the orchestrator consumes is the JSON
    /// payload between the sentinels.
    /// </summary>
    public static string BuildPrompt(CheckAndActSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append("# Check-and-Act task\n\n");
        sb.Append("You are evaluating a yes/no question against the current repository. ");
        sb.Append("Read the code, gather evidence by searching/grepping/reading files, ");
        sb.Append("and respond with a structured verdict.\n\n");
        sb.Append("## Question\n\n");
        sb.Append(spec.Question);
        sb.Append("\n\n## Response protocol (REQUIRED)\n\n");
        sb.Append("Output EXACTLY ONE JSON object enclosed by these sentinels, each on its own line:\n\n");
        sb.Append("```\n");
        sb.Append(StartSentinel);
        sb.Append('\n');
        sb.Append("{\"answer\": <true|false>, \"evidence\": \"<short citation>\", \"confidence\": \"<high|medium|low>\"}\n");
        sb.Append(EndSentinel);
        sb.Append("\n```\n\n");
        sb.Append("Rules:\n");
        sb.Append("- `answer` MUST be the JSON boolean literal `true` or `false` (no quotes).\n");
        sb.Append("- `evidence` MUST be a non-empty JSON string. Cite specific files / functions / patterns observed in the repo.\n");
        sb.Append("- `confidence` is optional but when present MUST be exactly `high`, `medium`, or `low`.\n");
        sb.Append("- The JSON object MUST live between the two sentinels. Other text outside the sentinels is fine but is ignored.\n");
        sb.Append("- Do NOT commit changes, open PRs, push branches, or modify any tracked files — this is a READ-ONLY audit.\n");
        sb.Append("- Do not echo this protocol back. Emit the sentinels and the JSON exactly once at the end of your run.\n");
        return sb.ToString();
    }

    public static CheckAndActCompletionPromptBlocks BuildCompletionPromptBlocks(
        CheckAndActSpec spec,
        string reviewContext)
    {
        var reviewBlock = new StringBuilder();
        reviewBlock.AppendLine("## Code / Diff Under Review");
        reviewBlock.AppendLine();
        reviewBlock.Append(string.IsNullOrWhiteSpace(reviewContext)
            ? "(No repository context was available.)"
            : reviewContext.Trim());

        var questionBlock = new StringBuilder();
        questionBlock.AppendLine("## Check Question");
        questionBlock.AppendLine();
        questionBlock.AppendLine(spec.Question.Trim());
        questionBlock.AppendLine();
        questionBlock.AppendLine("## Response Protocol");
        questionBlock.AppendLine();
        questionBlock.AppendLine("Output exactly one JSON verdict enclosed by these sentinels, each on its own line:");
        questionBlock.AppendLine();
        questionBlock.AppendLine(StartSentinel);
        questionBlock.AppendLine("{\"answer\": <true|false>, \"evidence\": \"<short citation>\", \"confidence\": \"<high|medium|low>\"}");
        questionBlock.AppendLine(EndSentinel);
        questionBlock.AppendLine();
        questionBlock.AppendLine("Rules:");
        questionBlock.AppendLine("- `answer` MUST be the JSON boolean literal `true` or `false`.");
        questionBlock.AppendLine("- `evidence` MUST be a non-empty JSON string grounded in the supplied code/diff block.");
        questionBlock.AppendLine("- `confidence` is optional but when present MUST be exactly `high`, `medium`, or `low`.");
        questionBlock.AppendLine("- Do not include markdown fences around the verdict.");

        return new CheckAndActCompletionPromptBlocks(
            CompletionSystemBlock.Trim(),
            reviewBlock.ToString().Trim(),
            questionBlock.ToString().Trim());
    }

    /// <summary>
    /// Extracts a <see cref="CheckVerdict"/> from agent stdout by locating the
    /// <see cref="StartSentinel"/> / <see cref="EndSentinel"/> block and
    /// deserialising the JSON between them. Returns true and populates
    /// <paramref name="verdict"/> on success; otherwise returns false and a
    /// short <paramref name="error"/> describing the failure. The parser is
    /// strict: missing sentinels, missing required fields, or unparsable JSON
    /// all fail rather than guessing. When multiple verdict blocks appear
    /// (e.g. the agent echoed the protocol earlier in its trace), the LAST
    /// block wins — agents that "decide" mid-run and then revise are honored.
    /// </summary>
    public static bool TryParseVerdict(string? stdout, out CheckVerdict? verdict, out string error)
    {
        verdict = null;
        if (string.IsNullOrEmpty(stdout))
        {
            error = "agent produced no stdout to parse for a verdict";
            return false;
        }

        var startIdx = stdout.LastIndexOf(StartSentinel, StringComparison.Ordinal);
        if (startIdx < 0)
        {
            error = $"verdict start sentinel '{StartSentinel}' not found in agent stdout";
            return false;
        }
        var afterStart = startIdx + StartSentinel.Length;
        var endIdx = stdout.IndexOf(EndSentinel, afterStart, StringComparison.Ordinal);
        if (endIdx < 0)
        {
            error = $"verdict end sentinel '{EndSentinel}' not found after start sentinel";
            return false;
        }

        var raw = stdout[afterStart..endIdx].Trim();
        // Strip a leading ```json / ``` fence if the agent wrapped the JSON in one.
        if (raw.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = raw.IndexOf('\n');
            if (firstNewline >= 0) raw = raw[(firstNewline + 1)..].Trim();
        }
        if (raw.EndsWith("```", StringComparison.Ordinal))
            raw = raw[..^3].Trim();

        CheckVerdictJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<CheckVerdictJson>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"verdict JSON could not be parsed: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "verdict JSON deserialised to null";
            return false;
        }
        if (parsed.Answer is null)
        {
            error = "verdict is missing required boolean field 'answer'";
            return false;
        }
        if (string.IsNullOrWhiteSpace(parsed.Evidence))
        {
            error = "verdict is missing required non-empty string field 'evidence'";
            return false;
        }

        verdict = new CheckVerdict
        {
            Answer = parsed.Answer.Value,
            Evidence = parsed.Evidence.Trim(),
            Confidence = string.IsNullOrWhiteSpace(parsed.Confidence) ? null : parsed.Confidence.Trim(),
        };
        error = "";
        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record CheckVerdictJson(bool? Answer, string? Evidence, string? Confidence);
}

public sealed record CheckAndActCompletionPromptBlocks(
    string SystemBlock,
    string ReviewBlock,
    string QuestionBlock)
{
    public string CacheablePrefix => $"[1: fixed generic system prompt]\n{SystemBlock}\n\n[2: the code/diff under review]\n{ReviewBlock}";

    public string Render() => $"{CacheablePrefix}\n\n[3: the specific check question]\n{QuestionBlock}";
}
