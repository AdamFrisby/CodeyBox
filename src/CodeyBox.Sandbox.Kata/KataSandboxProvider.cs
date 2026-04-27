using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Sandbox.Kata;

/// <summary>
/// Sandbox provider backed by Kata Containers (Firecracker hypervisor by
/// default). Each sandbox is a microVM with its own kernel — a guest-kernel
/// exploit does not reach the host kernel. This is the production-target
/// provider.
///
/// Implementation goes through podman with <c>--runtime kata</c>. Operator
/// host setup is required (see docs/sandbox-providers.md):
///   - Kata Containers installed; configuration.toml selects Firecracker
///   - podman 4+
///   - sandbox image present (digest-pinned)
///   - codeybox-egress CNI network defined, with host nftables policy
///     that drops all egress except the configured agent allowlist.
///
/// The provider has been written and code-reviewed but cannot be runtime-
/// tested on a host without Kata; treat as alpha.
/// </summary>
public sealed class KataSandboxProvider : ISandboxProvider
{
    private readonly PodmanDriver _driver;

    public KataSandboxProvider(KataSandboxOptions opts, ILogger<KataSandboxProvider> log)
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

    public string Name => "kata";

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        => _driver.CreateAsync(spec, ct);
}

public sealed record KataSandboxOptions
{
    public string PodmanBinary { get; init; } = "podman";
    public string RuntimeName { get; init; } = "kata";
    public string NetworkName { get; init; } = "codeybox-egress";
}
