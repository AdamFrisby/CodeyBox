using CodeyBox.Core;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Default <see cref="IReachabilityChecker"/>.
///
/// <list type="bullet">
///   <item><b>Viewport</b>: target's centre must lie inside
///   <c>(0, ScreenWidth) × (0, ScreenHeight)</c>. When it doesn't, we issue
///   real scroll events through <see cref="ComputerUseBridge"/>, polling
///   the screenshot for a stable frame between attempts, and re-evaluate.
///   If still off-screen after <see cref="ReplayOptions.MaxScrollAttempts"/>,
///   report <see cref="ReachabilityStatus.OffScreen"/>.</item>
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

    public ReachabilityChecker(ComputerUseBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public async Task<ReachabilityOutcome> EnsureReachableAsync(
        ISandbox sandbox,
        LocatedTarget target,
        TraceAccessibilityDescriptor? expectedAccessibility,
        ReplayOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(target);
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

            var dy = ResolveScrollDelta(current.CenterY, options);
            // The bridge resolves the scroll event from (ScrollX ?? X, ScrollY ?? Y)
            // and the validator rejects events that set both axes, so we pass the
            // scroll magnitude on the dedicated ScrollY axis only.
            await _bridge.ExecuteAsync(
                sandbox,
                new ComputerUseRequest { Action = "scroll", ScrollY = dy },
                ct).ConfigureAwait(false);

            current = current with
            {
                CenterY = current.CenterY - dy * 40,
                Region = current.Region with { Y = current.Region.Y - dy * 40 },
            };
        }

        if (expectedAccessibility is not null)
        {
            SandboxAccessibilitySnapshot? snap;
            try
            {
                snap = await sandbox.GetAccessibilityAtPointAsync(current.CenterX, current.CenterY, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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

    private static int ResolveScrollDelta(int centerY, ReplayOptions o)
    {
        if (centerY < 0) return -o.ScrollStep;
        if (centerY >= o.ScreenHeight) return o.ScrollStep;
        return o.ScrollStep;
    }

    private static string Describe(SandboxAccessibilitySnapshot s) =>
        $"role={s.Role ?? "?"} name={s.Name ?? "?"}";

    private static string Describe(TraceAccessibilityDescriptor d) =>
        $"role={d.Role ?? "?"} name={d.Name ?? "?"}";
}
