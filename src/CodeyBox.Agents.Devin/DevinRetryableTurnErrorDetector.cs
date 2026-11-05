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
/// Bounded: each input is truncated to the last
/// <see cref="ProviderTransientMatcher.MaxScannedChars"/> chars and at most
/// <see cref="MaxEnvelopesExamined"/> envelopes are examined; every
/// evaluation is total (never throws). The detection carries only the
/// fixed signature label. An auth signal in the same capture suppresses
/// the match (<see cref="DevinAuthSignals"/>).</para>
/// </summary>
internal static class DevinRetryableTurnErrorDetector
{
    /// <summary>Detector-owned signature label; never raw agent output.</summary>
    internal const string Signature = "devin-turn-error-retryable";

    private const int MaxEnvelopesExamined = 128;

    internal static ProviderTransientDetection? TryDetect(string? stderr, string? stdout, string? summary)
    {
        try
        {
            if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout) && string.IsNullOrEmpty(summary))
                return null;

            if (DevinAuthSignals.HasAuthSignal(stderr, stdout, summary))
                return null;

            if (HasRetryableTurnError(stderr) || HasRetryableTurnError(stdout) || HasRetryableTurnError(summary))
            {
                return new ProviderTransientDetection(
                    ProviderTransientKind.InfraTransport,
                    Signature,
                    "the provider marked the turn error retryable "
                    + "(cognition.ai/retryable); retry the turn with backoff");
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasRetryableTurnError(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            var bounded = text.Length > ProviderTransientMatcher.MaxScannedChars
                ? text[^ProviderTransientMatcher.MaxScannedChars..]
                : text;

            var examined = 0;
            foreach (var envelope in DevinAcpEnvelope.Enumerate(bounded))
            {
                if (++examined > MaxEnvelopesExamined)
                    break;
                if (!string.Equals(envelope.Event, DevinAcpEnvelope.EventTurnError, StringComparison.Ordinal))
                    continue;
                if (envelope.Root.TryGetProperty("retryable", out var retryable)
                    && retryable.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
