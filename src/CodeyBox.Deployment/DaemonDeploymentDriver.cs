using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Deployment;

/// <summary>
/// Drives a standalone process / daemon deployment: start, verify liveness,
/// expose whatever endpoint the recipe declares. Distinguished from
/// <see cref="WebAppDeploymentDriver"/> only by the readiness check shape —
/// daemons may not host an HTTP probe.
///
/// <para>The default readiness check uses a recipe-declared liveness command,
/// a declared TCP port, or the driver's managed process sidecar. When the
/// selected probe exits 0, the process is considered alive.</para>
///
/// <para>The exposed endpoint is TCP when the recipe declares at least one
/// port (host/port surfaced); otherwise Process (the metadata bag is the
/// only durable handle).</para>
/// </summary>
public sealed class DaemonDeploymentDriver : SandboxDeploymentDriverBase
{
    public const string SettingsKeyLivenessCommand = "liveness-command";
    public const string SettingsKeyProbeIntervalSeconds = "probe-interval-seconds";

    public DaemonDeploymentDriver(ILogger<DaemonDeploymentDriver>? log = null, Func<DateTimeOffset>? clock = null)
        : base(log, clock) { }

    public override string Kind => DeploymentKinds.Daemon;

    public override void ValidateRecipe(DeploymentRecipe recipe)
    {
        base.ValidateRecipe(recipe);
        if (string.IsNullOrWhiteSpace(recipe.RunCommand))
            throw new ArgumentException("DeploymentRecipe.RunCommand is required for kind 'daemon'.", nameof(recipe));
    }

    protected override async Task StartRuntimeAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        var result = await StartManagedProcessAsync(
            substrate,
            recipe,
            context,
            "daemon start",
            "primary",
            recipe.RunCommand!,
            recipe.Environment,
            ct).ConfigureAwait(false);
        if (!result.Success)
            throw DeploymentExecFailed("daemon start", result);
    }

    protected override async Task ProbeReadyAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        string[] probeArgv;
        if (recipe.Settings.TryGetValue(SettingsKeyLivenessCommand, out var explicitCmd)
            && !string.IsNullOrWhiteSpace(explicitCmd))
        {
            probeArgv = ["sh", "-c", explicitCmd];
        }
        else if (recipe.Ports.Count > 0 && !string.IsNullOrWhiteSpace(recipe.HealthEndpoint))
        {
            var port = recipe.Ports[0];
            var path = recipe.HealthEndpoint!.StartsWith('/') ? recipe.HealthEndpoint : "/" + recipe.HealthEndpoint;
            var probeUrl = $"http://127.0.0.1:{port}{path}";
            probeArgv = ["sh", "-c", $"curl -fsS -o /dev/null --max-time 5 {Shell.Quote(probeUrl)}"];
        }
        else
        {
            if (recipe.Ports.Count > 0)
            {
                // /dev/tcp is a BASH builtin — it is not in POSIX, dash (Ubuntu's
                // default /bin/sh) and busybox sh do NOT implement it. Invoke
                // bash explicitly so the redirection actually works on minimal
                // sandbox images; recipes that want a different probe shape can
                // override via SettingsKeyLivenessCommand.
                var port = recipe.Ports[0];
                probeArgv = ["bash", "-c", $"exec 3<>/dev/tcp/127.0.0.1/{port}"];
            }
            else
            {
                probeArgv = ["sh", "-c", BuildManagedProcessLivenessCommand("primary")];
            }
        }

        var interval = ResolveProbeInterval(recipe);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = await RunDeploymentExecAsync(
                substrate,
                recipe,
                context,
                "daemon readiness probe",
                probeArgv,
                recipe.Environment,
                ct).ConfigureAwait(false);
            if (result.Success)
                return;
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    protected override DeploymentEndpoint BuildEndpoint(IDeploymentSubstrate substrate, DeploymentRecipe recipe, DeploymentContext context)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["substrate.id"] = substrate.Id,
        };
        AddServiceEndpointMetadata(metadata, substrate, recipe);
        if (recipe.Ports.Count > 0)
        {
            var port = recipe.Ports[0];
            metadata["sandbox.local-endpoint"] = $"127.0.0.1:{port}";
            var request = new DeploymentEndpointRequest
            {
                Kind = DeploymentEndpointKind.Tcp,
                Port = port,
                Metadata = metadata,
            };
            if (CanPublishEndpoint(substrate, request))
                return PublishEndpoint(substrate, request);

            metadata["endpoint.scope"] = "sandbox-local";
            return new DeploymentEndpoint
            {
                Kind = DeploymentEndpointKind.Tcp,
                Host = "127.0.0.1",
                Port = port,
                Metadata = metadata,
            };
        }
        metadata["endpoint.scope"] = "sandbox-process";
        return new DeploymentEndpoint
        {
            Kind = DeploymentEndpointKind.Process,
            Metadata = metadata,
        };
    }
}
