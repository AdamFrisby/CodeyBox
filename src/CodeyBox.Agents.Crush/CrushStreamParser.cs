using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Crush;

/// <summary>
/// Stream parser slot for Crush. The <c>crush</c> CLI emits the model's
/// plain text in one-shot mode (<c>run</c>) — verified against
/// @charmland/crush 0.95.0: a bare reply, a piped-stdin reply, a repo-edit
/// run whose summary text landed on stdout, and the exit-1 styled
/// <c>ERROR</c> failure blocks on stderr — so there is no structured event
/// stream for this parser to claim. It exists so that
/// <see cref="AgentStreamParserSelection.ResolveKind"/> resolves Crush work
/// items to <see cref="AgentKind.Crush"/> rather than <c>unknown</c>; the
/// inherited <see cref="FlexibleAgentStreamParser.ParseAsync"/> returns
/// <see cref="AgentStreamSummary.Unsupported"/> for plaintext output, at
/// which point <see cref="StreamAnalysisService"/> re-runs the file through
/// the plaintext-fallback summariser. The row keeps
/// <see cref="AgentKind.Crush"/>, so Crush-filtered dashboards see the run.
///
/// <para>If a future Crush release adds structured stream output and a
/// discriminator emerges, override <see cref="TryClaim"/> to recognise it.
/// Until then this parser claims nothing by shape — notably NOT the styled
/// <c>ERROR</c> failure blocks: those are fixed-width human renderings with
/// no Crush-unique marker, and misattributing another agent's error text
/// would corrupt stream-file attribution. The runner lifts failures through
/// <see cref="CrushTerminalDiagnoser"/> independently of sniffing (same
/// discipline as the continue parser).</para>
/// </summary>
public sealed class CrushStreamParser : FlexibleAgentStreamParser
{
    public CrushStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Crush, options)
    {
    }

    public override bool TryClaim(JsonElement line) => false;
}
