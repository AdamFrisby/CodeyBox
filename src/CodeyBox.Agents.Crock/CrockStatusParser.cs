using System.Text.RegularExpressions;

namespace CodeyBox.Agents.Crock;

/// <summary>
/// Parser for <c>crock status &lt;task-id&gt;</c> output. The crock CLI's exact
/// stdout shape (plain text vs JSON, field naming) has not been verified
/// against a live binary in this environment, so the parser accepts a small
/// family of state-declaration shapes — <c>state: succeeded</c>,
/// <c>"state":"succeeded"</c>, <c>current state = failed</c> — rather than
/// blob-scanning for bare keywords. Anchoring on a state-prefix is the cheap
/// fix for the loose-keyword footgun called out in <see cref="CrockTaskStatus"/>'s
/// notes: blobs that contain incidental words like <c>error</c> (e.g.
/// <c>last error: none</c>) or <c>ok</c> (e.g. <c>{"connection":"ok"}</c>)
/// never resolve as terminal states because those lines do not introduce a
/// state field. If a future crock release ships a structured JSON contract,
/// replace this with a JSON deserialiser.
/// </summary>
public static class CrockStatusParser
{
    // State-declaration shape. The leading "(?:^|...)" anchors on the start
    // of a key rather than a position anywhere inside a longer word, so a
    // field like "stateless" or "last_error_state" never matches. Tokens can
    // be wrapped in matching ASCII quotes (JSON-style "state":"succeeded")
    // and separated by ':' or '='. A lookahead replaces trailing punctuation
    // consumption so the match works mid-line — e.g. inside a JSON object
    // like {"state":"running","connection":"ok"}.
    private static readonly Regex StatePattern = new(
        """
        (?:^|[\s,{(\[])["']?(?:current(?:\s+state)?|state)["']?\s*[:=]\s*["']?(?<token>[A-Za-z][A-Za-z0-9_\-]*)(?=["',;}\)\]\s]|$)
        """,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> SucceededTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "succeeded", "completed", "success", "finished", "done",
    };

    private static readonly HashSet<string> FailedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "failed", "failure", "error", "errored", "cancelled", "canceled",
        "aborted", "expired", "timed-out", "timed_out", "timedout",
    };

    private static readonly HashSet<string> InProgressTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "running", "pending", "queued", "submitted", "processing", "polling",
        "waiting", "started", "in-progress", "in_progress", "inprogress",
    };

    /// <summary>
    /// Classifies a <c>crock status</c> observation. <paramref name="stdout"/>
    /// and <paramref name="stderr"/> are scanned for state declarations and
    /// the LAST match wins (so a status containing <c>state: running</c>
    /// followed by <c>state: succeeded</c> resolves to Succeeded). Tokens
    /// that do not introduce a state field — <c>last error: none</c>,
    /// <c>{"connection":"ok"}</c>, free-form log messages — are ignored,
    /// eliminating the false-positive surface the earlier blob-scanning
    /// parser had.
    /// </summary>
    public static CrockTaskStatus Classify(string? stdout, string? stderr = null)
    {
        // Prefer stderr's last state token when present: state transitions
        // tend to land in the daemon's stderr trail, while stdout is more
        // commonly free-form. Fall through to stdout when stderr has none.
        var lastToken = LastStateToken(stderr) ?? LastStateToken(stdout);

        if (lastToken is null)
        {
            return new CrockTaskStatus(
                CrockTaskStateKind.Unknown,
                StateToken: null,
                Summary: "no state token detected");
        }

        var normalised = NormaliseToken(lastToken);
        var kind = ClassifyToken(normalised);
        return new CrockTaskStatus(kind, lastToken, $"state={normalised}");
    }

    private static string? LastStateToken(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var matches = StatePattern.Matches(text);
        if (matches.Count == 0) return null;
        return matches[^1].Groups["token"].Value;
    }

    private static string NormaliseToken(string token) =>
        token.Trim().ToLowerInvariant();

    private static CrockTaskStateKind ClassifyToken(string token)
    {
        if (SucceededTokens.Contains(token)) return CrockTaskStateKind.Succeeded;
        if (FailedTokens.Contains(token)) return CrockTaskStateKind.Failed;
        if (InProgressTokens.Contains(token)) return CrockTaskStateKind.InProgress;
        return CrockTaskStateKind.Unknown;
    }

    // Submit-step task-id extractor. The crock CLI documents `crock submit`
    // as "prints a task-id, then detaches"; the exact framing of the printed
    // id (bare uuid, `task-<uuid>`, JSON wrapper) is unverified. Both regexes
    // anchor on a letter / digit / `task-` / UUID prefix so a leading dash
    // (which could be interpreted as a CLI flag by `crock status`) can never
    // be returned. There is no permissive last-line fallback: if neither
    // shape matches, the runner fails the work item rather than fabricate a
    // task-id from an arbitrary trailing word like "ok" or "done".
    private static readonly Regex LabeledTaskIdPattern = new(
        @"task[-_]?id\s*[:=]\s*[""']?(?<id>[A-Za-z0-9][A-Za-z0-9._\-]{2,127})[""']?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareTaskIdPattern = new(
        @"(?<id>task[-_][A-Za-z0-9._\-]{3,127}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled);

    /// <summary>
    /// Pulls the task-id out of a <c>crock submit</c> stdout blob. Returns
    /// <c>null</c> when nothing recognisable was emitted — the runner then
    /// fails the work item rather than poll a synthetic id. Both accepted
    /// shapes start with an alphanumeric character, so the returned id can
    /// never be confused with a CLI flag.
    /// </summary>
    public static string? TryExtractTaskId(string? submitStdout)
    {
        if (string.IsNullOrWhiteSpace(submitStdout))
            return null;

        var labeled = LabeledTaskIdPattern.Match(submitStdout);
        if (labeled.Success)
            return labeled.Groups["id"].Value;

        var bare = BareTaskIdPattern.Match(submitStdout);
        if (bare.Success)
            return bare.Groups["id"].Value;

        return null;
    }
}
