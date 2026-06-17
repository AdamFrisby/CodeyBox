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
        if (!recipe.Settings.ContainsKey(SettingsKeyLivenessCommand)
            && recipe.Ports.Count == 0)
            throw new ArgumentException(
                "DeploymentRecipe needs either Settings['liveness-command'] or at least one Port for kind 'daemon'.",
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
        string probeCommand;
        if (recipe.Settings.TryGetValue(SettingsKeyLivenessCommand, out var explicitCmd)
            && !string.IsNullOrWhiteSpace(explicitCmd))
        {
            probeCommand = explicitCmd;
        }
        else
        {
            // Port-only recipe: probe the first port with /dev/tcp inside a
            // POSIX shell — portable across busybox / bash sandboxes without
            // requiring nc.
            var port = recipe.Ports[0];
            probeCommand = $"sh -c 'exec 3<>/dev/tcp/127.0.0.1/{port}' 2>/dev/null";
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
                Argv = ["sh", "-c", probeCommand],
                WorkingDirectory = context.WorkingDirectory,
            }, ct).ConfigureAwait(false);
            if (result.Success)
                return;
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
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
