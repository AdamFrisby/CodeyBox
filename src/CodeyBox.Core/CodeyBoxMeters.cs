using System.Diagnostics.Metrics;

namespace CodeyBox.Core;

/// <summary>
/// Shared Meter and instrument instances. Meters are always created; the OTel
/// SDK discards measurements when no MeterProvider is registered (zero overhead
/// when OTel is disabled). All instrument names follow the <c>codeybox.*</c>
/// namespace convention.
/// </summary>
public static class CodeyBoxMeters
{
    private static readonly Meter PipelineMeter = new("CodeyBox.Pipeline");
    private static readonly Meter SandboxMeter = new("CodeyBox.Sandbox");
    private static readonly Meter AuditMeter = new("CodeyBox.Audit");
    private static readonly Meter UpstreamMeter = new("CodeyBox.Upstream");

    /// <summary>Incremented on every work-item state transition. Tag: <c>to_state</c>.</summary>
    public static readonly Counter<long> PipelineTransitions =
        PipelineMeter.CreateCounter<long>("codeybox.work_item.transitions");

    /// <summary>Incremented once per audit iteration. Tag: <c>outcome</c> (passed | reworking | failed).</summary>
    public static readonly Counter<long> AuditIterations =
        AuditMeter.CreateCounter<long>("codeybox.audit.iterations");

    /// <summary>Blocking-finding count per audit iteration.</summary>
    public static readonly Histogram<long> AuditBlockingFindings =
        AuditMeter.CreateHistogram<long>("codeybox.audit.findings.blocking");

    /// <summary>Per-auditor wall-clock duration. Tags: <c>auditor.name</c>, <c>auditor.kind</c>, <c>iteration</c>.</summary>
    public static readonly Histogram<long> AuditorDuration =
        AuditMeter.CreateHistogram<long>("codeybox.auditor.duration_ms");

    /// <summary>Agent execution duration. Tags: <c>agent.kind</c>, <c>phase</c>.</summary>
    public static readonly Histogram<long> AgentDuration =
        PipelineMeter.CreateHistogram<long>("codeybox.agent.duration_ms");

    /// <summary>Sandbox lifecycle step duration. Tag: <c>step</c> (start | clone).</summary>
    public static readonly Histogram<long> SandboxLifecycle =
        SandboxMeter.CreateHistogram<long>("codeybox.sandbox.lifecycle.duration_ms");

    /// <summary>Upstream API call duration. Tags: <c>endpoint</c>, <c>status_code</c>.</summary>
    public static readonly Histogram<long> UpstreamApiCallDuration =
        UpstreamMeter.CreateHistogram<long>("codeybox.upstream.api_call.duration_ms");
}
