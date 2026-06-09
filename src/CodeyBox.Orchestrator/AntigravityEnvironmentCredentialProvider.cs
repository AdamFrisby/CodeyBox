using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Reads the Antigravity (<c>agy</c>) OAuth credentials JSON from a host env
/// var and ships a <em>sanitised</em> bundle to the sandbox: the
/// <c>refresh_token</c> field is removed before the env var crosses the VM
/// boundary so the in-VM CLI cannot self-refresh and rotate the refresh_token
/// out from under the host CLI and quota probes (same isolation invariant the
/// Claude credential path enforces).
///
/// <para>This is the env-var fallback for the canonical
/// <see cref="AgentInstanceCredentialResolver"/> file/per-instance path; the
/// generic <see cref="EnvironmentCredentialProvider"/> would copy the env var
/// verbatim and break the invariant.</para>
/// </summary>
public sealed class AntigravityEnvironmentCredentialProvider : ICredentialProvider
{
    /// <summary>
    /// Host env var carrying the raw Google OAuth creds JSON (same shape as
    /// <c>~/.gemini/oauth_creds.json</c>). Identical to the generic mapping's
    /// HostEnvironmentVariable — names a single operator-facing surface.
    /// </summary>
    public const string HostEnvironmentVariable = "CODEYBOX_ANTIGRAVITY_OAUTH_CREDS_JSON";

    private readonly ILogger<AntigravityEnvironmentCredentialProvider>? _log;

    public AntigravityEnvironmentCredentialProvider(
        ILogger<AntigravityEnvironmentCredentialProvider>? log = null)
    {
        _log = log;
    }

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (agent != AgentKind.Antigravity)
            return Task.FromResult<AgentCredential?>(null);

        var raw = Environment.GetEnvironmentVariable(HostEnvironmentVariable);
        if (string.IsNullOrEmpty(raw))
            return Task.FromResult<AgentCredential?>(null);

        if (!CredentialFileTokenExtractor.TryBuildAntigravitySanitisedBundle(raw, out var sanitised))
        {
            _log?.LogWarning(
                "Antigravity OAuth env var {EnvVar} is set but did not parse as Google OAuth creds JSON; ignoring.",
                HostEnvironmentVariable);
            return Task.FromResult<AgentCredential?>(null);
        }

        var env = new Dictionary<string, string>
        {
            [AntigravityConstants.OAuthCredsEnvVar] = sanitised,
        };
        return Task.FromResult<AgentCredential?>(
            new AgentCredential(AgentKind.Antigravity, env, new Dictionary<string, string>()));
    }
}
