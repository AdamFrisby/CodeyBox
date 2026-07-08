using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class QuotaRetryWorkItem
{
    public static string? RequiredCapabilityForRetry(WorkItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.QuotaRetryPhase))
            return RequiredCapabilityForPhase(item.QuotaRetryPhase);

        return NormalizeRetryFrom(item.QuotaRetryFrom) == "audit"
            ? WellKnownCapabilities.Audit
            : null;
    }

    public static string NormalizeRetryFrom(string? retryFrom) => retryFrom?.Trim().ToLowerInvariant() switch
    {
        "planning" => "planning",
        "audit" => "audit",
        "conflict_rework" => "conflict_rework",
        "merge" => "merge",
        "upstream" => "upstream",
        _ => "work",
    };

    private static string? RequiredCapabilityForPhase(string? phase) =>
        string.Equals(phase?.Trim(), "audit", StringComparison.OrdinalIgnoreCase)
            ? WellKnownCapabilities.Audit
            : null;
}
