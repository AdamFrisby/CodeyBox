using CodeyBox.Core;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Replays a recorded (or trimmed) <see cref="SessionTrace"/> against a
/// fresh <see cref="AppUnderTestSession"/> as a regression test, driving the
/// app with real keyboard / mouse input via <see cref="ComputerUseBridge"/> - never
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
/// reused / fanned out across many harness sessions in parallel. The recorded
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
    private readonly Func<ComputerUseBridge> _bridgeFactory;
    private readonly IElementLocator _locator;
    private readonly IReachabilityChecker? _reachability;
    private readonly IVisualWait _visualWait;
    private readonly IAssertionVerifier _assertions;
    private readonly ILocatorHealer? _healer;
    private readonly IAccessibilityMatcher _accessibilityMatcher;
    private readonly TimeProvider _timeProvider;

    public ReplayEngine(
        Func<ComputerUseBridge>? bridgeFactory = null,
        IElementLocator? locator = null,
        IReachabilityChecker? reachability = null,
        IVisualWait? visualWait = null,
        IAssertionVerifier? assertions = null,
        ILocatorHealer? healer = null,
        IAccessibilityMatcher? accessibilityMatcher = null,
        TimeProvider? timeProvider = null,
        IOcrTextLocator? ocrTextLocator = null)
    {
        _bridgeFactory = bridgeFactory ?? (() => new ComputerUseBridge());
        var matcher = accessibilityMatcher ?? DefaultAccessibilityMatcher.Instance;
        // Default chain: accessibility-tree recognition first (cheap, exact),
        // then visual-signature recognition for canvas / 3D / untagged targets
        // — the brief's "accessibility tree when present, ELSE visual /
        // OCR / template" contract. Richer template / OCR / vision-LLM
        // locators plug into the same chain via CompositeElementLocator.
        _locator = locator ?? new CompositeElementLocator(
            new AccessibilityElementLocator(matcher),
            new VisualSignatureElementLocator(
                new AccessibilityElementLocator(matcher),
                ocrTextLocator ?? TesseractOcrTextLocator.Instance));
        _reachability = reachability;
        _visualWait = visualWait ?? new ScreenshotStabilityWait(timeProvider);
        _assertions = assertions ?? new DefaultAssertionVerifier();
        _healer = healer;
        _accessibilityMatcher = matcher;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Launch a fresh harness session, replay <paramref name="trace"/>, and
    /// tear the session down. This is the production boundary: every replay
    /// gets its own seeded app instance and per-session input driver.
    /// </summary>
    public async Task<ReplayResult> ReplayAsync(
        IAppUnderTestHarness harness,
        WebAppRecipe recipe,
        SessionTrace trace,
        ReplayOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(trace);

        await using var session = await harness.LaunchAsync(recipe, ct).ConfigureAwait(false);
        return await ReplaySessionAsync(session.Sandbox, session.ComputerUse, trace, options, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Test seam for replaying against an already-created sandbox. Production
    /// callers should use the harness overload so seeded-state and lifecycle
    /// isolation cannot be bypassed.
    /// </summary>
    internal async Task<ReplayResult> ReplayAsync(
        ISandbox sandbox,
        SessionTrace trace,
        ReplayOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(trace);

        var bridge = _bridgeFactory()
            ?? throw new InvalidOperationException("ReplayEngine bridge factory returned null.");
        return await ReplaySessionAsync(sandbox, bridge, trace, options, ct).ConfigureAwait(false);
    }

    private async Task<ReplayResult> ReplaySessionAsync(
        ISandbox sandbox,
        ComputerUseBridge bridge,
        SessionTrace trace,
        ReplayOptions? options,
        CancellationToken ct)
    {
        var opts = options ?? new ReplayOptions();
        var reachability = _reachability ?? new ReachabilityChecker(
            bridge,
            _locator,
            _accessibilityMatcher,
            _visualWait);
        var start = _timeProvider.GetUtcNow();
        var steps = new List<ReplayStepResult>(trace.Entries.Count);

        foreach (var entry in trace.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var step = await ReplayStepAsync(sandbox, bridge, reachability, entry, opts, ct)
                .ConfigureAwait(false);
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
        ComputerUseBridge bridge,
        IReachabilityChecker reachability,
        TraceEntry entry,
        ReplayOptions options,
        CancellationToken ct)
    {
        var action = entry.Action;

        if (action.Kind == "screenshot")
        {
            return await RunScreenshotStepAsync(sandbox, entry, options, ct).ConfigureAwait(false);
        }

        LocatedTarget? located = null;
        var effectiveDescriptor = action.TargetDescriptor;
        if (IsTargetlessKeyboardAction(action))
        {
            return await FailAsync(
                sandbox, entry,
                ReplayFailureKind.NotFound,
                $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): keyboard input has no focus target descriptor; mark the action as global input only for deliberate application-wide shortcuts",
                locatedTarget: null,
                ct).ConfigureAwait(false);
        }
        if (IsTargetlessScrollAction(action))
        {
            return await FailAsync(
                sandbox, entry,
                ReplayFailureKind.NotFound,
                $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): scroll input has no target descriptor; replay refuses to send a blind wheel event at the current pointer",
                locatedTarget: null,
                ct).ConfigureAwait(false);
        }

        if (NeedsLocator(action))
        {
            try
            {
                located = await _locator.LocateAsync(sandbox, effectiveDescriptor, options, ct)
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
                    $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): locator failed: {DiagnosticText.Sanitize(ex.Message)}",
                    locatedTarget: null,
                    ct).ConfigureAwait(false);
            }

            if (located is null && _healer is not null)
            {
                var healed = await _healer.HealAsync(sandbox, entry, options, ct).ConfigureAwait(false);
                if (healed is not null)
                {
                    located = healed.Target;
                    effectiveDescriptor = healed.UpdatedDescriptor ?? effectiveDescriptor;
                }
            }

            if (located is null)
            {
                var scrollSearch = await TryLocateVisualMissByScrollingAsync(
                    sandbox,
                    bridge,
                    effectiveDescriptor,
                    options,
                    ct).ConfigureAwait(false);
                if (scrollSearch.Target is not null)
                {
                    located = scrollSearch.Target;
                }
                else if (scrollSearch.FailureKind is { } failureKind)
                {
                    return await FailAsync(
                        sandbox, entry,
                        failureKind,
                        $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): {DiagnosticText.Sanitize(scrollSearch.Diagnostic ?? failureKind.ToString())}",
                        locatedTarget: null,
                        ct).ConfigureAwait(false);
                }
            }

            if (located is null)
            {
                return await FailAsync(
                    sandbox, entry,
                    ReplayFailureKind.NotFound,
                    $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): recorded target not found on current screen (descriptor={DescribeDescriptor(effectiveDescriptor)})",
                    locatedTarget: null,
                    ct).ConfigureAwait(false);
            }

            ReachabilityOutcome reach;
            try
            {
                reach = await reachability.EnsureReachableAsync(
                    sandbox, located, effectiveDescriptor, options, ct).ConfigureAwait(false);
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
            await DispatchActionAsync(sandbox, bridge, action, located, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MalformedTraceException ex)
        {
            // Recorder bug, not a dispatch failure — surface the recording-
            // shape diagnostic verbatim so triage doesn't dead-end on the
            // generic "input dispatch failed" prefix.
            return await FailAsync(
                sandbox, entry, ReplayFailureKind.ActionFailed,
                $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): malformed recording: {DiagnosticText.Sanitize(ex.Message)}",
                locatedTarget: located,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await FailAsync(
                sandbox, entry, ReplayFailureKind.ActionFailed,
                $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): input dispatch failed: {DiagnosticText.Sanitize(ex.Message)}",
                locatedTarget: located,
                ct).ConfigureAwait(false);
        }

        ObservationWaitResult wait;
        try
        {
            wait = await WaitForObservationAsync(sandbox, entry, options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await FailAsync(
                sandbox, entry, ReplayFailureKind.ActionFailed,
                $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): visual wait failed: {DiagnosticText.Sanitize(ex.Message)}",
                locatedTarget: located,
                ct).ConfigureAwait(false);
        }

        if (wait.AssertionException is not null)
        {
            return BuildAssertionExceptionResult(entry, wait.Assertion!, wait.AssertionException, wait.AssertionScreenshot ?? wait.Screenshot, located);
        }

        if (wait.AssertionDiagnostic is not null)
        {
            return BuildAssertionMismatchResult(entry, wait.Assertion!, wait.AssertionDiagnostic, wait.AssertionScreenshot ?? wait.Screenshot, located);
        }

        var settled = wait.Screenshot;
        if (settled is null)
        {
            return await FailAsync(
                sandbox, entry, ReplayFailureKind.WaitTimeout,
                $"step {entry.Sequence} ({DiagnosticText.Sanitize(action.Kind)}): screen did not settle within {options.VisualWaitTimeout}",
                locatedTarget: located,
                ct).ConfigureAwait(false);
        }

        return new ReplayStepResult
        {
            Sequence = entry.Sequence,
            ActionKind = action.Kind,
            Passed = true,
            LocatedTarget = located,
        };
    }

    private async Task<VisualMissScrollSearchResult> TryLocateVisualMissByScrollingAsync(
        ISandbox sandbox,
        ComputerUseBridge bridge,
        TraceTargetDescriptor descriptor,
        ReplayOptions options,
        CancellationToken ct)
    {
        if (!ShouldScrollSearchAfterVisualMiss(descriptor, options, out var cx, out var cy))
            return VisualMissScrollSearchResult.Skipped;

        if (options.MaxScrollAttempts <= 0)
        {
            return VisualMissScrollSearchResult.Failed(
                ReplayFailureKind.OffScreen,
                $"visual target's recorded click point ({cx},{cy}) is outside viewport ({options.ScreenWidth}x{options.ScreenHeight}) and no scroll attempts are allowed");
        }

        for (var attempt = 1; attempt <= options.MaxScrollAttempts; attempt++)
        {
            var (dx, dy) = ResolveScrollDelta(cx, cy, options);
            var scrollRequest = dx != 0
                ? new ComputerUseRequest { Action = "scroll", ScrollX = dx }
                : new ComputerUseRequest { Action = "scroll", ScrollY = dy };

            try
            {
                await bridge.ExecuteAsync(sandbox, scrollRequest, ct).ConfigureAwait(false);
                var settled = await _visualWait.WaitAsync(sandbox, predicate: null, options, ct)
                    .ConfigureAwait(false);
                if (settled is null)
                {
                    return VisualMissScrollSearchResult.Failed(
                        ReplayFailureKind.OffScreen,
                        $"screen did not settle after scrolling toward off-screen visual target ({cx},{cy}) within {options.VisualWaitTimeout}");
                }

                var relocated = await _locator.LocateAsync(sandbox, descriptor, options, ct)
                    .ConfigureAwait(false);
                if (relocated is not null)
                    return VisualMissScrollSearchResult.Found(relocated);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return VisualMissScrollSearchResult.Failed(
                    ReplayFailureKind.ActionFailed,
                    $"scroll search for off-screen visual target failed: {DiagnosticText.Sanitize(ex.Message)}");
            }
        }

        return VisualMissScrollSearchResult.Failed(
            ReplayFailureKind.OffScreen,
            $"visual target's recorded click point ({cx},{cy}) is outside viewport ({options.ScreenWidth}x{options.ScreenHeight}); target not found after {options.MaxScrollAttempts} scroll attempts");
    }

    private async Task<ReplayStepResult> RunScreenshotStepAsync(
        ISandbox sandbox,
        TraceEntry entry,
        ReplayOptions options,
        CancellationToken ct)
    {
        ObservationWaitResult wait;
        try
        {
            wait = await WaitForObservationAsync(sandbox, entry, options, ct).ConfigureAwait(false);
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

        if (wait.AssertionException is not null)
        {
            return BuildAssertionExceptionResult(entry, wait.Assertion!, wait.AssertionException, wait.AssertionScreenshot ?? wait.Screenshot, located: null);
        }

        if (wait.AssertionDiagnostic is not null)
        {
            return BuildAssertionMismatchResult(entry, wait.Assertion!, wait.AssertionDiagnostic, wait.AssertionScreenshot ?? wait.Screenshot, located: null);
        }

        var current = wait.Screenshot;
        if (current is null)
        {
            return await FailAsync(
                sandbox, entry, ReplayFailureKind.WaitTimeout,
                $"step {entry.Sequence} (screenshot): screen did not settle within {options.VisualWaitTimeout}",
                locatedTarget: null,
                ct).ConfigureAwait(false);
        }

        return new ReplayStepResult
        {
            Sequence = entry.Sequence,
            ActionKind = entry.Action.Kind,
            Passed = true,
        };
    }

    private async Task<ObservationWaitResult> WaitForObservationAsync(
        ISandbox sandbox,
        TraceEntry entry,
        ReplayOptions options,
        CancellationToken ct)
    {
        var assertion = EffectiveAssertion(entry);
        AssertionPollingState? assertionState = assertion is null ? null : new AssertionPollingState();
        Func<byte[], CancellationToken, Task<bool>>? predicate = assertion is null
            ? null
            : async (current, token) =>
            {
                await VerifyAssertionForFrameAsync(sandbox, entry, assertion, current, assertionState!, token)
                    .ConfigureAwait(false);
                return assertionState!.Matched || assertionState.VerifierException is not null;
            };

        var screenshot = await _visualWait.WaitAsync(sandbox, predicate, options, ct).ConfigureAwait(false);
        if (assertion is not null
            && screenshot is not null
            && assertionState is { Matched: false, VerifierException: null })
        {
            await VerifyAssertionForFrameAsync(sandbox, entry, assertion, screenshot, assertionState, ct)
                .ConfigureAwait(false);
        }

        return new ObservationWaitResult(
            screenshot,
            assertion,
            assertionState?.LastDiagnostic,
            assertionState?.VerifierException,
            assertionState?.LastScreenshot);
    }

    private async Task VerifyAssertionForFrameAsync(
        ISandbox sandbox,
        TraceEntry entry,
        TraceAssertion assertion,
        byte[] current,
        AssertionPollingState state,
        CancellationToken ct)
    {
        try
        {
            var accessibility = await TryGetAccessibilityTreeAsync(sandbox, ct).ConfigureAwait(false);
            var diag = await _assertions
                .VerifyAsync(assertion, current, entry.Observation.ScreenshotPng, accessibility, ct)
                .ConfigureAwait(false);
            state.Record(current, diag);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.RecordException(current, ex);
        }
    }

    private static TraceAssertion? EffectiveAssertion(TraceEntry entry)
    {
        if (entry.Assertion is not null) return entry.Assertion;
        if (entry.Observation.ScreenshotPng is { Length: > 0 })
        {
            return new TraceAssertion { Kind = "visual-match" };
        }

        return null;
    }

    private static ReplayStepResult BuildAssertionExceptionResult(
        TraceEntry entry,
        TraceAssertion assertion,
        Exception exception,
        byte[]? screenshot,
        LocatedTarget? located)
        => new()
        {
            Sequence = entry.Sequence,
            ActionKind = entry.Action.Kind,
            Passed = false,
            FailureKind = ReplayFailureKind.AssertionMismatch,
            Diagnostic = $"step {entry.Sequence} ({DiagnosticText.Sanitize(entry.Action.Kind)}): assertion '{DiagnosticText.Sanitize(assertion.Kind)}' verifier threw: {DiagnosticText.Sanitize(exception.Message)}",
            DiagnosticScreenshotPng = screenshot,
            LocatedTarget = located,
        };

    private static ReplayStepResult BuildAssertionMismatchResult(
        TraceEntry entry,
        TraceAssertion assertion,
        string diagnostic,
        byte[]? screenshot,
        LocatedTarget? located)
        => new()
        {
            Sequence = entry.Sequence,
            ActionKind = entry.Action.Kind,
            Passed = false,
            FailureKind = ReplayFailureKind.AssertionMismatch,
            Diagnostic = $"step {entry.Sequence} ({DiagnosticText.Sanitize(entry.Action.Kind)}): assertion '{DiagnosticText.Sanitize(assertion.Kind)}' failed: {DiagnosticText.Sanitize(diagnostic)}",
            DiagnosticScreenshotPng = screenshot,
            LocatedTarget = located,
        };

    private sealed record ObservationWaitResult(
        byte[]? Screenshot,
        TraceAssertion? Assertion,
        string? AssertionDiagnostic,
        Exception? AssertionException,
        byte[]? AssertionScreenshot);

    private sealed class AssertionPollingState
    {
        public bool Matched { get; private set; }
        public string? LastDiagnostic { get; private set; }
        public Exception? VerifierException { get; private set; }
        public byte[]? LastScreenshot { get; private set; }

        public void Record(byte[] screenshot, string? diagnostic)
        {
            LastScreenshot = screenshot;
            Matched = diagnostic is null;
            if (diagnostic is null)
            {
                LastDiagnostic = null;
                return;
            }

            LastDiagnostic = diagnostic;
        }

        public void RecordException(byte[] screenshot, Exception exception)
        {
            LastScreenshot = screenshot;
            Matched = false;
            VerifierException = exception;
        }
    }

    private async Task DispatchActionAsync(
        ISandbox sandbox,
        ComputerUseBridge bridge,
        TraceAction action,
        LocatedTarget? located,
        CancellationToken ct)
    {
        var request = BuildRequestForReplay(action, located);
        await bridge.ExecuteAsync(sandbox, request, ct).ConfigureAwait(false);
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
            "scroll" => BuildScrollRequest(action, located),
            "key" => BuildTargetedKeyRequest(action, located),
            "type" => BuildTargetedTypeRequest(action, located),
            "events" => new ComputerUseRequest
            {
                Action = "events",
                Events = RelocateEvents(action.InputEvents, located, action),
            },
            _ => throw new NotSupportedException($"Unsupported replay action kind '{action.Kind}'."),
        };
    }

    private static ComputerUseRequest BuildTargetedKeyRequest(TraceAction action, LocatedTarget? located)
    {
        var key = FirstKey(action.InputEvents);
        if (located is not null)
        {
            return new ComputerUseRequest
            {
                Action = "events",
                Events =
                [
                    new SandboxInputEvent
                    {
                        Type = SandboxInputEventType.Click,
                        X = located.CenterX,
                        Y = located.CenterY,
                    },
                    new SandboxInputEvent
                    {
                        Type = SandboxInputEventType.Key,
                        Key = key,
                    },
                ],
            };
        }

        return new ComputerUseRequest
        {
            Action = "key",
            Key = key,
        };
    }

    private static ComputerUseRequest BuildTargetedTypeRequest(TraceAction action, LocatedTarget? located)
    {
        var text = FirstText(action.InputEvents);
        if (located is not null)
        {
            return new ComputerUseRequest
            {
                Action = "events",
                Events =
                [
                    new SandboxInputEvent
                    {
                        Type = SandboxInputEventType.Click,
                        X = located.CenterX,
                        Y = located.CenterY,
                    },
                    new SandboxInputEvent
                    {
                        Type = SandboxInputEventType.Type,
                        Text = text,
                    },
                ],
            };
        }

        return new ComputerUseRequest
        {
            Action = "type",
            Text = text,
        };
    }

    private static ComputerUseRequest BuildScrollRequest(TraceAction action, LocatedTarget? located)
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
        if (scrollEvent is null)
        {
            // Recorder bug: a scroll action with no Scroll-typed event in its
            // InputEvents. Surface as a categorical recording-shape failure
            // upfront so operators see "recording has no Scroll event" instead
            // of the bridge validator's generic "Scroll events require a
            // non-zero X or Y amount" once it tries to dispatch null axes.
            throw new MalformedTraceException(
                "scroll action carries no SandboxInputEvent of Type=Scroll (recorder bug)");
        }
        var sx = scrollEvent.X ?? 0;
        var sy = scrollEvent.Y ?? 0;
        if (sx == 0 && sy == 0)
        {
            // Recorder bug: a Scroll event with zero magnitude on both axes
            // would dispatch as a no-op the validator rejects.
            throw new MalformedTraceException(
                "scroll action's Scroll event has zero magnitude on both axes (recorder bug)");
        }
        if (sx != 0 && sy != 0)
        {
            // Drop the smaller-magnitude axis — vertical wins on ties.
            if (Math.Abs(sx) > Math.Abs(sy)) sy = 0;
            else sx = 0;
        }
        if (located is not null)
        {
            return new ComputerUseRequest
            {
                Action = "events",
                Events =
                [
                    new SandboxInputEvent
                    {
                        Type = SandboxInputEventType.Move,
                        X = located.CenterX,
                        Y = located.CenterY,
                    },
                    new SandboxInputEvent
                    {
                        Type = SandboxInputEventType.Scroll,
                        X = sx == 0 ? null : sx,
                        Y = sy == 0 ? null : sy,
                    },
                ],
            };
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
        var anchor = FindAnchor(events);
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

    private static (int X, int Y)? FindAnchor(IReadOnlyList<SandboxInputEvent> events)
    {
        foreach (var evt in events)
        {
            if (evt.X is int x && evt.Y is int y &&
                (evt.Type == SandboxInputEventType.Click || evt.Type == SandboxInputEventType.Move))
            {
                return (x, y);
            }
        }
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

    private static bool NeedsLocator(TraceAction action) =>
        action.Kind switch
        {
            "click" or "double_click" or "move" => true,
            "events" => !IsGlobalKeyboardEventsAction(action),
            "key" => !action.IsGlobalInput && HasUsableTargetSignal(action.TargetDescriptor),
            "scroll" or "type" => HasUsableTargetSignal(action.TargetDescriptor),
            _ => false,
        };

    private static bool IsTargetlessKeyboardAction(TraceAction action) =>
        action.Kind is "key" or "type"
        && !action.IsGlobalInput
        && !HasUsableTargetSignal(action.TargetDescriptor);

    private static bool IsGlobalKeyboardEventsAction(TraceAction action) =>
        action.Kind == "events"
        && action.IsGlobalInput
        && action.InputEvents.Count > 0
        && action.InputEvents.All(e =>
            e.Type == SandboxInputEventType.Key
            && !string.IsNullOrWhiteSpace(e.Key));

    private static bool IsTargetlessScrollAction(TraceAction action) =>
        action.Kind == "scroll"
        && !HasUsableTargetSignal(action.TargetDescriptor);

    private bool ShouldScrollSearchAfterVisualMiss(
        TraceTargetDescriptor descriptor,
        ReplayOptions options,
        out int cx,
        out int cy)
    {
        var visual = descriptor.Visual;
        var region = visual.Region;
        cx = 0;
        cy = 0;

        if (descriptor.Accessibility is { } accessibility
            && _accessibilityMatcher.HasAnyAccessibilitySignal(accessibility))
        {
            return false;
        }

        if (region.Width <= 0 || region.Height <= 0)
            return false;

        if (visual.TemplatePng is not { Length: > 0 }
            && visual.SourceScreenshotPng is not { Length: > 0 }
            && string.IsNullOrWhiteSpace(visual.OcrText))
        {
            return false;
        }

        cx = visual.ClickOffsetX is int offsetX && offsetX >= 0 && offsetX < region.Width
            ? region.X + offsetX
            : region.X + region.Width / 2;
        cy = visual.ClickOffsetY is int offsetY && offsetY >= 0 && offsetY < region.Height
            ? region.Y + offsetY
            : region.Y + region.Height / 2;
        return !PointInViewport(cx, cy, options);
    }

    private static bool PointInViewport(int x, int y, ReplayOptions options)
        => x >= 0 && x < options.ScreenWidth && y >= 0 && y < options.ScreenHeight;

    private static (int Dx, int Dy) ResolveScrollDelta(int x, int y, ReplayOptions options)
    {
        if (y < 0) return (0, -options.ScrollStep);
        if (y >= options.ScreenHeight) return (0, options.ScrollStep);
        if (x < 0) return (-options.ScrollStep, 0);
        if (x >= options.ScreenWidth) return (options.ScrollStep, 0);
        throw new InvalidOperationException(
            $"ResolveScrollDelta invoked for in-viewport point ({x},{y}).");
    }

    private static bool HasUsableTargetSignal(TraceTargetDescriptor descriptor)
    {
        var accessibility = descriptor.Accessibility;
        if (accessibility is not null
            && (!string.IsNullOrEmpty(accessibility.Role)
                || !string.IsNullOrEmpty(accessibility.Name)
                || !string.IsNullOrEmpty(accessibility.Text)
                || !string.IsNullOrEmpty(accessibility.ElementType)))
        {
            return true;
        }

        var visual = descriptor.Visual;
        var region = visual.Region;
        return region.Width > 0 && region.Height > 0
            || visual.TemplatePng is { Length: > 0 }
            || visual.SourceScreenshotPng is { Length: > 0 } && region.Width > 0 && region.Height > 0
            || !string.IsNullOrWhiteSpace(visual.OcrText);
    }

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

    private sealed record VisualMissScrollSearchResult(
        LocatedTarget? Target,
        ReplayFailureKind? FailureKind,
        string? Diagnostic)
    {
        public static VisualMissScrollSearchResult Skipped { get; } = new(null, null, null);
        public static VisualMissScrollSearchResult Found(LocatedTarget target) => new(target, null, null);
        public static VisualMissScrollSearchResult Failed(ReplayFailureKind kind, string diagnostic) => new(null, kind, diagnostic);
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
