using System.Reflection;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class ClaudeTextOnlyTests
{
    [Fact]
    public void ClaudeAgentRunner_DoesNotExposeDirectTextOnlyCapability()
    {
        Assert.False(typeof(ITextOnlyAgentRunner).IsAssignableFrom(typeof(ClaudeAgentRunner)));

        var declaredPublicMethods = typeof(ClaudeAgentRunner).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(declaredPublicMethods, method => method.Name == "RunTextOnlyAsync");
        Assert.DoesNotContain(declaredPublicMethods, method => method.Name == "GetTextOnlyUnavailabilityReason");
    }

    [Fact]
    public void ResolveCanonicalModelId_ExactMatchAndNoDatedVariant_ReturnsExactMatch()
    {
        // When no dated variant of the requested id is in the list, the exact
        // alias match is the best we can do.
        var result = ClaudeAgentRunner.ResolveCanonicalModelId(
            "claude-opus-4-8", ["claude-opus-4-8", "claude-haiku-4-5-20251001"]);
        Assert.Equal("claude-opus-4-8", result);
    }

    [Fact]
    public void ResolveCanonicalModelId_DatedVariantBeatsExactAliasMatch()
    {
        // Defensive: even when /v1/models lists both an undated alias and a
        // dated snapshot, prefer the dated one for callers that need a
        // canonical provider id.
        var result = ClaudeAgentRunner.ResolveCanonicalModelId(
            "claude-opus-4-8",
            ["claude-opus-4-8", "claude-opus-4-8-20260315", "claude-haiku-4-5-20251001"]);
        Assert.Equal("claude-opus-4-8-20260315", result);
    }

    [Fact]
    public void ResolveCanonicalModelId_AliasMapsToLatestDatedVariant()
    {
        var result = ClaudeAgentRunner.ResolveCanonicalModelId(
            "claude-opus-4-8",
            ["claude-opus-4-8-20260101", "claude-opus-4-8-20260315", "claude-sonnet-4-6-20260101"]);
        Assert.Equal("claude-opus-4-8-20260315", result);
    }

    [Fact]
    public void ResolveCanonicalModelId_NoMatch_ReturnsRequestedUnchanged()
    {
        var result = ClaudeAgentRunner.ResolveCanonicalModelId(
            "claude-opus-4-8", ["claude-sonnet-4-6-20260101"]);
        Assert.Equal("claude-opus-4-8", result);
    }

    [Fact]
    public void ResolveCanonicalModelId_PrefixOnlyMatchesOnHyphenBoundary()
    {
        var result = ClaudeAgentRunner.ResolveCanonicalModelId(
            "claude-opus-4-1", ["claude-opus-4-1-20250805", "claude-opus-4-8-20260315"]);
        Assert.Equal("claude-opus-4-1-20250805", result);
    }
}
