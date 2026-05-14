using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests.Uat.CostTelemetryAndStreams;

/// <summary>
/// UAT coverage for timing persistence, aggregate timing endpoints, Activity
/// export tags, and meter emission from the Cost, Telemetry, And Streams section.
/// Plan anchor:
/// docs/uat/00-plan.md#timing-and-opentelemetry-export---records-phase-timings-and-emits-fleet-observability-signals
/// </summary>
public sealed class TimingAndOpenTelemetryUatTests : IDisposable
{
    private readonly CostTelemetryWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task TimingScope_WritesLifecycleRowsAndEndpointExcludesIncompleteRowsFromTotals()
    {
        using var factory = new CostTelemetryApiFactory(
            _workspace.NewDatabasePath(),
            _workspace.NewStreamRoot(),
            CostTelemetryFixtures.Project());
        var item = CostTelemetryFixtures.WorkItem();
        await factory.SeedWorkItemAsync(item);
        await using (var scope = await TimingScope.BeginAsync(
            factory.Timings,
            item.Id,
            "work",
            "agent.exec",
            metadata: new Dictionary<string, object> { ["agent"] = "claude" }))
        {
            Assert.True(scope.ElapsedMs >= 0);
        }

        await factory.Timings.BeginAsync(new TimingRecord
        {
            Id = "uat-inflight",
            WorkItemId = item.Id,
            Phase = "merge",
            Step = "agent.exec",
            StartedAt = DateTimeOffset.Parse("2026-05-14T03:00:00Z"),
            MetadataJson = "{}",
        });

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/workitems/{item.Id}/timings");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("totalDurationMs").GetInt64() >= 0);
        Assert.True(json.GetProperty("byPhase").TryGetProperty("work", out _));
        Assert.True(json.GetProperty("byPhase").TryGetProperty("merge", out var merge));
        Assert.Equal(0, merge.GetProperty("durationMs").GetInt64());
    }

    [Fact]
    public async Task AggregateTimings_ComputesPercentilesAcrossCompletedItems()
    {
        using var factory = new CostTelemetryApiFactory(
            _workspace.NewDatabasePath(),
            _workspace.NewStreamRoot(),
            CostTelemetryFixtures.Project());
        var durations = new[] { 100L, 200L, 300L, 400L, 500L };
        var startedAt = DateTimeOffset.Parse("2026-05-14T04:00:00Z");
        foreach (var duration in durations)
        {
            var item = CostTelemetryFixtures.WorkItem();
            await factory.SeedWorkItemAsync(item);
            var rowId = Guid.NewGuid().ToString("N");
            await factory.Timings.BeginAsync(new TimingRecord
            {
                Id = rowId,
                WorkItemId = item.Id,
                Phase = "work",
                Step = "agent.exec",
                StartedAt = startedAt,
                MetadataJson = "{}",
            });
            await factory.Timings.EndAsync(rowId, startedAt.AddMilliseconds(duration), duration);
        }

        var client = factory.CreateClient();
        var response = await client.GetAsync("/workitems/timings/aggregate");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var agentExec = json.GetProperty("stepStats").EnumerateArray()
            .Single(step => step.GetProperty("step").GetString() == "agent.exec");
        Assert.Equal(5, json.GetProperty("workItemCount").GetInt32());
        Assert.Equal(300, agentExec.GetProperty("medianMs").GetInt64());
        Assert.Equal(400, agentExec.GetProperty("p95Ms").GetInt64());
    }

    [Fact]
    public async Task ActivityExport_IncludesWorkItemPhaseIterationAndMetadataTags()
    {
        using var source = new ActivitySource("CodeyBox.Uat.CostTelemetry." + Guid.NewGuid().ToString("N"));
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => captured = activity,
        };
        ActivitySource.AddActivityListener(listener);
        var item = CostTelemetryFixtures.WorkItem();

        await using (await TimingScope.BeginAsync(
            store: null,
            item.Id,
            phase: "audit",
            step: "auditor.llm",
            iteration: 2,
            metadata: new Dictionary<string, object> { ["model"] = "uat-model" },
            activitySource: source))
        {
        }

        Assert.NotNull(captured);
        Assert.Equal("auditor.llm", captured!.OperationName);
        Assert.Equal(item.Id.ToString(), captured.GetTagItem("codeybox.work_item_id")?.ToString());
        Assert.Equal("audit", captured.GetTagItem("codeybox.phase")?.ToString());
        Assert.Equal("2", captured.GetTagItem("codeybox.iteration")?.ToString());
        Assert.Equal("uat-model", captured.GetTagItem("codeybox.model")?.ToString());
    }

    [Fact]
    public void Metrics_InstrumentsEmitFleetObservabilityMeasurements()
    {
        var measurements = new List<(string Instrument, long Value, string? TagValue)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "CodeyBox.Pipeline" &&
                instrument.Name is "codeybox.work_item.transitions" or "codeybox.agent.duration_ms")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? tagValue = null;
            for (var i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key is "to_state" or "phase")
                    tagValue = tags[i].Value?.ToString();
            }

            measurements.Add((instrument.Name, value, tagValue));
        });
        listener.Start();

        CodeyBoxMeters.PipelineTransitions.Add(1, new KeyValuePair<string, object?>("to_state", "Working"));
        CodeyBoxMeters.AgentDuration.Record(250, new KeyValuePair<string, object?>("phase", "work"));

        Assert.Contains(measurements, m => m.Instrument == "codeybox.work_item.transitions" && m.Value == 1 && m.TagValue == "Working");
        Assert.Contains(measurements, m => m.Instrument == "codeybox.agent.duration_ms" && m.Value == 250 && m.TagValue == "work");
    }
}
