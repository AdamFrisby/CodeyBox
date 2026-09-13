namespace CodeyBox.Core;

/// <summary>
/// Auth/login-prompt classification plus the stream that supplied the evidence.
/// </summary>
public sealed record AgentAuthFailureDetection(
    AgentFailureClassification Classification,
    bool MatchedStderr,
    bool MatchedStdout,
    bool MatchedTrustedStdoutTranscript,
    bool MatchedConfiguredStdoutPattern = false,
    bool MatchedDefaultStdoutPattern = false)
{
    public bool MatchedConfiguredStderrPattern { get; init; }

    /// <summary>
    /// Catalogued shape that accepted the match (line prefix, exact line,
    /// OAuth-URL shape, login directive, or operator-configured pattern text).
    /// Null when the match came from cross-stream corroboration with no single
    /// default line to attribute.
    /// </summary>
    public string? MatchedPattern { get; init; }

    /// <summary>Stream that carried the matched line: <c>stderr</c> or <c>stdout</c>.</summary>
    public string? MatchedStream { get; init; }

    /// <summary>Surrounding matched line, trimmed and truncated for logs.</summary>
    public string? MatchedLine { get; init; }

    public bool IsStdoutOnly => MatchedStdout && !MatchedStderr;
}
