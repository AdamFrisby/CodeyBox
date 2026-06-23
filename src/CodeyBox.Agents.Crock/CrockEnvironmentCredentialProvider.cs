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
///   <item><description>Adds a bind-mount that exposes the host daemon Unix
///   socket inside the sandbox at
///   <see cref="CrockSandboxOptions.SandboxDaemonSocketPath"/>. The mount is
///   <em>not</em> read-only (the in-VM CLI must be able to write to the socket
///   to send daemon RPCs).</description></item>
///   <item><description>Sets the configured
///   <see cref="CrockSandboxOptions.DaemonSocketEnvVar"/> env var inside the
///   sandbox so the in-VM crock CLI knows where to connect.</description></item>
/// </list>
///
/// <para><b>Why a dedicated provider instead of an
/// <c>EnvironmentCredentialProvider</c> mapping.</b> The generic provider can
/// only carry env vars; the host-daemon path needs an additional bind-mount,
/// which only <see cref="AgentCredential.Mounts"/> can carry into the sandbox
/// spec.</para>
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
            mounts.Add(new SandboxMount
            {
                SandboxPath = sandboxSocketPath,
                HostPath = opts.HostDaemonSocketPath,
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
}
