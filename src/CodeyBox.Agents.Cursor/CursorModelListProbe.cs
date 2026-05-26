using CodeyBox.Core;

namespace CodeyBox.Agents.Cursor;

/// <summary>
/// Model-list probe for Cursor.
///
/// <para>Cursor does not currently document a model-enumeration endpoint
/// reachable from a subscription token. The probe returns a static set
/// matching the models the operator has configured under their subscription:
/// <c>composer-2.5</c> (default; Opus-4.6-equivalent quality). New models can
/// be added when Cursor releases them.</para>
///
/// <para>If a Cursor model-list endpoint surfaces later, model the
/// implementation on <c>CodexModelListProbe</c> (HTTP GET, JSON parse,
/// <c>AgentModelListResult.Success</c>/<c>Failed</c>).</para>
/// </summary>
public sealed class CursorModelListProbe : IAgentModelListProbe
{
    internal static readonly IReadOnlyList<string> KnownModels = new[]
    {
        "composer-2.5",
    };

    public AgentKind Kind => AgentKind.Cursor;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(KnownModels));
}
