using CodeyBox.Core;

namespace CodeyBox.Agents.Omp;

/// <summary>
/// Model-list probe for omp. Returns the curated <see cref="OmpKnownModels"/>
/// seed: the model catalog is per provider and server-side, and
/// <c>omp models --json</c> returns an empty set without login state, so it
/// cannot back a host-side startup probe. Operator-configured ids absent
/// from the seed surface as a startup warning, not a hard reject (see
/// <see cref="OmpKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Pi/Prime static-list probes. No quota-meter probe is
/// registered: <c>omp usage</c> only reports login-account balances, not
/// the env-key path the runner uses, so members fall through to the
/// <c>NullQuotaProbe</c> unknown path like pi/prime/goose.
/// </summary>
public sealed class OmpModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Omp;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(OmpKnownModels.All));
}
