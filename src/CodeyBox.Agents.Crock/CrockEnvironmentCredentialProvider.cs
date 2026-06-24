using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Crock;

/// <summary>
/// Credential provider for the <c>crock</c> agent. Reads the CrockCode config
/// JSON from <see cref="HostConfigEnvVar"/> on the host and ships it to the
/// sandbox via <see cref="CrockAgentRunner.ConfigEnvVar"/>. Additionally, when
/// <see cref="CrockSandboxOptions.HostDaemonSocketPath"/> is configured, the
/// provider:
///
/// <list type="number">
///   <item><description>Adds a bind-mount that exposes the daemon socket's
///   <em>parent directory</em> inside the sandbox at the parent of
///   <see cref="CrockSandboxOptions.SandboxDaemonSocketPath"/>. The mount is
///   <em>not</em> read-only (the in-VM CLI must be able to write to the socket
///   to send daemon RPCs).</description></item>
///   <item><description>Sets the configured
///   <see cref="CrockSandboxOptions.DaemonSocketEnvVar"/> env var inside the
///   sandbox so the in-VM crock CLI knows where to connect.</description></item>
/// </list>
///
/// <para><b>Directory mount, not file mount — Multipass compatibility.</b>
/// The Multipass sandbox provider mounts via
/// <c>multipass mount --type=native</c>, which only accepts a <em>directory</em>
/// source (virtiofs / 9p passthrough); pointing it at a Unix socket node would
/// be rejected by the provider. Binding the socket's parent directory works
/// uniformly across every shipped sandbox provider — Bubblewrap's <c>--bind</c>
/// accepts both files and directories so the directory binding is equally
/// correct there, and the Multipass virtiofs/9p passthrough faithfully
/// exposes any socket node inside the mounted directory so the in-VM
/// <c>connect(2)</c> reaches the host daemon. Operators sharing one
/// directory for multiple sockets get all of them at the cost of one mount;
/// dedicate the directory to the daemon socket if that surface matters.</para>
///
/// <para><b>Why a dedicated provider instead of an
/// <c>EnvironmentCredentialProvider</c> mapping.</b> The generic provider can
/// only carry env vars; the host-daemon path needs an additional bind-mount,
/// which only <see cref="AgentCredential.Mounts"/> can carry into the sandbox
/// spec.</para>
///
/// <para><b>Daemon authn/authz is the operator's responsibility.</b> The
/// bind-mount exposes the daemon's full RPC surface to every process inside
/// the sandbox — including any prompt-injected agent acting on attacker-
/// controlled prompt content (the LLM threat model is "prompt injection
/// happens"). The orchestrator does not enforce socket-level UID gating or
/// capability-scope tokens; the daemon implementation MUST authenticate the
/// caller (peer-cred check on <c>SO_PEERCRED</c>, capability tokens, scope-
/// limited RPC surface) before honouring any request. See
/// <see cref="CrockSandboxOptions"/> for the documented expectation.</para>
///
/// <para><b>Never logs.</b> Neither the API key (inside the config JSON) nor
/// the host daemon socket path is written to any log line — only structured
/// state ("config present: yes/no", "daemon socket: configured/unset") is
/// emitted at debug level.</para>
/// </summary>
public sealed class CrockEnvironmentCredentialProvider : ICredentialProvider
{
    /// <summary>
    /// Host env var carrying the raw CrockCode config JSON. Documented in
    /// <c>docs/agents.md</c>; the host populates this from
    /// <c>~/.crockcode/config.json</c> (or wherever the operator's secret
    /// management stages it), and the runner materialises it back inside the
    /// VM at <c>~/.crockcode/config.json</c> with mode 0600.
    /// </summary>
    public const string HostConfigEnvVar = "CODEYBOX_CROCK_CONFIG_JSON";

    private readonly CrockSandboxOptionsAccessor _sandboxOptions;
    private readonly ILogger<CrockEnvironmentCredentialProvider>? _log;

    public CrockEnvironmentCredentialProvider(
        CrockSandboxOptionsAccessor sandboxOptions,
        ILogger<CrockEnvironmentCredentialProvider>? log = null)
    {
        _sandboxOptions = sandboxOptions;
        _log = log;
    }

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (agent != AgentKind.Crock)
            return Task.FromResult<AgentCredential?>(null);

        var configJson = Environment.GetEnvironmentVariable(HostConfigEnvVar);
        if (string.IsNullOrEmpty(configJson))
        {
            _log?.LogDebug("Crock credential: {EnvVar} not set on host", HostConfigEnvVar);
            return Task.FromResult<AgentCredential?>(null);
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CrockAgentRunner.ConfigEnvVar] = configJson,
        };
        var mounts = new List<SandboxMount>();

        var opts = _sandboxOptions();
        if (!string.IsNullOrWhiteSpace(opts.HostDaemonSocketPath))
        {
            var sandboxSocketPath = string.IsNullOrWhiteSpace(opts.SandboxDaemonSocketPath)
                ? "/run/codeybox/crock-daemon.sock"
                : opts.SandboxDaemonSocketPath;

            // Bind the socket's PARENT DIRECTORY (not the socket file).
            // multipass mount --type=native only accepts a directory source
            // (virtiofs / 9p passthrough); pointing it at a Unix socket node is
            // rejected by the provider. The directory shape is universally
            // compatible — bubblewrap's --bind also accepts directories — and
            // the virtiofs/9p passthrough faithfully exposes any socket node
            // inside the mounted directory so the in-VM connect() reaches the
            // host daemon.
            var hostDir = Path.GetDirectoryName(opts.HostDaemonSocketPath);
            var sandboxDir = Path.GetDirectoryName(sandboxSocketPath);
            if (string.IsNullOrWhiteSpace(hostDir) || string.IsNullOrWhiteSpace(sandboxDir))
            {
                _log?.LogDebug(
                    "Crock credential: HostDaemonSocketPath has no parent directory; refusing to ship " +
                    "credential so the runner's pre-flight check fires with the missing-daemon marker");
                return Task.FromResult<AgentCredential?>(null);
            }

            if (IsForbiddenParentDirectory(hostDir))
            {
                // Catastrophe gate: an operator typo such as
                // HostDaemonSocketPath="/foo.sock" resolves the parent to "/"
                // and would bind-mount the entire host filesystem read-write
                // into the sandbox — defeating sandbox isolation in one
                // character. The shared-system-root list also blocks /run,
                // /var/run, /tmp, /var/tmp, /etc, $HOME and the home
                // directory's parent ("/home") because every one of those
                // exposes co-located secrets (peer sockets, system D-Bus,
                // every other tenant's home dir, host shell history, etc.)
                // to a prompt-injected agent. Operators MUST dedicate a
                // subdirectory to the daemon socket — see
                // CrockSandboxOptions docs.
                _log?.LogWarning(
                    "Crock credential: refusing to bind-mount catastrophic parent directory (configured " +
                    "HostDaemonSocketPath resolves to a system root). Operator must dedicate a subdirectory " +
                    "to the daemon socket. Dispatch will fail at the runner's pre-flight check.");
                return Task.FromResult<AgentCredential?>(null);
            }

            mounts.Add(new SandboxMount
            {
                SandboxPath = sandboxDir,
                HostPath = hostDir,
                ReadOnly = false,
            });

            var daemonEnvVar = string.IsNullOrWhiteSpace(opts.DaemonSocketEnvVar)
                ? "CROCK_DAEMON_SOCKET"
                : opts.DaemonSocketEnvVar;
            env[daemonEnvVar] = sandboxSocketPath;

            _log?.LogDebug(
                "Crock credential: config present, host daemon socket configured (env var {EnvVar})",
                daemonEnvVar);
        }
        else
        {
            _log?.LogDebug(
                "Crock credential: config present, no host daemon socket configured " +
                "(in-VM tunnels are not supported; dispatch will fail at the runner's pre-flight check)");
        }

        return Task.FromResult<AgentCredential?>(new AgentCredential(
            AgentKind.Crock,
            env,
            new Dictionary<string, string>())
        {
            Mounts = mounts,
        });
    }

    /// <summary>
    /// Returns true when <paramref name="hostDir"/> is one of the shared
    /// system directories that a bind-mount would expose huge swathes of
    /// the host to a prompt-injected sandboxed agent (peer sockets, system
    /// D-Bus, journald, every other tenant's home directory, etc.). The
    /// match is canonical-path based; comparisons normalise trailing
    /// slashes and case (Linux is case-sensitive but the operator may
    /// have hand-typed mixed casing). HOME and HOME's parent are computed
    /// at call time so the gate tracks the runtime user, not a baked-in
    /// constant.
    /// </summary>
    internal static bool IsForbiddenParentDirectory(string hostDir)
    {
        if (string.IsNullOrWhiteSpace(hostDir))
            return true;

        var normalized = NormalizePath(hostDir);
        if (normalized.Length == 0)
            return true;

        // Filesystem root (/, C:\, etc.) — Path.GetDirectoryName for
        // anything at the root returns this. The single most dangerous
        // shape because it bind-mounts /etc/shadow, /root/.ssh, every
        // tenant home dir and every host socket into the VM at once.
        if (normalized == "/" || normalized == "." || normalized == "..")
            return true;

        // Hard-coded shared system directories. Operator-friendly: the
        // log line names the gate so an operator can read the failure
        // and pick a dedicated subdirectory instead.
        var forbidden = new[]
        {
            "/", "/etc", "/run", "/var", "/var/run", "/tmp", "/var/tmp",
            "/usr", "/usr/bin", "/usr/lib", "/usr/local", "/usr/local/bin",
            "/bin", "/sbin", "/lib", "/lib64", "/root", "/home", "/dev",
            "/proc", "/sys", "/boot", "/opt", "/srv", "/mnt", "/media",
        };
        foreach (var f in forbidden)
        {
            if (string.Equals(normalized, f, StringComparison.Ordinal))
                return true;
        }

        // The current operator's HOME directly (would expose
        // .ssh / .aws / .config / shell history) and HOME's parent
        // (would expose every co-tenant home directory).
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var normalizedHome = NormalizePath(home);
            if (string.Equals(normalized, normalizedHome, StringComparison.Ordinal))
                return true;
            var homeParent = Path.GetDirectoryName(normalizedHome);
            if (!string.IsNullOrWhiteSpace(homeParent)
                && string.Equals(normalized, NormalizePath(homeParent!), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 0) return trimmed;
        // Strip trailing slashes except for the bare root.
        while (trimmed.Length > 1
            && (trimmed.EndsWith('/') || trimmed.EndsWith(Path.DirectorySeparatorChar)))
        {
            trimmed = trimmed[..^1];
        }
        return trimmed;
    }
}
