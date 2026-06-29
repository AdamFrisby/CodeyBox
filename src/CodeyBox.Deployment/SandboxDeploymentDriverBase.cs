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

        var spec = BuildSandboxSpec(recipe, context);
        ISandbox? sandbox = null;
        try
        {
            sandbox = await context.SandboxProvider.CreateAsync(spec, ct).ConfigureAwait(false);

            await RunBuildAsync(sandbox, recipe, context, ct).ConfigureAwait(false);

            // StartupTimeout bounds StartRuntimeAsync AND ProbeReadyAsync.
            // RunCommand is "fire and forget" by convention (recipe authors
            // background the server via nohup/&/exec), but a misconfigured
            // recipe that forgets to background can otherwise hang the deploy
            // indefinitely — neither the startup timeout nor the readiness
            // probe would fire. Wrapping start under the same bound surfaces
            // the failure as TimeoutException after StartupTimeout.
            //
            // The two stages share the deadline but are caught separately so
            // the operator-facing message tells them which stage hit it. A
            // start-stage timeout almost always means the recipe forgot to
            // background the run command (sandbox.ExecAsync is one-shot and
            // blocks until the child exits); a probe-stage timeout means the
            // server started but readiness never converged.
            using (var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                startupCts.CancelAfter(recipe.StartupTimeout);
                try
                {
                    await StartServicesAsync(sandbox, recipe, context, startupCts.Token).ConfigureAwait(false);
                    await ProbeServicesReadyAsync(sandbox, recipe, context, startupCts.Token).ConfigureAwait(false);
                    await StartRuntimeAsync(sandbox, recipe, context, startupCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Deployment kind '{Kind}' startup did not return within {recipe.StartupTimeout}. " +
                        "RunCommand likely runs the server in the foreground — sandbox.ExecAsync waits for the " +
                        "child process to exit, so the recipe must background the server (e.g. nohup ... &, exec, " +
                        "or a process supervisor) for StartRuntime to return. Tearing down substrate.");
                }
                try
                {
                    await ProbeReadyAsync(sandbox, recipe, context, startupCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Deployment kind '{Kind}' did not become ready within {recipe.StartupTimeout}. " +
                        "Tearing down substrate.");
                }
            }

            var endpoint = BuildEndpoint(sandbox, recipe, context);
            var id = Guid.NewGuid().ToString("N")[..DeploymentIdHexChars];
            // Capture the sandbox reference in a separate local so nulling the
            // outer one (to skip the catch's cleanup-on-failure path) does not
            // also nil out the closure used by the runtime health check.
            var owned = sandbox;
            var handle = new SandboxDeploymentHandle(
                id,
                Kind,
                owned,
                endpoint,
                runtimeCt => RunHealthCheckAsync(owned, recipe, context, runtimeCt));
            sandbox = null; // ownership transferred
            return handle;
        }
        catch
        {
            if (sandbox is not null)
            {
                try { await sandbox.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log.LogWarning(ex, "Driver {Kind} teardown after failed deploy threw", Kind); }
            }
            throw;
        }
    }

    /// <summary>
    /// Builds the substrate SandboxSpec from the recipe. Default merges the
    /// recipe's environment into a sandbox spec keyed off ImageReference and
    /// NetworkProfile. <see cref="SandboxNetworkPolicy.Denied"/> is the safe
    /// default when no profile is configured — deployment substrates are
    /// network-isolated unless the recipe declares otherwise.
    /// </summary>
    protected virtual SandboxSpec BuildSandboxSpec(DeploymentRecipe recipe, DeploymentContext context) => new()
    {
        ImageReference = recipe.ImageReference,
        Purpose = SandboxPurpose.Deployment,
        Mounts = context.Mounts,
        Environment = recipe.Environment,
        Network = recipe.NetworkProfile is null
            ? SandboxNetworkPolicy.Denied
            : new SandboxNetworkPolicy { ProfileName = recipe.NetworkProfile },
        WorkingDirectory = context.WorkingDirectory,
    };

    protected virtual async Task StartServicesAsync(
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        foreach (var service in recipe.Services)
        {
            var result = await RunDeploymentExecAsync(
                sandbox,
                recipe,
                context,
                $"service '{service.Name}' start",
                ["sh", "-c", service.RunCommand!],
                MergeEnvironment(recipe.Environment, service.Environment),
                ct).ConfigureAwait(false);
            if (!result.Success)
                throw DeploymentExecFailed($"service '{service.Name}' start", result);
        }
    }

    protected virtual async Task ProbeServicesReadyAsync(
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        foreach (var service in recipe.Services)
            await ProbeServiceReadyAsync(sandbox, recipe, context, service, ct).ConfigureAwait(false);
    }

    private async Task ProbeServiceReadyAsync(
        ISandbox sandbox,
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
                sandbox,
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
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipe.BuildCommand))
            return;
        var result = await RunDeploymentExecAsync(
            sandbox,
            recipe,
            context,
            "build",
            ["sh", "-c", recipe.BuildCommand],
            recipe.Environment,
            ct,
            recipe.MaxLifetime).ConfigureAwait(false);
        if (!result.Success)
            throw DeploymentExecFailed("build", result);
    }

    /// <summary>
    /// Driver-specific hook to launch the deployed software. Default no-op
    /// for kinds that do not start a long-running process (CLI, Library).
    /// </summary>
    protected virtual Task StartRuntimeAsync(
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>Driver-specific readiness probe. Throws on persistent failure; returns on success.</summary>
    protected abstract Task ProbeReadyAsync(
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct);

    /// <summary>Builds the public-facing endpoint surfaced on the handle.</summary>
    protected abstract DeploymentEndpoint BuildEndpoint(
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context);

    /// <summary>
    /// Hook for the runtime <see cref="IDeploymentHandle.HealthCheckAsync"/>.
    /// Default re-runs backing-service probes and the primary readiness probe
    /// under the recipe's startup timeout, unless the caller cancels first.
    /// </summary>
    protected virtual async Task RunHealthCheckAsync(
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        using var healthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        healthCts.CancelAfter(recipe.StartupTimeout);
        try
        {
            await ProbeServicesReadyAsync(sandbox, recipe, context, healthCts.Token).ConfigureAwait(false);
            await ProbeReadyAsync(sandbox, recipe, context, healthCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Deployment kind '{Kind}' health check did not complete within {recipe.StartupTimeout}.");
        }
    }

    protected async Task<SandboxExecResult> RunDeploymentExecAsync(
        ISandbox sandbox,
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
            var result = await sandbox.ExecAsync(new SandboxExec
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

    protected InvalidOperationException DeploymentExecFailed(string stage, SandboxExecResult result)
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

    protected static string? ResolveHostAddress(ISandbox sandbox)
        => sandbox is IRoutableSandbox { HostAddress: { } host } && !string.IsNullOrWhiteSpace(host)
            ? host
            : null;

    protected static void AddServiceEndpointMetadata(
        IDictionary<string, string> metadata,
        ISandbox sandbox,
        DeploymentRecipe recipe,
        string scheme = "http")
    {
        var host = ResolveHostAddress(sandbox);
        foreach (var service in recipe.Services)
        {
            if (service.Ports.Count == 0)
                continue;
            var port = service.Ports[0];
            var path = string.IsNullOrWhiteSpace(service.HealthEndpoint)
                ? string.Empty
                : service.HealthEndpoint!.StartsWith('/') ? service.HealthEndpoint : "/" + service.HealthEndpoint;
            metadata[$"service.{service.Name}.sandbox-local-url"] = $"{scheme}://{Localhost}:{port}{path}";
            if (host is not null)
                metadata[$"service.{service.Name}.url"] = $"{scheme}://{host}:{port}{path}";
        }
    }

    protected static string Tail(string? text, int maxChars = 256)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var redacted = RawOutputRedactor.Redact(text);
        maxChars = Math.Min(maxChars, ErrorOutputTailChars);
        return redacted.Length <= maxChars ? redacted : redacted[^maxChars..];
    }
}

/// <summary>
/// Default <see cref="IDeploymentHandle"/> implementation backed by a single
/// sandbox. DisposeAsync is idempotent — the second call is a no-op so the
/// leak reaper, graceful shutdown, and the normal teardown path can all call
/// it without coordinating.
/// </summary>
internal sealed class SandboxDeploymentHandle : IDeploymentHandle
{
    private readonly Func<CancellationToken, Task> _healthCheck;
    private int _disposed;

    public SandboxDeploymentHandle(
        string id,
        string kind,
        ISandbox sandbox,
        DeploymentEndpoint endpoint,
        Func<CancellationToken, Task> healthCheck)
    {
        Id = id;
        Kind = kind;
        Sandbox = sandbox;
        Endpoint = endpoint;
        _healthCheck = healthCheck;
    }

    public string Id { get; }
    public string Kind { get; }
    internal ISandbox Sandbox { get; }
    public DeploymentEndpoint Endpoint { get; }
    public bool IsAlive => Volatile.Read(ref _disposed) == 0;
    public string? SandboxId => Sandbox.Id;

    public Task HealthCheckAsync(CancellationToken ct = default)
    {
        if (!IsAlive)
            throw new ObjectDisposedException(nameof(SandboxDeploymentHandle));
        return _healthCheck(ct);
    }

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        if (!IsAlive)
            throw new ObjectDisposedException(nameof(SandboxDeploymentHandle));
        return Sandbox.ExecAsync(exec, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await Sandbox.DisposeAsync().ConfigureAwait(false);
    }
}
