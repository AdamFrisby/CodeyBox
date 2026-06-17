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
            await StartRuntimeAsync(sandbox, recipe, context, ct).ConfigureAwait(false);

            using (var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                probeCts.CancelAfter(recipe.StartupTimeout);
                try
                {
                    await ProbeReadyAsync(sandbox, recipe, context, probeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Deployment kind '{Kind}' failed readiness probe within {recipe.StartupTimeout}. " +
                        "Tearing down substrate.");
                }
            }

            var endpoint = BuildEndpoint(sandbox, recipe, context);
            var id = Guid.NewGuid().ToString("N")[..16];
            // Capture the sandbox reference in a separate local so nulling the
            // outer one (to skip the catch's cleanup-on-failure path) does not
            // also nil out the closure used by the runtime health check.
            var owned = sandbox;
            var handle = new SandboxDeploymentHandle(
                id,
                Kind,
                owned,
                endpoint,
                () => RunHealthCheckAsync(owned, recipe, context));
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
    /// NetworkProfile.
    /// </summary>
    protected virtual SandboxSpec BuildSandboxSpec(DeploymentRecipe recipe, DeploymentContext context) => new()
    {
        ImageReference = recipe.ImageReference,
        Mounts = context.Mounts,
        Environment = recipe.Environment,
        Network = recipe.NetworkProfile is null
            ? SandboxNetworkPolicy.Denied
            : new SandboxNetworkPolicy { ProfileName = recipe.NetworkProfile },
        WorkingDirectory = context.WorkingDirectory,
    };

    protected virtual async Task RunBuildAsync(
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipe.BuildCommand))
            return;
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", recipe.BuildCommand],
            WorkingDirectory = context.WorkingDirectory,
            ExtraEnvironment = recipe.Environment,
        }, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(
                $"Deployment kind '{Kind}' build step exited {result.ExitCode}; stderr tail: {Tail(result.Stderr)}");
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
    /// Default re-runs the readiness probe with no startup deadline.
    /// </summary>
    protected virtual Task RunHealthCheckAsync(
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context)
        => ProbeReadyAsync(sandbox, recipe, context, CancellationToken.None);

    protected static string Tail(string? text, int maxChars = 256)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxChars ? text : text[^maxChars..];
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
    private readonly Func<Task> _healthCheck;
    private int _disposed;

    public SandboxDeploymentHandle(
        string id,
        string kind,
        ISandbox sandbox,
        DeploymentEndpoint endpoint,
        Func<Task> healthCheck)
    {
        Id = id;
        Kind = kind;
        Sandbox = sandbox;
        Endpoint = endpoint;
        _healthCheck = healthCheck;
    }

    public string Id { get; }
    public string Kind { get; }
    public ISandbox Sandbox { get; }
    public DeploymentEndpoint Endpoint { get; }
    public bool IsAlive => Volatile.Read(ref _disposed) == 0;
    public string? SandboxId => Sandbox.Id;

    public Task HealthCheckAsync(CancellationToken ct = default)
    {
        if (!IsAlive)
            throw new ObjectDisposedException(nameof(SandboxDeploymentHandle));
        return _healthCheck();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await Sandbox.DisposeAsync().ConfigureAwait(false);
    }
}
