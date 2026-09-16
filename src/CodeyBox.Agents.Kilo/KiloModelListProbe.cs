using CodeyBox.Core;

namespace CodeyBox.Agents.Kilo;

/// <summary>
/// Model-list probe for kilo. Returns the curated
/// <see cref="KiloKnownModels"/> seed: the model catalog is per provider
/// and server-side, so it cannot back a host-side startup probe.
/// Operator-configured ids absent from the seed surface as a startup
/// warning, not a hard reject (see
/// <see cref="KiloKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Goose/Autohand static-list probes. No quota-meter probe is
/// registered: kilo exposes no meterable quota endpoint (<c>kilo stats</c>
/// is local history, not a balance), so members fall through to the
/// <c>NullQuotaProbe</c> unknown path like pi/goose/autohand.
/// </summary>
public sealed class KiloModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Kilo;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(KiloKnownModels.All));
}
