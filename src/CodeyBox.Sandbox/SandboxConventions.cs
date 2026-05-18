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

    /// <summary>Logical network profile used by opt-in graphical sandboxes.</summary>
    public const string GraphicalNetworkProfile = "graphical";

    /// <summary>X display exposed by the graphical Multipass flavor.</summary>
    public const string GraphicalDisplay = ":0";

    /// <summary>Known VNC port exposed by the graphical Multipass flavor.</summary>
    public const int GraphicalVncPort = 5900;
}
