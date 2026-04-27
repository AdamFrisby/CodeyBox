using System.Threading.Channels;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Channel-based in-process queue. Drop-in replacement for a durable queue;
/// loses pending work on process exit (the <see cref="IWorkItemStore"/> is
/// the durable record — at startup the orchestrator re-enqueues anything in
/// a non-terminal state).
/// </summary>
public sealed class InMemoryTaskQueue : ITaskQueue
{
    private readonly Channel<WorkItemId> _channel = Channel.CreateUnbounded<WorkItemId>();

    public ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(id, ct);

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
