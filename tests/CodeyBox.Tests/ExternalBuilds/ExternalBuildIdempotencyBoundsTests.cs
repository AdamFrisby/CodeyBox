using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Orchestrator.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

/// <summary>Bounds for sandbox-supplied idempotency keys and the bounded
/// single-flight start gate (no per-key table growth).</summary>
public sealed class ExternalBuildIdempotencyBoundsTests
{
    [Fact]
    public async Task Start_OverLongIdempotencyKey_IsRejectedBeforeDispatch()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, store, _, opts) = ExternalBuildTestKit.BuildService(provider);
        var request = ExternalBuildTestKit.Request() with
        {
            IdempotencyKey = new string('k', opts.MaxIdempotencyKeyChars + 1),
        };

        var ex = await Assert.ThrowsAsync<ExternalBuildInvalidRequestException>(
            () => service.StartAsync(request, new ExternalBuildSubmitInput()));

        Assert.Contains(opts.MaxIdempotencyKeyChars.ToString(), ex.Message);
        Assert.Equal(0, provider.SubmitCalls);
        Assert.Equal(0, await store.CountActiveAsync("proj"));
    }

    [Fact]
    public async Task Start_IdempotencyKeyAtMaxLength_IsAccepted()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, _, _, opts) = ExternalBuildTestKit.BuildService(provider);
        var request = ExternalBuildTestKit.Request() with
        {
            IdempotencyKey = new string('k', opts.MaxIdempotencyKeyChars),
        };

        var record = await service.StartAsync(request, new ExternalBuildSubmitInput());

        Assert.Equal(ExternalBuildState.Queued, record.State);
        Assert.Equal(1, provider.SubmitCalls);
    }

    [Fact]
    public async Task Start_IdempotencyKeyWithControlChars_IsRejected()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(provider);
        var request = ExternalBuildTestKit.Request() with { IdempotencyKey = "ok-key\nInjected" };

        await Assert.ThrowsAsync<ExternalBuildInvalidRequestException>(
            () => service.StartAsync(request, new ExternalBuildSubmitInput()));

        Assert.Equal(0, provider.SubmitCalls);
    }

    [Fact]
    public async Task Start_ManyDistinctKeys_UsesBoundedGate()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(
            provider, o => { o.MaxConcurrentPerProvider = 100; o.MaxQueuedPerProject = 100; });

        var stripes = (System.Reflection.FieldInfo?)typeof(ExternalBuildService)
            .GetField("_startStripes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(stripes);
        var array = Assert.IsType<SemaphoreSlim[]>(stripes!.GetValue(service));
        Assert.Equal(16, array.Length);

        for (var i = 0; i < 64; i++)
        {
            var request = ExternalBuildTestKit.Request(attempt: "distinct-" + i) with
            {
                IdempotencyKey = "distinct-key-" + i,
            };
            var record = await service.StartAsync(request, new ExternalBuildSubmitInput());
            Assert.Equal(ExternalBuildState.Queued, record.State);
        }
        Assert.Equal(64, provider.SubmitCalls);
        Assert.Equal(16, ((SemaphoreSlim[])stripes.GetValue(service)!).Length);
    }
}
