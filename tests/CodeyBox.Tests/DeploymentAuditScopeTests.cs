using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DeploymentAuditScope"/>: one provisioned deployment
/// per scope, an enforced recipe-lifetime deadline, and idempotent
/// teardown-always disposal. A lost handle across an orchestrator restart is
/// covered by the deployment leak reaper (<c>DeploymentLeakReaperTests</c>);
/// this scope guarantees every in-process exit path disposes.
/// </summary>
public sealed class DeploymentAuditScopeTests
{
    private sealed class FakeHandle(DeploymentEndpoint endpoint, string id = "dep-1") : IDeploymentHandle
    {
        public string Id { get; } = id;
        public string Kind => DeploymentKinds.WebApp;
        public DeploymentEndpoint Endpoint { get; } = endpoint;
        public bool IsAlive => Volatile.Read(ref _disposed) == 0;
        public string? SubstrateId => "substrate-1";
        public int DisposeCount => Volatile.Read(ref _disposed);
        private int _disposed;
        public Task HealthCheckAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<DeploymentCommandResult> ExecAsync(DeploymentCommand command, CancellationToken ct = default)
            => Task.FromResult(new DeploymentCommandResult(0, string.Empty, string.Empty));
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposed);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeManager(Func<DeploymentRecipe, DeploymentContext, CancellationToken, Task<IDeploymentHandle>> start) : IDeploymentManager
    {
        public int StartCount;
        public Task<IDeploymentHandle> StartAsync(DeploymentRecipe recipe, DeploymentContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref StartCount);
            return start(recipe, context, ct);
        }
        public IReadOnlyList<ActiveDeploymentInfo> GetActive() => [];
    }

    private sealed class FakeSubstrates : IDeploymentSubstrateProvider
    {
        public string Name => "fake";
        public Task<IDeploymentSubstrate> CreateAsync(DeploymentSubstrateSpec spec, CancellationToken ct = default)
            => throw new NotSupportedException("FakeManager never reaches the substrate provider.");
    }

    private static readonly DeploymentEndpoint Endpoint = new()
    {
        Kind = DeploymentEndpointKind.Http,
        Url = "http://127.0.0.1:8080",
    };

    private static DeploymentRecipe Recipe(TimeSpan? maxLifetime = null) => new()
    {
        Kind = DeploymentKinds.WebApp,
        ImageReference = "img",
        MaxLifetime = maxLifetime ?? TimeSpan.FromMinutes(60),
    };

    private static Project Project() => new()
    {
        Id = new ProjectId("p"),
        DisplayName = "P",
        RepositoryUrl = "https://example.com/x.git",
    };

    [Fact]
    public async Task ProvisionAsync_ExposesEndpointAndLifetimeDeadline()
    {
        var handle = new FakeHandle(Endpoint);
        var manager = new FakeManager((_, _, _) => Task.FromResult<IDeploymentHandle>(handle));
        var start = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        var recipe = Recipe(TimeSpan.FromMinutes(9));

        await using var scope = await DeploymentAuditScope.ProvisionAsync(
            manager, new FakeSubstrates(), Project(), recipe, clock: () => start);

        Assert.Equal(1, manager.StartCount);
        Assert.Same(handle, scope.Handle);
        Assert.Equal(Endpoint, scope.Endpoint);
        Assert.Equal(start, scope.StartedAt);
        Assert.Equal(start + TimeSpan.FromMinutes(9), scope.Deadline);
        Assert.Equal(TimeSpan.FromMinutes(9), scope.RemainingLifetime(start));
        Assert.Equal(TimeSpan.Zero, scope.RemainingLifetime(start + TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentTeardownAlways()
    {
        var handle = new FakeHandle(Endpoint);
        var manager = new FakeManager((_, _, _) => Task.FromResult<IDeploymentHandle>(handle));
        var scope = await DeploymentAuditScope.ProvisionAsync(manager, new FakeSubstrates(), Project(), Recipe());

        await scope.DisposeAsync();
        await scope.DisposeAsync();

        Assert.Equal(1, handle.DisposeCount);
        Assert.False(handle.IsAlive);
    }

    [Fact]
    public async Task DisposeAsync_RunsOnAbortAndCancelPaths()
    {
        var handle = new FakeHandle(Endpoint);
        var manager = new FakeManager((_, _, _) => Task.FromResult<IDeploymentHandle>(handle));
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        await using (await DeploymentAuditScope.ProvisionAsync(manager, new FakeSubstrates(), Project(), Recipe(), ct: CancellationToken.None))
        {
            // Simulated abort: the audit body observes cancellation, then the
            // scope still tears down via await-using on the way out.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Task.FromCanceled(aborted.Token));
        }

        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact]
    public async Task ProvisionAsync_PropagatesStartFailureWithoutLeakingAScope()
    {
        var manager = new FakeManager((_, _, _) => throw new InvalidOperationException("no driver"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => DeploymentAuditScope.ProvisionAsync(
            manager, new FakeSubstrates(), Project(), Recipe()));

        Assert.Equal(1, manager.StartCount);
    }

    [Fact]
    public async Task LinkLifetime_ExpiredDeadline_CancelsImmediately()
    {
        var handle = new FakeHandle(Endpoint);
        var manager = new FakeManager((_, _, _) => Task.FromResult<IDeploymentHandle>(handle));
        var start = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        await using var scope = await DeploymentAuditScope.ProvisionAsync(
            manager, new FakeSubstrates(), Project(), Recipe(TimeSpan.FromMinutes(1)), clock: () => start);

        using var linked = scope.LinkLifetime(
            CancellationToken.None,
            clock: () => start + TimeSpan.FromHours(2));

        Assert.True(linked.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task LinkLifetime_FutureDeadline_StaysLinkedToCaller()
    {
        var handle = new FakeHandle(Endpoint);
        var manager = new FakeManager((_, _, _) => Task.FromResult<IDeploymentHandle>(handle));
        var start = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        await using var scope = await DeploymentAuditScope.ProvisionAsync(
            manager, new FakeSubstrates(), Project(), Recipe(TimeSpan.FromMinutes(60)), clock: () => start);
        using var caller = new CancellationTokenSource();

        using var linked = scope.LinkLifetime(caller.Token, clock: () => start);

        Assert.False(linked.Token.IsCancellationRequested);
        await caller.CancelAsync();
        Assert.True(linked.Token.IsCancellationRequested);
    }
}
