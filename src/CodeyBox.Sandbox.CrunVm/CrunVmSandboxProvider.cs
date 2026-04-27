using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Sandbox.CrunVm;

/// <summary>
/// Sandbox provider backed by crun-vm (libkrun). Same kernel-isolation
/// property as Kata (separate guest kernel) with a lighter runtime. Goes
/// through podman with <c>--runtime crun-vm</c>.
///
/// Code-reviewed, runtime-untested on the development host. See
/// docs/sandbox-providers.md for host setup.
/// </summary>
public sealed class CrunVmSandboxProvider : ISandboxProvider
{
    private readonly PodmanDriver _driver;

    public CrunVmSandboxProvider(CrunVmSandboxOptions opts, ILogger<CrunVmSandboxProvider> log)
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

    public string Name => "crun-vm";

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        => _driver.CreateAsync(spec, ct);
}

public sealed record CrunVmSandboxOptions
{
    public string PodmanBinary { get; init; } = "podman";
    public string RuntimeName { get; init; } = "crun-vm";
    public string NetworkName { get; init; } = "codeybox-egress";
}
