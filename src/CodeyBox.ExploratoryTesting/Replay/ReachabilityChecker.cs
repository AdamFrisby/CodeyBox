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
///   it." For visual-only descriptors, the checker treats verified current
///   visual evidence as reachable even when a containing accessible surface
///   answers at the point; canvas/document roots are often the real click
///   receiver for untagged controls.</item>
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
    private readonly IVisualWait _visualWait;
    private readonly IVisualTargetVerifier _visualVerifier;

    public ReachabilityChecker(
        ComputerUseBridge bridge,
        IElementLocator? locator = null,
        IAccessibilityMatcher? matcher = null,
        IVisualWait? visualWait = null,
        IVisualTargetVerifier? visualVerifier = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _matcher = matcher ?? DefaultAccessibilityMatcher.Instance;
        _locator = locator ?? new AccessibilityElementLocator(_matcher);
        _visualWait = visualWait ?? new ScreenshotStabilityWait();
        _visualVerifier = visualVerifier ?? DescriptorVisualTargetVerifier.Instance;
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
            var settled = await _visualWait.WaitAsync(sandbox, predicate: null, options, ct)
                .ConfigureAwait(false);
            if (settled is null)
            {
                return new ReachabilityOutcome
                {
                    Status = ReachabilityStatus.OffScreen,
                    Target = current,
                    Diagnostic = $"screen did not settle after scrolling toward target centre ({current.CenterX},{current.CenterY}) within {options.VisualWaitTimeout}",
                };
            }

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
            try
            {
                snap = await sandbox.GetAccessibilityAtPointAsync(current.CenterX, current.CenterY, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ReachabilityOutcome
                {
                    Status = ReachabilityStatus.Occluded,
                    Target = current,
                    Diagnostic = $"top-most accessibility probe failed at ({current.CenterX},{current.CenterY}); cannot verify target is visible and unobstructed: {DiagnosticText.Sanitize(ex.Message)}",
                };
            }

            if (snap is null)
            {
                return new ReachabilityOutcome
                {
                    Status = ReachabilityStatus.Occluded,
                    Target = current,
                    Diagnostic = $"expected element ({Describe(expectedAccessibility)}) did not answer the top-most accessibility probe at ({current.CenterX},{current.CenterY}); cannot verify it is visible and unobstructed",
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

            if (HasPixelVisualSignal(descriptor.Visual))
            {
                var visualStatus = await GetCurrentVisualEvidenceStatusAsync(sandbox, current, descriptor, allowLocatorEvidence: false, ct)
                    .ConfigureAwait(false);
                if (visualStatus != VisualTargetVerificationStatus.Verified)
                {
                    return new ReachabilityOutcome
                    {
                        Status = ReachabilityStatus.Occluded,
                        Target = current,
                        Diagnostic = visualStatus == VisualTargetVerificationStatus.Mismatch
                            ? $"expected element ({Describe(expectedAccessibility)}) is accessibility-top-most at ({current.CenterX},{current.CenterY}) but its visible pixels no longer match the recorded descriptor"
                            : $"expected element ({Describe(expectedAccessibility)}) is accessibility-top-most at ({current.CenterX},{current.CenterY}) but its current visual descriptor could not be verified",
                    };
                }
            }
        }
        else
        {
            try
            {
                var snap = await sandbox.GetAccessibilityAtPointAsync(current.CenterX, current.CenterY, ct)
                    .ConfigureAwait(false);
                if (snap is not null)
                {
                    if (TopMostAccessibilityMatchesOcrTarget(snap, descriptor.Visual, current))
                        return new ReachabilityOutcome { Status = ReachabilityStatus.Reachable, Target = current };

                    var topMostVisualStatus = await GetCurrentVisualEvidenceStatusAsync(sandbox, current, descriptor, allowLocatorEvidence: true, ct)
                        .ConfigureAwait(false);
                    if (topMostVisualStatus == VisualTargetVerificationStatus.Verified)
                        return new ReachabilityOutcome { Status = ReachabilityStatus.Reachable, Target = current };

                    return new ReachabilityOutcome
                    {
                        Status = ReachabilityStatus.Occluded,
                        Target = current,
                        Diagnostic = topMostVisualStatus == VisualTargetVerificationStatus.Mismatch
                            ? $"accessibility element ({Describe(snap)}) is top-most over visual-only target at ({current.CenterX},{current.CenterY}) and target pixels no longer match the recorded descriptor"
                            : $"accessibility element ({Describe(snap)}) is top-most over visual-only target at ({current.CenterX},{current.CenterY}); cannot verify untagged target is unobstructed",
                    };
                }

                var visualStatus = await GetCurrentVisualEvidenceStatusAsync(sandbox, current, descriptor, allowLocatorEvidence: true, ct)
                    .ConfigureAwait(false);
                if (visualStatus != VisualTargetVerificationStatus.Verified)
                {
                    return new ReachabilityOutcome
                    {
                        Status = ReachabilityStatus.Occluded,
                        Target = current,
                        Diagnostic = visualStatus == VisualTargetVerificationStatus.Mismatch
                            ? $"visual-only target pixels no longer match the recorded descriptor at ({current.CenterX},{current.CenterY}); cannot verify it is visible and unobstructed"
                            : $"visual-only target at ({current.CenterX},{current.CenterY}) has no verifiable current visual signature or OCR evidence; cannot prove it is visible and unobstructed",
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ReachabilityOutcome
                {
                    Status = ReachabilityStatus.Occluded,
                    Target = current,
                    Diagnostic = $"top-most accessibility probe failed at ({current.CenterX},{current.CenterY}); cannot verify visual-only target is unobstructed: {DiagnosticText.Sanitize(ex.Message)}",
                };
            }
        }

        return new ReachabilityOutcome { Status = ReachabilityStatus.Reachable, Target = current };
    }

    private static bool HasPixelVisualSignal(TraceVisualDescriptor visual) =>
        visual.TemplatePng is { Length: > 0 }
        || visual.SourceScreenshotPng is { Length: > 0 } && visual.Region is { Width: > 0, Height: > 0 };

    private async Task<VisualTargetVerificationStatus> GetCurrentVisualEvidenceStatusAsync(
        ISandbox sandbox,
        LocatedTarget current,
        TraceTargetDescriptor descriptor,
        bool allowLocatorEvidence,
        CancellationToken ct)
    {
        if (allowLocatorEvidence
            && (current.Evidence & (LocatedTargetEvidence.Visual | LocatedTargetEvidence.Ocr)) != 0)
        {
            return VisualTargetVerificationStatus.Verified;
        }

        byte[] screenshot;
        try
        {
            screenshot = await sandbox.GetScreenshotAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return VisualTargetVerificationStatus.Unverifiable;
        }

        if (screenshot.Length == 0) return VisualTargetVerificationStatus.Unverifiable;
        return _visualVerifier.Verify(screenshot, descriptor.Visual, current);
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

    private static bool TopMostAccessibilityMatchesOcrTarget(
        SandboxAccessibilitySnapshot snap,
        TraceVisualDescriptor visual,
        LocatedTarget current)
    {
        if ((current.Evidence & LocatedTargetEvidence.Ocr) == 0) return false;
        var expected = visual.OcrText;
        if (string.IsNullOrWhiteSpace(expected)) return false;
        return ContainsTokenSequence(snap.Name, expected)
            || ContainsTokenSequence(snap.Text, expected)
            || ContainsTokenSequence(snap.ElementType, expected);
    }

    private static bool ContainsTokenSequence(string? value, string expected)
    {
        var actualTokens = Tokenize(value);
        var expectedTokens = Tokenize(expected);
        if (actualTokens.Length == 0 || expectedTokens.Length == 0 || expectedTokens.Length > actualTokens.Length)
            return false;

        for (var i = 0; i <= actualTokens.Length - expectedTokens.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < expectedTokens.Length; j++)
            {
                if (string.Equals(actualTokens[i + j], expectedTokens[j], StringComparison.OrdinalIgnoreCase))
                    continue;

                matched = false;
                break;
            }

            if (matched) return true;
        }

        return false;
    }

    private static string[] Tokenize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsLetterOrDigit(value[i]))
            {
                if (start < 0) start = i;
                continue;
            }

            if (start >= 0)
            {
                tokens.Add(value[start..i]);
                start = -1;
            }
        }

        if (start >= 0)
            tokens.Add(value[start..]);

        return tokens.ToArray();
    }
}
