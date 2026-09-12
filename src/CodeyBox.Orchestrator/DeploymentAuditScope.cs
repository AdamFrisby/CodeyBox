using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Owns one lazily-provisioned verification deployment for a single audit
/// iteration. Provisioning happens only after the code stage passes; disposal
/// tears the deployment down and is idempotent, so the audit loop can call it
/// from the normal-path finally block on every exit (pass, fail, abort,
/// cancel, iteration timeout) without coordinating. A process-level restart
/// between provision and disposal loses this handle — the
/// <c>DeploymentLeakReaper</c> sweeps that case as the safety net, since the
/// substrate itself is provider-managed and outlives the orchestrator.
/// A fresh scope (hence a fresh deployment) is provisioned per audit
/// iteration — a deployment is never reused against new code after rework.
/// </summary>
public sealed class DeploymentAuditScope : IAsyncDisposable
{
    private readonly IDeploymentHandle _handle;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private int _disposed;

    private DeploymentAuditScope(
        IDeploymentHandle handle,
        DateTimeOffset startedAt,
        DateTimeOffset deadline,
        ILogger log)
    {
        _handle = handle;
        StartedAt = startedAt;
        Deadline = deadline;
        _log = log;
    }

    /// <summary>Live handle to the provisioned deployment.</summary>
    public IDeploymentHandle Handle => _handle;

    /// <summary>Connection info handed to deployment-stage auditors via <see cref="AuditContext"/>.</summary>
    public DeploymentEndpoint Endpoint => _handle.Endpoint;

    /// <summary>When the deployment finished provisioning (clock time).</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Absolute time the deployment must be torn down by: start + the
    /// recipe's <see cref="DeploymentRecipe.MaxLifetime"/> (see
    /// <see cref="DeploymentAuditPolicy.EffectiveLifetime"/>).
    /// </summary>
    public DateTimeOffset Deadline { get; }

    /// <summary>Remaining lifetime from <paramref name="now"/>; never negative.</summary>
    public TimeSpan RemainingLifetime(DateTimeOffset now)
    {
        var remaining = Deadline - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Provisions ONE deployment from the project's recipe. The driver tears
    /// down whatever it had provisioned before throwing, so a failed
    /// <c>StartAsync</c> never leaks a half-built deployment through this scope.
    /// </summary>
    public static async Task<DeploymentAuditScope> ProvisionAsync(
        IDeploymentManager manager,
        IDeploymentSubstrateProvider substrates,
        Project project,
        DeploymentRecipe recipe,
        Func<DateTimeOffset>? clock = null,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(substrates);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(recipe);

        var context = new DeploymentContext
        {
            SubstrateProvider = substrates,
            ProjectId = project.Id,
        };
        var handle = await manager.StartAsync(recipe, context, ct).ConfigureAwait(false);
        var startedAt = (clock ?? (() => DateTimeOffset.UtcNow))();
        var scope = new DeploymentAuditScope(
            handle,
            startedAt,
            DeploymentAuditPolicy.DeadlineFor(recipe, startedAt),
            log ?? NullLogger.Instance);
        scope._log.LogInformation(
            "Deployment-stage audit provisioned deployment {DeploymentId} (kind {Kind}); deadline {Deadline}",
            handle.Id, handle.Kind, scope.Deadline);
        return scope;
    }

    /// <summary>
    /// Links the caller's token with the recipe's remaining lifetime: the
    /// returned source cancels when the caller cancels OR when the
    /// deployment's max lifetime elapses, whichever comes first. The caller
    /// disposes the source after the deployment-stage auditors complete.
    /// </summary>
    public CancellationTokenSource LinkLifetime(
        CancellationToken caller,
        Func<DateTimeOffset>? clock = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(caller);
        var remaining = RemainingLifetime((clock ?? (() => DateTimeOffset.UtcNow))());
        if (remaining <= TimeSpan.Zero)
            cts.Cancel();
        else
            cts.CancelAfter(remaining);
        return cts;
    }

    /// <summary>
    /// Tears the deployment down. Idempotent: repeated calls are no-ops, so
    /// abort, cancel, timeout, and normal paths can all dispose without
    /// coordinating. Never throws for an already-disposed scope; surfaces the
    /// underlying error (so a retry can re-attempt) while staying retryable.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            await _handle.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _disposed, 1);
            _log.LogInformation(
                "Deployment-stage audit tore down deployment {DeploymentId}",
                _handle.Id);
        }
        finally
        {
            _disposeGate.Release();
        }
    }
}
