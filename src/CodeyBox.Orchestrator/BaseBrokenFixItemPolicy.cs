using System.Text;
using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Pure text builders for the auto-filed base-fix work item.
///
/// Trust model: the carried strings (branch name, base build output) are
/// untrusted — branch-controlled content and build logs flow from repo
/// content through the compiler. The agent-consumed <see cref="BuildPrompt"/>
/// therefore embeds NO build output at all: the log is never prompt text,
/// so no log phrasing can reach the tool-bearing agent as instructions. The
/// captured excerpt is available to the operator only, via the
/// project-level condition record and the fix item's
/// operator-held note (<see cref="BuildOperatorNote"/>), neither of which
/// is composed into any agent prompt. The filed item is parked for
/// explicit operator approval before any dispatch, so even a crafted
/// branch name in the prompt cannot self-dispatch: an operator must
/// review and retry it first.
/// </summary>
internal static partial class BaseBrokenFixItemPolicy
{
    /// <summary>Maximum characters for the fix item title.</summary>
    public const int MaxTitleChars = 200;

    /// <summary>Maximum characters for a carried branch name.</summary>
    public const int MaxBranchChars = 128;

    /// <summary>Maximum characters for the carried base-build output excerpt.</summary>
    public const int MaxBuildOutputChars = 8 * 1024;

    /// <summary>Characters of the SHA shown in the title.</summary>
    private const int ShortShaChars = 10;

    /// <summary>
    /// Builds the fix item title from the base branch and broken SHA only —
    /// no build output (untrusted, and a title is rendered to terminals and
    /// embedded in later LLM prompts).
    /// </summary>
    public static string BuildTitle(string baseBranch, string baseSha)
    {
        var branch = SanitizeSingleLine(baseBranch, MaxBranchChars);
        if (branch.Length == 0)
            branch = "base";
        var sha = SanitizeSingleLine(baseSha, 64);
        var shortSha = sha.Length > ShortShaChars ? sha[..ShortShaChars] : sha;
        var title = $"[Base build] Fix broken base branch '{branch}' ({shortSha})";
        return title.Length <= MaxTitleChars ? title : title[..MaxTitleChars];
    }

    /// <summary>
    /// Builds the fix prompt: the base is broken at a known SHA and the
    /// agent must reproduce the failure itself. The captured build output
    /// is deliberately NOT embedded here — build logs are repo-derived
    /// content and must never reach the tool-bearing agent as prompt text,
    /// quoted or otherwise. The operator reviews the excerpt (condition
    /// record / held-item note) and approves this item before any dispatch.
    /// The detecting item appears by opaque id only — its caller-authored
    /// title is never embedded, because no sanitization can stop crafted
    /// title phrasing ("ignore previous instructions") from surviving as
    /// language the tool-bearing agent must interpret.
    /// </summary>
    public static string BuildPrompt(
        string baseBranch,
        string baseSha,
        WorkItemId parentId)
    {
        var branch = SanitizeSingleLine(
            string.IsNullOrWhiteSpace(baseBranch) ? "main" : baseBranch.Trim(), MaxBranchChars);
        if (branch.Length == 0)
            branch = "main";
        var sha = SanitizeSingleLine(baseSha, 64);

        var sb = new StringBuilder();
        sb.AppendLine($"The base branch '{branch}' does not build at tip {sha}. The failure is in the base itself, not in any work item's diff — multiple work items failed their required-build gate on this same base. An operator reviewed the captured failure and approved this repair. Your job is to repair the base branch so the required build (dotnet build) passes again.");
        sb.AppendLine();
        sb.AppendLine($"Detecting work item: {parentId}");
        sb.AppendLine($"Base branch: {branch}");
        sb.AppendLine($"Broken base tip: {sha}");
        sb.AppendLine();
        sb.AppendLine("The captured build output is deliberately not included here: build logs are untrusted repo-derived content and must never arrive as prompt text. Reproduce the failure yourself by checking out the recorded tip of the base branch in isolation and running the required build, then fix the compile/build errors at their source.");
        sb.AppendLine();
        sb.AppendLine("Steps:");
        sb.AppendLine("1. Reproduce the failure on the base branch in isolation (the build is broken at the recorded tip).");
        sb.AppendLine("2. Fix the compile/build errors at their source so `dotnet build` succeeds on the base branch tip.");
        sb.AppendLine("3. Keep the change minimal and scoped to the build failure; unrelated refactors mask the regression and widen review scope.");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the operator-held note stored on the fix item (its
    /// <c>LastError</c>, never composed into any agent prompt): why the
    /// item is parked for approval plus a bounded excerpt of the captured
    /// base build output for triage. The excerpt is untrusted repo-derived
    /// text rendered to operator surfaces, so it is control-stripped,
    /// length-bounded, and scrubbed of instruction-override phrasing —
    /// defense in depth for a non-prompt sink.
    /// </summary>
    public static string BuildOperatorNote(
        string baseBranch,
        string baseSha,
        string? baseBuildOutput)
    {
        var branch = SanitizeSingleLine(
            string.IsNullOrWhiteSpace(baseBranch) ? "main" : baseBranch.Trim(), MaxBranchChars);
        if (branch.Length == 0)
            branch = "main";
        var sha = SanitizeSingleLine(baseSha, 64);
        var excerpt = SanitizeBuildExcerpt(baseBuildOutput);
        return $"Held for operator approval: base branch '{branch}' tip {sha} fails the required build. " +
            "Review the captured excerpt, then retry this item to approve agent dispatch. " +
            $"Captured base build excerpt (untrusted data): {excerpt}";
    }

    /// <summary>
    /// Bounds and neutralizes captured build output for the operator-held
    /// note. The verifier already redacts and byte-truncates the log; this
    /// adds control-character stripping, instruction-override scrubbing, and
    /// a length bound so a crafted log line survives on an operator-only
    /// surface as inert text. Never used for prompt embedding: no build
    /// output reaches the agent as prompt text.
    /// </summary>
    private static string SanitizeBuildExcerpt(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "(no build output captured)";

        var stripped = StripControl(output);
        var bounded = stripped.Length > MaxBuildOutputChars
            ? stripped[..MaxBuildOutputChars]
            : stripped;
        return ScrubInstructionTriggers(bounded);
    }

    /// <summary>
    /// Instruction-override phrasing that survives intact inside quoted
    /// build output (multi-word imperative patterns only — never single
    /// words, so genuine compiler vocabulary such as the <c>override</c>
    /// keyword suggested by CS0114 is untouched). Best-effort sink guard,
    /// applied after bounding: the trigger plus the rest of its line (the
    /// attack payload rides the same line) is replaced with an inert
    /// placeholder so the surrounding diagnostic context stays readable.
    /// </summary>
    [GeneratedRegex(
        @"(?i)\b(ignore|disregard|forget)\s+(all\s+|any\s+|the\s+|your\s+)?(previous|prior|above|earlier\s+)?\s*(instructions?|orders|directives)\b[^\r\n]*"
        + @"|(?i)\b(follow|obey|execute|run)\s+(these\s+|the\s+following\s+)?(new\s+)?(instructions?|commands?|directives)\b[^\r\n]*"
        + @"|(?i)\bnew\s+(system\s+)?instructions?\s*:[^\r\n]*"
        + @"|(?i)\breveal\s+(your\s+)?(system\s+)?(prompt|instructions?)\b[^\r\n]*",
        RegexOptions.CultureInvariant)]
    private static partial Regex InstructionTriggerRegex();

    internal static string ScrubInstructionTriggers(string value)
        => InstructionTriggerRegex().Replace(value, "[instruction-like text withheld]");

    /// <summary>
    /// Removes control characters, ANSI escapes' escape byte, DEL, and the
    /// bidi/zero-width overrides from untrusted text while keeping ordinary
    /// newlines/tabs. Used anywhere build output or repo-controlled strings
    /// are stored, logged, or embedded in a prompt.
    /// </summary>
    internal static string StripControl(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '\x1B' || c == 0x7F || (c < 0x20 && c is not ('\n' or '\r' or '\t')))
                continue;
            if (c is '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E'
                or '\u2066' or '\u2067' or '\u2068' or '\u2069' or '\uFEFF')
                continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string SanitizeSingleLine(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in StripControl(value))
            sb.Append(c is '\r' or '\n' ? ' ' : c);
        var trimmed = sb.ToString().Trim();
        return trimmed.Length > maxChars ? trimmed[..maxChars] : trimmed;
    }
}
