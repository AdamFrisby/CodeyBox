namespace CodeyBox.Agents.Crock;

/// <summary>
/// Sandbox-side wiring for the <c>crock</c> agent. Bound under
/// <c>CodeyBox:Crock</c>; hot-reloadable through <c>IOptionsMonitor</c>.
///
/// <para><b>Why this exists — tunnel incompatibility.</b> CrockCode submits
/// work to Anthropic's Message Batches API and exposes a local MCP tool
/// surface that the batch worker calls back into via a public tunnel
/// (cloudflared / ngrok) when run on a developer workstation. CodeyBox's
/// sandbox model is fundamentally incompatible with the "public tunnel inside
/// the VM" shape:</para>
/// <list type="bullet">
///   <item><description><b>Network policy.</b> The sandbox network profile is
///   an outbound allow-list (<see cref="CodeyBox.Core.Project.AgentAllowedHosts"/>),
///   intentionally scoped to vetted hosts (api.anthropic.com, etc.). A public
///   tunnel relays inbound traffic through an outbound connection, so it
///   technically works against the allow-list IF the tunnel provider's hosts
///   are added — but that punches the allow-list wide enough that ANY agent
///   sharing the profile can reach the tunnel hosts too, eroding the
///   isolation the allow-list exists to provide.</description></item>
///   <item><description><b>Lifecycle.</b> Sandboxes are ephemeral and recycled
///   per work item. Spinning up a per-item cloudflared/ngrok tunnel burns
///   tunnel-provider rate-limits and quotas, and the public URL is sensitive
///   (anyone holding it can hit the MCP tools).</description></item>
///   <item><description><b>Credential surface.</b> Each tunnel requires its
///   own credentials (cloudflared OAuth / ngrok auth token) that the
///   orchestrator would have to ship into every sandbox — doubling the
///   secret-material surface for a feature only one agent needs.</description></item>
/// </list>
///
/// <para><b>Resolution — host-side daemon.</b> The operator runs <c>crock
/// daemon</c> on the host (with the tunnel and any MCP tools). The daemon
/// listens on a Unix socket. The sandbox bind-mounts that socket's parent
/// directory <em>read-only</em> and the in-VM <c>crock submit</c> connects to
/// the daemon over it — no tunnel, no public URL, no per-item tunnel cost.</para>
///
/// <para><b>Credential exposure — read this.</b> This is NOT a
/// "credential never leaves the host" design. The host env var
/// <c>CODEYBOX_CROCK_CONFIG_JSON</c> (which contains <c>anthropic_api_key</c>)
/// is shipped into the sandbox as <c>CROCK_CONFIG_JSON</c> and materialised by
/// the runner into <c>~/.crockcode/config.json</c> inside the VM, exactly like
/// every other agent's key. The host↦VM key materialisation is the accepted
/// provisioning shape for an ephemeral, internet-only sandbox; if the host
/// daemon is configured with its OWN key, that daemon-side key is what bills
/// the batch and the in-VM key is only used for the CLI's local handshake.</para>
///
/// <para><b>Operator setup.</b> Set
/// <see cref="HostDaemonSocketPath"/> to an absolute path under a directory
/// dedicated to the daemon socket (e.g. <c>/run/codeybox/crock-daemon.sock</c>)
/// and ensure the daemon is running before dispatching crock work. The runner
/// refuses to dispatch when the path is unset OR resolves (after collapsing
/// <c>..</c> segments and symlinks) to a shared system directory, so an
/// operator misconfiguration surfaces as a clear Infrastructure failure rather
/// than a hung batch or a catastrophic host mount.</para>
/// </summary>
public sealed class CrockSandboxOptions
{
    /// <summary>Default in-sandbox mount target for the daemon socket.</summary>
    public const string DefaultSandboxDaemonSocketPath = "/run/codeybox/crock-daemon.sock";

    /// <summary>Default env var the in-VM CLI reads to find the daemon socket.</summary>
    public const string DefaultDaemonSocketEnvVar = "CROCK_DAEMON_SOCKET";

    /// <summary>
    /// Host filesystem path to the <c>crock daemon</c> Unix socket. When set,
    /// the credential provider bind-mounts the socket's <em>parent directory</em>
    /// (read-only) into the sandbox at the parent of
    /// <see cref="SandboxDaemonSocketPath"/> and points the in-VM CLI at it via
    /// the <see cref="DaemonSocketEnvVar"/> env var. When null/empty the runner
    /// refuses to dispatch (in-VM tunnels are not supported — see the
    /// class-level docs).
    ///
    /// <para><b>Constraints (enforced at credential pickup):</b> MUST be an
    /// absolute path whose parent directory, after canonicalisation, is NOT a
    /// shared system root (<c>/</c>, <c>/etc</c>, <c>/run</c>, <c>/var/run</c>,
    /// <c>/tmp</c>, <c>$HOME</c>, …). Dedicate a subdirectory to the socket.</para>
    ///
    /// <para><b>Logging note:</b> the value itself is not written to any Crock
    /// log line, but the canonicalised <em>parent directory</em> is passed to
    /// the sandbox provider as a mount source, and some providers log mount
    /// sources in diagnostics — do not treat the parent directory path as a
    /// secret. The API key inside the config JSON is never logged.</para>
    /// </summary>
    public string? HostDaemonSocketPath { get; set; }

    /// <summary>
    /// In-sandbox path where <see cref="HostDaemonSocketPath"/> is bind-mounted.
    /// Default <see cref="DefaultSandboxDaemonSocketPath"/>. The runner passes
    /// this path to the in-VM CLI via the <see cref="DaemonSocketEnvVar"/> env
    /// var so the CLI knows where to connect inside the sandbox.
    /// </summary>
    public string SandboxDaemonSocketPath { get; set; } = DefaultSandboxDaemonSocketPath;

    /// <summary>
    /// Env var the in-VM crock CLI reads to find the daemon socket. Mirrors
    /// CrockCode's documented <c>CROCK_DAEMON_SOCKET</c> contract; kept as
    /// config so an operator running a custom crock build with a different
    /// env-var name can rewire without a code change. Default
    /// <see cref="DefaultDaemonSocketEnvVar"/>.
    /// </summary>
    public string DaemonSocketEnvVar { get; set; } = DefaultDaemonSocketEnvVar;

    /// <summary>
    /// The env var name the in-VM CLI reads to find the daemon socket, falling
    /// back to <see cref="DefaultDaemonSocketEnvVar"/> when
    /// <see cref="DaemonSocketEnvVar"/> is unset/blank. Single source of truth
    /// shared by <see cref="CrockEnvironmentCredentialProvider"/> (which SETS the
    /// var on the credential bundle) and <see cref="CrockAgentRunner"/> (which
    /// must CLASSIFY the same name as a direct credential env var, or
    /// <see cref="CodeyBox.Sandbox.SandboxEnvironmentVariablePolicy.SelectDirectCredentialEnvironment"/>
    /// rejects the whole bundle) so the two cannot drift.
    /// </summary>
    public string ResolveDaemonSocketEnvVar() =>
        string.IsNullOrWhiteSpace(DaemonSocketEnvVar)
            ? DefaultDaemonSocketEnvVar
            : DaemonSocketEnvVar;
}
