using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Deployment;

/// <summary>
/// Drives a standalone process / daemon deployment: start, verify liveness,
/// expose whatever endpoint the recipe declares. Distinguished from
/// <see cref="WebAppDeploymentDriver"/> only by the readiness check shape —
/// daemons may not host an HTTP probe.
///
/// <para>The default readiness check tails a recipe-declared liveness
/// command (<see cref="SettingsKeyLivenessCommand"/>) such as
/// <c>pgrep -f my-daemon</c>. When the command exits 0, the process is
/// considered alive. Recipes that listen on a TCP socket can override with
/// a netcat-style probe; nothing is hardcoded to a single style.</para>
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
        // Validate with the same predicate the probe uses (IsNullOrWhiteSpace
        // rather than ContainsKey) so a recipe with Settings['liveness-command']=''
        // is caught here instead of throwing IndexOutOfRangeException from
        // recipe.Ports[0] inside the probe.
        var hasLivenessCommand =
            recipe.Settings.TryGetValue(SettingsKeyLivenessCommand, out var explicitCmd)
            && !string.IsNullOrWhiteSpace(explicitCmd);
        if (!hasLivenessCommand && recipe.Ports.Count == 0)
            throw new ArgumentException(
                "DeploymentRecipe needs either a non-empty Settings['liveness-command'] or at least one Port for kind 'daemon'.",
                nameof(recipe));
    }

    protected override async Task StartRuntimeAsync(
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", recipe.RunCommand!],
            WorkingDirectory = context.WorkingDirectory,
            ExtraEnvironment = recipe.Environment,
        }, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(
                $"daemon start command exited {result.ExitCode}; stderr tail: {Tail(result.Stderr)}");
    }

    protected override async Task ProbeReadyAsync(
        ISandbox sandbox,
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
        else
        {
            // /dev/tcp is a BASH builtin — it is not in POSIX, dash (Ubuntu's
            // default /bin/sh) and busybox sh do NOT implement it. Invoke
            // bash explicitly so the redirection actually works on minimal
            // sandbox images; recipes that want a different probe shape can
            // override via SettingsKeyLivenessCommand.
            var port = recipe.Ports[0];
            probeArgv = ["bash", "-c", $"exec 3<>/dev/tcp/127.0.0.1/{port}"];
        }

        var interval = TimeSpan.FromSeconds(1);
        if (recipe.Settings.TryGetValue(SettingsKeyProbeIntervalSeconds, out var iv)
            && double.TryParse(iv, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            interval = TimeSpan.FromSeconds(Math.Min(seconds, 60));
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = probeArgv,
                WorkingDirectory = context.WorkingDirectory,
                ExtraEnvironment = recipe.Environment,
            }, ct).ConfigureAwait(false);
            if (result.Success)
                return;
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    protected override DeploymentEndpoint BuildEndpoint(ISandbox sandbox, DeploymentRecipe recipe, DeploymentContext context)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sandbox.id"] = sandbox.Id,
        };
        if (recipe.Ports.Count > 0)
        {
            var port = recipe.Ports[0];
            return new DeploymentEndpoint
            {
                Kind = DeploymentEndpointKind.Tcp,
                Host = "127.0.0.1",
                Port = port,
                Metadata = metadata,
            };
        }
        return new DeploymentEndpoint
        {
            Kind = DeploymentEndpointKind.Process,
            Metadata = metadata,
        };
    }
}
