using CodeyBox.Core;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Default <see cref="IReachabilityChecker"/>. Implements the three
/// reachability dimensions the brief calls out — in-viewport, visible, and
/// top-most.
///
/// <list type="bullet">
///   <item><b>Viewport</b>: target's centre must lie inside
///   <c>[0, ScreenWidth) × [0, ScreenHeight)</c>. When it doesn't, we issue
///   real scroll events through <see cref="ComputerUseBridge"/> and then
///   <b>re-locate via the locator</b> — never by arithmetic on stale
///   coordinates, because the recorder's "scroll units" do not have a stable
///   pixel ratio across hosts. Horizontal-only offset triggers a horizontal
///   scroll; vertical-only triggers a vertical scroll. After
///   <see cref="ReplayOptions.MaxScrollAttempts"/>, report
///   <see cref="ReachabilityStatus.OffScreen"/>.</item>
///   <item><b>Visible</b>: an accessibility-tagged descriptor that no longer
///   answers at the located centre is reported as
///   <see cref="ReachabilityStatus.Occluded"/> — display:none, opacity:0,
///   and other invisibility classes drop the element out of the
///   accessibility tree, so a null probe is equivalent to "user can't see
///   it." A non-accessibility descriptor's visibility is implicit in its
///   locator hit: the only shipped non-accessibility locator
///   (<see cref="VisualSignatureElementLocator"/>) only returns when the
///   current screen matches the recorded screen pixel-for-pixel, which
///   carries its own visibility guarantee.</item>
///   <item><b>Top-most</b>: when the descriptor carries a usable
///   accessibility signature, probe
///   <see cref="ISandbox.GetAccessibilityAtPointAsync"/> at the centre and
///   compare against the recorded descriptor via
///   <see cref="IAccessibilityMatcher"/>. If a different element answers,
///   report <see cref="ReachabilityStatus.Occluded"/>.</item>
///   <item>Otherwise, <see cref="ReachabilityStatus.Reachable"/>.</item>
/// </list>
/// </summary>
public sealed class ReachabilityChecker : IReachabilityChecker
{
    private readonly ComputerUseBridge _bridge;
    private readonly IElementLocator _locator;
    private readonly IAccessibilityMatcher _matcher;

    public ReachabilityChecker(
        ComputerUseBridge bridge,
        IElementLocator? locator = null,
        IAccessibilityMatcher? matcher = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _locator = locator ?? new AccessibilityElementLocator();
        _matcher = matcher ?? DefaultAccessibilityMatcher.Instance;
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
        var consecutiveRelocateMisses = 0;
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
            // not arithmetic on stale coordinates.
            var relocated = await _locator.LocateAsync(sandbox, descriptor, options, ct).ConfigureAwait(false);
            if (relocated is not null)
            {
                current = relocated;
                consecutiveRelocateMisses = 0;
                continue;
            }

            // Locator missed after the scroll. The real failure mode here is
            // "the post-scroll layout broke recognition", not "still
            // off-screen" — continuing to scroll the same direction (based on
            // the stale pre-scroll `current`) would just burn the remaining
            // attempt budget on a target the engine can no longer see anyway.
            // After a small grace window we surface a distinct lost-after-
            // scroll diagnostic so operators can triage the real cause
            // (layout reflow / element re-themed) instead of chasing a
            // misleading OffScreen.
            consecutiveRelocateMisses++;
            if (consecutiveRelocateMisses >= 2)
            {
                return new ReachabilityOutcome
                {
                    Status = ReachabilityStatus.OffScreen,
                    Target = current,
                    Diagnostic = $"target centre ({current.CenterX},{current.CenterY}) outside viewport ({options.ScreenWidth}x{options.ScreenHeight}); locator could not re-find the target after {consecutiveRelocateMisses} post-scroll attempts (layout likely reflowed)",
                };
            }
        }

        var expectedAccessibility = descriptor.Accessibility;
        if (expectedAccessibility is not null && _matcher.HasAnyAccessibilitySignal(expectedAccessibility))
        {
            SandboxAccessibilitySnapshot? snap;
            var probeFailed = false;
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
                // A transient accessibility-probe failure cannot be
                // distinguished from "no element here". Fall through to
                // Reachable so a flaky IPC blip does not falsely report
                // Occluded; the input dispatch that follows will surface a
                // real failure if the element is genuinely gone.
                snap = null;
                probeFailed = true;
            }

            if (snap is null && !probeFailed)
            {
                // No element answers at the located centre even though the
                // recorded descriptor had a clear accessibility signature.
                // Display:none / opacity:0 / removed-from-DOM all drop a
                // node out of the accessibility tree, so this is the
                // "visible" leg of the reachability check: a vanished
                // target is not user-reachable. Report as Occluded —
                // categorically a "the element the recording trusted is no
                // longer here at click time" failure class.
                return new ReachabilityOutcome
                {
                    Status = ReachabilityStatus.Occluded,
                    Target = current,
                    Diagnostic = $"expected element ({Describe(expectedAccessibility)}) is no longer visible at ({current.CenterX},{current.CenterY}) — display:none / opacity:0 / removed-from-tree",
                };
            }

            if (snap is not null && !_matcher.Matches(snap, expectedAccessibility))
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
        // The caller gates this call on !InViewport, so a both-axes-in-bounds
        // target is a logic error. Throw rather than emit a (0, 0) scroll —
        // the bridge would reject it with "Scroll events require a non-zero
        // X or Y amount" and that diagnostic would be reported against the
        // wrong layer.
        throw new InvalidOperationException(
            $"ResolveScrollDelta invoked for in-viewport target ({t.CenterX},{t.CenterY}).");
    }

    private static string Describe(SandboxAccessibilitySnapshot s) =>
        $"role={DiagnosticText.Sanitize(s.Role ?? "?")} name={DiagnosticText.Sanitize(s.Name ?? "?")}";

    private static string Describe(TraceAccessibilityDescriptor d) =>
        $"role={DiagnosticText.Sanitize(d.Role ?? "?")} name={DiagnosticText.Sanitize(d.Name ?? "?")}";
}
