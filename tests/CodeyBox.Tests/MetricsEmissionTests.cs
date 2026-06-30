using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that the CodeyBoxMeters instruments emit measurements with the
/// correct tags. Uses the built-in MeterListener API — no OTel SDK required.
/// </summary>
public sealed class MetricsEmissionTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (MeterListener Listener, ConcurrentQueue<(string Instrument, long Value, string? Tag, string? TagValue)> Measurements)
        CreateLongListener(string meterName, string instrumentName, string? tagKey = null)
    {
        var measurements = new ConcurrentQueue<(string, long, string?, string?)>();
        var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == meterName && instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? tagValue = null;
            if (tagKey is not null)
            {
                for (var i = 0; i < tags.Length; i++)
                    if (tags[i].Key == tagKey) tagValue = tags[i].Value?.ToString();
            }
            measurements.Enqueue((instrument.Name, value, tagKey, tagValue));
        });

        listener.Start();
        return (listener, measurements);
    }

    private static void AssertEventuallyContains(
        ConcurrentQueue<(string Instrument, long Value, string? Tag, string? TagValue)> measurements,
        Func<(string Instrument, long Value, string? Tag, string? TagValue), bool> predicate)
    {
        var found = SpinWait.SpinUntil(
            () => measurements.ToArray().Any(predicate),
            TimeSpan.FromSeconds(2));
        Assert.True(found, "Expected metric measurement was not observed.");
    }

    // ── PipelineTransitions ───────────────────────────────────────────────────

    [Fact]
    public void PipelineTransitions_Counter_EmitsWithToStateTag()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Pipeline", "codeybox.work_item.transitions", "to_state");
        using (listener)
        {
            CodeyBoxMeters.PipelineTransitions.Add(1, new KeyValuePair<string, object?>("to_state", "Working"));
            AssertEventuallyContains(measurements, measurement =>
                measurement.Value == 1L && measurement.TagValue == "Working");
        }
    }

    [Fact]
    public void PipelineTransitions_Counter_EmitsDifferentStates()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Pipeline", "codeybox.work_item.transitions", "to_state");
        using (listener)
        {
            CodeyBoxMeters.PipelineTransitions.Add(1, new KeyValuePair<string, object?>("to_state", "Merging"));
            CodeyBoxMeters.PipelineTransitions.Add(1, new KeyValuePair<string, object?>("to_state", "Done"));
            AssertEventuallyContains(measurements, measurement => measurement.TagValue == "Merging");
            AssertEventuallyContains(measurements, measurement => measurement.TagValue == "Done");
        }
    }

    // ── AuditIterations ───────────────────────────────────────────────────────

    [Fact]
    public void AuditIterations_Counter_EmitsPassedOutcome()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Audit", "codeybox.audit.iterations", "outcome");
        using (listener)
        {
            CodeyBoxMeters.AuditIterations.Add(1, new KeyValuePair<string, object?>("outcome", "passed"));
            AssertEventuallyContains(measurements, measurement =>
                measurement.Value == 1L && measurement.TagValue == "passed");
        }
    }

    [Fact]
    public void AuditIterations_Counter_EmitsReworkingOutcome()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Audit", "codeybox.audit.iterations", "outcome");
        using (listener)
        {
            CodeyBoxMeters.AuditIterations.Add(1, new KeyValuePair<string, object?>("outcome", "reworking"));
            AssertEventuallyContains(measurements, measurement =>
                measurement.Value == 1L && measurement.TagValue == "reworking");
        }
    }

    [Fact]
    public void AuditIterations_Counter_EmitsFailedOutcome()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Audit", "codeybox.audit.iterations", "outcome");
        using (listener)
        {
            CodeyBoxMeters.AuditIterations.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            AssertEventuallyContains(measurements, measurement =>
                measurement.Value == 1L && measurement.TagValue == "failed");
        }
    }

    [Fact]
    public void ReworkEmptyEvents_Counter_EmitsParkedOutcome()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Audit", "codeybox.audit.rework_empty.events", "outcome");
        using (listener)
        {
            CodeyBoxMeters.ReworkEmptyEvents.Add(1, new KeyValuePair<string, object?>("outcome", "parked"));
            AssertEventuallyContains(measurements, measurement =>
                measurement.Value == 1L && measurement.TagValue == "parked");
        }
    }

    // ── AuditBlockingFindings ─────────────────────────────────────────────────

    [Fact]
    public void AuditBlockingFindings_Histogram_EmitsCount()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Audit", "codeybox.audit.findings.blocking");
        using (listener)
        {
            CodeyBoxMeters.AuditBlockingFindings.Record(3, new KeyValuePair<string, object?>("iteration", "1"));
            AssertEventuallyContains(measurements, measurement => measurement.Value == 3L);
        }
    }

    // ── AuditorDuration ───────────────────────────────────────────────────────

    [Fact]
    public void AuditorDuration_Histogram_EmitsWithExpectedTags()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Audit", "codeybox.auditor.duration_ms", "auditor.name");
        using (listener)
        {
            CodeyBoxMeters.AuditorDuration.Record(150,
                new KeyValuePair<string, object?>("auditor.name", "shell-lint"),
                new KeyValuePair<string, object?>("auditor.kind", "shell"),
                new KeyValuePair<string, object?>("iteration", "1"));

            AssertEventuallyContains(measurements, measurement =>
                measurement.Value == 150L && measurement.TagValue == "shell-lint");
        }
    }

    // ── AgentDuration ─────────────────────────────────────────────────────────

    [Fact]
    public void AgentDuration_Histogram_EmitsWithAgentKindTag()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Pipeline", "codeybox.agent.duration_ms", "agent.kind");
        using (listener)
        {
            CodeyBoxMeters.AgentDuration.Record(5000,
                new KeyValuePair<string, object?>("agent.kind", "claude"),
                new KeyValuePair<string, object?>("phase", "work"));

            AssertEventuallyContains(measurements, measurement =>
                measurement.Value == 5000L && measurement.TagValue == "claude");
        }
    }

    [Fact]
    public void AgentDuration_Histogram_EmitsWithPhaseTag()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Pipeline", "codeybox.agent.duration_ms", "phase");
        using (listener)
        {
            CodeyBoxMeters.AgentDuration.Record(1200,
                new KeyValuePair<string, object?>("agent.kind", "claude"),
                new KeyValuePair<string, object?>("phase", "rework"));

            AssertEventuallyContains(measurements, measurement =>
                measurement.Value == 1200L && measurement.TagValue == "rework");
        }
    }

    // ── SandboxLifecycle ──────────────────────────────────────────────────────

    [Fact]
    public void SandboxLifecycle_Histogram_EmitsWithStepTag()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Sandbox", "codeybox.sandbox.lifecycle.duration_ms", "step");
        using (listener)
        {
            CodeyBoxMeters.SandboxLifecycle.Record(800, new KeyValuePair<string, object?>("step", "start"));
            CodeyBoxMeters.SandboxLifecycle.Record(200, new KeyValuePair<string, object?>("step", "clone"));

            AssertEventuallyContains(measurements, measurement =>
                measurement.Value == 800L && measurement.TagValue == "start");
            AssertEventuallyContains(measurements, measurement =>
                measurement.Value == 200L && measurement.TagValue == "clone");
        }
    }

    [Fact]
    public async Task CoordinatorSqliteWriteGateWait_EmitsFromGateWait()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cb-metrics-{Guid.NewGuid():N}.db");
        var (listener, measurements) = CreateLongListener(
            "CodeyBox.Coordinator",
            "codeybox.coordinator.sqlite.write_gate.wait_ms",
            "outcome");
        using (listener)
        using (var gate1 = SqliteDatabaseWriteGate.ForPath(path))
        using (var gate2 = SqliteDatabaseWriteGate.ForPath(path))
        {
            gate1.Wait();
            var waiter = Task.Run(async () =>
            {
                await gate2.WaitAsync();
                gate2.Release();
            });

            await Task.Delay(25);
            gate1.Release();
            await waiter;

            AssertEventuallyContains(measurements, measurement =>
                measurement.TagValue == "acquired");
        }
    }

    [Fact]
    public async Task CoordinatorAgentStreamCaptureDuration_EmitsFromCaptureDispose()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cb-stream-metrics-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "agent.log");
        var (listener, measurements) = CreateLongListener(
            "CodeyBox.Coordinator",
            "codeybox.coordinator.agent_stream.capture.duration_ms",
            "phase");
        using (listener)
        {
            var capture = new AgentStreamCapture(path, maxBytes: 1024 * 1024, phase: "work", NullLogger.Instance);
            capture.WriteChunk("hello\n");
            await capture.DisposeAsync();

            AssertEventuallyContains(measurements, measurement =>
                measurement.TagValue == "work");
        }

        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void CoordinatorAgentStreamBackpressureWait_Histogram_EmitsWithOutcomeTag()
    {
        var (listener, measurements) = CreateLongListener(
            "CodeyBox.Coordinator",
            "codeybox.coordinator.agent_stream.backpressure.wait_ms",
            "outcome");
        using (listener)
        {
            CodeyBoxMeters.CoordinatorAgentStreamBackpressureWait.Record(
                12,
                new KeyValuePair<string, object?>("phase", "work"),
                new KeyValuePair<string, object?>("outcome", "ready"));

            AssertEventuallyContains(measurements, measurement =>
                measurement.Value == 12L && measurement.TagValue == "ready");
        }
    }

    // NOTE: codeybox.dispatch.count, codeybox.agent.invocations,
    // codeybox.agent.fallbacks, codeybox.phase.duration_ms, codeybox.agent.tokens,
    // codeybox.agent.cost_usd, and codeybox.webhook.deliveries are intentionally
    // NOT asserted here by calling the static instrument directly — that would
    // only verify MeterListener plumbing. They are instead covered by
    // operation-driven tests that drive the real production call sites:
    //   - OrchestratorPerAgentConcurrencyTests.Dispatch_EmitsDispatchCountMeasurement
    //   - PipelineRunnerTimingTests.SuccessfulRun_EmitsPipelineSpansAndInvocationMetrics
    //   - PipelineRunnerQuotaFallbackTests.Codex_HitsQuota_FallsBackToClaude_EmitsFallbackAndInvocationMetrics
    //   - PipelineRunnerQuotaFallbackTests.AuditDrivenRework_EmitsReworkPhaseSpanAndDuration
    //   - PipelineRunnerCostCaptureTests.SuccessfulRun_EmitsTokenAndCostCounters
    //   - WebhookDispatcherTests.SuccessfulDelivery_EmitsDeliveredMeasurement / GivingUpAfterMaxAttempts_EmitsFailedMeasurement
    // The instrument-shape tests retained above cover instruments whose emission
    // is already asserted through their own subsystem suites.
}
