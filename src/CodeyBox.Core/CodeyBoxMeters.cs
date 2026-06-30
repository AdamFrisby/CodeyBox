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
    private static readonly Meter CoordinatorMeter = new("CodeyBox.Coordinator");

    /// <summary>Incremented on every work-item state transition. Tag: <c>to_state</c>.</summary>
    public static readonly Counter<long> PipelineTransitions =
        PipelineMeter.CreateCounter<long>("codeybox.work_item.transitions");

    /// <summary>
    /// Incremented once per code-audit iteration. Tags: <c>outcome</c>
    /// (passed | reworking | failed | needs_operator_input), <c>iteration</c>,
    /// <c>self_review_checklist</c> (on | off), and <c>planned</c> (on | off).
    /// The <c>planned</c> tag lets dashboards compare code-stage audit-iteration
    /// count for PLANNED vs UNPLANNED items — the measurement that proves whether
    /// planning net-reduces code cycles.
    /// </summary>
    public static readonly Counter<long> AuditIterations =
        AuditMeter.CreateCounter<long>("codeybox.audit.iterations");

    /// <summary>
    /// One increment per work item at its FIRST code-audit iteration. Tags:
    /// <c>outcome</c> (passed | failed) and <c>planned</c> (on | off). Unlike
    /// <see cref="SessionFirstAuditOutcome"/> (session-mode only), this is emitted
    /// for every work item, so first-audit pass-rate can be sliced by the planned
    /// cohort to measure whether planning improves the first-pass rate.
    /// </summary>
    public static readonly Counter<long> FirstAuditOutcome =
        AuditMeter.CreateCounter<long>("codeybox.audit.first_audit.outcome", unit: "{audit}");

    /// <summary>
    /// Incremented for empty-rework handling sub-events. Tag:
    /// <c>outcome</c> (<c>detected</c> | <c>escalation_succeeded</c> |
    /// <c>parked</c> | <c>failed</c>).
    /// </summary>
    public static readonly Counter<long> ReworkEmptyEvents =
        AuditMeter.CreateCounter<long>("codeybox.audit.rework_empty.events", unit: "{event}");

    /// <summary>
    /// One increment per pre-emptive self-review turn outcome on a session-
    /// mode work item. Tag: <c>outcome</c> (<c>committed_changes</c> |
    /// <c>no_changes</c> | <c>failed</c> | <c>skipped_empty_guidance</c>).
    /// Used to measure how often the feature actually produced new commits
    /// before the formal audit.
    /// </summary>
    public static readonly Counter<long> SessionPreemptiveSelfReviewTurns =
        AuditMeter.CreateCounter<long>("codeybox.session.preemptive_self_review.turns", unit: "{turn}");

    /// <summary>
    /// Recorded once per session-mode work-item audit completion. Tags:
    /// <c>self_review</c> (<c>on</c> | <c>off</c>) and <c>outcome</c>
    /// (<c>passed</c> | <c>failed</c> | <c>needs_operator_input</c>).
    /// Value is the iteration count consumed before the verdict. Pair with
    /// <see cref="SessionFirstAuditOutcome"/> to measure whether the pre-
    /// emptive self-review pass reduces audit-iteration count.
    /// </summary>
    public static readonly Histogram<long> SessionAuditIterations =
        AuditMeter.CreateHistogram<long>("codeybox.session.audit_iterations", unit: "{iteration}");

    /// <summary>
    /// One increment per first audit iteration on a session-mode work item.
    /// Tags: <c>self_review</c> (<c>on</c> | <c>off</c>) and <c>outcome</c>
    /// (<c>passed</c> | <c>failed</c>). Lets dashboards chart first-audit
    /// pass-rate with vs without the pre-emptive self-review turn.
    /// </summary>
    public static readonly Counter<long> SessionFirstAuditOutcome =
        AuditMeter.CreateCounter<long>("codeybox.session.first_audit.outcome", unit: "{audit}");

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

    /// <summary>Peak guest RAM captured at teardown. Tags: <c>phase</c>, <c>network_profile</c>.</summary>
    public static readonly Histogram<double> SandboxPeakRamMb =
        SandboxMeter.CreateHistogram<double>("codeybox.sandbox.resource.peak_ram_mb", unit: "MB");

    /// <summary>Lifetime-average guest CPU utilization captured at teardown. Tags: <c>phase</c>, <c>network_profile</c>.</summary>
    public static readonly Histogram<double> SandboxAvgCpuPercent =
        SandboxMeter.CreateHistogram<double>("codeybox.sandbox.resource.avg_cpu_pct", unit: "%");

    /// <summary>Cumulative guest network receive bytes captured at teardown, converted to MB. Tags: <c>phase</c>, <c>network_profile</c>.</summary>
    public static readonly Histogram<double> SandboxNetRxMb =
        SandboxMeter.CreateHistogram<double>("codeybox.sandbox.resource.net_rx_mb", unit: "MB");

    /// <summary>Cumulative guest network transmit bytes captured at teardown, converted to MB. Tags: <c>phase</c>, <c>network_profile</c>.</summary>
    public static readonly Histogram<double> SandboxNetTxMb =
        SandboxMeter.CreateHistogram<double>("codeybox.sandbox.resource.net_tx_mb", unit: "MB");

    /// <summary>Remote sandbox placement attempts. Tags: <c>host_id</c>, <c>outcome</c>.</summary>
    public static readonly Counter<long> SandboxRemotePlacements =
        SandboxMeter.CreateCounter<long>("codeybox.sandbox.remote_placement.count", unit: "{placement}");

    /// <summary>Remote sandbox placement deferrals. Tags: <c>reason</c>, <c>network_profile</c>.</summary>
    public static readonly Counter<long> SandboxRemotePlacementDeferrals =
        SandboxMeter.CreateCounter<long>("codeybox.sandbox.remote_placement.deferrals", unit: "{deferral}");

    /// <summary>Runtime remote-host health transitions. Tags: <c>host_id</c>, <c>state</c>.</summary>
    public static readonly Counter<long> SandboxRemoteHostHealthTransitions =
        SandboxMeter.CreateCounter<long>("codeybox.sandbox.remote_host.health_transitions", unit: "{transition}");

    /// <summary>Upstream API call duration. Tags: <c>endpoint</c>, <c>status_code</c>.</summary>
    public static readonly Histogram<long> UpstreamApiCallDuration =
        UpstreamMeter.CreateHistogram<long>("codeybox.upstream.api_call.duration_ms");

    /// <summary>SQLite single-writer gate wait time.</summary>
    public static readonly Histogram<long> CoordinatorSqliteWriteGateWait =
        CoordinatorMeter.CreateHistogram<long>("codeybox.coordinator.sqlite.write_gate.wait_ms", unit: "ms");

    /// <summary>Host-side git command duration. Tags: <c>operation</c>, <c>outcome</c>.</summary>
    public static readonly Histogram<long> CoordinatorGitCommandDuration =
        CoordinatorMeter.CreateHistogram<long>("codeybox.coordinator.git.command.duration_ms", unit: "ms");

    /// <summary>Agent stream capture writer duration. Tags: <c>phase</c>, <c>outcome</c>.</summary>
    public static readonly Histogram<long> CoordinatorAgentStreamCaptureDuration =
        CoordinatorMeter.CreateHistogram<long>("codeybox.coordinator.agent_stream.capture.duration_ms", unit: "ms");

    /// <summary>Agent stream bytes dropped by stream-size caps. Tags: <c>phase</c>, <c>reason</c>.</summary>
    public static readonly Counter<long> CoordinatorAgentStreamDroppedBytes =
        CoordinatorMeter.CreateCounter<long>("codeybox.coordinator.agent_stream.dropped_bytes", unit: "By");

    /// <summary>Incremented once each time a work item is dispatched to a worker.</summary>
    public static readonly Counter<long> Dispatches =
        PipelineMeter.CreateCounter<long>("codeybox.dispatch.count", unit: "{dispatch}");

    /// <summary>
    /// One increment per agent invocation attempt (work / rework / audit / merge /
    /// upstream). Tags: <c>agent.kind</c>, <c>model</c>, <c>agent_class</c>,
    /// <c>phase</c>, <c>outcome</c> (<c>success</c> | <c>error</c> | <c>canceled</c>).
    /// </summary>
    public static readonly Counter<long> AgentInvocations =
        PipelineMeter.CreateCounter<long>("codeybox.agent.invocations", unit: "{invocation}");

    /// <summary>
    /// One increment per agent fallback event (the routed member was swapped for
    /// another, or the class was fully exhausted). Tags: <c>from_agent</c>,
    /// <c>to_agent</c> (<c>(none)</c> when the class exhausted), <c>kind</c>
    /// (<c>quota</c> | <c>auth</c> | <c>timeout</c> |
    /// <c>resume_exhausted</c>), <c>phase</c>.
    /// </summary>
    public static readonly Counter<long> AgentFallbacks =
        PipelineMeter.CreateCounter<long>("codeybox.agent.fallbacks", unit: "{fallback}");

    /// <summary>Whole-phase wall-clock duration. Tag: <c>phase</c> (work | rework | audit | merge | upstream).</summary>
    public static readonly Histogram<long> PhaseDuration =
        PipelineMeter.CreateHistogram<long>("codeybox.phase.duration_ms", unit: "ms");

    /// <summary>
    /// Tokens consumed by an agent run, summed as they are recorded to the cost
    /// store. Tags: <c>agent.kind</c>, <c>model</c>, <c>token_type</c>
    /// (<c>input</c> | <c>cached_input</c> | <c>output</c>).
    /// </summary>
    public static readonly Counter<long> AgentTokens =
        PipelineMeter.CreateCounter<long>("codeybox.agent.tokens", unit: "{token}");

    /// <summary>
    /// Estimated USD cost of agent runs, summed as recorded to the cost store.
    /// Tags: <c>agent.kind</c>, <c>model</c>. Aligns with the per-work-item cost
    /// rows so dashboards do not double-count.
    /// </summary>
    public static readonly Counter<double> AgentCostUsd =
        PipelineMeter.CreateCounter<double>("codeybox.agent.cost_usd", unit: "USD");

    /// <summary>
    /// Webhook delivery attempts that reached a terminal outcome. Tags:
    /// <c>endpoint</c>, <c>event</c>, <c>outcome</c> (<c>delivered</c> | <c>failed</c>).
    /// </summary>
    public static readonly Counter<long> WebhookDeliveries =
        PipelineMeter.CreateCounter<long>("codeybox.webhook.deliveries", unit: "{delivery}");

    /// <summary>
    /// Registers an observable gauge on the <c>CodeyBox.Pipeline</c> meter. The
    /// returned instrument must be kept alive by the caller (the SDK holds only a
    /// weak reference); store it in a long-lived field. The callback runs only
    /// while a MeterProvider is collecting, so registration is free when OTel is
    /// disabled.
    /// </summary>
    public static ObservableGauge<T> CreatePipelineObservableGauge<T>(
        string name, Func<IEnumerable<Measurement<T>>> observe,
        string? unit = null, string? description = null) where T : struct =>
        PipelineMeter.CreateObservableGauge(name, observe, unit, description);

    /// <summary>Registers an observable gauge on the <c>CodeyBox.Sandbox</c> meter. See <see cref="CreatePipelineObservableGauge{T}"/>.</summary>
    public static ObservableGauge<T> CreateSandboxObservableGauge<T>(
        string name, Func<IEnumerable<Measurement<T>>> observe,
        string? unit = null, string? description = null) where T : struct =>
        SandboxMeter.CreateObservableGauge(name, observe, unit, description);
}
