using System.Text.RegularExpressions;

namespace CodeyBox.Agents.Crock;

/// <summary>
/// Heuristic parser for <c>crock status &lt;task-id&gt;</c> output. The crock
/// CLI's exact stdout shape (plain text vs JSON, field naming) has not been
/// verified against a live binary in this environment, so the parser is
/// deliberately lenient: it scans for a small allowlist of state tokens that
/// any submit-then-poll batch CLI is likely to emit, and short-circuits on the
/// first terminal token it finds. If a future crock release ships a structured
/// JSON contract, replace this with a JSON deserialiser and update
/// <see cref="CrockAgentRunner"/>'s poll loop to consume it.
/// </summary>
public static class CrockStatusParser
{
    // Order matters: terminal kinds are checked before in-progress so an
    // output that includes both an in-progress historical line and a final
    // succeeded/failed marker resolves to the terminal verdict. The patterns
    // are case-insensitive and matched against the entire stdout/stderr blob.
    private static readonly Regex SucceededPattern = new(
        @"\b(succeeded|completed|success|finished|done|ok)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FailedPattern = new(
        @"\b(failed|failure|error|errored|cancelled|canceled|aborted|expired|timed[ _-]?out)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InProgressPattern = new(
        @"\b(in[ _-]?progress|running|pending|queued|submitted|processing|polling|waiting|started)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Classifies a <c>crock status</c> observation. <paramref name="stdout"/>
    /// and <paramref name="stderr"/> are concatenated so the parser is
    /// resilient to CLIs that print the state line to either stream.
    /// </summary>
    public static CrockTaskStatus Classify(string? stdout, string? stderr = null)
    {
        var blob = string.Concat(stdout ?? string.Empty, "\n", stderr ?? string.Empty);

        var failed = FailedPattern.Match(blob);
        if (failed.Success)
        {
            return new CrockTaskStatus(
                CrockTaskStateKind.Failed,
                failed.Value,
                $"state={failed.Value.ToLowerInvariant()}");
        }

        var succeeded = SucceededPattern.Match(blob);
        if (succeeded.Success)
        {
            return new CrockTaskStatus(
                CrockTaskStateKind.Succeeded,
                succeeded.Value,
                $"state={succeeded.Value.ToLowerInvariant()}");
        }

        var inProgress = InProgressPattern.Match(blob);
        if (inProgress.Success)
        {
            return new CrockTaskStatus(
                CrockTaskStateKind.InProgress,
                inProgress.Value,
                $"state={inProgress.Value.ToLowerInvariant()}");
        }

        return new CrockTaskStatus(
            CrockTaskStateKind.Unknown,
            StateToken: null,
            Summary: "no state token detected");
    }

    // Submit-step task-id extractor. The crock CLI documents `crock submit`
    // as "prints a task-id, then detaches"; the exact framing of the printed
    // id (bare uuid, `task-<uuid>`, JSON wrapper) is unverified. The regex
    // matches the common shapes observed in similar batch CLIs and falls back
    // to a final-non-empty-line heuristic so a single bare token still works.
    private static readonly Regex LabeledTaskIdPattern = new(
        @"task[-_]?id\s*[:=]\s*([A-Za-z0-9][A-Za-z0-9._\-]{2,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareTaskIdPattern = new(
        @"\b(task[-_][A-Za-z0-9._\-]{3,}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Pulls the task-id out of a <c>crock submit</c> stdout blob. Returns
    /// <c>null</c> when nothing recognisable was emitted — the runner then
    /// fails the work item rather than poll a synthetic id.
    /// </summary>
    public static string? TryExtractTaskId(string? submitStdout)
    {
        if (string.IsNullOrWhiteSpace(submitStdout))
            return null;

        var labeled = LabeledTaskIdPattern.Match(submitStdout);
        if (labeled.Success)
            return labeled.Groups[1].Value;

        var bare = BareTaskIdPattern.Match(submitStdout);
        if (bare.Success)
            return bare.Groups[1].Value;

        // Fall back to the last non-empty line trimmed of whitespace —
        // matches CLIs that print only the bare id with no label or wrapper.
        var lines = submitStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length is >= 3 and <= 128
                && trimmed.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
                return trimmed;
        }
        return null;
    }
}
