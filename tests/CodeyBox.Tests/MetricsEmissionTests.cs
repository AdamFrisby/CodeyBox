using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using CodeyBox.Core;

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

    // ── Dispatch counter ──────────────────────────────────────────────────────

    [Fact]
    public void Dispatches_Counter_Emits()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Pipeline", "codeybox.dispatch.count");
        using (listener)
        {
            CodeyBoxMeters.Dispatches.Add(1);
            AssertEventuallyContains(measurements, m => m.Value == 1L);
        }
    }

    // ── AgentInvocations ──────────────────────────────────────────────────────

    [Fact]
    public void AgentInvocations_Counter_EmitsWithOutcomeTag()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Pipeline", "codeybox.agent.invocations", "outcome");
        using (listener)
        {
            CodeyBoxMeters.AgentInvocations.Add(1,
                new KeyValuePair<string, object?>("agent.kind", "claude"),
                new KeyValuePair<string, object?>("model", "claude-opus-4-8"),
                new KeyValuePair<string, object?>("agent_class", "default"),
                new KeyValuePair<string, object?>("phase", "work"),
                new KeyValuePair<string, object?>("outcome", "success"));
            AssertEventuallyContains(measurements, m => m.Value == 1L && m.TagValue == "success");
        }
    }

    // ── AgentFallbacks ────────────────────────────────────────────────────────

    [Fact]
    public void AgentFallbacks_Counter_EmitsWithKindTag()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Pipeline", "codeybox.agent.fallbacks", "kind");
        using (listener)
        {
            CodeyBoxMeters.AgentFallbacks.Add(1,
                new KeyValuePair<string, object?>("from_agent", "claude"),
                new KeyValuePair<string, object?>("to_agent", "codex"),
                new KeyValuePair<string, object?>("kind", "quota"),
                new KeyValuePair<string, object?>("phase", "work"));
            AssertEventuallyContains(measurements, m => m.Value == 1L && m.TagValue == "quota");
        }
    }

    // ── PhaseDuration ─────────────────────────────────────────────────────────

    [Fact]
    public void PhaseDuration_Histogram_EmitsWithPhaseTag()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Pipeline", "codeybox.phase.duration_ms", "phase");
        using (listener)
        {
            CodeyBoxMeters.PhaseDuration.Record(1234, new KeyValuePair<string, object?>("phase", "merge"));
            AssertEventuallyContains(measurements, m => m.Value == 1234L && m.TagValue == "merge");
        }
    }

    // ── AgentTokens ───────────────────────────────────────────────────────────

    [Fact]
    public void AgentTokens_Counter_EmitsPerTokenType()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Pipeline", "codeybox.agent.tokens", "token_type");
        using (listener)
        {
            CodeyBoxMeters.AgentTokens.Add(100,
                new KeyValuePair<string, object?>("agent.kind", "claude"),
                new KeyValuePair<string, object?>("model", "claude-opus-4-8"),
                new KeyValuePair<string, object?>("token_type", "input"));
            CodeyBoxMeters.AgentTokens.Add(50,
                new KeyValuePair<string, object?>("agent.kind", "claude"),
                new KeyValuePair<string, object?>("model", "claude-opus-4-8"),
                new KeyValuePair<string, object?>("token_type", "output"));
            AssertEventuallyContains(measurements, m => m.Value == 100L && m.TagValue == "input");
            AssertEventuallyContains(measurements, m => m.Value == 50L && m.TagValue == "output");
        }
    }

    // ── AgentCostUsd ──────────────────────────────────────────────────────────

    [Fact]
    public void AgentCostUsd_Counter_EmitsDoubleMeasurement()
    {
        var observed = new ConcurrentQueue<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "CodeyBox.Pipeline" && instrument.Name == "codeybox.agent.cost_usd")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => observed.Enqueue(value));
        listener.Start();

        CodeyBoxMeters.AgentCostUsd.Add(1.25,
            new KeyValuePair<string, object?>("agent.kind", "claude"),
            new KeyValuePair<string, object?>("model", "claude-opus-4-8"));

        var found = SpinWait.SpinUntil(() => observed.ToArray().Any(v => Math.Abs(v - 1.25) < 1e-9), TimeSpan.FromSeconds(2));
        Assert.True(found, "Expected cost measurement was not observed.");
    }

    // ── WebhookDeliveries ─────────────────────────────────────────────────────

    [Fact]
    public void WebhookDeliveries_Counter_EmitsWithOutcomeTag()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Pipeline", "codeybox.webhook.deliveries", "outcome");
        using (listener)
        {
            CodeyBoxMeters.WebhookDeliveries.Add(1,
                new KeyValuePair<string, object?>("endpoint", "tracker"),
                new KeyValuePair<string, object?>("event", "work_item.done"),
                new KeyValuePair<string, object?>("outcome", "delivered"));
            AssertEventuallyContains(measurements, m => m.Value == 1L && m.TagValue == "delivered");
        }
    }
}
