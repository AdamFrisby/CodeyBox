namespace CodeyBox.Orchestrator;

internal static class HostedLifecycleTask
{
    public static async Task StopAsync(
        Func<(CancellationTokenSource? Cts, Task? Task)> snapshot,
        Func<CancellationTokenSource?, CancellationTokenSource?> detachCurrentCts,
        CancellationToken ct)
    {
        var (cts, task) = snapshot();

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        if (task is not null)
        {
            try { await task.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        if (task is null || task.IsCompleted)
            detachCurrentCts(cts)?.Dispose();
    }
}
