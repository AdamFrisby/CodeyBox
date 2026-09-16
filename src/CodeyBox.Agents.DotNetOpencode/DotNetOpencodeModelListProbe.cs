using CodeyBox.Core;

namespace CodeyBox.Agents.DotNetOpencode;

/// <summary>
/// Model-list probe for dotnet-opencode. Returns the curated
/// <see cref="DotNetOpencodeKnownModels"/> seed: the CLI exposes no
/// non-interactive model catalog (enumeration lives behind the TUI /
/// authenticated server), so it cannot back a host-side startup probe the
/// way the opencode probe shells out to <c>opencode models</c>.
/// Operator-configured ids absent from the seed surface as a startup
/// warning, not a hard reject (see
/// <see cref="DotNetOpencodeKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Pi/Crock/Antigravity static-list probes.
/// </summary>
public sealed class DotNetOpencodeModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.DotNetOpencode;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(DotNetOpencodeKnownModels.All));
}
