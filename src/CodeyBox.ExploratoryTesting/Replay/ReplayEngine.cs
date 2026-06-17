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
/// reused / fanned out across many sandboxes in parallel. Each replay only
/// holds onto the <see cref="ISandbox"/> passed in.</para>
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
        _locator = locator ?? new AccessibilityElementLocator();
        _reachability = reachability ?? new ReachabilityChecker(_bridge);
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
                    Steps = steps,
                    FailedStep = step,
                    Duration = _timeProvider.GetUtcNow() - start,
                };
            }
        }

        return new ReplayResult
        {
            Passed = true,
            Steps = steps,
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
            return await RunScreenshotStepAsync(sandbox, entry, options, ct).ConfigureAwait(false);
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
                    $"step {entry.Sequence} ({action.Kind}): recorded target not found on current screen (descriptor={DescribeDescriptor(action.TargetDescriptor)})",
                    locatedTarget: null,
                    ct).ConfigureAwait(false);
            }

            var reach = await _reachability.EnsureReachableAsync(
                sandbox, located, action.TargetDescriptor.Accessibility, options, ct).ConfigureAwait(false);

            if (reach.Status != ReachabilityStatus.Reachable)
            {
                var kind = reach.Status == ReachabilityStatus.OffScreen
                    ? ReplayFailureKind.OffScreen
                    : ReplayFailureKind.Occluded;
                return await FailAsync(
                    sandbox, entry, kind,
                    $"step {entry.Sequence} ({action.Kind}): {reach.Diagnostic ?? reach.Status.ToString()}",
                    locatedTarget: reach.Target,
                    ct).ConfigureAwait(false);
            }

            located = reach.Target;
        }

        try
        {
            await DispatchActionAsync(sandbox, action, located, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAsync(
                sandbox, entry, ReplayFailureKind.ActionFailed,
                $"step {entry.Sequence} ({action.Kind}): input dispatch failed: {ex.Message}",
                locatedTarget: located,
                ct).ConfigureAwait(false);
        }

        var settled = await _visualWait.WaitAsync(sandbox, predicate: null, options, ct).ConfigureAwait(false);
        if (settled is null)
        {
            return await FailAsync(
                sandbox, entry, ReplayFailureKind.WaitTimeout,
                $"step {entry.Sequence} ({action.Kind}): screen did not settle within {options.VisualWaitTimeout}",
                locatedTarget: located,
                ct).ConfigureAwait(false);
        }

        if (entry.Assertion is { } assertion)
        {
            string? accessibility = null;
            try
            {
                accessibility = await sandbox.GetAccessibilityTreeJsonAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                accessibility = null;
            }

            if (_assertions is DefaultAssertionVerifier defaultVerifier)
            {
                defaultVerifier.CurrentRecordedScreenshot = entry.Observation.ScreenshotPng;
            }

            var diag = await _assertions
                .VerifyAsync(sandbox, assertion, settled, accessibility, ct)
                .ConfigureAwait(false);
            if (diag is not null)
            {
                return new ReplayStepResult
                {
                    Sequence = entry.Sequence,
                    ActionKind = action.Kind,
                    Passed = false,
                    FailureKind = ReplayFailureKind.AssertionMismatch,
                    Diagnostic = $"step {entry.Sequence} ({action.Kind}): assertion '{assertion.Kind}' failed: {diag}",
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
        ReplayOptions options,
        CancellationToken ct)
    {
        try
        {
            await _bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "screenshot" }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAsync(
                sandbox, entry, ReplayFailureKind.ActionFailed,
                $"step {entry.Sequence} (screenshot): {ex.Message}",
                locatedTarget: null,
                ct).ConfigureAwait(false);
        }

        if (entry.Assertion is { } assertion)
        {
            byte[]? current = await TryCaptureScreenshotAsync(sandbox, ct).ConfigureAwait(false);
            string? accessibility = null;
            try
            {
                accessibility = await sandbox.GetAccessibilityTreeJsonAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                accessibility = null;
            }

            if (_assertions is DefaultAssertionVerifier defaultVerifier)
            {
                defaultVerifier.CurrentRecordedScreenshot = entry.Observation.ScreenshotPng;
            }
            var diag = await _assertions
                .VerifyAsync(sandbox, assertion, current, accessibility, ct)
                .ConfigureAwait(false);
            if (diag is not null)
            {
                return new ReplayStepResult
                {
                    Sequence = entry.Sequence,
                    ActionKind = entry.Action.Kind,
                    Passed = false,
                    FailureKind = ReplayFailureKind.AssertionMismatch,
                    Diagnostic = $"step {entry.Sequence} (screenshot): assertion '{assertion.Kind}' failed: {diag}",
                    DiagnosticScreenshotPng = current,
                };
            }
        }
        _ = options;

        return new ReplayStepResult
        {
            Sequence = entry.Sequence,
            ActionKind = entry.Action.Kind,
            Passed = true,
        };
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
            "scroll" => BuildScrollRequest(action, located),
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
                Events = RelocateEvents(action.InputEvents, located),
            },
            _ => throw new NotSupportedException($"Unsupported replay action kind '{action.Kind}'."),
        };
    }

    private static ComputerUseRequest BuildScrollRequest(TraceAction action, LocatedTarget? located)
    {
        var first = action.InputEvents.Count > 0 ? action.InputEvents[0] : null;
        return new ComputerUseRequest
        {
            Action = "scroll",
            X = located?.CenterX ?? first?.X,
            Y = located?.CenterY ?? first?.Y,
            ScrollX = first?.X,
            ScrollY = first?.Y,
        };
    }

    private static IReadOnlyList<SandboxInputEvent> RelocateEvents(
        IReadOnlyList<SandboxInputEvent> events,
        LocatedTarget? located)
    {
        if (located is null) return events;
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
        actionKind is "click" or "double_click" or "move" or "scroll" or "events";

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string DescribeDescriptor(TraceTargetDescriptor descriptor)
    {
        var acc = descriptor.Accessibility;
        var accPart = acc is null
            ? "no-accessibility"
            : $"role={acc.Role ?? "?"} name={acc.Name ?? "?"}";
        return $"{accPart} region=({descriptor.Visual.Region.X},{descriptor.Visual.Region.Y} {descriptor.Visual.Region.Width}x{descriptor.Visual.Region.Height})";
    }
}
