namespace CodeyBox.Core;

/// <summary>
/// Periodic telemetry sampler. The orchestrator host runs every registered
/// <see cref="IMetricSampler"/> on its own loop, asking the sampler when to
/// fire (<see cref="Interval"/>) and whether to fire (<see cref="Enabled"/>)
/// each tick — both are re-read every cycle so plugins can honour hot-reload
/// without restarting the host.
///
/// <para>A sampler owns its own persistence. The host gives it a logger and a
/// scoped configuration section via <c>IPluginInitializer</c>; what it does
/// with the sampled value (SQLite row, OTLP metric, in-memory ring buffer …)
/// is up to the sampler.</para>
///
/// <para>Samplers are discovered through the same plugin loader as auditors
/// and credential providers — there is no separate registry. A plugin class
/// decorated with <c>[CodeyBoxPlugin]</c> that implements
/// <see cref="IMetricSampler"/> is registered as a singleton under this
/// interface and the orchestrator's <c>MetricSamplerHost</c> picks it up via
/// the standard <c>IEnumerable&lt;IMetricSampler&gt;</c> injection pattern.</para>
///
/// <para>Implementations MUST be safe to invoke concurrently with their own
/// <see cref="Enabled"/> / <see cref="Interval"/> reads. The host serialises
/// <see cref="SampleOnceAsync"/> calls for the same sampler instance, but
/// <see cref="Enabled"/> / <see cref="Interval"/> may be read at any time.</para>
/// </summary>
public interface IMetricSampler
{
    /// <summary>
    /// Stable identifier used in logs, audit events, and as the registry key
    /// when surfacing per-sampler stats. Lowercase, dot-separated. Example:
    /// <c>"quota"</c>, <c>"work-item-throughput"</c>.
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// Desired delay between sampling attempts. Re-read each cycle, so a
    /// sampler may change cadence at runtime by reflecting hot-reloaded
    /// configuration. A non-positive value is treated by the host as "disable
    /// the loop" — the sampler will not fire until a positive value is
    /// returned.
    /// </summary>
    TimeSpan Interval { get; }

    /// <summary>
    /// Whether sampling is currently enabled. Re-read each cycle: a sampler
    /// disabled at runtime stops firing immediately but its loop remains alive
    /// so it can re-arm without a host restart.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Capture one sample and persist it. The host invokes this method at
    /// most once per <see cref="Interval"/> tick. Failures should be logged
    /// internally and swallowed; throwing here causes the host to log a
    /// warning and skip to the next tick.
    /// </summary>
    Task SampleOnceAsync(CancellationToken ct);
}
