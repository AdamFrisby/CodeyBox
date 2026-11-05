using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Structured match for the Devin ACP model-substitution abort: the shim
/// (<c>Resources/devin-acp-client.py</c>) refuses to run a turn on a model
/// other than the configured <c>--model</c> and terminates with a
/// <c>model_mismatch</c> envelope when devin reports the requested value
/// did not resolve or the session's active model differs
/// (sig-devin-model-unresolved). The run must park for bounded transient
/// retry — the observed trigger was a remote-config fetch timeout leaving
/// the CLI's model registry stale — never hard-fail the item, and never
/// allow the substituted (possibly metered) model to run.
///
/// <para>Primary signal is the shim-emitted
/// <see cref="DevinAcpEnvelope.EventModelMismatch"/> envelope — shim
/// provenance, trusted whenever the capture is envelope-framed. The
/// secondary text fallback matches the CLI's stderr WARN phrase
/// (<see cref="StderrFallbackMarker"/>) inside a verified
/// <c>codeybox.stderr</c> envelope's <c>text</c> field, but ONLY on a
/// complete capture carrying no <c>prompt_sent</c> envelope: before the
/// first prompt fd 2 is still CLI-only stderr (provider-authentic), while
/// after it fd 2 also carries tool-subprocess stderr the agent/model
/// controls — the marker phrase there is a forgery vector, so post-prompt
/// captures rely on the shim's own <c>model_mismatch</c> envelope instead.
/// Raw stderr is never scanned: on framed runs the exec wrapper pins the
/// CLI's fd 2 into the envelope stream (the WARN cannot appear there),
/// and on unframed captures stderr is agent/model text. The phrase is
/// held in the single documented constant <see cref="StderrFallbackMarker"/>,
/// mirrored verbatim by the shim's <c>MODEL_FALLBACK_STDERR_MARKER</c>.</para>
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
        => DevinTransientDetectionCore.TryMatch(
            result,
            unauthorizedPatterns,
            Signature,
            "the session ran on a model other than the configured --model "
            + "(silent substitution); the turn was aborted and is retried "
            + "so only the configured model is ever billed",
            r => HasEnvelopeMismatch(r.Stdout, framed: r.StdoutIsEnvelopeFramed));

    /// <summary>
    /// Framed-capture scan over the bounded tail: a shim-emitted
    /// <c>model_mismatch</c> envelope anywhere in the window (authoritative
    /// — it also covers a terminal envelope pushed past the last-N cap by
    /// trailing relay lines), or the stderr WARN phrase inside a verified
    /// <c>codeybox.stderr</c> envelope that precedes any turn-proof
    /// envelope. Unframed captures never match.
    /// </summary>
    private static bool HasEnvelopeMismatch(string? stdout, bool framed)
    {
        if (!framed || string.IsNullOrEmpty(stdout))
            return false;

        try
        {
            var bounded = ProviderTransientMatcher.BoundToScannedTail(stdout);
            var sawPrePromptEnvelope = false;
            var sawPrePromptMarker = false;
            var turnStarted = false;

            foreach (var typed in DevinAcpEnvelope.EnumerateEnvelopes(bounded))
            {
                if (typed.Type == DevinAcpEnvelope.EnvelopeType)
                {
                    if (string.Equals(typed.Event, DevinAcpEnvelope.EventModelMismatch, StringComparison.Ordinal))
                        return true;

                    if (!turnStarted && ProvesTurnRan(typed.Root, typed.Event))
                    {
                        turnStarted = true;
                        continue;
                    }

                    if (!turnStarted)
                        sawPrePromptEnvelope = true;
                    continue;
                }

                // A marker only counts while the stream still shows the
                // pre-prompt handshake: once the turn started fd 2 also
                // carries tool-subprocess stderr the agent/model controls,
                // so the phrase there is forgeable. Stream order is
                // emission order, so a marker relayed before the first
                // turn-proof envelope genuinely predates the prompt —
                // fd 2 was still CLI-only stderr then.
                if (!turnStarted
                    && typed.Type == CliAgentRunnerBase.StderrEnvelopeType
                    && CliAgentRunnerBase.TryReadStderrEnvelope(typed.Root, out var stderrText)
                    && stderrText?.Contains(StderrFallbackMarker, StringComparison.Ordinal) == true)
                {
                    sawPrePromptMarker = true;
                }
            }

            // The pre-prompt-envelope requirement closes the retained-tail
            // edge: a post-terminal stderr flood can fill the whole scan
            // window with codeybox.stderr lines and no devin.acp evidence,
            // which must not let agent text pose as handshake WARN output.
            return sawPrePromptMarker && sawPrePromptEnvelope;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True when a <c>devin.acp</c> envelope proves the turn already ran —
    /// the boundary after which fd 2 is shared with tool subprocesses and
    /// the WARN phrase stops being provider-authentic. Covers
    /// <c>prompt_sent</c> itself plus every envelope kind that can only be
    /// emitted while the turn runs, so the check still holds when
    /// <c>prompt_sent</c> scrolled out of the retained tail: terminal
    /// envelopes, permission auto-answers, turn-scoped
    /// <c>session/update</c> kinds (everything except the handshake's
    /// <c>config_option_update</c>), and <c>fatal</c> envelopes stamped
    /// with a post-prompt stage.
    /// </summary>
    private static bool ProvesTurnRan(JsonElement root, string? eventName)
    {
        switch (eventName)
        {
            case DevinAcpEnvelope.EventPromptSent:
            case DevinAcpEnvelope.EventTurnComplete:
            case DevinAcpEnvelope.EventTurnError:
            case DevinAcpEnvelope.EventPermissionAutoGranted:
            case DevinAcpEnvelope.EventPermissionAutoCancelled:
                return true;
            case DevinAcpEnvelope.EventSessionUpdate:
                return !(DevinAcpEnvelope.TryGetUpdate(root, out var update)
                    && update.TryGetProperty(DevinAcpEnvelope.SessionUpdatePropertyName, out var kind)
                    && kind.ValueKind == JsonValueKind.String
                    && string.Equals(kind.GetString(), DevinAcpEnvelope.UpdateKindConfigOptionUpdate, StringComparison.Ordinal));
            case DevinAcpEnvelope.EventFatal:
                return root.TryGetProperty("stage", out var stage)
                    && stage.ValueKind == JsonValueKind.String
                    && (string.Equals(stage.GetString(), "turn", StringComparison.Ordinal)
                        || string.Equals(stage.GetString(), "session/prompt", StringComparison.Ordinal));
            default:
                return false;
        }
    }
}
