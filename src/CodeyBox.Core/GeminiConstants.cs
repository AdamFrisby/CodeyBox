namespace CodeyBox.Core;

/// <summary>
/// Well-known constants shared between <c>CodeyBox.Orchestrator</c> and
/// <c>CodeyBox.Agents.Gemini</c> to eliminate cross-module magic strings.
/// </summary>
public static class GeminiConstants
{
    /// <summary>
    /// Environment variable that carries the raw contents of
    /// <c>~/.gemini/oauth_creds.json</c> from the host into sandboxes.
    /// </summary>
    public const string OAuthCredsEnvVar = "CODEYBOX_GEMINI_OAUTH_CREDS_JSON";

    /// <summary>
    /// Environment variable that carries the raw contents of
    /// <c>~/.gemini/settings.json</c> from the host into sandboxes.
    /// </summary>
    public const string SettingsEnvVar = "CODEYBOX_GEMINI_SETTINGS_JSON";
}
