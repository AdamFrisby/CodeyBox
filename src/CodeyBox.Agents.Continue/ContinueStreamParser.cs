using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Continue;

/// <summary>
/// Stream parser slot for Continue. The <c>cn</c> CLI emits the model's
/// plain text in one-shot mode (<c>--print</c>) — verified against
/// @continuedev/cli 1.5.47: a bare reply, an empty reply on a file-edit run,
/// and the exit-0 <c>{"status":"error",…}</c> failure envelope — so there is
/// no structured event stream for this parser to claim. It exists so that
/// <see cref="AgentStreamParserSelection.ResolveKind"/> resolves Continue
/// work items to <see cref="AgentKind.Continue"/> rather than
/// <c>unknown</c>; the inherited
/// <see cref="FlexibleAgentStreamParser.ParseAsync"/> returns
/// <see cref="AgentStreamSummary.Unsupported"/> for plaintext output, at
/// which point <see cref="StreamAnalysisService"/> re-runs the file through
/// the plaintext-fallback summariser. The row keeps
/// <see cref="AgentKind.Continue"/>, so Continue-filtered dashboards see the
/// run.
///
/// <para>If a future Continue release adds structured stream output and a
/// discriminator emerges, override <see cref="TryClaim"/> to recognise it.
/// Until then this parser claims nothing by shape — notably NOT the
/// <c>{"status":"error",…}</c> envelope: that shape is provider-relay text
/// with no Continue-unique marker, and misattributing another agent's error
/// line would corrupt stream-file attribution. The runner lifts the envelope
/// through <see cref="ContinueTerminalDiagnoser"/> independently of sniffing
/// (same discipline as the aider parser).</para>
/// </summary>
public sealed class ContinueStreamParser : FlexibleAgentStreamParser
{
    public ContinueStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Continue, options)
    {
    }

    public override bool TryClaim(JsonElement line) => false;
}
