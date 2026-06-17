using CodeyBox.Core;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Default <see cref="IReachabilityChecker"/>.
///
/// <list type="bullet">
///   <item><b>Viewport</b>: target's centre must lie inside
///   <c>(0, ScreenWidth) × (0, ScreenHeight)</c>. When it doesn't, we issue
///   real scroll events through <see cref="ComputerUseBridge"/> and then
///   <b>re-locate via the locator</b> — never by arithmetic on stale
///   coordinates, because the recorder's "scroll units" do not have a stable
///   pixel ratio across hosts. Horizontal-only offset triggers a horizontal
///   scroll; vertical-only triggers a vertical scroll. After
///   <see cref="ReplayOptions.MaxScrollAttempts"/>, report
///   <see cref="ReachabilityStatus.OffScreen"/>.</item>
///   <item><b>Top-most</b>: when the descriptor carries an accessibility
///   signature, probe <see cref="ISandbox.GetAccessibilityAtPointAsync"/>
///   at the centre. If a different element answers, report
///   <see cref="ReachabilityStatus.Occluded"/>.</item>
///   <item>Otherwise, <see cref="ReachabilityStatus.Reachable"/>.</item>
/// </list>
/// </summary>
public sealed class ReachabilityChecker : IReachabilityChecker
{
    private readonly ComputerUseBridge _bridge;
    private readonly IElementLocator _locator;

    public ReachabilityChecker(ComputerUseBridge bridge, IElementLocator? locator = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _locator = locator ?? new AccessibilityElementLocator();
    }

    public async Task<ReachabilityOutcome> EnsureReachableAsync(
        ISandbox sandbox,
        LocatedTarget target,
        TraceTargetDescriptor descriptor,
        ReplayOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);

        var current = target;
        for (var attempt = 0; attempt <= options.MaxScrollAttempts; attempt++)
        {
            if (InViewport(current, options))
                break;

            if (attempt == options.MaxScrollAttempts)
            {
                return new ReachabilityOutcome
                {
                    Status = ReachabilityStatus.OffScreen,
                    Target = current,
                    Diagnostic = $"target centre ({current.CenterX},{current.CenterY}) outside viewport ({options.ScreenWidth}x{options.ScreenHeight}) after {attempt} scroll attempts",
                };
            }

            var (dx, dy) = ResolveScrollDelta(current, options);
            // The bridge validator rejects two-axis scroll events, and X/Y on
            // a scroll request resolve as fallback for ScrollX/Y — so we pass
            // the scroll magnitude on a single dedicated axis and leave X/Y
            // null. Horizontal and vertical scrolls are dispatched separately.
            var scrollRequest = dx != 0
                ? new ComputerUseRequest { Action = "scroll", ScrollX = dx }
                : new ComputerUseRequest { Action = "scroll", ScrollY = dy };
            await _bridge.ExecuteAsync(sandbox, scrollRequest, ct).ConfigureAwait(false);

            // Re-locate on the CURRENT screen — the brief mandates recognition,
            // not arithmetic on stale coordinates. If the locator now can't see
            // the target, the next loop iteration's viewport check still acts on
            // the prior `current` and either finishes the attempt budget or
            // tries another scroll in the same direction.
            var relocated = await _locator.LocateAsync(sandbox, descriptor, options, ct).ConfigureAwait(false);
            if (relocated is not null)
                current = relocated;
        }

        var expectedAccessibility = descriptor.Accessibility;
        if (expectedAccessibility is not null)
        {
            SandboxAccessibilitySnapshot? snap;
            try
            {
                snap = await sandbox.GetAccessibilityAtPointAsync(current.CenterX, current.CenterY, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                snap = null;
            }

            if (snap is not null && !AccessibilityElementLocator.Matches(snap, expectedAccessibility))
            {
                return new ReachabilityOutcome
                {
                    Status = ReachabilityStatus.Occluded,
                    Target = current,
                    Diagnostic = $"another element ({Describe(snap)}) is on top of the expected target ({Describe(expectedAccessibility)}) at ({current.CenterX},{current.CenterY})",
                };
            }
        }

        return new ReachabilityOutcome { Status = ReachabilityStatus.Reachable, Target = current };
    }

    private static bool InViewport(LocatedTarget t, ReplayOptions o) =>
        t.CenterX >= 0 && t.CenterX < o.ScreenWidth && t.CenterY >= 0 && t.CenterY < o.ScreenHeight;

    private static (int Dx, int Dy) ResolveScrollDelta(LocatedTarget t, ReplayOptions o)
    {
        // Pick the axis that's actually off-screen. Vertical takes priority when
        // both axes are out — most layouts scroll vertically far more often than
        // horizontally, and the recorder rarely emits two-axis scrolls.
        if (t.CenterY < 0) return (0, -o.ScrollStep);
        if (t.CenterY >= o.ScreenHeight) return (0, o.ScrollStep);
        if (t.CenterX < 0) return (-o.ScrollStep, 0);
        if (t.CenterX >= o.ScreenWidth) return (o.ScrollStep, 0);
        // In-viewport on both axes — shouldn't be called in this case, but if
        // it is, treat as "no useful scroll" and let the caller exhaust attempts.
        return (0, 0);
    }

    private static string Describe(SandboxAccessibilitySnapshot s) =>
        $"role={DiagnosticText.Sanitize(s.Role ?? "?")} name={DiagnosticText.Sanitize(s.Name ?? "?")}";

    private static string Describe(TraceAccessibilityDescriptor d) =>
        $"role={DiagnosticText.Sanitize(d.Role ?? "?")} name={DiagnosticText.Sanitize(d.Name ?? "?")}";
}
