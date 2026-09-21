using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Stream parser for devin print-mode output. The CLI has no stream-json flag
/// (verified against devin 3000.11.1): <c>-p</c> writes plain text to stdout,
/// so the capture file carries no NDJSON events for this parser to claim.
/// <c>--export</c> writes an ATIF transcript to a separate file, not stdout,
/// so it cannot back stream parsing either.
/// </summary>
public sealed class DevinStreamParser : FlexibleAgentStreamParser
{
    public DevinStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Devin, options)
    {
    }

    /// <summary>
    /// Devin emits plaintext, so a devin capture can never produce a claimable
    /// NDJSON shape — claiming by shape would mis-tag other agents' streams.
    /// Attribution is delegated to
    /// <see cref="AgentStreamParserSelection.ResolveKind"/>, which uses cost
    /// rows and the work item's declared agent.
    /// </summary>
    public override bool TryClaim(JsonElement line) => false;
}
