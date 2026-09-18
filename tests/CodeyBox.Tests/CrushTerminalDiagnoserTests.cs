using CodeyBox.Agents.Crush;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CrushTerminalDiagnoser"/> over recorded real
/// @charmland/crush 0.95.0 output: the exit-1 styled <c>ERROR</c> failure
/// blocks (all observed on stderr with empty stdout), healthy plain-text
/// replies, and empty output. Only the anchored failure markers lift —
/// notably NOT the non-fatal small-model title advisory or bare
/// <c>ERROR</c> headers without a cause.
/// </summary>
public sealed class CrushTerminalDiagnoserTests
{
    [Fact]
    public void TryExtractTerminalError_NoProvidersConfigured_ReturnsLine()
    {
        // Recorded real shape (bare machine, no key).
        const string stderr =
            "No providers configured - please run 'crush' to set up a provider interactively.";

        var error = CrushTerminalDiagnoser.TryExtractTerminalError(string.Empty, stderr);

        Assert.NotNull(error);
        Assert.Contains("No providers configured", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractTerminalError_UnknownModelId_ReturnsLine()
    {
        // Recorded real shape (unknown -m id).
        const string stderr =
            "Failed to override models: large model \"openrouter/no-such-model-xyz\" not found.";

        var error = CrushTerminalDiagnoser.TryExtractTerminalError(string.Empty, stderr);

        Assert.NotNull(error);
        Assert.Contains("Failed to override models:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractTerminalError_SpendLimitRefusal_ReturnsProviderMessage()
    {
        // Recorded real shape ($0-spend-limit key against a paid model).
        const string stderr =
            "Agent processing failed: failed to start agent processing stream: forbidden: Key limit exceeded (total limit).\n" +
            "Manage it using https://openrouter.ai/workspaces/default/keys/64835ad0564749843e34c8e2e6d1482456dad7a14fbbd107a5a174ea87fc6058.";

        var error = CrushTerminalDiagnoser.TryExtractTerminalError(string.Empty, stderr);

        Assert.NotNull(error);
        Assert.Contains("Agent processing failed:", error, StringComparison.Ordinal);
        Assert.Contains("Key limit exceeded", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractTerminalError_UnknownFlag_ReturnsLine()
    {
        // Recorded real shape (`crush run --yolo` — root-only flag).
        const string stderr = "Unknown flag: --yolo.";

        var error = CrushTerminalDiagnoser.TryExtractTerminalError(string.Empty, stderr);

        Assert.NotNull(error);
        Assert.Contains("Unknown flag:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractTerminalError_ScansStdoutToo()
    {
        // The markers are human rendering, not a stream-guaranteed
        // envelope — a build that moves them to stdout must still lift.
        var error = CrushTerminalDiagnoser.TryExtractTerminalError(
            "No providers configured - please run 'crush' to set up a provider interactively.",
            string.Empty);

        Assert.NotNull(error);
        Assert.Contains("No providers configured", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractTerminalError_LastMarkerWins()
    {
        const string stderr =
            "No providers configured - please run 'crush' to set up a provider interactively.\n" +
            "Agent processing failed: failed to start agent processing stream: forbidden: Key limit exceeded (total limit).";

        var error = CrushTerminalDiagnoser.TryExtractTerminalError(string.Empty, stderr);

        Assert.NotNull(error);
        Assert.Contains("Agent processing failed:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractTerminalError_HealthyReply_ReturnsNull()
    {
        // Recorded real replies: free text is not a failure.
        Assert.Null(CrushTerminalDiagnoser.TryExtractTerminalError("hello-crush-ok\n", string.Empty));
        Assert.Null(CrushTerminalDiagnoser.TryExtractTerminalError(
            "Fixed. The `add` function in `/tmp/crushrepo/calc.py:1` now returns `a + b` instead of `a - b`.",
            string.Empty));
    }

    [Fact]
    public void TryExtractTerminalError_EmptyOutput_ReturnsNull()
    {
        // A run whose work landed in files may reply with empty stdout.
        // Empty output is success, not failure.
        Assert.Null(CrushTerminalDiagnoser.TryExtractTerminalError(string.Empty, string.Empty));
        Assert.Null(CrushTerminalDiagnoser.TryExtractTerminalError(null, null));
    }

    [Fact]
    public void TryExtractTerminalError_TitleAdvisory_ReturnsNull()
    {
        // Recorded real advisory (verbose run against the shipped free-tier
        // model): non-fatal — the run still exits 0 with the reply intact.
        // Lifting it would mislabel success.
        const string stderr =
            "Error generating title with small model; trying next err=\"forbidden: Key limit exceeded (total limit).\"";

        Assert.Null(CrushTerminalDiagnoser.TryExtractTerminalError(string.Empty, stderr));
    }

    [Fact]
    public void TryExtractTerminalError_BareErrorHeader_ReturnsNull()
    {
        // The styled block's header line carries no cause on its own.
        Assert.Null(CrushTerminalDiagnoser.TryExtractTerminalError(string.Empty, "ERROR"));
        Assert.Null(CrushTerminalDiagnoser.TryExtractTerminalError(string.Empty, "something else went wrong"));
    }

    [Fact]
    public void TryExtractTerminalError_LongLine_IsCapped()
    {
        var stderr = "Agent processing failed: " + new string('x', 2000);

        var error = CrushTerminalDiagnoser.TryExtractTerminalError(string.Empty, stderr);

        Assert.NotNull(error);
        Assert.True(error.Length <= CrushTerminalDiagnoser.MaxDiagnosticChars + 1);
        Assert.EndsWith("…", error);
    }
}
