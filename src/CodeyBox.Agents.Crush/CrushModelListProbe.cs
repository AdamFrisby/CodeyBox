using CodeyBox.Core;

namespace CodeyBox.Agents.Crush;

/// <summary>
/// Model-list probe for Crush. Returns the curated
/// <see cref="CrushKnownModels"/> seed: the model catalog is per provider
/// and server-side (the <c>crush models</c> listing is a static registry,
/// not a live entitlement check), so it cannot back a host-side startup
/// probe. Operator-configured ids absent from the seed surface as a startup
/// warning, not a hard reject (see
/// <see cref="CrushKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Continue/Cmd static-list probes. No quota-meter probe is
/// registered: <c>crush stats</c> renders an HTML usage report with no
/// machine-readable balance, so members fall through to the
/// <c>NullQuotaProbe</c> unknown path like continue/cmd and the router gates
/// on observed failures.
/// </summary>
public sealed class CrushModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Crush;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(CrushKnownModels.All));
}
