using System.Threading.Channels;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Channel-based in-process dispatch notification stream. The channel only
/// carries wake-up kicks; the <see cref="IWorkItemStore"/> remains the durable
/// source of truth for queued work.
/// </summary>
public sealed class InMemoryTaskQueue : ITaskQueue
{
    private readonly Channel<WorkItemId> _channel = Channel.CreateUnbounded<WorkItemId>();

    public ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(id, ct);

    public int Count => _channel.Reader.Count;

    public async ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default)
    {
        try
        {
            return await _channel.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public void Complete() => _channel.Writer.TryComplete();
}
