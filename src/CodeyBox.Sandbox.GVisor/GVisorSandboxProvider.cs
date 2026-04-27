using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Sandbox.GVisor;

/// <summary>
/// Sandbox provider backed by gVisor (<c>runsc</c>). User-space kernel
/// intercepts container syscalls instead of forwarding them to the host
/// kernel — a kernel exploit in the agent has to escape gVisor's much
/// smaller, Go-implemented kernel before it can touch the host.
///
/// <para><b>Setup:</b> <c>apt install runsc</c> from the gVisor apt repo
/// (or download the static binary), then add one line to
/// <c>~/.config/containers/containers.conf</c>:</para>
///
/// <code>
/// [engine.runtimes]
/// runsc = ["/usr/bin/runsc"]
/// </code>
///
/// <para>That's the entire setup. No KVM group membership needed (runsc
/// runs in user space), no /etc edits, no CNI surgery — podman handles
/// the network and gVisor handles the kernel surface.</para>
///
/// <para>Implementation: thin wrapper over <see cref="PodmanDriver"/> with
/// <c>RuntimeName = "runsc"</c>. The driver already handles --read-only
/// rootfs, --env-file for secrets, mounts, and resource limits.</para>
///
/// <para><b>Tested status:</b> code-reviewed only. The orchestrator dev
/// box doesn't have runsc installed; runtime testing is on the operator
/// host.</para>
/// </summary>
public sealed class GVisorSandboxProvider : ISandboxProvider
{
    private readonly PodmanDriver _driver;

    public GVisorSandboxProvider(GVisorSandboxOptions opts, ILogger<GVisorSandboxProvider> log)
    {
        _driver = new PodmanDriver(
            new PodmanDriverOptions
            {
                PodmanBinary = opts.PodmanBinary,
                RuntimeName = opts.RuntimeName,
                NetworkName = opts.NetworkName,
            },
            log);
    }

    public string Name => "gvisor";

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        => _driver.CreateAsync(spec, ct);
}

public sealed record GVisorSandboxOptions
{
    public string PodmanBinary { get; init; } = "podman";

    /// <summary>
    /// Name of the OCI runtime registered with podman. Default <c>runsc</c>;
    /// override only if you registered gVisor under a different name.
    /// </summary>
    public string RuntimeName { get; init; } = "runsc";

    /// <summary>
    /// Name of the podman network used when the sandbox needs egress.
    /// Operators should configure host firewall rules on this network to
    /// enforce the agent allowlist.
    /// </summary>
    public string NetworkName { get; init; } = "codeybox-egress";
}
