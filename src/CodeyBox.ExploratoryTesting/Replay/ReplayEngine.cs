using CodeyBox.Core;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Replays a recorded (or trimmed) <see cref="SessionTrace"/> against a
/// live <see cref="ISandbox"/> as a regression test, driving the app with
/// real keyboard / mouse input via <see cref="ComputerUseBridge"/> — never
/// synthetic selector dispatch.
///
/// <para>For each step the engine:</para>
/// <list type="number">
///   <item>Re-locates the recorded target on the CURRENT screen by
///   recognition (via <see cref="IElementLocator"/>) — recorded raw
///   coordinates are never trusted.</item>
///   <item>Verifies the target is genuinely user-reachable (viewport,
///   visible, top-most) via <see cref="IReachabilityChecker"/>. When it
///   isn't, FAIL with <see cref="ReplayFailureKind.OffScreen"/> or
///   <see cref="ReplayFailureKind.Occluded"/> — those are real bug
///   classes the engine surfaces, not noise.</item>
///   <item>Drives real input through <see cref="ComputerUseBridge"/>.</item>
///   <item>Waits for the screen to settle (or reach an expected state) via
///   <see cref="IVisualWait"/> — observational, not DOM-ready, so it
///   generalises to canvas / 3D.</item>
///   <item>Optionally verifies a post-condition assertion via
///   <see cref="IAssertionVerifier"/>.</item>
/// </list>
///
/// <para>The engine is stateless: it owns nothing across calls and can be
/// reused / fanned out across many sandboxes in parallel. The recorded
/// screenshot for visual-match assertions flows through
/// <see cref="IAssertionVerifier.VerifyAsync"/> as a parameter, not through
/// a shared mutable property — so a single verifier instance is safe to
/// share across concurrent replays.</para>
///
/// <para><b>Locator-miss policy:</b> FAIL deterministically with a clear
/// diagnostic. The optional <see cref="ILocatorHealer"/> seam is consulted
/// only when an implementation is supplied; no self-heal ships in this
/// item — the brief calls that out explicitly.</para>
/// </summary>
public sealed class ReplayEngine
{
    private readonly ComputerUseBridge _bridge;
    private readonly IElementLocator _locator;
    private readonly IReachabilityChecker _reachability;
    private readonly IVisualWait _visualWait;
    private readonly IAssertionVerifier _assertions;
    private readonly ILocatorHealer? _healer;
    private readonly TimeProvider _timeProvider;

    public ReplayEngine(
        ComputerUseBridge bridge,
        IElementLocator? locator = null,
        IReachabilityChecker? reachability = null,
        IVisualWait? visualWait = null,
        IAssertionVerifier? assertions = null,
        ILocatorHealer? healer = null,
        TimeProvider? timeProvider = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        // Default chain: accessibility-tree recognition first (cheap, exact),
        // then visual-signature recognition for canvas / 3D / untagged targets
        // — the brief's "accessibility tree when present, ELSE visual /
        // OCR / template" contract. Richer template / OCR / vision-LLM
        // locators plug into the same chain via CompositeElementLocator.
        _locator = locator ?? new CompositeElementLocator(
            new AccessibilityElementLocator(),
            new VisualSignatureElementLocator());
        _reachability = reachability ?? new ReachabilityChecker(_bridge, _locator);
        _visualWait = visualWait ?? new ScreenshotStabilityWait(timeProvider);
        _assertions = assertions ?? new DefaultAssertionVerifier();
        _healer = healer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Replay <paramref name="trace"/> against <paramref name="sandbox"/>
    /// and return a structured <see cref="ReplayResult"/>. The first failed
    /// step terminates the replay; remaining steps are not attempted.
    /// </summary>
    public async Task<ReplayResult> ReplayAsync(
        ISandbox sandbox,
        SessionTrace trace,
        ReplayOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(trace);

        var opts = options ?? new ReplayOptions();
        var start = _timeProvider.GetUtcNow();
        var steps = new List<ReplayStepResult>(trace.Entries.Count);

        foreach (var entry in trace.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var step = await ReplayStepAsync(sandbox, entry, opts, ct).ConfigureAwait(false);
            steps.Add(step);
            if (!step.Passed)
            {
                return new ReplayResult
                {
                    Passed = false,
                    Steps = steps.ToArray(),
                    FailedStep = step,
                    Duration = _timeProvider.GetUtcNow() - start,
                };
            }
        }

        return new ReplayResult
        {
            Passed = true,
            Steps = steps.ToArray(),
            Duration = _timeProvider.GetUtcNow() - start,
        };
    }

    private async Task<ReplayStepResult> ReplayStepAsync(
        ISandbox sandbox,
        TraceEntry entry,
        ReplayOptions options,
        CancellationToken ct)
    {
        var action = entry.Action;

        if (action.Kind == "screenshot")
        {
            return await RunScreenshotStepAsync(sandbox, entry, ct).ConfigureAwait(false);
        }

        LocatedTarget? located = null;
        if (NeedsLocator(action.Kind))
        {
            located = await _locator.LocateAsync(sandbox, action.TargetDescriptor, options, ct)
                .ConfigureAwait(false);

            if (located is null && _healer is not null)
            {
                located = await _healer.HealAsync(sandbox, entry, options, ct).ConfigureAwait(false);
            }

            if (located is null)
            {
                return await FailAsync(
                    sandbox, entry,
                    ReplayFailureKind.NotFound,
                    $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): recorded target not found on current screen (descriptor={DescribeDescriptor(action.TargetDescriptor)})",
                    locatedTarget: null,
                    ct).ConfigureAwait(false);
            }

            ReachabilityOutcome reach;
            try
            {
                reach = await _reachability.EnsureReachableAsync(
                    sandbox, located, action.TargetDescriptor, options, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return await FailAsync(
                    sandbox, entry, ReplayFailureKind.ActionFailed,
                    $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): reachability check failed: {DiagnosticText.Sanitize(ex.Message)}",
                    locatedTarget: located,
                    ct).ConfigureAwait(false);
            }

            if (reach.Status != ReachabilityStatus.Reachable)
            {
                var kind = reach.Status == ReachabilityStatus.OffScreen
                    ? ReplayFailureKind.OffScreen
                    : ReplayFailureKind.Occluded;
                return await FailAsync(
                    sandbox, entry, kind,
                    $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): {DiagnosticText.Sanitize(reach.Diagnostic ?? reach.Status.ToString())}",
                    locatedTarget: reach.Target,
                    ct).ConfigureAwait(false);
            }

            located = reach.Target;
        }

        try
        {
            await DispatchActionAsync(sandbox, action, located, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await FailAsync(
                sandbox, entry, ReplayFailureKind.ActionFailed,
                $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): input dispatch failed: {DiagnosticText.Sanitize(ex.Message)}",
                locatedTarget: located,
                ct).ConfigureAwait(false);
        }

        // Short-circuit the stability wait when the assertion is a visual-match
        // and we have the expected screenshot in hand — this is the brief's
        // 'wait until expected element/state appears' leg, not the pure
        // 'animation has settled' fallback. Other assertion kinds rely on the
        // accessibility tree (re-fetched after the wait) so they do not gain a
        // useful per-frame predicate.
        var predicate = BuildExpectedStatePredicate(entry);
        var settled = await _visualWait.WaitAsync(sandbox, predicate, options, ct).ConfigureAwait(false);
        if (settled is null)
        {
            return await FailAsync(
                sandbox, entry, ReplayFailureKind.WaitTimeout,
                $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): screen did not settle within {options.VisualWaitTimeout}",
                locatedTarget: located,
                ct).ConfigureAwait(false);
        }

        if (entry.Assertion is { } assertion)
        {
            // Fetch the accessibility tree only for assertion kinds that
            // consume it. Visual-match looks at screenshots only — issuing
            // an extra accessibility-tree IPC roundtrip per step burns cost
            // for no diagnostic value.
            var accessibility = AssertionConsumesAccessibilityTree(assertion)
                ? await TryGetAccessibilityTreeAsync(sandbox, ct).ConfigureAwait(false)
                : null;
            string? diag;
            try
            {
                diag = await _assertions
                    .VerifyAsync(sandbox, assertion, settled, entry.Observation.ScreenshotPng, accessibility, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A throwing verifier must not leak out of ReplayAsync and
                // abort the structured-step contract; coerce into an
                // AssertionMismatch so the rest of the result envelope still
                // surfaces, matching how reachability / dispatch errors are
                // handled.
                return new ReplayStepResult
                {
                    Sequence = entry.Sequence,
                    ActionKind = action.Kind,
                    Passed = false,
                    FailureKind = ReplayFailureKind.AssertionMismatch,
                    Diagnostic = $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): assertion '{DiagnosticText.Sanitize(assertion.Kind)}' verifier threw: {DiagnosticText.Sanitize(ex.Message)}",
                    DiagnosticScreenshotPng = settled,
                    LocatedTarget = located,
                };
            }
            if (diag is not null)
            {
                return new ReplayStepResult
                {
                    Sequence = entry.Sequence,
                    ActionKind = action.Kind,
                    Passed = false,
                    FailureKind = ReplayFailureKind.AssertionMismatch,
                    Diagnostic = $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): assertion '{DiagnosticText.Sanitize(assertion.Kind)}' failed: {DiagnosticText.Sanitize(diag)}",
                    DiagnosticScreenshotPng = settled,
                    LocatedTarget = located,
                };
            }
        }

        return new ReplayStepResult
        {
            Sequence = entry.Sequence,
            ActionKind = action.Kind,
            Passed = true,
            LocatedTarget = located,
        };
    }

    private async Task<ReplayStepResult> RunScreenshotStepAsync(
        ISandbox sandbox,
        TraceEntry entry,
        CancellationToken ct)
    {
        ComputerUseResult bridgeResult;
        try
        {
            bridgeResult = await _bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "screenshot" }, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await FailAsync(
                sandbox, entry, ReplayFailureKind.ActionFailed,
                $"step {entry.Sequence} (screenshot): {DiagnosticText.Sanitize(ex.Message)}",
                locatedTarget: null,
                ct).ConfigureAwait(false);
        }

        if (entry.Assertion is { } assertion)
        {
            // Reuse the bridge's captured frame instead of issuing a second
            // GetScreenshotAsync — two captures can disagree on a moving UI
            // and the user only asked for one screenshot at this checkpoint.
            var current = bridgeResult.ScreenshotPng;
            var accessibility = AssertionConsumesAccessibilityTree(assertion)
                ? await TryGetAccessibilityTreeAsync(sandbox, ct).ConfigureAwait(false)
                : null;
            string? diag;
            try
            {
                diag = await _assertions
                    .VerifyAsync(sandbox, assertion, current, entry.Observation.ScreenshotPng, accessibility, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ReplayStepResult
                {
                    Sequence = entry.Sequence,
                    ActionKind = entry.Action.Kind,
                    Passed = false,
                    FailureKind = ReplayFailureKind.AssertionMismatch,
                    Diagnostic = $"step {entry.Sequence} (screenshot): assertion '{DiagnosticText.Sanitize(assertion.Kind)}' verifier threw: {DiagnosticText.Sanitize(ex.Message)}",
                    DiagnosticScreenshotPng = current,
                };
            }
            if (diag is not null)
            {
                return new ReplayStepResult
                {
                    Sequence = entry.Sequence,
                    ActionKind = entry.Action.Kind,
                    Passed = false,
                    FailureKind = ReplayFailureKind.AssertionMismatch,
                    Diagnostic = $"step {entry.Sequence} (screenshot): assertion '{DiagnosticText.Sanitize(assertion.Kind)}' failed: {DiagnosticText.Sanitize(diag)}",
                    DiagnosticScreenshotPng = current,
                };
            }
        }

        return new ReplayStepResult
        {
            Sequence = entry.Sequence,
            ActionKind = entry.Action.Kind,
            Passed = true,
        };
    }

    private static bool AssertionConsumesAccessibilityTree(TraceAssertion assertion) =>
        assertion.Kind is "text-contains" or "element-present";

    private static Func<byte[], bool>? BuildExpectedStatePredicate(TraceEntry entry)
    {
        if (entry.Assertion is not { Kind: "visual-match" } assertion) return null;
        var expected = entry.Observation.ScreenshotPng;
        if (expected is null || expected.Length == 0) return null;
        // Detail-named recordings are resolved by the verifier, not by the
        // wait — building a predicate would require duplicating the named-
        // recording map here. The wait still stops on stability, the verifier
        // still runs on the stable frame, so this only loses the short-
        // circuit, not the verification.
        if (!string.IsNullOrEmpty(assertion.Detail)) return null;
        return current => current.Length == expected.Length && current.AsSpan().SequenceEqual(expected);
    }

    private async Task DispatchActionAsync(
        ISandbox sandbox,
        TraceAction action,
        LocatedTarget? located,
        CancellationToken ct)
    {
        var request = BuildRequestForReplay(action, located);
        await _bridge.ExecuteAsync(sandbox, request, ct).ConfigureAwait(false);
    }

    private static ComputerUseRequest BuildRequestForReplay(TraceAction action, LocatedTarget? located)
    {
        return action.Kind switch
        {
            "click" => new ComputerUseRequest
            {
                Action = "click",
                X = located!.CenterX,
                Y = located.CenterY,
            },
            "double_click" => new ComputerUseRequest
            {
                Action = "double_click",
                X = located!.CenterX,
                Y = located.CenterY,
            },
            "move" => new ComputerUseRequest
            {
                Action = "move",
                X = located!.CenterX,
                Y = located.CenterY,
            },
            "scroll" => BuildScrollRequest(action),
            "key" => new ComputerUseRequest
            {
                Action = "key",
                Key = FirstKey(action.InputEvents),
            },
            "type" => new ComputerUseRequest
            {
                Action = "type",
                Text = FirstText(action.InputEvents),
            },
            "events" => new ComputerUseRequest
            {
                Action = "events",
                Events = RelocateEvents(action.InputEvents, located, action),
            },
            _ => throw new NotSupportedException($"Unsupported replay action kind '{action.Kind}'."),
        };
    }

    private static ComputerUseRequest BuildScrollRequest(TraceAction action)
    {
        // The bridge resolves the scroll event from (ScrollX ?? X, ScrollY ?? Y)
        // and the validator rejects events with both axes non-zero. We:
        //   - pull the magnitude from the first SandboxInputEvent of Type=Scroll,
        //     not action.InputEvents[0] verbatim (a malformed recording whose
        //     first event is a Click could push pixel coords as scroll units);
        //   - zero the smaller axis when the recording emits a two-axis scroll,
        //     so the validator never rejects a real recording for shape.
        SandboxInputEvent? scrollEvent = null;
        foreach (var e in action.InputEvents)
        {
            if (e.Type == SandboxInputEventType.Scroll)
            {
                scrollEvent = e;
                break;
            }
        }
        var sx = scrollEvent?.X ?? 0;
        var sy = scrollEvent?.Y ?? 0;
        if (sx != 0 && sy != 0)
        {
            // Drop the smaller-magnitude axis — vertical wins on ties.
            if (Math.Abs(sx) > Math.Abs(sy)) sy = 0;
            else sx = 0;
        }
        return new ComputerUseRequest
        {
            Action = "scroll",
            ScrollX = sx == 0 ? null : sx,
            ScrollY = sy == 0 ? null : sy,
        };
    }

    private static IReadOnlyList<SandboxInputEvent> RelocateEvents(
        IReadOnlyList<SandboxInputEvent> events,
        LocatedTarget? located,
        TraceAction action)
    {
        if (located is null) return events;
        // Anchor relative offsets at the recorded centre so a drag (or any
        // multi-event sequence with internal motion) preserves its geometry
        // when the target moved on screen: each event's recorded (X, Y) is
        // translated by the delta from the recorded anchor to the located
        // anchor. If the action has no recorded coordinates to anchor on, the
        // first Click/Move position acts as the anchor.
        var anchor = FindAnchor(events, action);
        if (anchor is null)
        {
            return CollapseToCentre(events, located);
        }
        var deltaX = located.CenterX - anchor.Value.X;
        var deltaY = located.CenterY - anchor.Value.Y;
        var result = new List<SandboxInputEvent>(events.Count);
        foreach (var evt in events)
        {
            result.Add(evt.Type switch
            {
                SandboxInputEventType.Click or SandboxInputEventType.Move when evt.X is not null && evt.Y is not null =>
                    evt with { X = evt.X + deltaX, Y = evt.Y + deltaY },
                SandboxInputEventType.Click or SandboxInputEventType.Move =>
                    evt with { X = located.CenterX, Y = located.CenterY },
                _ => evt,
            });
        }
        return result;
    }

    private static (int X, int Y)? FindAnchor(IReadOnlyList<SandboxInputEvent> events, TraceAction action)
    {
        foreach (var evt in events)
        {
            if (evt.X is int x && evt.Y is int y &&
                (evt.Type == SandboxInputEventType.Click || evt.Type == SandboxInputEventType.Move))
            {
                return (x, y);
            }
        }
        // Fall back to the recorded descriptor's centre when no Click/Move is
        // present — covers events sequences that only carry keyboard input.
        var region = action.TargetDescriptor.Visual.Region;
        if (region.Width > 0 && region.Height > 0)
            return (region.X + region.Width / 2, region.Y + region.Height / 2);
        return null;
    }

    private static IReadOnlyList<SandboxInputEvent> CollapseToCentre(
        IReadOnlyList<SandboxInputEvent> events,
        LocatedTarget located)
    {
        var result = new List<SandboxInputEvent>(events.Count);
        foreach (var evt in events)
        {
            result.Add(evt.Type switch
            {
                SandboxInputEventType.Click or SandboxInputEventType.Move =>
                    evt with { X = located.CenterX, Y = located.CenterY },
                _ => evt,
            });
        }
        return result;
    }

    private static string? FirstKey(IReadOnlyList<SandboxInputEvent> events)
    {
        foreach (var e in events)
            if (e.Type == SandboxInputEventType.Key) return e.Key;
        return null;
    }

    private static string? FirstText(IReadOnlyList<SandboxInputEvent> events)
    {
        foreach (var e in events)
            if (e.Type == SandboxInputEventType.Type) return e.Text;
        return null;
    }

    private static bool NeedsLocator(string actionKind) =>
        actionKind is "click" or "double_click" or "move" or "events";

    private async Task<ReplayStepResult> FailAsync(
        ISandbox sandbox,
        TraceEntry entry,
        ReplayFailureKind kind,
        string diagnostic,
        LocatedTarget? locatedTarget,
        CancellationToken ct)
    {
        var screenshot = await TryCaptureScreenshotAsync(sandbox, ct).ConfigureAwait(false);
        return new ReplayStepResult
        {
            Sequence = entry.Sequence,
            ActionKind = entry.Action.Kind,
            Passed = false,
            FailureKind = kind,
            Diagnostic = diagnostic,
            DiagnosticScreenshotPng = screenshot,
            LocatedTarget = locatedTarget,
        };
    }

    private static async Task<byte[]?> TryCaptureScreenshotAsync(ISandbox sandbox, CancellationToken ct)
    {
        try
        {
            return await sandbox.GetScreenshotAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Best-effort diagnostic screenshot — failing to capture it must
            // not mask the underlying step failure (e.g. the sandbox went
            // away mid-step). The step result already carries the categorical
            // failure kind; the missing PNG just means the reporter has no
            // screenshot for this finding.
            return null;
        }
    }

    private static async Task<string?> TryGetAccessibilityTreeAsync(ISandbox sandbox, CancellationToken ct)
    {
        try
        {
            return await sandbox.GetAccessibilityTreeJsonAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Accessibility-tree fetch is auxiliary diagnostic context for the
            // assertion verifier; a transient failure should not abort the
            // step. Verifiers that need the tree will surface "tree is empty"
            // diagnostics when this returns null.
            return null;
        }
    }

    private static string DescribeDescriptor(TraceTargetDescriptor descriptor)
    {
        var acc = descriptor.Accessibility;
        var accPart = acc is null
            ? "no-accessibility"
            : $"role={DiagnosticText.Sanitize(acc.Role ?? "?")} name={DiagnosticText.Sanitize(acc.Name ?? "?")}";
        var region = descriptor.Visual.Region;
        return $"{accPart} region=({region.X},{region.Y} {region.Width}x{region.Height})";
    }
}
