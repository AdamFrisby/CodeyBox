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
    /// supplied, a matching screenshot may return early, but implementations
    /// may still return a stable non-matching frame so the caller can run a
    /// richer assertion verifier and report a precise mismatch. Returns the
    /// selected screenshot on success, or null when the wait timed out.
    /// </summary>
    Task<byte[]?> WaitAsync(
        ISandbox sandbox,
        Func<byte[], bool>? predicate,
        ReplayOptions options,
        CancellationToken ct);
}
