using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Structured match for a Devin ACP <c>turn_error</c> the provider itself
/// marked retryable: the session/prompt JSON-RPC error's
/// <c>data["cognition.ai/retryable"] == true</c> is forwarded by the shim
/// onto the terminal envelope as <c>"retryable": true</c>
/// (sig-devin-turn-error-retryable). Observed as error code -32013
/// <c>Permission denied: This model is no longer available for your
/// account. Please switch to a different model.</c> — the backend rejecting
/// the CLI's silently substituted model. The run must park for bounded
/// transient retry, not hard-fail the item.
///
/// <para>Only the structured envelope flag counts — a bare code, the same
/// message without the flag, or retryable text in prose never match.
/// Provenance: the envelope is trusted only when
/// <see cref="AgentResult.StdoutIsEnvelopeFramed"/> — unframed stdout,
/// stderr, and summary are agent/model text that could quote a forged
/// <c>turn_error</c> line, so they never match. Bounded: the framed scan
/// truncates to the last <see cref="ProviderTransientMatcher.MaxScannedChars"/>
/// chars and examines the last <see cref="DevinAcpEnvelope.MaxEnvelopesExamined"/>
/// envelopes via <see cref="DevinAcpEnvelope.EnumerateBounded"/>, which
/// stops at <c>turn_complete</c> so post-terminal forged lines are never
/// examined; every evaluation is total (never throws). The detection
/// carries only the fixed signature label. An auth signal in the same
/// capture suppresses the match (<see cref="DevinAuthSignals"/>).</para>
/// </summary>
internal static class DevinRetryableTurnErrorDetector
{
    /// <summary>Detector-owned signature label; never raw agent output.</summary>
    internal const string Signature = "devin-turn-error-retryable";

    internal static ProviderTransientDetection? TryDetect(
        AgentResult result,
        IReadOnlyList<QuotaFailurePattern> unauthorizedPatterns)
        => DevinTransientDetectionCore.TryMatch(
            result,
            unauthorizedPatterns,
            Signature,
            "the provider marked the turn error retryable "
            + "(cognition.ai/retryable); retry the turn with backoff",
            r => HasRetryableTurnError(r.Stdout, framed: r.StdoutIsEnvelopeFramed));

    /// <summary>
    /// Framed-capture envelope scan for a <c>turn_error</c> carrying
    /// <c>"retryable": true</c>. Unframed captures never match: their
    /// stdout is agent/model text that could quote a forged envelope line.
    /// </summary>
    private static bool HasRetryableTurnError(string? stdout, bool framed)
    {
        if (!framed || string.IsNullOrEmpty(stdout))
            return false;

        try
        {
            foreach (var envelope in DevinAcpEnvelope.EnumerateBounded(stdout))
            {
                if (!string.Equals(envelope.Event, DevinAcpEnvelope.EventTurnError, StringComparison.Ordinal))
                    continue;
                if (envelope.Root.TryGetProperty("retryable", out var retryable)
                    && retryable.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }

        return false;
    }
}
