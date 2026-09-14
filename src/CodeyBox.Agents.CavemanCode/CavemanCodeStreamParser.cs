using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.CavemanCode;

/// <summary>
/// Stream parser slot for caveman-code. The runner invokes
/// <c>caveman-code -p</c> in plain-text mode (print-mode <c>--mode json</c>
/// emits the CLI's internal unfrozen session events, and the frozen
/// <c>exec --json</c> stream cannot take the prompt on stdin — see
/// <see cref="CavemanCodeAgentRunner"/>), so the captured stream file is
/// plaintext stdout/stderr. This parser exists so that
/// <see cref="AgentStreamParserSelection.ResolveKind"/> resolves caveman-code
/// work items to <see cref="AgentKind.CavemanCode"/> rather than
/// <c>unknown</c>; the inherited
/// <see cref="FlexibleAgentStreamParser.ParseAsync"/> returns
/// <see cref="AgentStreamSummary.Unsupported"/> for plaintext output, at
/// which point <see cref="StreamAnalysisService"/> re-runs the file through
/// the plaintext-fallback summariser.
///
/// <para>If a future release documents a stable stream shape (today only the
/// <c>exec --json</c> event envelope is frozen, and the runner does not use
/// it), override <see cref="TryClaim"/> to recognise it. Until then this
/// parser claims nothing by shape.</para>
/// </summary>
public sealed class CavemanCodeStreamParser : FlexibleAgentStreamParser
{
    public CavemanCodeStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.CavemanCode, options)
    {
    }

    public override bool TryClaim(JsonElement line) => false;
}
