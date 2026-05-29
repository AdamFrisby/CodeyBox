using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CodeyBox.Tests;

/// <summary>
/// Captures measurements emitted by named <see cref="System.Diagnostics.Metrics"/>
/// instruments via the in-box <see cref="MeterListener"/>. Used by
/// operation-driven tests that drive real production code paths and then assert
/// the expected instrument fired with the expected tags — as opposed to calling
/// the static instrument directly.
/// </summary>
internal sealed class MetricCapture : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ConcurrentQueue<(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)> _items = new();

    public MetricCapture(params string[] instrumentNames)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrumentNames.Contains(instrument.Name))
                    l.EnableMeasurementEvents(instrument);
            },
        };
        _listener.SetMeasurementEventCallback<long>((inst, v, tags, _) => _items.Enqueue((inst.Name, v, tags.ToArray())));
        _listener.SetMeasurementEventCallback<double>((inst, v, tags, _) => _items.Enqueue((inst.Name, v, tags.ToArray())));
        _listener.Start();
    }

    public IReadOnlyList<(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)> Items => _items.ToArray();

    public bool Any(string instrument, params (string Key, string Value)[] tags) =>
        Items.Any(m => m.Instrument == instrument && tags.All(t => TagEquals(m.Tags, t.Key, t.Value)));

    private static bool TagEquals(KeyValuePair<string, object?>[] tags, string key, string value)
        => tags.Any(t => t.Key == key && string.Equals(t.Value?.ToString(), value, StringComparison.Ordinal));

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// Captures spans emitted by named <see cref="ActivitySource"/> instances. The
/// listener must be live before the traced operation runs (an ActivitySource
/// produces no Activity unless a listener is sampling), so construct it inside
/// the test before invoking the pipeline.
/// </summary>
internal sealed class SpanCapture : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _spans = new();

    public SpanCapture(params string[] sourceNames)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => sourceNames.Contains(s.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => _spans.Enqueue(a),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Spans => _spans.ToArray();

    public bool Any(string operationName, params (string Key, string Value)[] tags) =>
        Spans.Any(a => a.OperationName == operationName
            && tags.All(t => string.Equals(a.GetTagItem(t.Key)?.ToString(), t.Value, StringComparison.Ordinal)));

    public void Dispose() => _listener.Dispose();
}
