using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Observational synchronisation primitive: polls screenshots (and
/// optionally the accessibility tree) until the screen has either "settled"
/// — successive frames are pixel-identical — or matches a caller-supplied
/// predicate. Replaces DOM-ready / event-based waits so the engine
/// generalises to canvas / 3D targets that have no event surface.
///
/// <para>The engine calls this after every input action to absorb
/// loading-time jitter (animations, spinners) before re-locating the next
/// target or evaluating an assertion. A timeout surfaces as
/// <see cref="ReplayFailureKind.WaitTimeout"/>.</para>
/// </summary>
public interface IVisualWait
{
    /// <summary>
    /// Wait for the screen to settle. When <paramref name="predicate"/> is
    /// supplied, it is the expected-state gate: implementations should return
    /// only a matching screenshot, or null when the expected state never
    /// appears before timeout. Without a predicate, a stable screenshot is a
    /// successful wait result.
    /// </summary>
    Task<byte[]?> WaitAsync(
        ISandbox sandbox,
        Func<byte[], CancellationToken, Task<bool>>? predicate,
        ReplayOptions options,
        CancellationToken ct);
}
