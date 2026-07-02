using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Deployment;

/// <summary>
/// Common scaffold for drivers that provision a single sandbox VM as the
/// substrate. Centralises the Provision → Build → Run → HealthCheck →
/// Expose pipeline so each kind only fills in its driver-specific
/// readiness probe and endpoint construction.
///
/// <para>The base class enforces the "teardown always runs on failure"
/// invariant: any exception between Provision and Expose disposes the
/// sandbox before rethrowing. Cancellation propagates the same way.</para>
/// </summary>
public abstract class SandboxDeploymentDriverBase : IDeploymentDriver
{
    /// <summary>
    /// Length of the hex-encoded deployment id derived from <c>Guid.NewGuid().ToString("N")</c>.
    /// 16 hex chars = 64 bits of identity — collision probability under any realistic
    /// deployment count is vanishingly small, while staying short enough to read in log
    /// lines and dashboards.
    /// </summary>
    private const int DeploymentIdHexChars = 16;
    private const int DeploymentExecOutputCaptureBytes = 256 * 1024;
    private const int ErrorOutputTailChars = 2048;
    private const string Localhost = "127.0.0.1";
    private const string ServiceImagePlaceholder = "{image}";

    protected ILogger Log { get; }
    protected Func<DateTimeOffset> Clock { get; }

    protected SandboxDeploymentDriverBase(ILogger? log = null, Func<DateTimeOffset>? clock = null)
    {
        Log = log ?? NullLogger.Instance;
        Clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public abstract string Kind { get; }

    public virtual void ValidateRecipe(DeploymentRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (string.IsNullOrWhiteSpace(recipe.ImageReference))
            throw new ArgumentException($"DeploymentRecipe.ImageReference is required for kind '{Kind}'.", nameof(recipe));
        if (recipe.StartupTimeout <= TimeSpan.Zero)
            throw new ArgumentException($"DeploymentRecipe.StartupTimeout must be positive for kind '{Kind}'.", nameof(recipe));
        if (recipe.MaxLifetime <= TimeSpan.Zero)
            throw new ArgumentException($"DeploymentRecipe.MaxLifetime must be positive for kind '{Kind}'.", nameof(recipe));
        foreach (var port in recipe.Ports)
        {
            if (port is < 1 or > 65535)
                throw new ArgumentException($"DeploymentRecipe.Ports contains invalid port {port}; must be 1..65535.", nameof(recipe));
        }

        foreach (var service in recipe.Services)
        {
            if (service is null)
                throw new ArgumentException("DeploymentRecipe.Services cannot contain null entries.", nameof(recipe));
            if (string.IsNullOrWhiteSpace(service.Name))
                throw new ArgumentException("DeploymentRecipe.Services[].Name is required.", nameof(recipe));
            if (string.IsNullOrWhiteSpace(service.ImageReference))
                throw new ArgumentException($"DeploymentRecipe.Services['{service.Name}'].ImageReference is required.", nameof(recipe));
            if (string.IsNullOrWhiteSpace(service.RunCommand))
                throw new ArgumentException($"DeploymentRecipe.Services['{service.Name}'].RunCommand is required.", nameof(recipe));
            if (!string.Equals(service.ImageReference, recipe.ImageReference, StringComparison.Ordinal)
                && !service.RunCommand!.Contains(ServiceImagePlaceholder, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"DeploymentRecipe.Services['{service.Name}'].RunCommand must include {ServiceImagePlaceholder} when its ImageReference differs from the primary deployment image.",
                    nameof(recipe));
            }
            if (service.Ports.Count == 0)
                throw new ArgumentException($"DeploymentRecipe.Services['{service.Name}'].Ports must contain at least one port.", nameof(recipe));
            foreach (var port in service.Ports)
            {
                if (port is < 1 or > 65535)
                    throw new ArgumentException($"DeploymentRecipe.Services['{service.Name}'].Ports contains invalid port {port}; must be 1..65535.", nameof(recipe));
            }
        }
    }

    public async Task<IDeploymentHandle> DeployAsync(
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(context);

        ValidateRecipe(recipe);

        var spec = BuildSubstrateSpec(recipe, context);
        IDeploymentSubstrate? substrate = null;
        try
        {
            substrate = await context.SubstrateProvider.CreateAsync(spec, ct).ConfigureAwait(false);
            await ValidateProvisionedSubstrateAsync(substrate, recipe, context, ct).ConfigureAwait(false);

            await RunBuildAsync(substrate, recipe, context, ct).ConfigureAwait(false);

            using (var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                startupCts.CancelAfter(recipe.StartupTimeout);
                try
                {
                    await StartServicesAsync(substrate, recipe, context, startupCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Deployment kind '{Kind}' backing services did not start within {recipe.StartupTimeout}. " +
                        "Tearing down substrate.");
                }
                try
                {
                    await ProbeServicesReadyAsync(substrate, recipe, context, startupCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Deployment kind '{Kind}' backing services did not become ready within {recipe.StartupTimeout}. " +
                        "Tearing down substrate.");
                }
                try
                {
                    await StartRuntimeAsync(substrate, recipe, context, startupCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Deployment kind '{Kind}' runtime did not start within {recipe.StartupTimeout}. " +
                        "Tearing down substrate.");
                }
                try
                {
                    await ProbeReadyAsync(substrate, recipe, context, startupCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Deployment kind '{Kind}' did not become ready within {recipe.StartupTimeout}. " +
                        "Tearing down substrate.");
                }
            }

            var endpoint = BuildEndpoint(substrate, recipe, context);
            using (var exposeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                exposeCts.CancelAfter(recipe.StartupTimeout);
                try
                {
                    await VerifyExposedEndpointAsync(substrate, recipe, context, endpoint, exposeCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Deployment kind '{Kind}' exposed endpoint did not become reachable within {recipe.StartupTimeout}. " +
                        "Tearing down substrate.");
                }
            }

            var id = Guid.NewGuid().ToString("N")[..DeploymentIdHexChars];
            // Capture the substrate reference in a separate local so nulling the
            // outer one (to skip the catch's cleanup-on-failure path) does not
            // also nil out the closure used by the runtime health check.
            var owned = substrate;
            var handle = new SandboxDeploymentHandle(
                id,
                Kind,
                owned,
                endpoint,
                runtimeCt => RunHealthCheckAsync(owned, recipe, context, runtimeCt));
            substrate = null; // ownership transferred
            return handle;
        }
        catch
        {
            if (substrate is not null)
            {
                try { await substrate.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    if (substrate is IDeploymentActiveLease lease)
                    {
                        try { lease.ReleaseActiveTracking(); }
                        catch (Exception leaseEx)
                        {
                            Log.LogWarning(leaseEx, "Driver {Kind} failed to release active tracking after teardown failure", Kind);
                        }
                    }
                    Log.LogWarning(ex, "Driver {Kind} teardown after failed deploy threw", Kind);
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Builds the substrate spec from the recipe. Default merges the recipe's
    /// environment into a spec keyed off ImageReference and NetworkProfile.
    /// When no network profile is configured, the sandbox adapter maps that to
    /// the provider's denied-network policy.
    /// </summary>
    protected virtual DeploymentSubstrateSpec BuildSubstrateSpec(DeploymentRecipe recipe, DeploymentContext context) => new()
    {
        ImageReference = recipe.ImageReference,
        Mounts = context.Mounts,
        Environment = recipe.Environment,
        NetworkProfile = recipe.NetworkProfile,
        WorkingDirectory = context.WorkingDirectory,
    };

    protected virtual Task ValidateProvisionedSubstrateAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct) => Task.CompletedTask;

    protected virtual async Task StartServicesAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        foreach (var service in recipe.Services)
        {
            var command = service.RunCommand!.Replace(
                ServiceImagePlaceholder,
                Shell.Quote(service.ImageReference),
                StringComparison.Ordinal);
            var result = await StartManagedProcessAsync(
                substrate,
                recipe,
                context,
                $"service '{service.Name}' start",
                $"service-{service.Name}",
                command,
                MergeEnvironment(recipe.Environment, service.Environment),
                ct).ConfigureAwait(false);
            if (!result.Success)
                throw DeploymentExecFailed($"service '{service.Name}' start", result);
        }
    }

    protected virtual async Task ProbeServicesReadyAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        foreach (var service in recipe.Services)
            await ProbeServiceReadyAsync(substrate, recipe, context, service, ct).ConfigureAwait(false);
    }

    private async Task ProbeServiceReadyAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        DeploymentService service,
        CancellationToken ct)
    {
        var port = service.Ports[0];
        string[] probeArgv;
        if (!string.IsNullOrWhiteSpace(service.HealthEndpoint))
        {
            var path = service.HealthEndpoint!.StartsWith('/') ? service.HealthEndpoint : "/" + service.HealthEndpoint;
            var probeUrl = $"http://{Localhost}:{port}{path}";
            probeArgv = ["sh", "-c", $"curl -fsS -o /dev/null --max-time 5 {Shell.Quote(probeUrl)}"];
        }
        else
        {
            probeArgv = ["bash", "-c", $"exec 3<>/dev/tcp/{Localhost}/{port}"];
        }

        var interval = ResolveProbeInterval(recipe);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = await RunDeploymentExecAsync(
                substrate,
                recipe,
                context,
                $"service '{service.Name}' readiness probe",
                probeArgv,
                MergeEnvironment(recipe.Environment, service.Environment),
                ct).ConfigureAwait(false);
            if (result.Success)
                return;
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    protected virtual async Task RunBuildAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipe.BuildCommand))
            return;
        var result = await RunDeploymentExecAsync(
            substrate,
            recipe,
            context,
            "build",
            ["sh", "-c", recipe.BuildCommand],
            recipe.Environment,
            ct).ConfigureAwait(false);
        if (!result.Success)
            throw DeploymentExecFailed("build", result);
    }

    /// <summary>
    /// Driver-specific hook to launch the deployed software. Default no-op
    /// for kinds that do not start a long-running process (CLI, Library).
    /// </summary>
    protected virtual Task StartRuntimeAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>Driver-specific readiness probe. Throws on persistent failure; returns on success.</summary>
    protected abstract Task ProbeReadyAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct);

    /// <summary>Builds the public-facing endpoint surfaced on the handle.</summary>
    protected abstract DeploymentEndpoint BuildEndpoint(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context);

    /// <summary>
    /// Optional post-expose verification hook. Driver implementations whose
    /// endpoint must be reachable from the host should probe the published
    /// endpoint here before the handle is returned to callers.
    /// </summary>
    protected virtual Task VerifyExposedEndpointAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        DeploymentEndpoint endpoint,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Hook for the runtime <see cref="IDeploymentHandle.HealthCheckAsync"/>.
    /// Default re-runs backing-service probes and the primary readiness probe
    /// under the recipe's startup timeout, unless the caller cancels first.
    /// </summary>
    protected virtual async Task RunHealthCheckAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        using var healthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        healthCts.CancelAfter(recipe.StartupTimeout);
        try
        {
            await ProbeServicesReadyAsync(substrate, recipe, context, healthCts.Token).ConfigureAwait(false);
            await ProbeReadyAsync(substrate, recipe, context, healthCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Deployment kind '{Kind}' health check did not complete within {recipe.StartupTimeout}.");
        }
    }

    protected async Task<DeploymentCommandResult> RunDeploymentExecAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        string stage,
        IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? recipe.StartupTimeout;
        using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        execCts.CancelAfter(effectiveTimeout);
        try
        {
            var result = await substrate.ExecAsync(new DeploymentCommand
            {
                Argv = argv,
                WorkingDirectory = context.WorkingDirectory,
                ExtraEnvironment = environment,
                MaxStdoutBytes = DeploymentExecOutputCaptureBytes,
                MaxStderrBytes = DeploymentExecOutputCaptureBytes,
                KillOnOutputLimit = true,
            }, execCts.Token).ConfigureAwait(false);
            if (result.OutputLimitExceeded)
            {
                throw new InvalidOperationException(
                    $"Deployment kind '{Kind}' {stage} exceeded the {DeploymentExecOutputCaptureBytes} byte output capture limit; " +
                    $"stdout tail: {Tail(result.Stdout)}; stderr tail: {Tail(result.Stderr)}");
            }
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Deployment kind '{Kind}' {stage} command did not finish within {effectiveTimeout}.");
        }
    }

    protected async Task<DeploymentCommandResult> StartManagedProcessAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        string stage,
        string processName,
        string command,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct)
    {
        var supervisor = """
            set -eu
            codeybox_name=$1
            codeybox_command=$2
            codeybox_dir="${CODEYBOX_DEPLOYMENT_RUNTIME_DIR:-/tmp/codeybox-deployment}"
            mkdir -p "$codeybox_dir"
            codeybox_base="$codeybox_dir/$codeybox_name"
            rm -f "$codeybox_base.pid" "$codeybox_base.exit" "$codeybox_base.stdout" "$codeybox_base.stderr"
            (
                set +e
                sh -c "$codeybox_command"
                codeybox_rc=$?
                printf '%s\n' "$codeybox_rc" > "$codeybox_base.exit"
            ) > "$codeybox_base.stdout" 2> "$codeybox_base.stderr" < /dev/null &
            codeybox_pid=$!
            printf '%s\n' "$codeybox_pid" > "$codeybox_base.pid"
            exit 0
            """;

        return await RunDeploymentExecAsync(
            substrate,
            recipe,
            context,
            stage,
            ["sh", "-c", supervisor, "codeybox-deployment-start", SanitizeProcessName(processName), command],
            environment,
            ct).ConfigureAwait(false);
    }

    protected static string BuildManagedProcessLivenessCommand(string processName)
    {
        var quotedName = Shell.Quote(SanitizeProcessName(processName));
        return $$"""
            codeybox_dir="${CODEYBOX_DEPLOYMENT_RUNTIME_DIR:-/tmp/codeybox-deployment}"
            codeybox_base="$codeybox_dir"/{{quotedName}}
            test -r "$codeybox_base.pid" || exit 1
            codeybox_pid=$(cat -- "$codeybox_base.pid") || exit 1
            case "$codeybox_pid" in ''|*[!0-9]*|0) exit 1 ;; esac
            if test -f "$codeybox_base.exit"; then
              exit 1
            fi
            kill -0 "$codeybox_pid" 2>/dev/null
            """;
    }

    protected InvalidOperationException DeploymentExecFailed(string stage, DeploymentCommandResult result)
        => new(
            $"Deployment kind '{Kind}' {stage} command exited {result.ExitCode}; " +
            $"stdout tail: {Tail(result.Stdout)}; stderr tail: {Tail(result.Stderr)}");

    protected static IReadOnlyDictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string> primary,
        IReadOnlyDictionary<string, string> overlay)
    {
        if (overlay.Count == 0)
            return primary;
        var merged = new Dictionary<string, string>(primary, StringComparer.Ordinal);
        foreach (var (key, value) in overlay)
            merged[key] = value;
        return merged;
    }

    protected static TimeSpan ResolveProbeInterval(DeploymentRecipe recipe)
    {
        if (recipe.Settings.TryGetValue("probe-interval-seconds", out var iv)
            && double.TryParse(iv, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, 60));
        }
        return TimeSpan.FromSeconds(1);
    }

    protected bool CanPublishEndpoint(IDeploymentSubstrate substrate, DeploymentEndpointRequest request)
        => substrate.CanPublishEndpoint(request);

    protected DeploymentEndpoint PublishEndpoint(IDeploymentSubstrate substrate, DeploymentEndpointRequest request)
    {
        if (!substrate.CanPublishEndpoint(request))
            throw new NotSupportedException(
                $"Deployment kind '{Kind}' cannot publish {request.Kind} endpoint on port {request.Port?.ToString() ?? "<none>"} " +
                $"from substrate '{substrate.Id}'.");
        return substrate.PublishEndpoint(request);
    }

    protected static void AddServiceEndpointMetadata(
        IDictionary<string, string> metadata,
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        string scheme = "http")
    {
        foreach (var service in recipe.Services)
        {
            if (service.Ports.Count == 0)
                continue;
            var port = service.Ports[0];
            var path = string.IsNullOrWhiteSpace(service.HealthEndpoint)
                ? string.Empty
                : service.HealthEndpoint!.StartsWith('/') ? service.HealthEndpoint : "/" + service.HealthEndpoint;
            metadata[$"service.{service.Name}.image"] = service.ImageReference;
            if (!string.IsNullOrWhiteSpace(service.HealthEndpoint))
            {
                metadata[$"service.{service.Name}.sandbox-local-url"] = $"{scheme}://{Localhost}:{port}{path}";
                var request = new DeploymentEndpointRequest
                {
                    Kind = DeploymentEndpointKind.Http,
                    Scheme = scheme,
                    Port = port,
                    Path = path,
                };
                if (substrate.CanPublishEndpoint(request))
                {
                    var endpoint = substrate.PublishEndpoint(request);
                    if (!string.IsNullOrWhiteSpace(endpoint.Url))
                        metadata[$"service.{service.Name}.url"] = endpoint.Url!;
                }
            }
            else
            {
                metadata[$"service.{service.Name}.sandbox-local-endpoint"] = $"{Localhost}:{port}";
                var request = new DeploymentEndpointRequest
                {
                    Kind = DeploymentEndpointKind.Tcp,
                    Port = port,
                };
                if (substrate.CanPublishEndpoint(request))
                {
                    var endpoint = substrate.PublishEndpoint(request);
                    if (!string.IsNullOrWhiteSpace(endpoint.Host) && endpoint.Port is { } publishedPort)
                        metadata[$"service.{service.Name}.endpoint"] = $"{endpoint.Host}:{publishedPort}";
                }
            }
        }
    }

    protected static string Tail(string? text, int maxChars = 256)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var redacted = RawOutputRedactor.Redact(text);
        maxChars = Math.Min(maxChars, ErrorOutputTailChars);
        return redacted.Length <= maxChars ? redacted : redacted[^maxChars..];
    }

    private static string SanitizeProcessName(string name)
    {
        var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        var sanitized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "process" : sanitized;
    }
}

/// <summary>
/// Default <see cref="IDeploymentHandle"/> implementation backed by a single
/// substrate. DisposeAsync is idempotent — the second call is a no-op so the
/// leak reaper, graceful shutdown, and the normal teardown path can all call
/// it without coordinating.
/// </summary>
internal sealed class SandboxDeploymentHandle : IDeploymentHandle
{
    private readonly Func<CancellationToken, Task> _healthCheck;
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private int _disposed;

    public SandboxDeploymentHandle(
        string id,
        string kind,
        IDeploymentSubstrate substrate,
        DeploymentEndpoint endpoint,
        Func<CancellationToken, Task> healthCheck)
    {
        Id = id;
        Kind = kind;
        Substrate = substrate;
        Endpoint = endpoint;
        _healthCheck = healthCheck;
    }

    public string Id { get; }
    public string Kind { get; }
    internal IDeploymentSubstrate Substrate { get; }
    public DeploymentEndpoint Endpoint { get; }
    public bool IsAlive => Volatile.Read(ref _disposed) == 0;
    public string? SubstrateId => Substrate.Id;

    public Task HealthCheckAsync(CancellationToken ct = default)
    {
        if (!IsAlive)
            throw new ObjectDisposedException(nameof(SandboxDeploymentHandle));
        return _healthCheck(ct);
    }

    public Task<DeploymentCommandResult> ExecAsync(DeploymentCommand command, CancellationToken ct = default)
    {
        if (!IsAlive)
            throw new ObjectDisposedException(nameof(SandboxDeploymentHandle));
        return Substrate.ExecAsync(command, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            await Substrate.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _disposed, 1);
        }
        finally
        {
            _disposeGate.Release();
        }
    }
}
