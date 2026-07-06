using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Re-locates an interaction target on the current screen by recognising
/// its recorded descriptor. The replay engine never trusts the recorded raw
/// coordinates: it asks the locator for a fresh hit, then drives real input
/// to the located rectangle's centre.
///
/// <para>Implementations may consult the accessibility tree, OCR, or a
/// visual template match — whatever signal the descriptor carries. Return
/// null when nothing matches; the engine surfaces this as
/// <see cref="ReplayFailureKind.NotFound"/>.</para>
/// </summary>
public interface IElementLocator
{
    /// <summary>
    /// Attempt to re-locate the target described by <paramref name="descriptor"/>
    /// on <paramref name="sandbox"/>. Return null when no match is found.
    /// </summary>
    Task<LocatedTarget?> LocateAsync(
        ISandbox sandbox,
        TraceTargetDescriptor descriptor,
        ReplayOptions options,
        CancellationToken ct);
}
