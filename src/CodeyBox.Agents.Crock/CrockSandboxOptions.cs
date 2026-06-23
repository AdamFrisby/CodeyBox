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
/// daemon</c> on the host (with the API key, the tunnel, and any MCP tools).
/// The daemon listens on a Unix socket. The sandbox bind-mounts that socket
/// read-write and the in-VM <c>crock submit</c> connects to the daemon over
/// it — no tunnel, no public URL, no per-item tunnel cost, and the daemon's
/// credential never leaves the host.</para>
///
/// <para><b>Operator setup.</b> Set
/// <see cref="HostDaemonSocketPath"/> to the daemon's socket path
/// (e.g. <c>/run/codeybox/crock-daemon.sock</c>) and ensure the daemon is
/// running before dispatching crock work. The runner refuses to dispatch
/// when the path is unset, so an operator misconfiguration surfaces as a
/// clear failure rather than as a hung batch with no callback path.</para>
/// </summary>
public sealed class CrockSandboxOptions
{
    /// <summary>
    /// Host filesystem path to the <c>crock daemon</c> Unix socket. When set,
    /// the runner bind-mounts the socket into the sandbox at
    /// <see cref="SandboxDaemonSocketPath"/> and points the in-VM CLI at it
    /// via the <see cref="DaemonSocketEnvVar"/> env var. When null/empty the
    /// runner refuses to dispatch (in-VM tunnels are not supported — see the
    /// class-level docs).
    ///
    /// <para>The path is never logged. The orchestrator's audit log records
    /// <em>that</em> a daemon socket is configured, not where.</para>
    /// </summary>
    public string? HostDaemonSocketPath { get; set; }

    /// <summary>
    /// In-sandbox path where <see cref="HostDaemonSocketPath"/> is bind-mounted.
    /// Default <c>/run/codeybox/crock-daemon.sock</c>. The runner passes this
    /// path to the in-VM CLI via the <see cref="DaemonSocketEnvVar"/> env var
    /// so the CLI knows where to connect inside the sandbox.
    /// </summary>
    public string SandboxDaemonSocketPath { get; set; } = "/run/codeybox/crock-daemon.sock";

    /// <summary>
    /// Env var the in-VM crock CLI reads to find the daemon socket. Mirrors
    /// CrockCode's documented <c>CROCK_DAEMON_SOCKET</c> contract; kept as
    /// config so an operator running a custom crock build with a different
    /// env-var name can rewire without a code change.
    /// </summary>
    public string DaemonSocketEnvVar { get; set; } = "CROCK_DAEMON_SOCKET";
}
