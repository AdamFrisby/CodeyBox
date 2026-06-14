using System.Globalization;
using System.Text;
using CodeyBox.Agents;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Default <see cref="ICrossAgentHandoffBriefBuilder"/>: composes a condensed
/// summary of what the prior agent did (parsed from the host-captured agent
/// stream) and the state of the work branch (last few commits + branch diff
/// stat against the project's base branch) so the fallback agent can pick up
/// where the prior one left off without a from-scratch restart.
/// <para>
/// Design notes:
/// <list type="bullet">
/// <item><description>Returns <c>null</c> on any failure — the preprocessor
/// treats null as "skip the injection" and the fallback agent runs from
/// scratch as today. Never throws.</description></item>
/// <item><description>Gated on <see cref="PipelineTuningOptions.EnableHandoffSeeding"/>;
/// returns <c>null</c> when the flag is off so an operator can disable the
/// feature without unregistering the builder.</description></item>
/// <item><description>Produces a SUMMARY — bounded counts, recent commit
/// subjects, branch diff stat, a short tail of the prior agent's final
/// assistant message — NOT a raw transcript replay. Raw replay would confuse
/// the fallback agent with the prior agent's tool-call framing.</description></item>
/// <item><description>The preprocessor sanitises and fences the returned text
/// (<c>NeutraliseStructuralDelimiters</c>, <c>LimitBriefText</c>,
/// <c>[UNTRUSTED DATA SECTION]</c> markers); the builder itself applies a
/// smaller pre-cap so the unsanitised text it constructs stays well below
/// the preprocessor's 32 KiB ceiling.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class AgentStreamBriefBuilder : ICrossAgentHandoffBriefBuilder
{
    /// <summary>
    /// Upper bound on the brief size the builder produces. Defence in depth:
    /// the preprocessor will re-cap at 32 KiB, but the builder caps earlier
    /// so a runaway parser output (e.g. a stream with a single very large
    /// final-assistant message) does not have to round-trip through the
    /// preprocessor before being clamped.
    /// </summary>
    internal const int MaxBriefChars = 8 * 1024;

    /// <summary>
    /// Per-section cap on the prior agent's final-assistant message tail.
    /// The full message can be many KiB; the brief only needs the closing
    /// thought.
    /// </summary>
    internal const int MaxFinalMessageTailChars = 2_000;

    /// <summary>
    /// Number of recent commits to include in the brief. Subjects only — the
    /// full diff lives in the working tree the fallback agent already has on
    /// disk.
    /// </summary>
    internal const int RecentCommitCount = 5;

    /// <summary>
    /// Number of tool-call kinds to enumerate. Beyond this the brief just
    /// says "+ N others" so a tool-call-heavy stream cannot blow the budget.
    /// </summary>
    internal const int MaxToolKindsListed = 6;

    private readonly IAgentStreamStore? _streams;
    private readonly IReadOnlyDictionary<AgentKind, IAgentStreamParser> _parsers;
    private readonly PipelineTuningSnapshot _tuning;
    private readonly ILogger<AgentStreamBriefBuilder> _log;

    public AgentStreamBriefBuilder(
        PipelineTuningSnapshot tuning,
        ILogger<AgentStreamBriefBuilder> log,
        IEnumerable<IAgentStreamParser>? parsers = null,
        IAgentStreamStore? streams = null)
    {
        _tuning = tuning;
        _log = log;
        _streams = streams;
        _parsers = (parsers ?? Array.Empty<IAgentStreamParser>())
            .GroupBy(p => p.Kind)
            .ToDictionary(g => g.Key, g => g.First());
    }

    public async Task<string?> BuildAsync(PromptContext ctx, AgentKind priorAgent, CancellationToken ct = default)
    {
        if (!_tuning.Current.EnableHandoffSeeding)
            return null;

        try
        {
            var streamSummary = await TryReadPriorStreamSummaryAsync(ctx, priorAgent, ct).ConfigureAwait(false);
            var branchSummary = await TryReadBranchSummaryAsync(ctx, ct).ConfigureAwait(false);

            if (streamSummary is null && branchSummary is null)
                return null;

            return Compose(priorAgent, ctx.AgentKind, streamSummary, branchSummary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                ex,
                "Cross-agent handoff brief construction failed for work item {WorkItemId} ({PriorAgent} -> {CurrentAgent}); fallback will run from-scratch",
                ctx.ItemId,
                priorAgent.Value,
                ctx.AgentKind.Value);
            return null;
        }
    }

    private async Task<AgentStreamSummary?> TryReadPriorStreamSummaryAsync(
        PromptContext ctx,
        AgentKind priorAgent,
        CancellationToken ct)
    {
        if (_streams is null)
            return null;
        if (!_parsers.TryGetValue(priorAgent, out var parser))
            return null;

        var files = await _streams.ListAsync(ctx.ItemId, limit: AgentStreamStore.MaxListLimit, includeLineCount: false, ct).ConfigureAwait(false);
        if (files.Count == 0)
            return null;

        // Same phase + iteration as the new invocation: this is the cross-
        // agent fallback within a single phase iteration, so the prior
        // agent's capture lives under the same (phase, iteration). We pick
        // the most recently captured matching file.
        var phase = ctx.Phase.Value;
        var match = files
            .Where(f => string.Equals(f.Phase, phase, StringComparison.OrdinalIgnoreCase) && f.Iteration == ctx.Iteration)
            .OrderByDescending(f => f.CapturedAt)
            .FirstOrDefault();
        if (match is null)
            return null;

        await using var stream = await _streams.OpenReadAsync(ctx.ItemId, match.FileName, ct).ConfigureAwait(false);
        if (stream is null)
            return null;

        var summary = await parser.ParseAsync(stream, ct).ConfigureAwait(false);
        return summary.IsUnsupported ? null : summary;
    }

    private async Task<BranchSummary?> TryReadBranchSummaryAsync(PromptContext ctx, CancellationToken ct)
    {
        var workDir = ctx.WorkingDirectory;
        var baseBranch = ctx.Project.DefaultBaseBranch;

        var headSha = await GitOutputAsync(ctx.Sandbox, workDir, ct, "rev-parse", "--short", "HEAD").ConfigureAwait(false);
        var recentCommits = await GitOutputAsync(
            ctx.Sandbox,
            workDir,
            ct,
            "log",
            "--no-color",
            "--max-count", RecentCommitCount.ToString(CultureInfo.InvariantCulture),
            "--pretty=format:%h %s").ConfigureAwait(false);

        string? diffStat = null;
        if (!string.IsNullOrWhiteSpace(baseBranch))
        {
            // origin/<base> first — the agent runs against a clone with origin
            // configured. Fall back to the local ref if origin tracking is
            // missing (e.g. detached / non-default test fixtures).
            diffStat = await GitOutputAsync(ctx.Sandbox, workDir, ct, "diff", "--stat", $"origin/{baseBranch}...HEAD").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(diffStat))
                diffStat = await GitOutputAsync(ctx.Sandbox, workDir, ct, "diff", "--stat", $"{baseBranch}...HEAD").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(headSha)
            && string.IsNullOrWhiteSpace(recentCommits)
            && string.IsNullOrWhiteSpace(diffStat))
            return null;

        return new BranchSummary(headSha, recentCommits, diffStat);
    }

    private static async Task<string?> GitOutputAsync(
        ISandbox sandbox,
        string workDir,
        CancellationToken ct,
        params string[] args)
    {
        try
        {
            var argv = new List<string> { "git", "-C", workDir };
            argv.AddRange(args);
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = argv,
                MaxStdoutBytes = 8 * 1024,
                MaxStderrBytes = 4 * 1024,
            }, ct).ConfigureAwait(false);
            if (!result.Success)
                return null;
            return result.Stdout?.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sandbox already torn down, exec unavailable, etc. — caller
            // treats null as "missing section" and still emits a brief from
            // whatever else was readable.
            _ = ex;
            return null;
        }
    }

    internal static string Compose(
        AgentKind priorAgent,
        AgentKind currentAgent,
        AgentStreamSummary? streamSummary,
        BranchSummary? branchSummary)
    {
        var sb = new StringBuilder();
        sb.Append("Prior agent: ").Append(priorAgent.Value).Append('\n');
        sb.Append("Now routed to: ").Append(currentAgent.Value).Append('\n');

        if (streamSummary is not null)
        {
            sb.Append('\n');
            sb.Append("Prior agent execution summary:\n");
            sb.Append("- duration: ").Append(FormatDuration(streamSummary.TotalDuration)).Append('\n');
            if (streamSummary.InputTokens > 0 || streamSummary.OutputTokens > 0)
            {
                sb.Append("- tokens: input=").Append(streamSummary.InputTokens)
                    .Append(" output=").Append(streamSummary.OutputTokens);
                if (streamSummary.CachedInputTokens > 0)
                    sb.Append(" cached=").Append(streamSummary.CachedInputTokens);
                sb.Append('\n');
            }
            if (streamSummary.ToolCalls.Count > 0)
            {
                sb.Append("- tool calls (").Append(streamSummary.ToolCalls.Count).Append("): ");
                AppendToolKinds(sb, streamSummary.ToolCalls);
                sb.Append('\n');
            }
            if (streamSummary.Stalls.Count > 0)
            {
                sb.Append("- stalls observed: ").Append(streamSummary.Stalls.Count).Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(streamSummary.FinalAssistantMessage))
            {
                sb.Append('\n');
                sb.Append("Prior agent's closing message (tail):\n");
                sb.Append(TakeTail(streamSummary.FinalAssistantMessage, MaxFinalMessageTailChars));
                sb.Append('\n');
            }
        }

        if (branchSummary is not null)
        {
            sb.Append('\n');
            sb.Append("Branch state on disk (already shared with you via the work tree):\n");
            if (!string.IsNullOrWhiteSpace(branchSummary.HeadSha))
                sb.Append("- HEAD: ").Append(branchSummary.HeadSha).Append('\n');
            if (!string.IsNullOrWhiteSpace(branchSummary.RecentCommits))
            {
                sb.Append("- recent commits:\n");
                foreach (var line in branchSummary.RecentCommits.Split('\n'))
                {
                    var trimmed = line.TrimEnd();
                    if (trimmed.Length == 0)
                        continue;
                    sb.Append("    ").Append(trimmed).Append('\n');
                }
            }
            if (!string.IsNullOrWhiteSpace(branchSummary.DiffStat))
            {
                sb.Append("- diff vs base (--stat):\n");
                foreach (var line in branchSummary.DiffStat.Split('\n'))
                {
                    var trimmed = line.TrimEnd();
                    if (trimmed.Length == 0)
                        continue;
                    sb.Append("    ").Append(trimmed).Append('\n');
                }
            }
        }

        sb.Append('\n');
        sb.Append("Continue the work item from this point: review the on-disk commits, decide what is still needed, and either build on the prior agent's progress or correct it. Do not redo finished work.");

        var brief = sb.ToString();
        if (brief.Length > MaxBriefChars)
            brief = brief[..MaxBriefChars];
        return brief;
    }

    private static void AppendToolKinds(StringBuilder sb, IReadOnlyList<ToolCallInvocation> toolCalls)
    {
        var counts = toolCalls
            .GroupBy(t => string.IsNullOrWhiteSpace(t.ToolName) ? "unknown" : t.ToolName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(p => p.Count)
            .ToList();
        var listed = 0;
        for (var i = 0; i < counts.Count && i < MaxToolKindsListed; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(counts[i].Name).Append('×').Append(counts[i].Count);
            listed++;
        }
        if (counts.Count > listed)
        {
            sb.Append(", +").Append(counts.Count - listed).Append(" other kinds");
        }
    }

    internal static string FormatDuration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
            return "0s";
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes}m{span.Seconds}s";
        return $"{(int)span.TotalSeconds}s";
    }

    internal static string TakeTail(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        var cut = text.Length - maxChars;
        // Avoid splitting a surrogate pair.
        if (cut > 0 && char.IsLowSurrogate(text[cut]))
            cut++;
        return "[...truncated]\n" + text[cut..];
    }

    internal sealed record BranchSummary(string? HeadSha, string? RecentCommits, string? DiffStat);
}
