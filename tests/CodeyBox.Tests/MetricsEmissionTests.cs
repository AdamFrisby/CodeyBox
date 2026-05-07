using System.Diagnostics.Metrics;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that the CodeyBoxMeters instruments emit measurements with the
/// correct tags. Uses the built-in MeterListener API — no OTel SDK required.
/// </summary>
public sealed class MetricsEmissionTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (MeterListener Listener, List<(string Instrument, long Value, string? Tag, string? TagValue)> Measurements)
        CreateLongListener(string meterName, string instrumentName, string? tagKey = null)
    {
        var measurements = new List<(string, long, string?, string?)>();
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
            measurements.Add((instrument.Name, value, tagKey, tagValue));
        });

        listener.Start();
        return (listener, measurements);
    }

    // ── PipelineTransitions ───────────────────────────────────────────────────

    [Fact]
    public void PipelineTransitions_Counter_EmitsWithToStateTag()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Pipeline", "codeybox.work_item.transitions", "to_state");
        using (listener)
        {
            CodeyBoxMeters.PipelineTransitions.Add(1, new KeyValuePair<string, object?>("to_state", "Working"));
            Assert.Contains(measurements, measurement =>
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
            Assert.Contains(measurements, measurement => measurement.TagValue == "Merging");
            Assert.Contains(measurements, measurement => measurement.TagValue == "Done");
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
            Assert.Single(measurements);
            Assert.Equal("passed", measurements[0].TagValue);
        }
    }

    [Fact]
    public void AuditIterations_Counter_EmitsReworkingOutcome()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Audit", "codeybox.audit.iterations", "outcome");
        using (listener)
        {
            CodeyBoxMeters.AuditIterations.Add(1, new KeyValuePair<string, object?>("outcome", "reworking"));
            Assert.Single(measurements);
            Assert.Equal("reworking", measurements[0].TagValue);
        }
    }

    [Fact]
    public void AuditIterations_Counter_EmitsFailedOutcome()
    {
        var (listener, measurements) = CreateLongListener("CodeyBox.Audit", "codeybox.audit.iterations", "outcome");
        using (listener)
        {
            CodeyBoxMeters.AuditIterations.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            Assert.Single(measurements);
            Assert.Equal("failed", measurements[0].TagValue);
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
            Assert.Single(measurements);
            Assert.Equal(3L, measurements[0].Value);
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

            Assert.Single(measurements);
            Assert.Equal(150L, measurements[0].Value);
            Assert.Equal("shell-lint", measurements[0].TagValue);
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

            Assert.Single(measurements);
            Assert.Equal(5000L, measurements[0].Value);
            Assert.Equal("claude", measurements[0].TagValue);
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

            Assert.Single(measurements);
            Assert.Equal("rework", measurements[0].TagValue);
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

            Assert.Equal(2, measurements.Count);
            Assert.Equal("start", measurements[0].TagValue);
            Assert.Equal("clone", measurements[1].TagValue);
        }
    }
}
