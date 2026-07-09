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

    public bool IsStdoutOnly => MatchedStdout && !MatchedStderr;
}
