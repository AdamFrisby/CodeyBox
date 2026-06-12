using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Stream parser slot for opencode. The current opencode CLI does NOT emit
/// structured stream-json in non-interactive mode (see
/// <see cref="OpencodeAgentRunner"/> — the runner deliberately does not
/// implement <see cref="IStructuredStreamAgentRunner"/>), so the captured
/// stream file is plaintext stdout/stderr. This parser exists so that
/// <see cref="AgentStreamParserSelection.ResolveKind"/> resolves opencode
/// work items to <c>AgentKind.Opencode</c> rather than <c>unknown</c>; the
/// inherited <see cref="FlexibleAgentStreamParser.ParseAsync"/> returns
/// <see cref="AgentStreamSummary.Unsupported"/> for plaintext output, at
/// which point <see cref="StreamAnalysisService"/> re-runs the file through
/// the plaintext-fallback summariser. The row keeps
/// <c>AgentKind.Opencode</c>, so opencode-filtered dashboards see the run.
///
/// <para>If a future opencode release adds structured stream-json output and
/// a discriminator emerges, override <see cref="TryClaim"/> to recognise it.
/// Until then this parser claims nothing by shape — opencode shares the same
/// "no provider-unique marker" property as cursor and antigravity.</para>
/// </summary>
public sealed class OpencodeStreamParser : FlexibleAgentStreamParser
{
    public OpencodeStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Opencode, options)
    {
    }

    public override bool TryClaim(JsonElement line) => false;
}
