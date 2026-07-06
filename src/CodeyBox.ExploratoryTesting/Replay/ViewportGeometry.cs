namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Shared viewport-containment and scroll-direction arithmetic. Both the
/// reachability checker's post-locate scroll loop and its visual-miss recovery
/// resolve "is this point on screen?" and "which way do I scroll to reach it?"
/// the same way — keeping the policy here means a change to scroll direction
/// (e.g. horizontal-wins-on-ties) is made once, not in parallel copies.
/// </summary>
internal static class ViewportGeometry
{
    public static bool PointInViewport(int x, int y, ReplayOptions options)
        => x >= 0 && x < options.ScreenWidth && y >= 0 && y < options.ScreenHeight;

    /// <summary>
    /// Resolve the single-axis scroll delta that moves an off-viewport point
    /// toward the screen. Vertical takes priority when both axes are out —
    /// most layouts scroll vertically far more often than horizontally, and the
    /// recorder rarely emits two-axis scrolls. Callers must gate on
    /// <see cref="PointInViewport"/> returning false; an in-viewport point is a
    /// caller logic error (throwing beats emitting a (0,0) scroll the bridge
    /// would reject with a diagnostic pinned to the wrong layer).
    /// </summary>
    public static (int Dx, int Dy) ResolveScrollDelta(int x, int y, ReplayOptions options)
    {
        if (y < 0) return (0, -options.ScrollStep);
        if (y >= options.ScreenHeight) return (0, options.ScrollStep);
        if (x < 0) return (-options.ScrollStep, 0);
        if (x >= options.ScreenWidth) return (options.ScrollStep, 0);
        throw new InvalidOperationException(
            $"ResolveScrollDelta invoked for in-viewport point ({x},{y}).");
    }
}
