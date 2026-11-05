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
/// (<see cref="DevinCliSignals.ModelFallbackStderrMarker"/>) inside a
/// verified <c>codeybox.stderr</c> envelope's <c>text</c> field, but ONLY
/// while the bounded window still shows strictly pre-prompt evidence: the
/// marker must precede the first turn-proof envelope AND at least one
/// envelope that can only exist before <c>session/prompt</c>
/// (<c>session_started</c>, <c>mode_set</c>, or a handshake-stage
/// <c>fatal</c>) must be present. Before the first prompt fd 2 is still
/// CLI-only stderr (provider-authentic); after it fd 2 also carries
/// tool-subprocess stderr the agent/model controls — and mid-turn-legal
/// kinds (<c>notification</c>, <c>protocol_error</c>,
/// <c>unhandled_request</c>, <c>config_option_update</c>) prove nothing
/// about which side of the prompt the window sits on, so they count as
/// neither turn-proof nor pre-prompt evidence. Raw stderr is never
/// scanned: on framed runs the exec wrapper pins the CLI's fd 2 into the
/// envelope stream (the WARN cannot appear there), and on unframed
/// captures stderr is agent/model text. The short phrase is held in the
/// single documented constant <see cref="DevinCliSignals.ModelFallbackStderrMarker"/>,
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
/// examines EVERY typed envelope line in that window in stream order —
/// the interleaved <c>codeybox.stderr</c> ordering the pre-prompt proof
/// needs is incompatible with the last-N devin.acp-only bound, so work is
/// bounded by the char window alone. Like every consumer of
/// <see cref="DevinAcpEnvelope.Enumerate"/>, the scan honours the
/// post-<c>turn_complete</c> provenance rule: envelopes after the shim's
/// terminal <c>turn_complete</c> cannot be shim output and are ignored.
/// Every evaluation is total (never throws). The detection carries only
/// the fixed signature label — never raw agent output. An auth signal in
/// the same capture suppresses the match
/// (<see cref="DevinAuthSignals"/>).</para>
/// </summary>
internal static class DevinModelMismatchDetector
{
    /// <summary>Detector-owned signature label; never raw agent output.</summary>
    internal const string Signature = "devin-model-unresolved";

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
    /// <c>model_mismatch</c> envelope anywhere in the window up to
    /// <c>turn_complete</c> (authoritative — it also covers a terminal
    /// envelope pushed past the last-N cap by trailing relay lines), or
    /// the stderr WARN phrase inside a verified <c>codeybox.stderr</c>
    /// envelope that precedes any turn-proof envelope while at least one
    /// strictly pre-prompt envelope is present. Unframed captures never
    /// match.
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
            var sawTurnComplete = false;

            foreach (var typed in DevinAcpEnvelope.EnumerateEnvelopes(bounded))
            {
                if (typed.Type == DevinAcpEnvelope.EnvelopeType)
                {
                    // turn_complete is the shim's last envelope — the shim
                    // exits immediately after emitting it, so a devin.acp
                    // line positioned after it is pipe-injected, never shim
                    // output (mirrors DevinAcpEnvelope.Enumerate's rule for
                    // the untyped consumers).
                    if (sawTurnComplete)
                        continue;
                    if (string.Equals(typed.Event, DevinAcpEnvelope.EventTurnComplete, StringComparison.Ordinal))
                        sawTurnComplete = true;

                    if (string.Equals(typed.Event, DevinAcpEnvelope.EventModelMismatch, StringComparison.Ordinal))
                        return true;

                    if (!turnStarted && ProvesTurnRan(typed.Root, typed.Event))
                    {
                        turnStarted = true;
                        continue;
                    }

                    if (!turnStarted && IsPrePromptOnly(typed.Root, typed.Event))
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
                    && stderrText?.Contains(DevinCliSignals.ModelFallbackStderrMarker, StringComparison.Ordinal) == true)
                {
                    sawPrePromptMarker = true;
                }
            }

            // Both halves of the proof are required: the marker must precede
            // the first turn-proof envelope, AND the window must still carry
            // an envelope that can only exist pre-prompt. Mid-turn-legal
            // kinds (notification, protocol_error, unhandled_request,
            // config_option_update session_updates) are neutral — when
            // prompt_sent and the early turn-proof envelopes scroll out of
            // the retained tail they leave no pre-prompt evidence, so a
            // post-prompt forged marker cannot pose as the WARN fallback.
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

    /// <summary>
    /// True when a <c>devin.acp</c> envelope can only exist BEFORE
    /// <c>session/prompt</c> — the handshake-stage events whose presence in
    /// the bounded window proves the capture still shows the
    /// provider-authentic fd-2 era. Every other kind is neutral: turn-scoped
    /// kinds are caught by <see cref="ProvesTurnRan"/> first, and
    /// mid-turn-legal kinds (<c>notification</c>, <c>protocol_error</c>,
    /// <c>unhandled_request</c>, <c>config_option_update</c>
    /// <c>session/update</c>s, non-handshake <c>fatal</c> stages) can be
    /// emitted while the turn runs, so they prove neither side of the
    /// prompt boundary. Without this restriction a retained tail that lost
    /// <c>prompt_sent</c> and the early turn-proof envelopes could let a
    /// post-prompt forged stderr marker satisfy the fallback's evidence
    /// requirement.
    /// </summary>
    private static bool IsPrePromptOnly(JsonElement root, string? eventName)
    {
        switch (eventName)
        {
            case DevinAcpEnvelope.EventSessionStarted:
            case DevinAcpEnvelope.EventModeSet:
                return true;
            case DevinAcpEnvelope.EventFatal:
                // Only the handshake stages can coexist with the pre-prompt
                // WARN window: "turn"/"session/prompt" are post-prompt
                // (ProvesTurnRan owns them), and "spawn"/"prompt" failures
                // precede the CLI ever resolving a model, so no WARN can
                // legitimately accompany them.
                return root.TryGetProperty("stage", out var stage)
                    && stage.ValueKind == JsonValueKind.String
                    && stage.GetString() is { } stageName
                    && (string.Equals(stageName, "initialize", StringComparison.Ordinal)
                        || string.Equals(stageName, "session/new", StringComparison.Ordinal)
                        || string.Equals(stageName, "session/set_mode", StringComparison.Ordinal));
            default:
                return false;
        }
    }
}
