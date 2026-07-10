using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Built-in preprocessor that prepends the project's house rules file to agent
/// prompts, except separated-channel PlanReview verdict calls. Those calls keep
/// their user message as the exact bounded JSON envelope promised by the trusted
/// system contract. The rules path is read from
/// <see cref="IOptionsMonitor{TOptions}"/> on each invocation so config edits
/// hot-reload for the next agent run.
/// </summary>
public sealed class ProjectRulesPromptPreprocessor : IAgentPromptPreprocessor
{
    private const int MaxRulesBytes = 256 * 1024;

    // Lines that look like our fence delimiter (`---...`) or a markdown
    // header (`## ...`) are neutralised so a committer can't break out of
    // the BEGIN/END PROJECT RULES fence or impersonate the "## Agent prompt"
    // header that follows.
    private static readonly Regex StructuralLine = new(
        @"^[ \t]*(---+.*|##+\s.*)$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IOptionsMonitor<AgentPromptPreprocessingOptions> _options;
    private readonly ILogger<ProjectRulesPromptPreprocessor> _log;

    public ProjectRulesPromptPreprocessor(
        IOptionsMonitor<AgentPromptPreprocessingOptions> options,
        ILogger<ProjectRulesPromptPreprocessor> log)
    {
        _options = options;
        _log = log;
    }

    public int Order => AgentPromptPreprocessorOrder.BuiltInFirst;

    public async Task<string> ProcessAsync(PromptContext ctx, string prompt, CancellationToken ct = default)
    {
        if (ctx.Phase == AgentPromptPhase.PlanReview)
        {
            _log.LogDebug(
                "Project rules prompt preprocessor skipped plan-review prompt for work item {WorkItemId}",
                ctx.ItemId);
            return prompt;
        }

        var path = NormalizeRulesPath(_options.CurrentValue.ProjectRulesPath);
        if (path is null)
        {
            _log.LogWarning(
                "Project rules prompt preprocessor skipped invalid ProjectRulesPath '{Path}' for work item {WorkItemId}",
                _options.CurrentValue.ProjectRulesPath,
                ctx.ItemId);
            return prompt;
        }

        // Resolve the rules file relative to the agent's working directory in
        // the sandbox, not a hardcoded `/work`. The deep-audit path clones the
        // repo into `/work/repo`, so hardcoding `/work` made `head` exit
        // non-zero and silently dropped the rules block for every deep-audit
        // invocation.
        //
        // head -c bounds the read at the sandbox level: even if a work agent
        // writes a multi-GB AGENTS.md, the orchestrator only buffers ~256 KiB
        // (+ one byte) into stdout, preventing a DoS via oversized rules file.
        var workingDir = string.IsNullOrWhiteSpace(ctx.WorkingDirectory)
            ? SandboxConventions.WorkDir
            : ctx.WorkingDirectory;
        var read = await ctx.Sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["head", "-c", (MaxRulesBytes + 1).ToString(CultureInfo.InvariantCulture), "--", path],
            WorkingDirectory = workingDir,
        }, ct).ConfigureAwait(false);

        if (!read.Success)
        {
            _log.LogDebug(
                "Project rules file '{Path}' not found/readable for work item {WorkItemId}; prompt left unchanged",
                path,
                ctx.ItemId);
            return prompt;
        }

        var rules = LimitRulesText(read.Stdout);
        if (string.IsNullOrWhiteSpace(rules))
            return prompt;

        var sanitisedRules = NeutraliseStructuralDelimiters(rules.TrimEnd());

        return $$"""
            ## Project rules (must follow)

            Loaded from `{{path}}`.

            --- BEGIN PROJECT RULES ---
            {{sanitisedRules}}
            --- END PROJECT RULES ---

            ## Agent prompt

            {{prompt}}
            """;
    }

    private static string NeutraliseStructuralDelimiters(string text) =>
        StructuralLine.Replace(text, "​$&");

    private static string? NormalizeRulesPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = path.Trim().Replace('\\', '/');
        if (path[0] == '/'
            || path.Contains('\0')
            || path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(static part => part == ".."))
            return null;

        return path;
    }

    private static string LimitRulesText(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes <= MaxRulesBytes)
            return text;

        var maxChars = text.Length;
        while (maxChars > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, maxChars)) > MaxRulesBytes)
            maxChars /= 2;

        while (maxChars < text.Length && Encoding.UTF8.GetByteCount(text.AsSpan(0, maxChars + 1)) <= MaxRulesBytes)
            maxChars++;

        // Never split a UTF-16 surrogate pair: if the cut lands between a high
        // and low surrogate, step back one char so the truncated prefix is a
        // valid UTF-16 string and downstream re-encoding cannot emit U+FFFD.
        if (maxChars > 0 && maxChars < text.Length && char.IsHighSurrogate(text[maxChars - 1]))
            maxChars--;

        return text[..maxChars] + $"\n\n[Project rules truncated by CodeyBox at {MaxRulesBytes / 1024} KiB.]";
    }
}
