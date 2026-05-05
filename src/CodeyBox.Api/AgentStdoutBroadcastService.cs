using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using CodeyBox.Api.Hubs;
using CodeyBox.Core;

namespace CodeyBox.Api;

/// <summary>
/// Implements <see cref="IStdoutBroadcaster"/> using SignalR. Manages:
/// <list type="bullet">
///   <item>Per-work-item <see cref="StdoutRingBuffer"/> for late-joining clients.</item>
///   <item>Per-work-item debounced batcher (100 ms or 4 KB, whichever first)
///   to reduce SignalR message rate for chatty agents.</item>
///   <item>Redaction via <see cref="RawChunkRedactor"/> before any broadcast.</item>
/// </list>
/// </summary>
public sealed class AgentStdoutBroadcastService : IStdoutBroadcaster, IDisposable
{
    private readonly IHubContext<AgentStdoutHub> _hub;
    private readonly ConcurrentDictionary<WorkItemId, StdoutRingBuffer> _buffers = new();
    private readonly ConcurrentDictionary<WorkItemId, WorkItemBatcher> _batchers = new();

    public AgentStdoutBroadcastService(IHubContext<AgentStdoutHub> hub) => _hub = hub;

    public void BroadcastChunk(WorkItemId workItemId, string phase, string chunk)
    {
        var redacted = RawChunkRedactor.Redact(chunk);
        _buffers.GetOrAdd(workItemId, _ => new StdoutRingBuffer()).Append(redacted);
        _batchers.GetOrAdd(workItemId, id => new WorkItemBatcher(id, _hub)).Add(phase, redacted);
    }

    public async Task CompleteAsync(WorkItemId workItemId)
    {
        if (_batchers.TryRemove(workItemId, out var batcher))
            await batcher.FlushAndDisposeAsync();

        await _hub.Clients.Group($"wi:{workItemId}")
            .SendAsync("streamComplete", new { workItemId = workItemId.ToString() });
    }

    public string? GetTail(WorkItemId workItemId)
        => _buffers.TryGetValue(workItemId, out var buf) ? buf.GetContents() : null;

    public void Dispose()
    {
        foreach (var batcher in _batchers.Values)
            batcher.Dispose();
        _batchers.Clear();
    }

    /// <summary>
    /// Accumulates chunks from a single work item and flushes them to the
    /// SignalR group at most every 100 ms or when 4 KB is accumulated.
    /// Flushing on phase change avoids mixing phases in one batch.
    /// </summary>
    internal sealed class WorkItemBatcher : IDisposable
    {
        internal const int FlushBytes = 4096;
        internal static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

        private readonly WorkItemId _workItemId;
        private readonly IHubContext<AgentStdoutHub> _hub;
        private readonly object _lock = new();
        private readonly StringBuilder _pending = new();
        private string _phase = "";
        private Timer? _timer;
        private bool _disposed;

        public WorkItemBatcher(WorkItemId workItemId, IHubContext<AgentStdoutHub> hub)
        {
            _workItemId = workItemId;
            _hub = hub;
        }

        public void Add(string phase, string chunk)
        {
            bool shouldFlush;
            string? pendingToFlush = null;
            lock (_lock)
            {
                if (_disposed) return;
                // Flush immediately on phase change to avoid mixing phases.
                if (_phase != "" && _phase != phase && _pending.Length > 0)
                {
                    pendingToFlush = _pending.ToString();
                    _pending.Clear();
                    _timer?.Dispose();
                    _timer = null;
                }
                _phase = phase;
                _pending.Append(chunk);
                shouldFlush = _pending.Length >= FlushBytes;
                if (!shouldFlush && _timer is null)
                    _timer = new Timer(_ => _ = FlushAsync(), null, FlushInterval, Timeout.InfiniteTimeSpan);
            }

            if (pendingToFlush is not null)
                _ = SendAsync(pendingToFlush, phase);

            if (shouldFlush)
                _ = FlushAsync();
        }

        private async Task FlushAsync()
        {
            string batch;
            string phase;
            lock (_lock)
            {
                if (_pending.Length == 0) return;
                batch = _pending.ToString();
                phase = _phase;
                _pending.Clear();
                _timer?.Dispose();
                _timer = null;
            }
            await SendAsync(batch, phase);
        }

        private async Task SendAsync(string batch, string phase)
        {
            try
            {
                await _hub.Clients.Group($"wi:{_workItemId}").SendAsync(
                    "stdoutChunk", new { workItemId = _workItemId.ToString(), phase, chunk = batch });
            }
            catch { /* fire-and-forget: disconnected clients are expected */ }
        }

        public async Task FlushAndDisposeAsync()
        {
            await FlushAsync();
            Dispose();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
