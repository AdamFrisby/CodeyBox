using CodeyBox.Core;

namespace CodeyBox.Deployment;

/// <summary>
/// Deployment substrate adapter backed by the existing sandbox provider. This
/// keeps sandbox provisioning and command DTOs out of the public deployment
/// driver contract while preserving the current Multipass/process substrate.
/// </summary>
public sealed class SandboxDeploymentSubstrateProvider : IDeploymentSubstrateProvider
{
    private readonly ISandboxProvider _inner;

    public SandboxDeploymentSubstrateProvider(ISandboxProvider inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string Name => _inner.Name;

    public async Task<IDeploymentSubstrate> CreateAsync(
        DeploymentSubstrateSpec spec,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var sandbox = await _inner.CreateAsync(ToSandboxSpec(spec), ct).ConfigureAwait(false);
        return new SandboxDeploymentSubstrate(sandbox);
    }

    private static SandboxSpec ToSandboxSpec(DeploymentSubstrateSpec spec) => new()
    {
        ImageReference = spec.ImageReference,
        Purpose = SandboxPurpose.Deployment,
        Mounts = spec.Mounts.Select(ToSandboxMount).ToList(),
        Environment = spec.Environment,
        Network = spec.NetworkProfile is null
            ? SandboxNetworkPolicy.Denied
            : new SandboxNetworkPolicy { ProfileName = spec.NetworkProfile },
        WorkingDirectory = spec.WorkingDirectory,
    };

    private static SandboxMount ToSandboxMount(DeploymentMount mount) => new()
    {
        SandboxPath = mount.SubstratePath,
        HostPath = mount.HostPath,
        ReadOnly = mount.ReadOnly,
        Tmpfs = mount.Tmpfs,
        SizeBytes = mount.SizeBytes,
    };
}

internal sealed class SandboxDeploymentSubstrate : IDeploymentSubstrate
{
    private readonly ISandbox _inner;
    private readonly IDeploymentEndpointPublisher? _endpointPublisher;

    public SandboxDeploymentSubstrate(ISandbox inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _endpointPublisher = inner as IDeploymentEndpointPublisher;
    }

    public string Id => _inner.Id;

    public async Task<DeploymentCommandResult> ExecAsync(
        DeploymentCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = await _inner.ExecAsync(ToSandboxExec(command), ct).ConfigureAwait(false);
        return new DeploymentCommandResult(
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            result.StdoutLimitExceeded,
            result.StderrLimitExceeded,
            result.ExecutionUnavailable);
    }

    public bool CanPublishEndpoint(DeploymentEndpointRequest request)
        => _endpointPublisher?.CanPublishEndpoint(request) == true;

    public DeploymentEndpoint PublishEndpoint(DeploymentEndpointRequest request)
        => _endpointPublisher is not null && _endpointPublisher.CanPublishEndpoint(request)
            ? _endpointPublisher.PublishEndpoint(request)
            : throw new NotSupportedException(
                $"Deployment substrate '{Id}' cannot publish {request.Kind} endpoint on port {request.Port?.ToString() ?? "<none>"}.");

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private static SandboxExec ToSandboxExec(DeploymentCommand command) => new()
    {
        Argv = command.Argv,
        WorkingDirectory = command.WorkingDirectory,
        ExtraEnvironment = command.ExtraEnvironment,
        Stdin = command.Stdin,
        MaxStdoutBytes = command.MaxStdoutBytes,
        MaxStderrBytes = command.MaxStderrBytes,
        KillOnOutputLimit = command.KillOnOutputLimit,
    };
}
