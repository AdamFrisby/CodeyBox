using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the stderr/stdout auth-required parity guard: both
/// captured streams are potentially model-controlled, so both require the same
/// short, whole-line CLI-login shape. Prose that merely embeds an auth phrase
/// — expected output when the work item itself concerns credential handling —
/// must not classify as an auth failure and halt the fleet.
/// </summary>
public sealed class AgentFailureStderrGuardTests
{
    private const string GenuineRefusal = "not logged into agy; run `agy login`";

    [Fact]
    public void StderrProseEmbeddingLoginDirective_DoesNotClassifyAsAuth()
    {
        var stderr = "I investigated the credential handling code paths in the runner at length. "
            + "The docs mention run `agy login` as an example of the interactive re-auth flow, so I updated "
            + "the handler, added regression tests for the new behaviour, and verified the surrounding code in detail.";

        var classification = AgentFailureClassifier.Classify(stderr: stderr);

        Assert.NotEqual(AgentFailureKind.AuthRequired, classification.Kind);
        Assert.NotEqual(AgentFailureKind.AuthError, classification.Kind);
        Assert.False(AgentFailureClassifier.ContainsAuthRequiredPatternInStderr(stderr));
    }

    [Fact]
    public void StderrProseEmbeddingLoginPromptPrefix_DoesNotClassifyAsAuth()
    {
        var stderr = "The review found that Authentication required. Please visit the URL to log in "
            + "appears in the user-facing docs for the credential rotation feature, so the wording was "
            + "kept as-is and the surrounding handler logic was refactored to match the new policy text.";

        var classification = AgentFailureClassifier.Classify(stderr: stderr);

        Assert.NotEqual(AgentFailureKind.AuthRequired, classification.Kind);
        Assert.False(AgentFailureClassifier.ContainsAuthRequiredPatternInStderr(stderr));
    }

    [Fact]
    public void StderrMultilineProseWithEmbeddedPrompt_DoesNotClassifyAsAuth()
    {
        var stderr = string.Join('\n',
            "Build succeeded with 3 warnings.",
            "Note: users who see Authentication required. Please visit the URL to log in should re-run setup;",
            "the credential handler now surfaces this hint directly in the failure classification output text.",
            "All 142 tests passed.");

        var classification = AgentFailureClassifier.Classify(stderr: stderr);

        Assert.NotEqual(AgentFailureKind.AuthRequired, classification.Kind);
        Assert.False(AgentFailureClassifier.ContainsAuthRequiredPatternInStderr(stderr));
    }

    [Fact]
    public void StderrExceedingStdoutBound_DoesNotClassifyAsAuth()
    {
        var transcript = "Authentication required. Please visit the URL to log in:\n"
            + "Waiting for authentication (timeout 30s)...\n";
        var stderr = string.Concat(Enumerable.Repeat(transcript, 60));
        Assert.True(stderr.Length > 4096);

        var classification = AgentFailureClassifier.Classify(stderr: stderr);

        Assert.NotEqual(AgentFailureKind.AuthRequired, classification.Kind);
        Assert.False(AgentFailureClassifier.ContainsAuthRequiredPatternInStderr(stderr));
    }

    [Fact]
    public void GenuineSingleLineRefusal_OnStderr_ClassifiesAsAuthRequired()
    {
        var classification = AgentFailureClassifier.Classify(stderr: GenuineRefusal);

        Assert.Equal(AgentFailureKind.AuthRequired, classification.Kind);
        Assert.True(AgentFailureClassifier.ContainsAuthRequiredPatternInStderr(GenuineRefusal));
    }

    [Fact]
    public void GenuineTranscript_OnStderr_ClassifiesAsAuthRequired()
    {
        var stderr = "Authentication required. Please visit the URL to log in:\n"
            + "Waiting for authentication (timeout 30s)...\n"
            + "Error: authentication timed out.";

        var classification = AgentFailureClassifier.Classify(stderr: stderr);

        Assert.Equal(AgentFailureKind.AuthRequired, classification.Kind);
    }

    [Theory]
    [InlineData("not logged into agy; run `agy login`")]
    [InlineData("Authentication required. Please visit the URL to log in:")]
    [InlineData("Waiting for authentication (timeout 30s)... Error: authentication timed out.")]
    [InlineData("https://accounts.google.com/o/oauth2/auth?client_id=redacted")]
    [InlineData("http://localhost:3000/oauth-callback?code=redacted")]
    [InlineData("Error: authentication timed out.")]
    [InlineData("I investigated the credential handling code paths in the runner at length. The docs mention run `agy login` as an example of the interactive re-auth flow, so I updated the handler, added regression tests, and verified the code in detail.")]
    [InlineData("The endpoint requires Authentication required for users in role admin.")]
    [InlineData("If the user is not logged in we should redirect to /signin.")]
    [InlineData("Waiting for authentication to complete before issuing the JWT.")]
    [InlineData("The OAuth callback endpoint is http://localhost:3000/oauth-callback.")]
    [InlineData("Use https://accounts.google.com/o/oauth2/auth when implementing Google sign-in.")]
    [InlineData("")]
    public void StderrAndStdoutPredicates_AgreeOnSameInputs(string input)
    {
        Assert.Equal(
            AgentFailureClassifier.ContainsAuthRequiredPatternInStdout(input),
            AgentFailureClassifier.ContainsAuthRequiredPatternInStderr(input));
    }

    [Fact]
    public void StderrAndStdoutPredicates_AgreeOnOverBoundInput()
    {
        var input = "not logged into agy; run `agy login`\n" + new string('x', 8192);

        Assert.Equal(
            AgentFailureClassifier.ContainsAuthRequiredPatternInStdout(input),
            AgentFailureClassifier.ContainsAuthRequiredPatternInStderr(input));
        Assert.False(AgentFailureClassifier.ContainsAuthRequiredPatternInStderr(input));
    }

    [Fact]
    public void ProseInput_YieldsNoDetectionOnEitherStream()
    {
        var prose = "The credential handler quotes run `agy login` as an example in its very long explanatory "
            + "output line that walks through the rotation policy, the retry budget, and the audit trail in detail "
            + "so reviewers understand the full behaviour end to end without ambiguity.";
        var classifier = new AgentAuthFailureClassifier();

        Assert.Null(classifier.DetectDetailed(AgentKind.Codex, stderr: prose, stdout: null));
        Assert.Null(classifier.DetectDetailed(AgentKind.Codex, stderr: null, stdout: prose));
    }

    [Fact]
    public void Detection_RecordsPatternStreamAndLine()
    {
        var detection = new AgentAuthFailureClassifier().DetectDetailed(
            AgentKind.Codex,
            stderr: GenuineRefusal,
            stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(AgentFailureKind.AuthRequired, detection.Classification.Kind);
        Assert.NotNull(detection.MatchedPattern);
        Assert.NotNull(detection.MatchedStream);
        Assert.Equal("stderr", detection.MatchedStream);
        Assert.NotNull(detection.MatchedLine);
        Assert.Contains(GenuineRefusal, detection.MatchedLine, StringComparison.Ordinal);
        Assert.Contains(detection.MatchedStream, detection.Classification.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(detection.MatchedLine, detection.Classification.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Detection_RecordsStdoutEvidenceSeparately()
    {
        var detection = new AgentAuthFailureClassifier().DetectDetailed(
            AgentKind.Codex,
            stderr: null,
            stdout: GenuineRefusal);

        Assert.NotNull(detection);
        Assert.Equal("stdout", detection.MatchedStream);
        Assert.Contains(GenuineRefusal, detection.MatchedLine, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthErrorClassification_RecordsPatternStreamAndLine()
    {
        // The fleet halts under review were triggered on this path, whose
        // bare "auth pattern matched" reason forced reviewers to read source
        // to establish that the trigger was the agent's own text. The reason
        // now carries the pattern, the stream, and the surrounding line.
        const string stderr = "request failed: API Error: 401 Unauthorized for url";

        var classification = AgentFailureClassifier.Classify(stderr: stderr);

        Assert.Equal(AgentFailureKind.AuthError, classification.Kind);
        Assert.Contains("API Error: 401", classification.Reason, StringComparison.Ordinal);
        Assert.Contains("stderr", classification.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(stderr, classification.Reason, StringComparison.Ordinal);
    }
}
