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
///   <item><description>Adds a <em>read-only</em> bind-mount that exposes the
///   daemon socket's <em>parent directory</em> inside the sandbox at the parent
///   of <see cref="CrockSandboxOptions.SandboxDaemonSocketPath"/>. Connecting to
///   a Unix socket does not require write access to the containing directory, so
///   the mount is read-only — a less-trusted sandbox process cannot create,
///   replace, or delete host directory entries (including the socket
///   itself).</description></item>
///   <item><description>Sets the configured
///   <see cref="CrockSandboxOptions.DaemonSocketEnvVar"/> env var inside the
///   sandbox so the in-VM crock CLI knows where to connect.</description></item>
/// </list>
///
/// <para>The host directory used as the mount source is canonicalised
/// (<c>Path.GetFullPath</c> collapses <c>..</c> segments; the final directory
/// symlink is resolved) BEFORE the forbidden-directory gate and BEFORE it is
/// used as the mount source, so a <c>/dedicated/../etc</c> or symlink shape
/// cannot slip a shared system root past the gate.</para>
///
/// <para><b>Directory mount, not file mount.</b>
/// The Multipass sandbox provider mounts via
/// <c>multipass mount --type=native</c>, which only accepts a <em>directory</em>
/// source; pointing it at a Unix socket node would be rejected. Binding the
/// socket's parent directory works on the local Bubblewrap/Multipass providers
/// whose mounts preserve a live host Unix-domain socket. Providers that stage a
/// directory <em>copy</em> onto another host (e.g. the remote/sprite providers)
/// cannot preserve a live local socket; the host-daemon fallback is only
/// supported on providers that keep a live local bind, and an unsupported
/// provider will simply fail to connect at run time. Dedicate the directory to
/// the daemon socket so no co-located host files are exposed.</para>
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
/// <para><b>Logging.</b> The API key (inside the config JSON) is never written
/// to any log line — only structured state ("config present: yes/no", "daemon
/// socket: configured/unset") is emitted at debug level. The canonicalised
/// parent directory IS handed to the sandbox provider as a mount source, and
/// some providers log mount sources in diagnostics; that directory path is not
/// treated as a secret (the key never appears in it).</para>
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
        if (string.IsNullOrWhiteSpace(opts.HostDaemonSocketPath))
        {
            _log?.LogDebug(
                "Crock credential: config present, no host daemon socket configured " +
                "(in-VM tunnels are not supported; dispatch will fail at the runner's pre-flight check)");
        }
        else if (TryResolveMountParent(opts.HostDaemonSocketPath, out var hostDir, out var reason))
        {
            // Bind the socket's canonical PARENT DIRECTORY (not the socket file)
            // read-only. multipass mount --type=native only accepts a directory
            // source; connecting to the socket needs no write access to the
            // directory. hostDir is already canonicalised + symlink-resolved by
            // TryResolveMountParent, so it is safe to use as the mount source.
            var sandboxSocketPath = string.IsNullOrWhiteSpace(opts.SandboxDaemonSocketPath)
                ? CrockSandboxOptions.DefaultSandboxDaemonSocketPath
                : opts.SandboxDaemonSocketPath;
            var sandboxDir = Path.GetDirectoryName(sandboxSocketPath);
            if (string.IsNullOrWhiteSpace(sandboxDir))
            {
                // Config still ships; the runner pre-flight rejects (the daemon
                // path resolved but the in-sandbox target is malformed).
                _log?.LogWarning(
                    "Crock credential: SandboxDaemonSocketPath has no parent directory; shipping config " +
                    "without a daemon mount so the runner pre-flight rejects with the daemon marker");
            }
            else
            {
                mounts.Add(new SandboxMount
                {
                    SandboxPath = sandboxDir,
                    HostPath = hostDir,
                    ReadOnly = true,
                });

                var daemonEnvVar = opts.ResolveDaemonSocketEnvVar();
                env[daemonEnvVar] = sandboxSocketPath;

                _log?.LogDebug(
                    "Crock credential: config present, host daemon socket configured (env var {EnvVar})",
                    daemonEnvVar);
            }
        }
        else
        {
            // The daemon path is SET but not a safe dedicated mount source
            // (absent parent, non-absolute, or a shared system root after
            // canonicalisation). Ship the config WITHOUT the mount — never a
            // catastrophic host bind — and let the runner's pre-flight classify
            // this as a daemon (Infrastructure) failure rather than a missing
            // credential (AuthError). The reason is structured, not the path.
            _log?.LogWarning(
                "Crock credential: host daemon socket path is not a safe dedicated mount source ({Reason}); " +
                "shipping config without a daemon mount so the runner pre-flight rejects with the daemon marker",
                reason);
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
    /// Resolves the configured host socket path to the canonical, symlink-free
    /// parent directory that is safe to bind-mount, or returns false with a
    /// non-sensitive <paramref name="reason"/> when it is unset, non-absolute,
    /// uncanonicalisable, or (after collapsing <c>..</c> and symlinks) a shared
    /// system directory. The returned <paramref name="canonicalHostDir"/> is
    /// what callers MUST mount — validating one path and mounting another would
    /// reintroduce a TOCTOU/symlink bypass.
    /// </summary>
    internal static bool TryResolveMountParent(
        string? hostSocketPath, out string canonicalHostDir, out string reason)
    {
        canonicalHostDir = string.Empty;
        if (string.IsNullOrWhiteSpace(hostSocketPath))
        {
            reason = "host daemon socket path is not configured";
            return false;
        }

        var trimmed = hostSocketPath.Trim();
        if (!Path.IsPathRooted(trimmed))
        {
            reason = "host daemon socket path must be absolute";
            return false;
        }

        string parent;
        try
        {
            var full = Path.GetFullPath(trimmed);          // collapses ./ and ../
            var dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir))
            {
                reason = "host daemon socket path has no parent directory";
                return false;
            }
            parent = ResolveDirectorySymlink(dir);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException
            or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            reason = "host daemon socket path could not be canonicalised";
            return false;
        }

        if (IsForbiddenParentDirectory(parent))
        {
            reason = "host daemon socket parent resolves to a shared system directory";
            return false;
        }

        canonicalHostDir = parent;
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="dir"/> to its final symlink target when the
    /// directory itself is a symlink (best-effort — the common
    /// <c>/dedicated -&gt; /run</c> shape), then re-canonicalises. A path that
    /// does not exist or is not a link is returned unchanged.
    /// </summary>
    private static string ResolveDirectorySymlink(string dir)
    {
        try
        {
            var target = Directory.ResolveLinkTarget(dir, returnFinalTarget: true);
            if (target is not null)
                return Path.GetFullPath(target.FullName);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return dir;
    }

    /// <summary>
    /// Returns true when <paramref name="hostDir"/> — after canonicalisation —
    /// is one of the shared system directories that a bind-mount would expose
    /// huge swathes of the host to a prompt-injected sandboxed agent (peer
    /// sockets, system D-Bus, journald, every other tenant's home directory,
    /// etc.). Callers that pass a raw operator value get canonicalised here too,
    /// so <c>/run/../etc</c> and a symlinked directory are both caught. HOME and
    /// HOME's parent are computed at call time so the gate tracks the runtime
    /// user, not a baked-in constant.
    /// </summary>
    internal static bool IsForbiddenParentDirectory(string hostDir)
    {
        if (string.IsNullOrWhiteSpace(hostDir))
            return true;

        string normalized;
        try
        {
            normalized = Path.IsPathRooted(hostDir.Trim())
                ? ResolveDirectorySymlink(Path.GetFullPath(hostDir.Trim()))
                : hostDir.Trim();
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException
            or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Uncanonicalisable → fail closed (treat as forbidden).
            return true;
        }
        normalized = TrimTrailingSeparators(normalized);
        if (normalized.Length == 0 || normalized == "." || normalized == "..")
            return true;

        // Shared system directories. Linux is case-sensitive so an ordinal
        // compare against the canonical path is correct.
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
            var normalizedHome = TrimTrailingSeparators(home.Trim());
            if (string.Equals(normalized, normalizedHome, StringComparison.Ordinal))
                return true;
            var homeParent = Path.GetDirectoryName(normalizedHome);
            if (!string.IsNullOrWhiteSpace(homeParent)
                && string.Equals(normalized, TrimTrailingSeparators(homeParent!), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string TrimTrailingSeparators(string path)
    {
        var trimmed = path.Trim();
        while (trimmed.Length > 1
            && (trimmed.EndsWith('/') || trimmed.EndsWith(Path.DirectorySeparatorChar)))
        {
            trimmed = trimmed[..^1];
        }
        return trimmed;
    }
}
