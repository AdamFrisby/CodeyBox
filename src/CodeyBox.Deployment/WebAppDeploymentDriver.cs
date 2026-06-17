using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Deployment;

/// <summary>
/// Drives a web-application deployment: app + its backing services from the
/// recipe, exposes an HTTP(S) URL. Readiness uses the recipe's
/// <see cref="DeploymentRecipe.HealthEndpoint"/> against the primary port.
///
/// <para>The driver starts the app via <see cref="DeploymentRecipe.RunCommand"/>
/// in a background sandbox exec, then polls the configured health endpoint
/// inside the sandbox using <c>curl</c> (or a busybox equivalent if the
/// recipe overrides it via <see cref="DeploymentRecipe.Settings"/> key
/// <c>health-probe-command</c>). Polling stays inside the substrate so we
/// do not require host-side network reachability for the readiness check.</para>
/// </summary>
public sealed class WebAppDeploymentDriver : SandboxDeploymentDriverBase
{
    public const string SettingsKeyHealthProbeCommand = "health-probe-command";
    public const string SettingsKeyScheme = "scheme";
    public const string SettingsKeyProbeIntervalSeconds = "probe-interval-seconds";

    public WebAppDeploymentDriver(ILogger<WebAppDeploymentDriver>? log = null, Func<DateTimeOffset>? clock = null)
        : base(log, clock) { }

    public override string Kind => DeploymentKinds.WebApp;

    public override void ValidateRecipe(DeploymentRecipe recipe)
    {
        base.ValidateRecipe(recipe);
        if (string.IsNullOrWhiteSpace(recipe.RunCommand))
            throw new ArgumentException("DeploymentRecipe.RunCommand is required for kind 'web-app'.", nameof(recipe));
        if (recipe.Ports.Count == 0)
            throw new ArgumentException("DeploymentRecipe.Ports must contain at least one port for kind 'web-app'.", nameof(recipe));
        if (string.IsNullOrWhiteSpace(recipe.HealthEndpoint))
            throw new ArgumentException("DeploymentRecipe.HealthEndpoint is required for kind 'web-app'.", nameof(recipe));
    }

    protected override async Task StartRuntimeAsync(
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        // The recipe's RunCommand is expected to background the server (or
        // exec with nohup) — sandbox.ExecAsync is one-shot, so a foreground
        // server would block forever. The driver does NOT manage the daemonisation
        // itself; that is recipe-author territory (different stacks have different
        // conventions: systemd-via-spawn, nohup, npm pm2, …).
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", recipe.RunCommand!],
            WorkingDirectory = context.WorkingDirectory,
            ExtraEnvironment = recipe.Environment,
        }, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(
                $"web-app start command exited {result.ExitCode}; stderr tail: {Tail(result.Stderr)}");
    }

    protected override async Task ProbeReadyAsync(
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        var port = recipe.Ports[0];
        var scheme = recipe.Settings.TryGetValue(SettingsKeyScheme, out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : "http";
        var path = recipe.HealthEndpoint!.StartsWith('/') ? recipe.HealthEndpoint! : "/" + recipe.HealthEndpoint;
        var probeUrl = $"{scheme}://127.0.0.1:{port}{path}";

        var probeCommand = recipe.Settings.TryGetValue(SettingsKeyHealthProbeCommand, out var p) && !string.IsNullOrWhiteSpace(p)
            ? p.Replace("{url}", probeUrl, StringComparison.Ordinal)
            : $"curl -fsS -o /dev/null --max-time 5 '{probeUrl}'";

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
        var port = recipe.Ports[0];
        var scheme = recipe.Settings.TryGetValue(SettingsKeyScheme, out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : "http";
        var url = $"{scheme}://127.0.0.1:{port}";
        return new DeploymentEndpoint
        {
            Kind = DeploymentEndpointKind.Http,
            Url = url,
            Host = "127.0.0.1",
            Port = port,
            Path = recipe.HealthEndpoint,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sandbox.id"] = sandbox.Id,
            },
        };
    }
}
