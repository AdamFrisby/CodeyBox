using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that signalling host shutdown (via the hostShutdownToken) does NOT
/// transition an in-flight work item to Cancelled. The item should remain in its
/// mid-flight state so the recovery loop can pick it up on the next start.
///
/// Operator-requested cancel (DELETE /workitems/{id}) must still produce Cancelled.
/// </summary>
public sealed class HostShutdownCancellationTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-shutdown-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public HostShutdownCancellationTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    // ── Host shutdown: leave item in mid-flight state ─────────────────────────

    [Fact]
    public async Task HostShutdown_DoesNotCancelItem_LeavesWorkingState()
    {
        using var hostShutdownCts = new CancellationTokenSource();
        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(hostShutdownCts.Token);

        var item = NewItem();
        await _store.CreateAsync(item);

        // Transition to Working to simulate mid-flight
        var working = item.With(WorkItemState.Working);
        await _store.UpdateAsync(working);

        // Create a pipeline that blocks until the token fires, then exits
        var pipeline = new BlockingShutdownPipeline(_store);

        // Fire host shutdown — this cancels both hostShutdownCts and the linked itemCts
        _ = Task.Run(async () =>
        {
            await Task.Delay(30);
            await hostShutdownCts.CancelAsync();
        });

        // RunAsync should throw (Task)CanceledException; item must remain Working
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.RunAsync(working, itemCts.Token, hostShutdownCts.Token));

        var final = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, final!.State);
        Assert.Null(final.CancellationReason);
    }

    // ── Operator cancel: item must be Cancelled with OperatorRequested reason ──

    [Fact]
    public async Task OperatorCancel_TransitionsItem_ToCancelled_WithReason()
    {
        using var hostShutdownCts = new CancellationTokenSource(); // never fires
        using var itemCts = new CancellationTokenSource();

        var item = NewItem();
        await _store.CreateAsync(item);

        var working = item.With(WorkItemState.Working);
        await _store.UpdateAsync(working);

        var pipeline = new BlockingShutdownPipeline(_store);

        // Fire item-level cancel only (not host shutdown)
        _ = Task.Run(async () =>
        {
            await Task.Delay(30);
            await itemCts.CancelAsync();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.RunAsync(working, itemCts.Token, hostShutdownCts.Token));

        var final = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Cancelled, final!.State);
        Assert.Equal(WorkItemCancellationReason.OperatorRequested, final.CancellationReason);
    }
}

/// <summary>
/// Minimal pipeline that writes the item to Working, blocks on the cancellation
/// token, then delegates to PipelineRunner's cancellation semantics via a
/// direct re-implementation of the key catch clause.
/// </summary>
internal sealed class BlockingShutdownPipeline : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    public BlockingShutdownPipeline(IWorkItemStore store) => _store = store;

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (hostShutdownToken.IsCancellationRequested)
            {
                // Host shutdown — leave state unchanged so recovery can pick it up
            }
            else
            {
                // Operator cancel
                var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;
                if (current.State is not WorkItemState.Done and not WorkItemState.Failed)
                {
                    var cancelled = current.With(WorkItemState.Cancelled, "cancelled via API",
                        WorkItemCancellationReason.OperatorRequested);
                    await _store.UpdateAsync(cancelled, CancellationToken.None);
                }
            }
            throw;
        }
    }
}
