using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Deployment;

/// <summary>
/// Drives a web-application deployment: app + its backing services from the
/// recipe, exposes an HTTP(S) URL. Readiness uses the recipe's
/// <see cref="DeploymentRecipe.HealthEndpoint"/> against the primary port.
///
/// <para>The driver starts the app via <see cref="DeploymentRecipe.RunCommand"/>
/// under a managed background supervisor, then polls the configured health endpoint
/// inside the substrate using <c>curl</c> (or a busybox equivalent if the
/// recipe overrides it via <see cref="DeploymentRecipe.Settings"/> key
/// <c>health-probe-command</c>). Polling stays inside the substrate so we
/// do not require host-side network reachability for the readiness check.</para>
/// </summary>
public sealed class WebAppDeploymentDriver : SandboxDeploymentDriverBase
{
    public const string SettingsKeyHealthProbeCommand = "health-probe-command";
    public const string SettingsKeyScheme = "scheme";
    public const string SettingsKeyProbeIntervalSeconds = "probe-interval-seconds";
    private static readonly HttpClient HostProbeHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };
    private readonly Func<Uri, CancellationToken, Task<bool>> _hostHttpProbe;

    public WebAppDeploymentDriver(
        ILogger<WebAppDeploymentDriver>? log = null,
        Func<DateTimeOffset>? clock = null,
        Func<Uri, CancellationToken, Task<bool>>? hostHttpProbe = null)
        : base(log, clock)
    {
        _hostHttpProbe = hostHttpProbe ?? DefaultHostHttpProbeAsync;
    }

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
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        var result = await StartManagedProcessAsync(
            substrate,
            recipe,
            context,
            "web-app start",
            "primary",
            recipe.RunCommand!,
            recipe.Environment,
            ct).ConfigureAwait(false);
        if (!result.Success)
            throw DeploymentExecFailed("web-app start", result);
    }

    protected override async Task ProbeReadyAsync(
        IDeploymentSubstrate substrate,
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
                substrate,
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

    protected override Task ValidateProvisionedSubstrateAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        var request = new DeploymentEndpointRequest
        {
            Kind = DeploymentEndpointKind.Http,
            Scheme = ResolveScheme(recipe),
            Port = recipe.Ports[0],
        };
        if (!CanPublishEndpoint(substrate, request))
            throw new NotSupportedException(
                $"Deployment kind '{Kind}' requires a substrate that can publish HTTP endpoint port {recipe.Ports[0]} before the app is started.");
        return Task.CompletedTask;
    }

    protected override DeploymentEndpoint BuildEndpoint(IDeploymentSubstrate substrate, DeploymentRecipe recipe, DeploymentContext context)
    {
        var port = recipe.Ports[0];
        var scheme = ResolveScheme(recipe);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["substrate.id"] = substrate.Id,
            ["sandbox.local-url"] = $"{scheme}://127.0.0.1:{port}",
            ["http.health-path"] = recipe.HealthEndpoint!,
        };
        AddServiceEndpointMetadata(metadata, substrate, recipe, scheme);
        return PublishEndpoint(substrate, new DeploymentEndpointRequest
        {
            Kind = DeploymentEndpointKind.Http,
            Port = port,
            Metadata = metadata,
            Scheme = scheme,
        });
    }

    protected override async Task VerifyExposedEndpointAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        DeploymentEndpoint endpoint,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Url))
            throw new InvalidOperationException($"Deployment kind '{Kind}' published an HTTP endpoint without a URL.");

        var probeUri = BuildProbeUri(endpoint.Url!, recipe.HealthEndpoint!);
        var interval = ResolveProbeInterval(recipe);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await _hostHttpProbe(probeUri, ct).ConfigureAwait(false))
                return;
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    protected override async Task RunHealthCheckAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        using var healthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        healthCts.CancelAfter(recipe.StartupTimeout);
        try
        {
            await base.RunHealthCheckAsync(substrate, recipe, context, healthCts.Token).ConfigureAwait(false);
            var endpoint = BuildEndpoint(substrate, recipe, context);
            await VerifyExposedEndpointAsync(substrate, recipe, context, endpoint, healthCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Deployment kind '{Kind}' health check did not complete within {recipe.StartupTimeout}.");
        }
    }

    private static string ResolveScheme(DeploymentRecipe recipe)
        => recipe.Settings.TryGetValue(SettingsKeyScheme, out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : "http";

    private static Uri BuildProbeUri(string baseUrl, string healthEndpoint)
    {
        var normalized = healthEndpoint.StartsWith("/", StringComparison.Ordinal)
            ? healthEndpoint
            : "/" + healthEndpoint;
        return new Uri(baseUrl.TrimEnd('/') + normalized, UriKind.Absolute);
    }

    private static async Task<bool> DefaultHostHttpProbeAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var response = await HostProbeHttp
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}
