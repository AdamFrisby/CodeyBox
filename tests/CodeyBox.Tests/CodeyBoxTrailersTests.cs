using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CodeyBoxTrailers.Compose"/>: every commit produced
/// by the work pipeline must carry an attribution trailer block that survives
/// a DB wipe (<c>git log --grep 'CodeyBox-Agent: gemini'</c> as source of
/// truth, per issue tracker).
/// </summary>
public sealed class CodeyBoxTrailersTests
{
    private static readonly WorkItemId TestItemId =
        new(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    [Fact]
    public void Compose_NoFallbacks_EmitsWorkItemAgentAndCoAuthoredBy()
    {
        var trailer = CodeyBoxTrailers.Compose(
            TestItemId,
            AgentKind.Claude,
            "claude-opus-4-7",
            fallbackHistory: null);

        var lines = trailer.Split('\n');
        Assert.Equal($"CodeyBox-WorkItem: {TestItemId}", lines[0]);
        Assert.Equal("CodeyBox-Agent: claude/claude-opus-4-7", lines[1]);
        Assert.Equal(CodeyBoxTrailers.CoAuthoredBy, lines[2]);
        Assert.Equal(3, lines.Length);
        Assert.DoesNotContain("CodeyBox-Fallbacks:", trailer);
    }

    [Fact]
    public void Compose_EmptyFallbackList_OmitsFallbacksTrailer()
    {
        var trailer = CodeyBoxTrailers.Compose(
            TestItemId,
            AgentKind.Codex,
            "gpt-5-codex",
            fallbackHistory: Array.Empty<AgentFallbackRecord>());

        Assert.DoesNotContain("CodeyBox-Fallbacks:", trailer);
        Assert.Contains($"CodeyBox-WorkItem: {TestItemId}", trailer);
        Assert.Contains("CodeyBox-Agent: codex/gpt-5-codex", trailer);
        Assert.EndsWith(CodeyBoxTrailers.CoAuthoredBy, trailer);
    }

    [Fact]
    public void Compose_NullModel_OmitsModelSuffix()
    {
        var trailer = CodeyBoxTrailers.Compose(
            TestItemId,
            AgentKind.Gemini,
            finalModel: null);

        Assert.Contains("CodeyBox-Agent: gemini\n", trailer);
        Assert.DoesNotContain("gemini/", trailer);
    }

    [Fact]
    public void Compose_WhitespaceModel_OmitsModelSuffix()
    {
        var trailer = CodeyBoxTrailers.Compose(
            TestItemId,
            AgentKind.Gemini,
            finalModel: "   ");

        Assert.Contains("CodeyBox-Agent: gemini\n", trailer);
        Assert.DoesNotContain("gemini/", trailer);
    }

    [Fact]
    public void Compose_SingleFallback_EmitsCodeyBoxFallbacksTrailer()
    {
        var fallback = new AgentFallbackRecord(
            Id: Guid.NewGuid(),
            WorkItemId: TestItemId,
            Phase: "work",
            Iteration: 1,
            FromAgent: AgentKind.Codex,
            FromModel: "gpt-5-codex",
            ToAgent: AgentKind.Claude,
            ToModel: "claude-opus-4-7",
            Reason: "quota exhausted",
            OccurredAt: DateTimeOffset.UtcNow);

        var trailer = CodeyBoxTrailers.Compose(
            TestItemId,
            AgentKind.Claude,
            "claude-opus-4-7",
            fallbackHistory: new[] { fallback });

        Assert.Contains("CodeyBox-Fallbacks: codex→claude (×1 quota exhausted)", trailer);
    }

    [Fact]
    public void Compose_GroupsByFromToAgentAndPicksMostCommonReason()
    {
        var now = DateTimeOffset.UtcNow;
        var records = new[]
        {
            new AgentFallbackRecord(Guid.NewGuid(), TestItemId, "work", 1,
                AgentKind.Codex, "m1", AgentKind.Claude, "n1", "quota", now),
            new AgentFallbackRecord(Guid.NewGuid(), TestItemId, "work", 2,
                AgentKind.Codex, "m1", AgentKind.Claude, "n1", "quota", now.AddMinutes(1)),
            new AgentFallbackRecord(Guid.NewGuid(), TestItemId, "work", 3,
                AgentKind.Codex, "m1", AgentKind.Claude, "n1", "timeout", now.AddMinutes(2)),
            new AgentFallbackRecord(Guid.NewGuid(), TestItemId, "audit", 1,
                AgentKind.Claude, "n1", AgentKind.Gemini, "g1", "rate-limit", now.AddMinutes(3)),
        };

        var summary = CodeyBoxTrailers.ComposeFallbackSummary(records);

        Assert.Equal("claude→gemini (×1 rate-limit); codex→claude (×3 quota)", summary);
    }

    [Fact]
    public void Compose_ExhaustedFallback_RendersExhaustedSentinel()
    {
        var record = new AgentFallbackRecord(
            Id: Guid.NewGuid(),
            WorkItemId: TestItemId,
            Phase: "audit",
            Iteration: 2,
            FromAgent: AgentKind.Claude,
            FromModel: "claude-opus-4-7",
            ToAgent: null,
            ToModel: null,
            Reason: "all members exhausted",
            OccurredAt: DateTimeOffset.UtcNow);

        var summary = CodeyBoxTrailers.ComposeFallbackSummary(new[] { record });

        Assert.Equal("claude→(exhausted) (×1 all members exhausted)", summary);
    }

    [Fact]
    public void Compose_ReasonWithNewlines_CollapsesToSingleLine()
    {
        var record = new AgentFallbackRecord(
            Id: Guid.NewGuid(),
            WorkItemId: TestItemId,
            Phase: "work",
            Iteration: 1,
            FromAgent: AgentKind.Codex,
            FromModel: null,
            ToAgent: AgentKind.Claude,
            ToModel: null,
            Reason: "stderr:\nstack\ttrace\r\nmore",
            OccurredAt: DateTimeOffset.UtcNow);

        var trailer = CodeyBoxTrailers.Compose(
            TestItemId, AgentKind.Claude, finalModel: null,
            fallbackHistory: new[] { record });

        Assert.Contains("CodeyBox-Fallbacks: codex→claude (×1 stderr: stack trace more)", trailer);
        Assert.DoesNotContain("CodeyBox-Fallbacks: codex→claude (×1 stderr:\n", trailer);
        foreach (var line in trailer.Split('\n'))
            Assert.DoesNotContain('\r', line);
    }

    [Fact]
    public void Compose_FallbackTrailerIsSingleRfc5322Line()
    {
        var now = DateTimeOffset.UtcNow;
        var records = new[]
        {
            new AgentFallbackRecord(Guid.NewGuid(), TestItemId, "work", 1,
                AgentKind.Codex, null, AgentKind.Claude, null, "quota", now),
            new AgentFallbackRecord(Guid.NewGuid(), TestItemId, "audit", 2,
                AgentKind.Claude, null, AgentKind.Gemini, null, "timeout", now.AddMinutes(1)),
        };

        var trailer = CodeyBoxTrailers.Compose(TestItemId, AgentKind.Gemini, null, records);

        var fallbacksLine = trailer
            .Split('\n')
            .Single(l => l.StartsWith("CodeyBox-Fallbacks:", StringComparison.Ordinal));
        Assert.DoesNotContain('\r', fallbacksLine);
        Assert.Equal(fallbacksLine.Trim(), fallbacksLine);
    }

    [Fact]
    public void Compose_TrailerBlockTerminatesWithCoAuthoredBy()
    {
        var trailer = CodeyBoxTrailers.Compose(TestItemId, AgentKind.Claude, "m");
        Assert.EndsWith(CodeyBoxTrailers.CoAuthoredBy, trailer);
        Assert.False(trailer.EndsWith('\n'),
            "trailer block should not have a trailing newline; callers add separators");
    }

    [Fact]
    public void ComposeFallbackSummary_NullInput_ReturnsNull()
    {
        Assert.Null(CodeyBoxTrailers.ComposeFallbackSummary(null));
    }

    [Fact]
    public void ComposeFallbackSummary_EmptyInput_ReturnsNull()
    {
        Assert.Null(CodeyBoxTrailers.ComposeFallbackSummary(Array.Empty<AgentFallbackRecord>()));
    }
}
