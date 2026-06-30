using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Deployment;

/// <summary>
/// Drives a web-application deployment: app + its backing services from the
/// recipe, exposes an HTTP(S) URL. Readiness uses the recipe's
/// <see cref="DeploymentRecipe.HealthEndpoint"/> against the primary port.
///
/// <para>The driver starts the app via <see cref="DeploymentRecipe.RunCommand"/>
/// in an attached sandbox exec that must return quickly, then polls the configured health endpoint
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
        var result = await RunDeploymentExecAsync(
            sandbox,
            recipe,
            context,
            "web-app start",
            ["sh", "-c", recipe.RunCommand!],
            recipe.Environment,
            ct).ConfigureAwait(false);
        if (!result.Success)
            throw DeploymentExecFailed("web-app start", result);
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

        // Shell-quote the probe URL so a recipe with a single quote (or other
        // shell metacharacter) in the HealthEndpoint cannot break out of the
        // curl argument. The override template owns its own quoting; the
        // {url} substitution feeds a single-quoted form into operator
        // templates that include the placeholder verbatim.
        var quotedProbeUrl = Shell.Quote(probeUrl);
        var probeCommand = recipe.Settings.TryGetValue(SettingsKeyHealthProbeCommand, out var p) && !string.IsNullOrWhiteSpace(p)
            ? p.Replace("{url}", quotedProbeUrl, StringComparison.Ordinal)
            : $"curl -fsS -o /dev/null --max-time 5 {quotedProbeUrl}";

        var interval = ResolveProbeInterval(recipe);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = await RunDeploymentExecAsync(
                sandbox,
                recipe,
                context,
                "web-app readiness probe",
                ["sh", "-c", probeCommand],
                recipe.Environment,
                ct).ConfigureAwait(false);
            if (result.Success)
                return;
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    protected override DeploymentEndpoint BuildEndpoint(ISandbox sandbox, DeploymentRecipe recipe, DeploymentContext context)
    {
        var port = recipe.Ports[0];
        var scheme = recipe.Settings.TryGetValue(SettingsKeyScheme, out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : "http";
        var host = ResolveHostAddress(sandbox);
        if (host is null)
            throw new InvalidOperationException(
                $"Deployment kind '{Kind}' requires a routable sandbox host address to expose HTTP port {port}.");
        var url = $"{scheme}://{host}:{port}";
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sandbox.id"] = sandbox.Id,
            ["endpoint.scope"] = "host-routable",
            ["sandbox.local-url"] = $"{scheme}://127.0.0.1:{port}",
            ["http.health-path"] = recipe.HealthEndpoint!,
        };
        AddServiceEndpointMetadata(metadata, sandbox, recipe, scheme);
        return new DeploymentEndpoint
        {
            Kind = DeploymentEndpointKind.Http,
            Url = url,
            Host = host,
            Port = port,
            // DeploymentEndpoint.Path is documented as a file-path slot for
            // artifact-bearing kinds. For HTTP deployments the health-probe
            // URL path is surfaced via Metadata instead so the file-path
            // semantic stays intact.
            Metadata = metadata,
        };
    }
}
