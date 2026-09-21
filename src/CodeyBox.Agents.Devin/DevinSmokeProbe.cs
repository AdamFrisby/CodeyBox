using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Credential viability check for the devin CLI. Returns Ok when the bundle
/// carries a <c>CODEYBOX_DEVIN_AUTH_TOML</c> blob that parses as the CLI's
/// credentials file with a non-empty token field (<c>api_key</c> or
/// <c>windsurf_api_key</c> — current logins write only the latter;
/// materialised into the sandbox by <see cref="DevinAgentRunner"/>); returns
/// Fail otherwise.
///
/// <para>Like the cursor/opencode probes this does NOT issue a network call:
/// the CLI's auth surface is the credentials file itself, and the remote
/// quota endpoint (<c>GetUserStatus</c>) is exercised separately by
/// <see cref="DevinQuotaProbe"/>. Requiring a token field catches a
/// malformed or half-written credentials file before dispatch rather than
/// letting it surface as a per-item auth failure.</para>
/// </summary>
public sealed class DevinSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<DevinSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.Devin;

    public DevinSmokeProbe(ILogger<DevinSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        // No I/O happens here — a Stopwatch would report ~0. Report
        // TimeSpan.Zero explicitly so the value is honest about what was
        // measured.
        if (!credential.EnvironmentVariables.TryGetValue(
                DevinAgentRunner.AuthTomlEnvironmentVariable, out var toml)
            || string.IsNullOrEmpty(toml))
        {
            _log?.LogDebug("Devin smoke probe found no CODEYBOX_DEVIN_AUTH_TOML in credential bundle");
            return Task.FromResult(new AgentSmokeResult(
                false, "no credentials in credential bundle", TimeSpan.Zero, SmokeFailureCategory.Persistent));
        }

        if (string.IsNullOrWhiteSpace(DevinCredentialsToml.TryGetToken(toml)))
        {
            _log?.LogDebug("Devin smoke probe: credentials.toml carries no api_key/windsurf_api_key field");
            return Task.FromResult(new AgentSmokeResult(
                false, "credentials.toml has no api key", TimeSpan.Zero, SmokeFailureCategory.Persistent));
        }

        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
