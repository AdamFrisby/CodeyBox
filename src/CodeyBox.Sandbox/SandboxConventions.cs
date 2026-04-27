namespace CodeyBox.Sandbox;

/// <summary>
/// Conventional paths used inside every sandbox regardless of provider. Keeps
/// agent runners and the orchestrator agnostic to which provider is in use.
/// </summary>
public static class SandboxConventions
{
    /// <summary>Cloned working tree the agent edits.</summary>
    public const string WorkDir = "/work";

    /// <summary>Tmpfs mount for credential files; ephemeral, never persisted.</summary>
    public const string CredentialsDir = "/run/codeybox/creds";

    /// <summary>Default tmpfs size for credentials.</summary>
    public const long CredentialsTmpfsBytes = 4L * 1024 * 1024;
}
