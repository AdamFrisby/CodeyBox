using System.Text;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Built-in preprocessor that prepends the project's house rules file to every
/// agent prompt. The rules path is read from <see cref="IOptionsMonitor{TOptions}"/>
/// on each invocation so config edits hot-reload for the next agent run.
/// </summary>
public sealed class ProjectRulesPromptPreprocessor : IAgentPromptPreprocessor
{
    private const int MaxRulesBytes = 256 * 1024;

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
        var path = NormalizeRulesPath(_options.CurrentValue.ProjectRulesPath);
        if (path is null)
        {
            _log.LogWarning(
                "Project rules prompt preprocessor skipped invalid ProjectRulesPath '{Path}' for work item {WorkItemId}",
                _options.CurrentValue.ProjectRulesPath,
                ctx.ItemId);
            return prompt;
        }

        var read = await ctx.Sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["cat", "--", path],
            WorkingDirectory = SandboxConventions.WorkDir,
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

        return $$"""
            ## Project rules (must follow)

            Loaded from `{{path}}`.

            --- BEGIN PROJECT RULES ---
            {{rules.TrimEnd()}}
            --- END PROJECT RULES ---

            ## Agent prompt

            {{prompt}}
            """;
    }

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

        return text[..maxChars] + "\n\n[Project rules truncated by CodeyBox at 256 KiB.]";
    }
}
