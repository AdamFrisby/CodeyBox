using System.Collections.Concurrent;

namespace CodeyBox.Orchestrator;

public sealed class E2eRunCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running =
        new(StringComparer.Ordinal);

    public CancellationTokenSource Register(string runId)
    {
        var cts = new CancellationTokenSource();
        if (_running.TryAdd(runId, cts))
            return cts;

        cts.Dispose();
        throw new InvalidOperationException($"E2E run '{runId}' is already registered as running.");
    }

    public bool Cancel(string runId)
    {
        if (!_running.TryGetValue(runId, out var cts))
            return false;

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Unregister(string runId, CancellationTokenSource cts)
    {
        _running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(runId, cts));
        cts.Dispose();
    }
}
