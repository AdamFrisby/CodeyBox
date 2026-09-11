using CodeyBox.Agents;
using CodeyBox.Agents.Copilot;
using CodeyBox.Agents.Opencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// The provider rate-limit set has one owner (<see cref="SharedRateLimitPatterns"/>).
/// Every pattern added there once must classify as a transient rate limit
/// through both the Copilot and the opencode detectors — if either detector
/// drifted off the shared set, the corresponding theory row fails.
/// </summary>
public sealed class SharedRateLimitPatternsTests
{
    private readonly CopilotQuotaFailureDetector _copilot = new();
    private readonly OpencodeQuotaFailureDetector _opencode = new();

    public static IEnumerable<object[]> SharedPatterns() =>
        SharedRateLimitPatterns.ProviderRateLimitPatterns.Select(p => new object[] { p.Pattern });

    [Theory]
    [MemberData(nameof(SharedPatterns))]
    public void CopilotDetector_HonoursEverySharedPattern(string pattern)
    {
        var detection = _copilot.Detect(stderr: $"Upstream request failed: {pattern}; retry later.", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Theory]
    [MemberData(nameof(SharedPatterns))]
    public void OpencodeDetector_HonoursEverySharedPattern(string pattern)
    {
        var detection = _opencode.Detect(stderr: $"Upstream request failed: {pattern}; retry later.", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void BothDetectors_AgreeOnEverySharedPattern()
    {
        foreach (var entry in SharedRateLimitPatterns.ProviderRateLimitPatterns)
        {
            var stderr = $"Upstream request failed: {entry.Pattern}; retry later.";
            var copilot = _copilot.Detect(stderr: stderr, stdout: null);
            var opencode = _opencode.Detect(stderr: stderr, stdout: null);

            Assert.NotNull(copilot);
            Assert.NotNull(opencode);
            Assert.Equal(copilot!.Kind, opencode!.Kind);
            Assert.Equal(QuotaFailureKind.RateLimitExceeded, copilot.Kind);
        }
    }
}
