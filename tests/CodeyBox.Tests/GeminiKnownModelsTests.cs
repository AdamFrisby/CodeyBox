using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="GeminiKnownModels"/>: bucket-list membership, the
/// <c>auto</c> sentinel detection, and the config-time provider-list warning.
/// </summary>
public sealed class GeminiKnownModelsTests
{
    [Fact]
    public void All_HasFourCurrentlyKnownModels()
    {
        // The addendum spec calls out "the four currently-known models" —
        // changing this count is a deliberate operator-facing change.
        Assert.Equal(4, GeminiKnownModels.All.Count);
        Assert.Contains("gemini-2.5-pro", GeminiKnownModels.All);
        Assert.Contains("gemini-2.5-flash", GeminiKnownModels.All);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("AUTO", true)]
    [InlineData("Auto", true)]
    [InlineData("gemini-2.5-pro", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAuto(string? modelId, bool expected)
        => Assert.Equal(expected, GeminiKnownModels.IsAuto(modelId));

    [Theory]
    [InlineData("gemini-2.5-pro", true)]
    [InlineData("GEMINI-2.5-PRO", true)] // case-insensitive
    [InlineData("gemini-9-pro-preview", false)]
    [InlineData("auto", false)] // auto is a sentinel, not a known model id
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsKnown(string? modelId, bool expected)
        => Assert.Equal(expected, GeminiKnownModels.IsKnown(modelId));

    [Fact]
    public void Validator_AutoSentinel_DoesNotWarn()
    {
        var capture = new TestLogCapture();
        var warning = GeminiKnownModels.ValidateModelIdAgainstProviderList(
            "frontier", AgentKind.Gemini, "auto", capture);

        Assert.Null(warning);
        Assert.Empty(capture.Warnings);
    }

    [Fact]
    public void Validator_KnownModel_DoesNotWarn()
    {
        var capture = new TestLogCapture();
        var warning = GeminiKnownModels.ValidateModelIdAgainstProviderList(
            "frontier", AgentKind.Gemini, "gemini-2.5-pro", capture);

        Assert.Null(warning);
        Assert.Empty(capture.Warnings);
    }

    [Fact]
    public void Validator_UnknownGeminiModel_Warns()
    {
        var capture = new TestLogCapture();
        var warning = GeminiKnownModels.ValidateModelIdAgainstProviderList(
            "frontier", AgentKind.Gemini, "gemini-9-ultra-preview", capture);

        Assert.NotNull(warning);
        Assert.Contains("gemini-9-ultra-preview", warning);
        Assert.Contains("not in the known provider list", warning);
        Assert.Single(capture.Warnings);
    }

    [Fact]
    public void Validator_NullModelId_DoesNotWarn()
    {
        var capture = new TestLogCapture();
        var warning = GeminiKnownModels.ValidateModelIdAgainstProviderList(
            "frontier", AgentKind.Gemini, null, capture);

        Assert.Null(warning);
        Assert.Empty(capture.Warnings);
    }

    [Fact]
    public void Validator_CopilotAlwaysSkipped()
    {
        // Copilot ignores --model entirely; the validator does not surface
        // warnings for an inert ModelId there.
        var capture = new TestLogCapture();
        var warning = GeminiKnownModels.ValidateModelIdAgainstProviderList(
            "frontier", AgentKind.Copilot, "copilot-fake-model", capture);

        Assert.Null(warning);
        Assert.Empty(capture.Warnings);
    }

    [Fact]
    public void Validator_NonGeminiAgents_NoWarning()
    {
        // Claude/Codex do not currently have a static registry; the validator
        // is a no-op for them rather than rejecting unknown ids.
        var capture = new TestLogCapture();
        var w1 = GeminiKnownModels.ValidateModelIdAgainstProviderList(
            "frontier", AgentKind.Claude, "claude-anything", capture);
        var w2 = GeminiKnownModels.ValidateModelIdAgainstProviderList(
            "frontier", AgentKind.Codex, "gpt-anything", capture);

        Assert.Null(w1);
        Assert.Null(w2);
        Assert.Empty(capture.Warnings);
    }

    private sealed class TestLogCapture : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
