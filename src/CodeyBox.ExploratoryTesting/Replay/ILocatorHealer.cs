using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Optional vision-assisted self-heal seam for the replay engine. When a
/// step fails with <see cref="ReplayFailureKind.NotFound"/> and the engine
/// has been configured with a healer, the engine calls
/// <see cref="HealAsync"/> once per failing step to give the healer a chance
/// to re-locate the target by richer means (e.g. an LLM vision model) and
/// rewrite the trace artefact for future replays.
///
/// <para><b>Out of scope for this item:</b> there is intentionally no
/// shipped implementation. The seam exists so the engine can adopt one
/// later without a behavioural rewrite. Until a real healer ships, the
/// engine FAILS deterministically on locator misses — the brief calls this
/// out explicitly.</para>
/// </summary>
public interface ILocatorHealer
{
    /// <summary>
    /// Attempt to re-locate the target by a richer signal than the default
    /// locator was able to use. Return null when no heal is possible — the
    /// engine will surface the original
    /// <see cref="ReplayFailureKind.NotFound"/>.
    /// </summary>
    Task<LocatedTarget?> HealAsync(
        ISandbox sandbox,
        TraceEntry entry,
        ReplayOptions options,
        CancellationToken ct);
}
