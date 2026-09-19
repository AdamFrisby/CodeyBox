using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Shared provider-quota evidence for tests that install an in-process
/// exhaustion verdict. <see cref="AgentClassRouter.MarkExhausted"/> requires
/// genuine quota/rate-limit evidence, so tests pass this instead of bare TTLs.
/// </summary>
internal static class QuotaTestEvidence
{
    internal static QuotaExhaustionEvidence Default { get; } =
        new(QuotaFailureKind.RateLimitExceeded, "test", "test quota verdict");
}
