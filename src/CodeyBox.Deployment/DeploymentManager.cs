using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Deployment;

/// <summary>
/// Default <see cref="IDeploymentManager"/> — looks up the driver by recipe
/// Kind, runs the deploy lifecycle, and tracks the resulting handle in an
/// in-memory active set until the caller disposes it. Disposal is reflected
/// back into the active set automatically via a tracking wrapper around the
/// driver-returned handle, so callers never have to coordinate the removal.
///
/// <para>The active set is also the source of truth for the deployment leak
/// reaper: any provider-managed sandbox whose id is not in the manager's
/// snapshot at sweep time is treated as an orphan.</para>
/// </summary>
public sealed class DeploymentManager : IDeploymentManager
{
    private readonly IDeploymentDriverRegistry _registry;
    private readonly ILogger<DeploymentManager> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, TrackedDeployment> _active = new(StringComparer.Ordinal);

    public DeploymentManager(IDeploymentDriverRegistry registry)
        : this(registry, NullLogger<DeploymentManager>.Instance, clock: null) { }

    public DeploymentManager(
        IDeploymentDriverRegistry registry,
        ILogger<DeploymentManager> log,
        Func<DateTimeOffset>? clock = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _log = log ?? NullLogger<DeploymentManager>.Instance;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IDeploymentHandle> StartAsync(
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(context);

        if (!_registry.TryGet(recipe.Kind, out var driver))
            throw new InvalidOperationException(
                $"No deployment driver registered for kind '{recipe.Kind}'. Registered: " +
                $"[{string.Join(", ", _registry.AvailableKinds)}]");

        driver.ValidateRecipe(recipe);

        var inner = await driver.DeployAsync(recipe, context, ct).ConfigureAwait(false);
        var startedAt = _clock();
        var tracked = new TrackedDeployment(inner, this, recipe.Kind, context.ProjectId, startedAt);
        _active[inner.Id] = tracked;
        _log.LogInformation(
            "Deployment {Id} of kind {Kind} started; endpoint={EndpointKind} sandbox={SandboxId}",
            inner.Id, recipe.Kind, inner.Endpoint.Kind, inner.SandboxId ?? "<none>");
        return tracked;
    }

    public IReadOnlyList<ActiveDeploymentInfo> GetActive()
    {
        var snapshot = _active.Values.ToArray();
        var result = new List<ActiveDeploymentInfo>(snapshot.Length);
        foreach (var t in snapshot)
        {
            if (!t.Inner.IsAlive)
                continue;
            result.Add(new ActiveDeploymentInfo(
                t.Inner.Id,
                t.Kind,
                t.ProjectId,
                t.Inner.SandboxId,
                t.StartedAt,
                t.Inner.Endpoint));
        }
        return result;
    }

    private void Untrack(string id) => _active.TryRemove(id, out _);

    private sealed class TrackedDeployment(
        IDeploymentHandle inner,
        DeploymentManager owner,
        string kind,
        ProjectId? projectId,
        DateTimeOffset startedAt) : IDeploymentHandle
    {
        public IDeploymentHandle Inner { get; } = inner;
        public string Kind { get; } = kind;
        public ProjectId? ProjectId { get; } = projectId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        private int _disposed;

        public string Id => Inner.Id;
        public DeploymentEndpoint Endpoint => Inner.Endpoint;
        public bool IsAlive => Volatile.Read(ref _disposed) == 0 && Inner.IsAlive;
        public string? SandboxId => Inner.SandboxId;

        public Task HealthCheckAsync(CancellationToken ct = default) => Inner.HealthCheckAsync(ct);
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Inner.ExecAsync(exec, ct);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                await Inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                owner.Untrack(Id);
            }
        }
    }
}
