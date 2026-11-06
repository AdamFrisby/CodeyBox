namespace CodeyBox.Agents.Devin;

/// <summary>
/// Constants for signals the devin CLI itself emits on its stderr — the
/// wire-protocol vocabulary shared by the ACP shim
/// (<c>Resources/devin-acp-client.py</c>'s mirror constants), the framed
/// transient detectors, and the print-mode veto. Kept in one place because
/// the phrase is a property of the CLI's output protocol, not of any single
/// consumer's classification concern.
/// </summary>
internal static class DevinCliSignals
{
    /// <summary>
    /// The stable phrase inside the devin 3000.11.1 <c>chisel_server::acp</c>
    /// stderr WARN emitted when the <c>--model</c> value does not resolve to
    /// an available model (full line: <c>chisel_server::acp: `devin acp
    /// --model` value did not resolve to an available model; falling back to
    /// your configured `agent.model` or the default</c>). This is the short
    /// form: it is only matched where fd 2 is provably CLI-only (the
    /// pre-prompt window inside verified <c>codeybox.stderr</c> envelopes on
    /// an envelope-framed capture). The phrase contains no JSON-escapable
    /// characters, so it matches verbatim inside a decoded envelope
    /// <c>text</c> field. Keep in lockstep with the shim's
    /// <c>MODEL_FALLBACK_STDERR_MARKER</c>.
    /// </summary>
    internal const string ModelFallbackStderrMarker = "did not resolve to an available model";

    /// <summary>
    /// The extended WARN shape — <see cref="ModelFallbackStderrMarker"/>
    /// plus the CLI's own fallback clause — for surfaces where stderr is
    /// shared with agent-controlled tool subprocesses (print-mode
    /// <c>devin -p</c> execs). The longer phrase shrinks the accidental
    /// forgery window (e.g. a tool error quoting just the short marker);
    /// a determined same-uid writer can still print it verbatim, which is
    /// why the print-mode veto reports a POSSIBLE substitution rather than
    /// asserting the cause.
    /// </summary>
    internal const string ModelFallbackStderrMarkerFull =
        ModelFallbackStderrMarker + "; falling back to your configured `agent.model` or the default";
}
