namespace CodeyBox.Core;

/// <summary>
/// Well-known constants shared between <c>CodeyBox.Orchestrator</c> and
/// <c>CodeyBox.Agents.Antigravity</c> to eliminate cross-module magic strings.
/// Antigravity is Google's successor to the gemini-cli (Gemini Code Assist is
/// being sunset 2026-06-18); the <c>agy</c> binary uses the same Sign-in-with-
/// Google OAuth path as gemini-cli but stores credentials under a separate
/// home directory so a single host can hold both at once during the
/// transition.
/// </summary>
public static class AntigravityConstants
{
    /// <summary>
    /// Environment variable that carries the raw contents of the host's
    /// Antigravity OAuth credentials JSON into sandboxes. The exact on-host
    /// path is operator-configured (the <c>agy</c> binary is proprietary
    /// closed-source); the runner materialises this back to the CLI's
    /// expected location inside the VM at run time.
    /// </summary>
    public const string OAuthCredsEnvVar = "CODEYBOX_ANTIGRAVITY_OAUTH_CREDS_JSON";
}
