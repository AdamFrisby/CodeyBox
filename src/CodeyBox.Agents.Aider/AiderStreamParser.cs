using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Aider;

/// <summary>
/// Stream parser slot for aider. The aider CLI emits plaintext in one-shot
/// mode (run header, thinking/answer blocks, <c>Tokens: …</c> accounting,
/// <c>Applied edit to …</c> lines — see <see cref="AiderAgentRunner"/>), so
/// there is no NDJSON event stream for this parser to claim. It exists so that
/// <see cref="AgentStreamParserSelection.ResolveKind"/> resolves aider work
/// items to <see cref="AgentKind.Aider"/> rather than <c>unknown</c>; the
/// inherited <see cref="FlexibleAgentStreamParser.ParseAsync"/> returns
/// <see cref="AgentStreamSummary.Unsupported"/> for plaintext output, at which
/// point <see cref="StreamAnalysisService"/> re-runs the file through the
/// plaintext-fallback summariser. The row keeps <see cref="AgentKind.Aider"/>,
/// so aider-filtered dashboards see the run.
///
/// <para>If a future aider release adds structured stream output and a
/// discriminator emerges, override <see cref="TryClaim"/> to recognise it.
/// Until then this parser claims nothing by shape — aider shares the same "no
/// provider-unique marker" property as cursor, opencode, and antigravity.</para>
/// </summary>
public sealed class AiderStreamParser : FlexibleAgentStreamParser
{
    public AiderStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Aider, options)
    {
    }

    public override bool TryClaim(JsonElement line) => false;
}
