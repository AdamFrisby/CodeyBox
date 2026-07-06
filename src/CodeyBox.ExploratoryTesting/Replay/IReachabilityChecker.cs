using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Verifies that a re-located target is genuinely user-reachable on the
/// current screen — in the viewport, visible, and top-most at the intended
/// click point. May scroll like a human to bring an off-screen target into
/// view, then re-locate via the locator (not by stale-coordinate arithmetic).
/// Returns a structured outcome so the engine can surface precise failure
/// reasons (off-screen vs. occluded).
///
/// <para>This is its own seam (not folded into the locator) because
/// reachability is genuinely a separate failure class — an element can be
/// recognised perfectly and still be unclickable (covered by a modal, off
/// the visible area of an infinite scroll, etc.). Those are real bugs the
/// engine must surface, not noise to swallow.</para>
/// </summary>
public interface IReachabilityChecker
{
    /// <summary>
    /// Verify that <paramref name="target"/> is genuinely user-reachable.
    /// Scrolls and re-locates via the original <paramref name="descriptor"/>
    /// when the target is off-screen; the recorded raw coordinates are not
    /// trusted, recognition is.
    /// </summary>
    Task<ReachabilityOutcome> EnsureReachableAsync(
        ISandbox sandbox,
        LocatedTarget target,
        TraceTargetDescriptor descriptor,
        ReplayOptions options,
        CancellationToken ct);

    /// <summary>
    /// Recovery for the case where the locator found NOTHING for a visual-only
    /// descriptor whose recorded click point is off-viewport: scroll toward the
    /// recorded region like a human, then re-locate. This is the same scroll
    /// orchestrator as <see cref="EnsureReachableAsync"/>'s off-viewport loop —
    /// it lives behind this seam (not in the engine) so there is ONE owner of
    /// scroll-and-relocate. Returns <see cref="VisualMissScrollOutcome.Skipped"/>
    /// when the descriptor doesn't qualify (has an accessibility signal, no
    /// visual signal, or its recorded point is already on screen), a
    /// <see cref="VisualMissScrollOutcome.Found"/> with the re-located target,
    /// or a <see cref="VisualMissScrollOutcome.Failed"/> categorical failure.
    /// </summary>
    Task<VisualMissScrollOutcome> TryScrollOffscreenVisualMissIntoViewAsync(
        ISandbox sandbox,
        TraceTargetDescriptor descriptor,
        ReplayOptions options,
        CancellationToken ct);
}

/// <summary>
/// Outcome of <see cref="IReachabilityChecker.TryScrollOffscreenVisualMissIntoViewAsync"/>.
/// Exactly one of the three shapes: skipped (descriptor didn't qualify),
/// found (re-located after scrolling), or failed (categorical failure the
/// engine surfaces verbatim).
/// </summary>
public sealed record VisualMissScrollOutcome
{
    public LocatedTarget? Target { get; init; }
    public ReplayFailureKind? FailureKind { get; init; }
    public string? Diagnostic { get; init; }

    public static VisualMissScrollOutcome Skipped { get; } = new();
    public static VisualMissScrollOutcome Found(LocatedTarget target) => new() { Target = target };
    public static VisualMissScrollOutcome Failed(ReplayFailureKind kind, string diagnostic)
        => new() { FailureKind = kind, Diagnostic = diagnostic };
}

/// <summary>
/// Verdict from <see cref="IReachabilityChecker"/>. <see cref="Status"/> is
/// the categorical outcome; <see cref="Target"/> is the (possibly updated)
/// target the engine should act on when status is <see cref="ReachabilityStatus.Reachable"/>
/// — scrolling can move the located rectangle.
/// </summary>
public sealed record ReachabilityOutcome
{
    public required ReachabilityStatus Status { get; init; }
    public required LocatedTarget Target { get; init; }
    public string? Diagnostic { get; init; }
}

public enum ReachabilityStatus
{
    Reachable = 0,
    OffScreen = 1,
    Occluded = 2,
}
