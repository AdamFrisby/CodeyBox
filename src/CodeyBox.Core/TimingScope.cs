using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Core;

/// <summary>
/// Lightweight RAII wrapper that writes a timing row on Begin and updates it on Dispose.
/// Failures in either direction are swallowed (with a warning) so timing instrumentation
/// never aborts a pipeline phase. Use <see cref="BeginAsync"/> as the factory.
/// </summary>
public sealed class TimingScope : IAsyncDisposable
{
    private readonly ITimingStore? _store;
    private readonly string _id;
    private readonly Stopwatch _sw;
    private readonly DateTimeOffset _startedAt;
    private readonly ILogger? _log;
    private readonly Activity? _activity;
    private bool _disposed;

    private TimingScope(ITimingStore? store, string id, Stopwatch sw, DateTimeOffset startedAt, ILogger? log, Activity? activity)
    {
        _store = store;
        _id = id;
        _sw = sw;
        _startedAt = startedAt;
        _log = log;
        _activity = activity;
    }

    /// <summary>
    /// Creates a timing scope and writes the begin row to the store.
    /// Returns a no-op scope when <paramref name="store"/> is null.
    /// Never throws — failures are logged as warnings.
    /// When <paramref name="activitySource"/> is provided and a listener is registered,
    /// an OTel <see cref="Activity"/> is started and stopped on disposal.
    /// </summary>
    public static async Task<TimingScope> BeginAsync(
        ITimingStore? store,
        WorkItemId itemId,
        string phase,
        string step,
        int? iteration = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        ILogger? log = null,
        ActivitySource? activitySource = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var activity = activitySource?.StartActivity(step, ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("codeybox.work_item_id", itemId.ToString());
            activity.SetTag("codeybox.phase", phase);
            if (iteration is not null)
                activity.SetTag("codeybox.iteration", iteration.Value.ToString());
            if (metadata is not null)
                foreach (var (k, v) in metadata)
                    activity.SetTag($"codeybox.{k}", v.ToString());
        }

        if (store is null)
            return new TimingScope(null, id, sw, startedAt, log, activity);

        var metaJson = metadata is not null
            ? JsonSerializer.Serialize(metadata)
            : "{}";

        var record = new TimingRecord
        {
            Id = id,
            WorkItemId = itemId,
            Phase = phase,
            Iteration = iteration,
            Step = step,
            StartedAt = startedAt,
            MetadataJson = metaJson,
        };

        try
        {
            await store.BeginAsync(record, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Timing: failed to write begin for {Step}", step);
            return new TimingScope(null, id, sw, startedAt, log, activity);
        }

        return new TimingScope(store, id, sw, startedAt, log, activity);
    }

    /// <summary>Elapsed milliseconds as of the last Stop (after DisposeAsync) or current tick.</summary>
    public long ElapsedMs => (long)_sw.Elapsed.TotalMilliseconds;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _sw.Stop();

        _activity?.Dispose();

        if (_store is null) return;

        var endedAt = _startedAt.AddMilliseconds(_sw.Elapsed.TotalMilliseconds);
        var durationMs = (long)_sw.Elapsed.TotalMilliseconds;

        try
        {
            await _store.EndAsync(_id, endedAt, durationMs, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Timing: failed to write end for id {Id}", _id);
        }
    }
}
