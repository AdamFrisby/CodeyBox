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
    private readonly Channel<DispatchSignal> _channel = Channel.CreateUnbounded<DispatchSignal>();

    public ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(DispatchSignal.ForWorkItem(id), ct);

    public ValueTask EnqueueDispatchWakeAsync(CancellationToken ct = default)
        => _channel.Writer.WriteAsync(DispatchSignal.GenericWake, ct);

    public int Count => _channel.Reader.Count;

    public async ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default)
    {
        try
        {
            var signal = await _channel.Reader.ReadAsync(ct);
            return signal.WorkItemId;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async ValueTask<bool> DequeueDispatchSignalAsync(CancellationToken ct = default)
    {
        try
        {
            await _channel.Reader.ReadAsync(ct);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    public void Complete() => _channel.Writer.TryComplete();

    private readonly record struct DispatchSignal(WorkItemId? WorkItemId)
    {
        public static DispatchSignal ForWorkItem(WorkItemId id) => new(id);
        public static DispatchSignal GenericWake { get; } = new(null);
    }
}
