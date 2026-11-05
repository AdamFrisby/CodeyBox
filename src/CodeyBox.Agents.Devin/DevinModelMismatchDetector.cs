using CodeyBox.Agents;
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
/// <see cref="DevinAcpEnvelope.EventModelMismatch"/> envelope; the
/// secondary text fallback matches the CLI's own stderr WARN phrase
/// (<see cref="StderrFallbackMarker"/>) on exactly two trusted surfaces:
/// raw stderr (the provider's own diagnostic channel on every capture
/// shape) and the <c>text</c> field of <c>codeybox.stderr</c> envelopes on
/// an envelope-framed capture (covering a shim killed between the WARN
/// and its abort envelope). The phrase is held in the single documented
/// constant <see cref="StderrFallbackMarker"/>, mirrored verbatim by the
/// shim's <c>MODEL_FALLBACK_STDERR_MARKER</c>.</para>
///
/// <para>Provenance: envelope structure is only trusted when
/// <see cref="AgentResult.StdoutIsEnvelopeFramed"/> — the runner stamped
/// that the exec pinned stderr off stdout and the shim owns the stream.
/// Envelope payload fields (agent_message_chunk text, turn_complete
/// finalText) and unframed stdout/summary are agent-influenceable, so a
/// marker phrase quoted there — or a forged <c>model_mismatch</c> line —
/// never matches. Bounded: the framed scan truncates to the last
/// <see cref="ProviderTransientMatcher.MaxScannedChars"/> chars and
/// examines at most <see cref="DevinAcpEnvelope.MaxEnvelopesExamined"/>
/// envelopes; every evaluation is total (never throws). The detection
/// carries only the fixed signature label — never raw agent output. An
/// auth signal in the same capture suppresses the match
/// (<see cref="DevinAuthSignals"/>).</para>
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
    /// <c>codeybox.stderr</c> envelope's decoded <c>text</c> field.
    /// </summary>
    internal const string StderrFallbackMarker = "did not resolve to an available model";

    internal static ProviderTransientDetection? TryDetect(
        AgentResult result,
        IReadOnlyList<QuotaFailurePattern> unauthorizedPatterns)
    {
        try
        {
            if (string.IsNullOrEmpty(result.Stderr)
                && string.IsNullOrEmpty(result.Stdout)
                && string.IsNullOrEmpty(result.Summary))
            {
                return null;
            }

            var matched = HasEnvelopeMismatch(result.Stdout, framed: result.StdoutIsEnvelopeFramed)
                || HasStderrFallbackMarker(result.Stderr);
            if (!matched)
                return null;

            if (DevinAuthSignals.HasAuthSignal(
                    result.Stderr, result.Stdout, result.Summary, unauthorizedPatterns))
            {
                return null;
            }

            return new ProviderTransientDetection(
                ProviderTransientKind.InfraTransport,
                Signature,
                "the CLI reported the requested --model did not resolve and substituted "
                + "another; the turn was aborted and is retried so only the configured "
                + "model is ever billed");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Framed-capture envelope scan: a <c>model_mismatch</c> terminal
    /// envelope, or the stderr WARN phrase inside a verified
    /// <c>codeybox.stderr</c> envelope's <c>text</c> field. Never scans
    /// envelope payload fields or unframed text — those are
    /// agent-influenceable.
    /// </summary>
    private static bool HasEnvelopeMismatch(string? stdout, bool framed)
    {
        if (!framed || string.IsNullOrEmpty(stdout))
            return false;

        try
        {
            foreach (var envelope in DevinAcpEnvelope.EnumerateBounded(stdout))
            {
                if (envelope.Type == DevinAcpEnvelope.EnvelopeType
                    && string.Equals(envelope.Event, DevinAcpEnvelope.EventModelMismatch, StringComparison.Ordinal))
                {
                    return true;
                }

                if (envelope.Type == CliAgentRunnerBase.StderrEnvelopeType
                    && CliAgentRunnerBase.TryReadStderrEnvelope(envelope.Root, out var stderrText)
                    && stderrText?.Contains(StderrFallbackMarker, StringComparison.Ordinal) == true)
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

    /// <summary>
    /// Raw-stderr fallback for the WARN phrase: stderr is the provider's
    /// own diagnostic channel on every capture shape (print-mode stderr,
    /// pre-relay shim noise, and — on framed runs — the stream the exec
    /// wrapper pins off stdout). Bounded to the last
    /// <see cref="ProviderTransientMatcher.MaxScannedChars"/> chars.
    /// </summary>
    private static bool HasStderrFallbackMarker(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return false;

        try
        {
            var bounded = stderr.Length > ProviderTransientMatcher.MaxScannedChars
                ? stderr[^ProviderTransientMatcher.MaxScannedChars..]
                : stderr;
            return bounded.Contains(StderrFallbackMarker, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
