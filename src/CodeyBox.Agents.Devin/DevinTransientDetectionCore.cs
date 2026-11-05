using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// The shared evaluation skeleton for Devin's structured
/// provider-transient detectors (<see cref="DevinTeamSettingsTimeoutDetector"/>,
/// <see cref="DevinModelMismatchDetector"/>,
/// <see cref="DevinRetryableTurnErrorDetector"/>): the all-empty-input
/// guard, the detector's own signature match, the shared auth-signal
/// suppression (<see cref="DevinAuthSignals"/>), and the total (never
/// throws) contract. One copy so a change to the empty-input or
/// suppression contract lands once instead of drifting across detectors.
/// </summary>
internal static class DevinTransientDetectionCore
{
    /// <summary>
    /// Returns a <see cref="ProviderTransientKind.InfraTransport"/>
    /// detection carrying <paramref name="signature"/> and
    /// <paramref name="detail"/> when <paramref name="matches"/> accepts
    /// the result and no auth signal suppresses it; otherwise null. Never
    /// throws — any evaluation failure degrades to "no match".
    /// </summary>
    internal static ProviderTransientDetection? TryMatch(
        AgentResult result,
        IReadOnlyList<QuotaFailurePattern> unauthorizedPatterns,
        string signature,
        string detail,
        Func<AgentResult, bool> matches)
    {
        try
        {
            if (string.IsNullOrEmpty(result.Stderr)
                && string.IsNullOrEmpty(result.Stdout)
                && string.IsNullOrEmpty(result.Summary))
            {
                return null;
            }

            if (!matches(result))
                return null;

            if (DevinAuthSignals.HasAuthSignal(
                    result.Stderr, result.Stdout, result.Summary, unauthorizedPatterns))
            {
                return null;
            }

            return new ProviderTransientDetection(
                ProviderTransientKind.InfraTransport,
                signature,
                detail);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
