using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Reads the Gemini CLI's OAuth credentials and settings from a directory on
/// the host (default <c>~/.gemini/</c>) on every <see cref="GetAsync"/> call
/// and surfaces them as environment variables that <see
/// cref="CodeyBox.Agents.Gemini.GeminiAgentRunner.PrepareSandboxAsync"/>
/// materialises into <c>~/.gemini/</c> inside the sandbox.
///
/// <para>Unlike Claude (which accepts an OAuth token directly via an env var),
/// the Gemini CLI hard-reads <c>~/.gemini/oauth_creds.json</c> and
/// <c>~/.gemini/settings.json</c> in its target user's home — there is no
/// env-var alternative for OAuth. So we ship the file contents through env vars
/// and let the runner write them to the canonical paths inside the VM.</para>
///
/// <para>Re-reading on each pickup picks up token rotations from the host's
/// <c>gemini</c> CLI without an orchestrator restart.</para>
///
/// Only handles <see cref="AgentKind.Gemini"/>; returns null for others so a
/// chained env-var provider can supply API-key based auth.
/// </summary>
public sealed class GeminiOAuthFileCredentialProvider : ICredentialProvider
{
    public const string OAuthCredsEnvVar = "CODEYBOX_GEMINI_OAUTH_CREDS_JSON";
    public const string SettingsEnvVar = "CODEYBOX_GEMINI_SETTINGS_JSON";

    private readonly string _oauthCredsPath;
    private readonly string _settingsPath;
    private readonly ILogger<GeminiOAuthFileCredentialProvider>? _log;

    public GeminiOAuthFileCredentialProvider(
        string oauthCredsPath,
        string settingsPath,
        ILogger<GeminiOAuthFileCredentialProvider>? log = null)
    {
        _oauthCredsPath = oauthCredsPath ?? throw new ArgumentNullException(nameof(oauthCredsPath));
        _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
        _log = log;
    }

    public async Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (agent != AgentKind.Gemini)
            return null;

        if (!File.Exists(_oauthCredsPath))
        {
            _log?.LogDebug("Gemini OAuth file not found at {Path}; falling through", _oauthCredsPath);
            return null;
        }

        string oauthCreds;
        try
        {
            oauthCreds = await File.ReadAllTextAsync(_oauthCredsPath, ct);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to read Gemini OAuth file {Path}; falling through", _oauthCredsPath);
            return null;
        }

        if (string.IsNullOrWhiteSpace(oauthCreds))
            return null;

        var env = new Dictionary<string, string> { [OAuthCredsEnvVar] = oauthCreds };

        // settings.json is optional but typically required to tell the Gemini
        // CLI which auth method to use (e.g. "oauth-personal" for free /
        // subscription accounts). When absent, we ship a minimal default so the
        // CLI uses GCA OAuth rather than prompting for an API key.
        if (File.Exists(_settingsPath))
        {
            try
            {
                env[SettingsEnvVar] = await File.ReadAllTextAsync(_settingsPath, ct);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Failed to read Gemini settings file {Path}; using default", _settingsPath);
                env[SettingsEnvVar] = DefaultSettingsJson;
            }
        }
        else
        {
            env[SettingsEnvVar] = DefaultSettingsJson;
        }

        return new AgentCredential(AgentKind.Gemini, env, new Dictionary<string, string>());
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
