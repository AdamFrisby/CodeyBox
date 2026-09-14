using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.CavemanCode;

/// <summary>
/// Minimal credential viability check for caveman-code. Returns Ok when the
/// bundle contains a non-empty provider API key from
/// <see cref="CavemanCodeAgentRunner.CredentialEnvironmentVariables"/>
/// (BYOK auth, shipped to the sandbox environment by the runner);
/// returns Fail otherwise.
///
/// <para>This does NOT issue a network call: with no key configured the CLI
/// itself reports <c>No API key found</c> (verified live against 0.65.2),
/// and with a key the static model registry answers <c>--list-models</c>
/// offline, so a host-side HTTP probe would add no signal. Key validity is
/// verified in-VM by <see cref="CavemanCodeInVmSmokeProbe"/> (the table must
/// parse) and at dispatch by the quota-failure detector. Mirrors the
/// opencode credential-presence probe shape.</para>
/// </summary>
public sealed class CavemanCodeSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<CavemanCodeSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.CavemanCode;

    public CavemanCodeSmokeProbe(ILogger<CavemanCodeSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        foreach (var variable in CavemanCodeAgentRunner.CredentialEnvironmentVariables)
        {
            if (credential.EnvironmentVariables.TryGetValue(variable, out var value)
                && !string.IsNullOrEmpty(value))
            {
                return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
            }
        }

        _log?.LogDebug("CavemanCode smoke probe found no provider API key in credential bundle");
        return Task.FromResult(new AgentSmokeResult(
            false, "no provider API key in credential bundle", TimeSpan.Zero, SmokeFailureCategory.Persistent));
    }
}
