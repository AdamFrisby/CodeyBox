using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Reads the Antigravity (<c>agy</c>) OAuth token bundle from a host env var and
/// ships it <em>verbatim</em> to the sandbox, where the runner writes it to
/// <c>~/.gemini/antigravity-cli/antigravity-oauth-token</c> (agy's
/// <c>fileTokenStorage</c> path, used when no system keyring is present). The
/// <c>refresh_token</c> is intentionally retained — agy must self-refresh the
/// short-lived access_token in-VM; see
/// <see cref="CredentialFileTokenExtractor.TryBuildAntigravityTokenBundle"/> for
/// the isolation rationale (the host authenticates from the keyring, a separate
/// store).
///
/// <para>This is the env-var fallback for the canonical
/// <see cref="AgentInstanceCredentialResolver"/> file/per-instance path.</para>
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

        if (!CredentialFileTokenExtractor.TryBuildAntigravityTokenBundle(raw, out var bundle))
        {
            _log?.LogWarning(
                "Antigravity OAuth env var {EnvVar} is set but did not parse as an agy OAuth token bundle; ignoring.",
                HostEnvironmentVariable);
            return Task.FromResult<AgentCredential?>(null);
        }

        var env = new Dictionary<string, string>
        {
            [AntigravityConstants.OAuthCredsEnvVar] = bundle,
        };
        return Task.FromResult<AgentCredential?>(
            new AgentCredential(AgentKind.Antigravity, env, new Dictionary<string, string>()));
    }
}
