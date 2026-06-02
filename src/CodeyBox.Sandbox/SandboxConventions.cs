using CodeyBox.Core;

namespace CodeyBox.Sandbox;

/// <summary>
/// Conventional paths used inside every sandbox regardless of provider. Keeps
/// agent runners and the orchestrator agnostic to which provider is in use.
/// </summary>
public static class SandboxConventions
{
    /// <summary>Cloned working tree the agent edits.</summary>
    public const string WorkDir = "/work";

    /// <summary>Bare repository mount used as the sandbox clone origin and push target.</summary>
    public const string RepoDir = "/repo";

    /// <summary>Tmpfs mount for credential files; ephemeral, never persisted.</summary>
    public const string CredentialsDir = "/run/codeybox/creds";

    /// <summary>Default tmpfs size for credentials.</summary>
    public const long CredentialsTmpfsBytes = 4L * 1024 * 1024;

    /// <summary>
    /// R8-core: directory inside the sandbox where the exec wrapper writes the
    /// active agent CLI's tee'd stdout/stderr (one file per agent invocation,
    /// named with the agent run id). Persisted to the work item as
    /// <c>WorkItem.AgentLogPath</c> so the startup resume handler can re-tail
    /// it after a multipass suspend/start cycle. Lives under <see cref="WorkDir"/>
    /// because that mount is preserved across a suspend (the host bind-mount
    /// stays intact when the VM is frozen and re-attached on start).
    /// </summary>
    public const string AgentLogDir = "/work/.codeybox/agent-logs";

    /// <summary>
    /// Environment variable name the exec wrapper looks for to enable tee'd
    /// capture of stdout/stderr into <see cref="AgentLogDir"/>. Set by the
    /// orchestrator on every agent CLI invocation.
    /// </summary>
    public const string AgentLogFileEnv = "CODEYBOX_AGENT_LOG_FILE";

    /// <summary>
    /// Environment variable used to mark sandbox-owned host processes with the
    /// work item that caused them. Providers derive it from
    /// <see cref="SandboxSpec.TimingWorkItemId"/> so watchdog process probes
    /// are scoped to the item instead of every agent process on the host.
    /// </summary>
    public const string WorkItemIdEnvironmentVariable = "CODEYBOX_WORK_ITEM_ID";

    /// <summary>Logical network profile used by opt-in graphical sandboxes.</summary>
    public const string GraphicalNetworkProfile = "graphical";

    /// <summary>X display exposed by the graphical Multipass flavor.</summary>
    public const string GraphicalDisplay = ":0";

    /// <summary>Known VNC port exposed by the graphical Multipass flavor.</summary>
    public const int GraphicalVncPort = 5900;

    /// <summary>
    /// Returns a spec whose environment includes the work-item marker implied
    /// by <see cref="SandboxSpec.TimingWorkItemId"/>. Explicit caller-provided
    /// environment values are otherwise preserved.
    /// </summary>
    public static SandboxSpec WithTimingEnvironment(SandboxSpec spec)
    {
        if (spec.TimingWorkItemId is not { } workItemId || workItemId.Value == Guid.Empty)
            return spec;

        var env = new Dictionary<string, string>(spec.Environment, StringComparer.Ordinal)
        {
            [WorkItemIdEnvironmentVariable] = workItemId.ToString(),
        };
        return spec with { Environment = env };
    }
}
