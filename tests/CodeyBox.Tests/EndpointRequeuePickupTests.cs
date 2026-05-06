using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that HTTP endpoints which resurrect terminal items do both pieces
/// of work: persist Queued state and publish the item to the in-memory queue
/// consumed by the worker pool.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class EndpointRequeuePickupTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public EndpointRequeuePickupTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Retry_FailedItem_EnqueuesAndWorkerPicksUp()
    {
        var item = NewItem(WorkItemState.Failed) with { LastError = "previous failure" };
        await _factory.Store.CreateAsync(item);

        var queue = _factory.Services.GetRequiredService<ITaskQueue>();
        Assert.Equal(0, queue.Count);

        var resp = await _client.PostAsJsonAsync($"/workitems/{item.Id}/retry", new { from = "work" });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal(1, queue.Count);

        var pipeline = new FakePipelineRunner(_factory.Store);
        using var svc = BuildOrchestrator(queue, pipeline);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            await WaitForExecutionAsync(pipeline, item.Id);
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }

        var final = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task Uncancel_ParentCascadedItem_EnqueuesAndWorkerPicksUp()
    {
        var item = NewItem(WorkItemState.Cancelled) with
        {
            LastError = "parent dependency cancelled",
            CancellationReason = WorkItemCancellationReason.ParentCascaded,
        };
        await _factory.Store.CreateAsync(item);

        var queue = _factory.Services.GetRequiredService<ITaskQueue>();
        Assert.Equal(0, queue.Count);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/uncancel", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, queue.Count);

        var pipeline = new FakePipelineRunner(_factory.Store);
        using var svc = BuildOrchestrator(queue, pipeline);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            await WaitForExecutionAsync(pipeline, item.Id);
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }

        var final = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    private OrchestratorService BuildOrchestrator(ITaskQueue queue, IPipelineRunner pipeline)
        => new(
            queue,
            _factory.Store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

    private static WorkItem NewItem(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = state,
    };

    private static async Task WaitForExecutionAsync(FakePipelineRunner pipeline, WorkItemId id)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (pipeline.Executed.Contains(id))
                return;
            await Task.Delay(20);
        }

        Assert.Fail($"Worker did not pick up work item {id} before timeout");
    }
}
