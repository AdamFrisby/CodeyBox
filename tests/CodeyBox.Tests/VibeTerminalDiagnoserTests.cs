using CodeyBox.Agents.Vibe;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="VibeTerminalDiagnoser"/> over recorded real vibe
/// 2.25.4 failure shapes.
/// </summary>
public sealed class VibeTerminalDiagnoserTests
{
    [Fact]
    public void TryExtract_MissingKey_ReturnsErrorLine()
    {
        // Recorded real shape: exit 1, cause only on stderr.
        const string stderr =
            "Error: Missing OPENROUTER_API_KEY environment variable for openrouter provider. " +
            "Set the environment variable (e.g. in ~/.vibe/.env or your shell), " +
            "or run `vibe --setup` once interactively.";

        var error = VibeTerminalDiagnoser.TryExtractTerminalError(stderr, stdout: string.Empty);

        Assert.NotNull(error);
        Assert.Contains("Missing OPENROUTER_API_KEY", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtract_ProviderRejection_ReturnsFirstErrorLine()
    {
        // Recorded real shape: exit 1 with a multi-line API-error body; the
        // first Error: line is the lift, not the whole body.
        const string stderr =
            "Error: API error from openrouter (model: anthropic/claude-haiku-4.5): LLM backend error [openrouter]\n" +
            "  status: 403 Forbidden\n" +
            "  provider_message: Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/64835ad0564749843e34c8e2e6d1482456dad7a14fbbd107a5a174ea87fc6058";

        var error = VibeTerminalDiagnoser.TryExtractTerminalError(stderr, stdout: "stdout");

        Assert.NotNull(error);
        Assert.StartsWith("Error: API error from openrouter", error, StringComparison.Ordinal);
        Assert.DoesNotContain("provider_message", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtract_HealthyStreams_ReturnsNull()
    {
        // Recorded real healthy run: history-entry JSON on stdout, empty
        // stderr. No Error: line exists anywhere.
        const string stdout =
            """{"id":"265ca6b7-305c-40f6-a8ca-46cca31fd819","sessionId":"a54c1241-bc61-28d6-0141-2a675c3860c1","turnId":"b6f13933-c15e-45b8-bf46-b419a881ef5b","createdAt":1789580847231,"updatedAt":1789580847320,"generationStatus":"completed","relatedEntryId":null,"type":"message","role":"assistant","content":[{"type":"text","text":"8"}],"source":null,"userDisplayContent":null}""";

        Assert.Null(VibeTerminalDiagnoser.TryExtractTerminalError(string.Empty, stdout));
        Assert.Null(VibeTerminalDiagnoser.TryExtractTerminalError(null, null));
        Assert.Null(VibeTerminalDiagnoser.TryExtractTerminalError("some log line\nanother", "stdout"));
    }

    [Fact]
    public void TryExtract_StdoutErrorLine_LiftedWhenStderrSilent()
    {
        // Defensive: a harness that merges streams must still lift the error.
        var error = VibeTerminalDiagnoser.TryExtractTerminalError(
            string.Empty,
            "noise\nError: Missing OPENROUTER_API_KEY environment variable for openrouter provider.");

        Assert.NotNull(error);
        Assert.Contains("Missing OPENROUTER_API_KEY", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtract_LongErrorLine_Truncated()
    {
        var stderr = "Error: " + new string('x', VibeTerminalDiagnoser.MaxDiagnosticChars + 100);

        var error = VibeTerminalDiagnoser.TryExtractTerminalError(stderr, stdout: null);

        Assert.NotNull(error);
        Assert.True(error.Length <= VibeTerminalDiagnoser.MaxDiagnosticChars + 1);
        Assert.EndsWith("…", error, StringComparison.Ordinal);
    }
}
