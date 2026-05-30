using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Reads the Gemini CLI's OAuth credentials and settings from the host
/// (default <c>~/.gemini/</c>) and surfaces them as environment variables that
/// <see cref="CodeyBox.Agents.Gemini.GeminiAgentRunner.PrepareSandboxAsync"/>
/// materialises into <c>~/.gemini/</c> inside the sandbox. Backed by shared
/// <see cref="CredentialFileSource"/> instances so the host's file-watcher
/// picks up out-of-band token rotations (operator running <c>gemini</c> on
/// the host, scripted refresh, etc.) and every new sandbox is handed the
/// fresh token without an orchestrator restart.
///
/// <para>Unlike Claude (which accepts an OAuth token directly via an env var),
/// the Gemini CLI hard-reads <c>~/.gemini/oauth_creds.json</c> and
/// <c>~/.gemini/settings.json</c> in its target user's home — there is no
/// env-var alternative for OAuth. So we ship the file contents through env vars
/// and let the runner write them to the canonical paths inside the VM.</para>
///
/// Only handles <see cref="AgentKind.Gemini"/>; returns null for others so a
/// chained env-var provider can supply API-key based auth.
/// </summary>
public sealed class GeminiOAuthFileCredentialProvider : ICredentialProvider, IDisposable
{
    public const string OAuthCredsEnvVar = CodeyBox.Core.GeminiConstants.OAuthCredsEnvVar;
    public const string SettingsEnvVar = CodeyBox.Core.GeminiConstants.SettingsEnvVar;

    private readonly CredentialFileSource _oauthSource;
    private readonly CredentialFileSource _settingsSource;
    private readonly ILogger<GeminiOAuthFileCredentialProvider>? _log;
    private readonly bool _ownsSources;
    private bool _disposed;

    public GeminiOAuthFileCredentialProvider(
        string oauthCredsPath,
        string settingsPath,
        ILogger<GeminiOAuthFileCredentialProvider>? log = null,
        bool watch = true)
        : this(
            new CredentialFileSource(
                oauthCredsPath ?? throw new ArgumentNullException(nameof(oauthCredsPath)), log, watch),
            new CredentialFileSource(
                settingsPath ?? throw new ArgumentNullException(nameof(settingsPath)), log, watch),
            log,
            ownsSources: true)
    {
    }

    public GeminiOAuthFileCredentialProvider(
        CredentialFileSource oauthSource,
        CredentialFileSource settingsSource,
        ILogger<GeminiOAuthFileCredentialProvider>? log = null)
        : this(oauthSource, settingsSource, log, ownsSources: false)
    {
    }

    private GeminiOAuthFileCredentialProvider(
        CredentialFileSource oauthSource,
        CredentialFileSource settingsSource,
        ILogger<GeminiOAuthFileCredentialProvider>? log,
        bool ownsSources)
    {
        _oauthSource = oauthSource ?? throw new ArgumentNullException(nameof(oauthSource));
        _settingsSource = settingsSource ?? throw new ArgumentNullException(nameof(settingsSource));
        _log = log;
        _ownsSources = ownsSources;
    }

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (agent != AgentKind.Gemini)
            return Task.FromResult<AgentCredential?>(null);

        var oauthCreds = _oauthSource.GetRaw();
        if (string.IsNullOrWhiteSpace(oauthCreds))
        {
            _log?.LogDebug("Gemini OAuth file not present or empty at {Path}; falling through", _oauthSource.FilePath);
            return Task.FromResult<AgentCredential?>(null);
        }

        var env = new Dictionary<string, string> { [OAuthCredsEnvVar] = oauthCreds };

        // settings.json is optional but typically required to tell the Gemini
        // CLI which auth method to use (e.g. "oauth-personal" for free /
        // subscription accounts). When absent, we ship a minimal default so the
        // CLI uses GCA OAuth rather than prompting for an API key.
        var settings = _settingsSource.GetRaw();
        env[SettingsEnvVar] = string.IsNullOrWhiteSpace(settings) ? DefaultSettingsJson : settings;

        return Task.FromResult<AgentCredential?>(
            new AgentCredential(AgentKind.Gemini, env, new Dictionary<string, string>()));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsSources)
        {
            _oauthSource.Dispose();
            _settingsSource.Dispose();
        }
    }

    private const string DefaultSettingsJson = """
        {
          "security": {
            "auth": {
              "selectedType": "oauth-personal"
            }
          }
        }
        """;
}
