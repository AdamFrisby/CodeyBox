using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Structured match for the Devin ACP model-substitution abort: the shim
/// (<c>Resources/devin-acp-client.py</c>) refuses to run a turn on a model
/// other than the configured <c>--model</c> and terminates with a
/// <c>model_mismatch</c> envelope when devin reports the requested value
/// did not resolve (sig-devin-model-unresolved). The run must park for
/// bounded transient retry — the observed trigger was a remote-config
/// fetch timeout leaving the CLI's model registry stale — never hard-fail
/// the item, and never allow the substituted (possibly metered) model to
/// run.
///
/// <para>Primary signal is the shim-emitted
/// <see cref="DevinAcpEnvelope.EventModelMismatch"/> envelope. A secondary
/// text fallback matches the CLI's own stderr WARN phrase inside a
/// <c>codeybox.stderr</c> envelope (or a raw stream), covering a shim
/// killed between the WARN and its abort envelope; the phrase is held in
/// the single documented constant <see cref="StderrFallbackMarker"/>,
/// mirrored verbatim by the shim's
/// <c>MODEL_FALLBACK_STDERR_MARKER</c>.</para>
///
/// <para>Bounded: each input is truncated to the last
/// <see cref="ProviderTransientMatcher.MaxScannedChars"/> chars and at most
/// <see cref="MaxEnvelopesExamined"/> envelopes are examined; every
/// evaluation is total (never throws). The detection carries only the fixed
/// signature label — never raw agent output. An auth signal in the same
/// capture suppresses the match (<see cref="DevinAuthSignals"/>).</para>
/// </summary>
internal static class DevinModelMismatchDetector
{
    /// <summary>Detector-owned signature label; never raw agent output.</summary>
    internal const string Signature = "devin-model-unresolved";

    /// <summary>
    /// The devin 3000.11.1 <c>chisel_server::acp</c> stderr WARN phrase
    /// emitted when the <c>devin acp --model</c> value does not resolve to
    /// an available model — the single constant the text fallback keys on
    /// (full line: <c>`devin acp --model` value did not resolve to an
    /// available model; falling back to your configured `agent.model` or
    /// the default</c>). Keep in lockstep with the shim's
    /// <c>MODEL_FALLBACK_STDERR_MARKER</c>. The phrase contains no
    /// JSON-escapable characters, so it matches verbatim inside a
    /// <c>codeybox.stderr</c> envelope line.
    /// </summary>
    internal const string StderrFallbackMarker = "did not resolve to an available model";

    private const int MaxEnvelopesExamined = 128;

    internal static ProviderTransientDetection? TryDetect(string? stderr, string? stdout, string? summary)
    {
        try
        {
            if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout) && string.IsNullOrEmpty(summary))
                return null;

            if (DevinAuthSignals.HasAuthSignal(stderr, stdout, summary))
                return null;

            if (HasMismatchSignal(stderr) || HasMismatchSignal(stdout) || HasMismatchSignal(summary))
            {
                return new ProviderTransientDetection(
                    ProviderTransientKind.InfraTransport,
                    Signature,
                    "the CLI reported the requested --model did not resolve and substituted "
                    + "another; the turn was aborted and is retried so only the configured "
                    + "model is ever billed");
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasMismatchSignal(string? text)
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
                if (string.Equals(envelope.Event, DevinAcpEnvelope.EventModelMismatch, StringComparison.Ordinal))
                    return true;
            }

            return bounded.Contains(StderrFallbackMarker, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
