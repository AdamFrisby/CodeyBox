using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Cursor;

public sealed class CursorStreamParser : FlexibleAgentStreamParser
{
    public CursorStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Cursor, options)
    {
    }

    /// <summary>
    /// Cursor CLI emits NDJSON in the literal Claude-shape (system/user/
    /// assistant/result) when <c>--output-format stream-json
    /// --stream-partial-output</c> are set, so the on-wire event vocabulary
    /// is byte-identical to Claude's. There is no cursor-only marker we can
    /// claim by shape; doing so would either lose a sniff tie or mis-tag
    /// real Claude streams. Attribution is instead delegated to
    /// <see cref="AgentStreamParserSelection.ResolveKind"/>, which uses
    /// cost rows and the work item's declared agent — the authoritative
    /// cursor signal at orchestration time.
    /// </summary>
    public override bool TryClaim(JsonElement line) => false;
}
