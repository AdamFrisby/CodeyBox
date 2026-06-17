using CodeyBox.Core;
using CodeyBox.ExploratoryTesting;
using CodeyBox.ExploratoryTesting.Replay;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.Tests;

public sealed class ReplayEngineTests
{
    private static readonly byte[] StableScreenshotA = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] StableScreenshotB = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 4, 5, 6];

    private static readonly DateTimeOffset FrozenNow = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------
    // Successful replay
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_SucceedsWhenUiUnchanged()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }),
            TypeEntry(seq: 2, text: "user"));

        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        Assert.Null(result.FailedStep);
        Assert.Equal(2, result.Steps.Count);
        Assert.All(result.Steps, s => Assert.True(s.Passed));
        var clickStep = result.Steps[0];
        Assert.Equal(1, clickStep.Sequence);
        Assert.Equal("click", clickStep.ActionKind);
        Assert.NotNull(clickStep.LocatedTarget);
        Assert.Equal(170, clickStep.LocatedTarget.CenterX); // 150 + 40/2
        Assert.Equal(90, clickStep.LocatedTarget.CenterY);  // 80 + 20/2
        Assert.Equal("accessibility-point", clickStep.LocatedTarget.Source);

        // Drove real input via the bridge — not synthetic selector dispatch.
        var clickInputs = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Click).ToList();
        Assert.Single(clickInputs);
        Assert.Equal(170, clickInputs[0].X);
        Assert.Equal(90, clickInputs[0].Y);
        var typeInputs = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Type).ToList();
        Assert.Single(typeInputs);
        Assert.Equal("user", typeInputs[0].Text);
    }

    // ------------------------------------------------------------------
    // Locator-miss = deterministic FAIL with diagnostic + screenshot
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_FailsWithNotFound_WhenLocatorReturnsNoMatch()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Different"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));

        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.NotNull(result.FailedStep);
        Assert.Equal(ReplayFailureKind.NotFound, result.FailedStep.FailureKind);
        Assert.Contains("step 1", result.FailedStep.Diagnostic);
        Assert.Contains("not found", result.FailedStep.Diagnostic);
        Assert.Equal(StableScreenshotA, result.FailedStep.DiagnosticScreenshotPng);
        Assert.Null(result.FailedStep.LocatedTarget);
    }

    [Fact]
    public async Task Replay_FailsWithNotFound_WhenDescriptorHasNoAccessibility()
    {
        // No accessibility signature on the descriptor — locator strictly
        // surfaces NotFound rather than falling back to raw recorded coords.
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var trace = MakeTrace(
            ClickEntry(seq: 1,
                region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: null));

        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.NotNull(result.FailedStep);
        Assert.Equal(ReplayFailureKind.NotFound, result.FailedStep.FailureKind);
        Assert.Contains("no-accessibility", result.FailedStep.Diagnostic);
        Assert.Empty(sandbox.RecordedInputEvents);
    }

    [Fact]
    public async Task Replay_HealerCanRescue_NotFound_LocatorMiss()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Different"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));
        var healer = new StubHealer(new LocatedTarget
        {
            CenterX = 300,
            CenterY = 200,
            Region = new TraceBoundingRegion { X = 280, Y = 180, Width = 40, Height = 40 },
            Source = "healer-vision",
            Confidence = 0.9,
        });
        var engine = NewEngineFor(sandbox, healer: healer);
        sandbox.AccessibilityAtPoint = (x, y) =>
        {
            if (x == 300 && y == 200) return Accessible("button", "Login");
            return Accessible("button", "Different");
        };

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        Assert.Equal(1, healer.Calls);
        // Drove input at the healer's coordinates, not at the original recorded centre.
        var clicks = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Click).ToList();
        Assert.Single(clicks);
        Assert.Equal(300, clicks[0].X);
        Assert.Equal(200, clicks[0].Y);
    }

    // ------------------------------------------------------------------
    // Off-screen and occluded
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_FailsWithOffScreen_WhenTargetNeverScrollsIntoView()
    {
        // Off-screen recorded centre (Y=1510 in an 800-tall viewport). The
        // sandbox keeps answering with the matching element at that coord
        // — simulating an element that exists somewhere off-screen but
        // never moves into view despite scroll attempts.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1,
                region: new TraceBoundingRegion { X = 200, Y = 1500, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));

        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.NotNull(result.FailedStep);
        Assert.Equal(ReplayFailureKind.OffScreen, result.FailedStep.FailureKind);
        Assert.Contains("outside viewport", result.FailedStep.Diagnostic);
        Assert.Equal(StableScreenshotA, result.FailedStep.DiagnosticScreenshotPng);
        // Scroll attempts produced real scroll events (one per failed attempt).
        var scrolls = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Scroll).ToList();
        Assert.NotEmpty(scrolls);
        Assert.All(scrolls, s => Assert.True((s.X ?? 0) == 0 || (s.Y ?? 0) == 0,
            "scroll events must set only one axis at a time"));
    }

    [Fact]
    public async Task Replay_FailsWithOccluded_WhenDifferentElementSitsOnTop()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 100, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));

        // First call resolves locator (Login). Subsequent calls — by the reachability checker —
        // see a modal blocker on top of the located element.
        var calls = 0;
        sandbox.AccessibilityAtPoint = (_, _) =>
        {
            calls++;
            return calls == 1
                ? Accessible("button", "Login")
                : Accessible("dialog", "Confirm");
        };

        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.NotNull(result.FailedStep);
        Assert.Equal(ReplayFailureKind.Occluded, result.FailedStep.FailureKind);
        Assert.Contains("on top", result.FailedStep.Diagnostic);
    }

    // ------------------------------------------------------------------
    // Assertion mismatch
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_FailsWithAssertionMismatch_OnVisualMatchDifference()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotB)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        var entry = ClickEntry(seq: 1,
            region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
            accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" },
            observationScreenshot: StableScreenshotA);
        entry = entry with { Assertion = new TraceAssertion { Kind = "visual-match" } };
        var trace = MakeTrace(entry);

        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.NotNull(result.FailedStep);
        Assert.Equal(ReplayFailureKind.AssertionMismatch, result.FailedStep.FailureKind);
        Assert.Contains("visual-match", result.FailedStep.Diagnostic);
    }

    [Fact]
    public async Task Replay_PassesAssertion_OnVisualMatchExact()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        var entry = ClickEntry(seq: 1,
            region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
            accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" },
            observationScreenshot: StableScreenshotA);
        entry = entry with { Assertion = new TraceAssertion { Kind = "visual-match" } };
        var trace = MakeTrace(entry);

        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
    }

    [Fact]
    public async Task Replay_PassesAssertion_OnTextContainsInAccessibilityTree()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
            AccessibilityTreeJson = "{\"tree\":[{\"text\":\"Welcome user\"}]}",
        };
        var entry = ClickEntry(seq: 1,
            region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
            accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" });
        entry = entry with
        {
            Assertion = new TraceAssertion { Kind = "text-contains", Detail = "Welcome user" },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
    }

    [Fact]
    public async Task Replay_FailsAssertion_OnTextContainsMissing()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
            AccessibilityTreeJson = "{\"tree\":[]}",
        };
        var entry = ClickEntry(seq: 1,
            region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
            accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" });
        entry = entry with
        {
            Assertion = new TraceAssertion { Kind = "text-contains", Detail = "Welcome" },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.NotNull(result.FailedStep);
        Assert.Equal(ReplayFailureKind.AssertionMismatch, result.FailedStep.FailureKind);
    }

    [Fact]
    public async Task Replay_FailsAssertion_OnElementPresentMissing()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
            AccessibilityTreeJson = "{\"tree\":[{\"role\":\"button\"}]}",
        };
        var entry = ClickEntry(seq: 1,
            region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
            accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" });
        entry = entry with
        {
            Assertion = new TraceAssertion { Kind = "element-present", Detail = "checkout-banner" },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.AssertionMismatch, result.FailedStep!.FailureKind);
        Assert.Contains("element-present", result.FailedStep.Diagnostic);
    }

    // ------------------------------------------------------------------
    // Visual wait under jitter / timeout
    // ------------------------------------------------------------------

    [Fact]
    public async Task VisualWait_AbsorbsArtificiallyJitteredLoad()
    {
        // Frames: jitter, jitter, jitter, then two stable frames in a row.
        var frames = new Queue<byte[]>(new[]
        {
            new byte[] { 1, 1 },
            new byte[] { 2, 2 },
            new byte[] { 3, 3 },
            new byte[] { 9, 9 },
            new byte[] { 9, 9 },
        });
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        sandbox.GetScreenshot = _ => frames.Count > 0
            ? Task.FromResult(frames.Dequeue())
            : Task.FromResult<byte[]>(new byte[] { 9, 9 });

        var clock = new FakeTimeProvider(FrozenNow);
        var wait = new ScreenshotStabilityWait(clock);
        var options = new ReplayOptions
        {
            VisualWaitPollInterval = TimeSpan.FromMilliseconds(50),
            VisualWaitTimeout = TimeSpan.FromSeconds(10),
            StableFrameCount = 2,
        };

        var task = wait.WaitAsync(sandbox, predicate: null, options, CancellationToken.None);
        // Drive the clock past every poll iteration so we converge promptly.
        for (var i = 0; i < 10 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(60));
            await Task.Yield();
        }
        var settled = await task;

        Assert.NotNull(settled);
        Assert.Equal(new byte[] { 9, 9 }, settled);
    }

    [Fact]
    public async Task VisualWait_ReturnsNullOnTimeout_WhenScreenNeverSettles()
    {
        var counter = 0;
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        sandbox.GetScreenshot = _ => Task.FromResult(new byte[] { (byte)(counter++ & 0x7F) });

        var clock = new FakeTimeProvider(FrozenNow);
        var wait = new ScreenshotStabilityWait(clock);
        var options = new ReplayOptions
        {
            VisualWaitPollInterval = TimeSpan.FromMilliseconds(50),
            VisualWaitTimeout = TimeSpan.FromMilliseconds(200),
            StableFrameCount = 2,
        };

        var task = wait.WaitAsync(sandbox, predicate: null, options, CancellationToken.None);
        for (var i = 0; i < 30 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(60));
            await Task.Yield();
        }
        var settled = await task;

        // Never-settling screen is a real failure class — must surface as null
        // so the engine reports WaitTimeout instead of silently proceeding.
        Assert.Null(settled);
        Assert.True(clock.GetUtcNow() - FrozenNow > options.VisualWaitTimeout,
            "wait must consume the full timeout window before reporting failure");
    }

    [Fact]
    public async Task Replay_FailsWithWaitTimeout_WhenScreenNeverSettles()
    {
        // Engine reports WaitTimeout when the default visual wait returns null.
        var counter = 0;
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        sandbox.GetScreenshot = _ => Task.FromResult(new byte[] { (byte)(counter++ & 0x7F) });

        var clock = new FakeTimeProvider(FrozenNow);
        var engine = new ReplayEngine(
            bridge: new ComputerUseBridge(timeProvider: clock),
            visualWait: new ImmediateNullWait(),
            timeProvider: clock);
        var trace = MakeTrace(ClickEntry(seq: 1,
            region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
            accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.WaitTimeout, result.FailedStep!.FailureKind);
        Assert.Contains("did not settle", result.FailedStep.Diagnostic);
    }

    // ------------------------------------------------------------------
    // Determinism + parallel safety
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_RunsDeterministically_AcrossRepeatedInvocations()
    {
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }),
            ClickEntry(seq: 2, region: new TraceBoundingRegion { X = 400, Y = 200, Width = 30, Height = 30 },
                accessibility: new TraceAccessibilityDescriptor { Role = "link", Name = "Next" }));

        async Task<ReplayResult> RunOne()
        {
            var sandbox = new ScriptedSandbox(StableScreenshotA);
            sandbox.AccessibilityAtPoint = (x, y) =>
            {
                if (Math.Abs(x - 170) <= 1 && Math.Abs(y - 90) <= 1) return Accessible("button", "Login");
                if (Math.Abs(x - 415) <= 1 && Math.Abs(y - 215) <= 1) return Accessible("link", "Next");
                return null;
            };
            var engine = NewEngineFor(sandbox);
            return await engine.ReplayAsync(sandbox, trace);
        }

        var r1 = await RunOne();
        var r2 = await RunOne();
        var r3 = await RunOne();

        Assert.True(r1.Passed);
        Assert.True(r2.Passed);
        Assert.True(r3.Passed);
        Assert.Equal(r1.Steps.Count, r2.Steps.Count);
        Assert.Equal(r1.Steps.Count, r3.Steps.Count);
        for (var i = 0; i < r1.Steps.Count; i++)
        {
            Assert.Equal(r1.Steps[i].Sequence, r2.Steps[i].Sequence);
            Assert.Equal(r1.Steps[i].LocatedTarget?.CenterX, r2.Steps[i].LocatedTarget?.CenterX);
            Assert.Equal(r1.Steps[i].LocatedTarget?.CenterY, r2.Steps[i].LocatedTarget?.CenterY);
            Assert.Equal(r1.Steps[i].LocatedTarget?.CenterX, r3.Steps[i].LocatedTarget?.CenterX);
            Assert.Equal(r1.Steps[i].LocatedTarget?.CenterY, r3.Steps[i].LocatedTarget?.CenterY);
        }
    }

    [Fact]
    public async Task Replay_RunsInParallel_AgainstIndependentSandboxes_IncludingAssertions()
    {
        // Each parallel replay carries its own per-step recorded screenshot
        // through VerifyAsync — proves no shared mutable state leaks between
        // concurrent replays sharing one engine + verifier instance.
        var engine = new ReplayEngine(new ComputerUseBridge());

        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            var recorded = new byte[] { 0x10, 0x20, 0x30, (byte)i };
            var sandbox = new ScriptedSandbox(recorded)
            {
                AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
            };
            var entry = ClickEntry(
                seq: 1,
                region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" },
                observationScreenshot: recorded);
            entry = entry with { Assertion = new TraceAssertion { Kind = "visual-match" } };
            var trace = MakeTrace(entry);
            var result = await engine.ReplayAsync(sandbox, trace);
            var click = sandbox.RecordedInputEvents.SingleOrDefault(e => e.Type == SandboxInputEventType.Click);
            return (i, result.Passed, click?.X, click?.Y);
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r =>
        {
            Assert.True(r.Passed);
            Assert.Equal(170, r.X);
            Assert.Equal(90, r.Y);
        });
    }

    // ------------------------------------------------------------------
    // Screenshot-step replay (no locator path)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_RunsScreenshotStep_WithoutLocator()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [],
                Kind = "screenshot",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Visual = new TraceVisualDescriptor
                    {
                        Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 },
                    },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        Assert.Single(result.Steps);
        Assert.Equal("screenshot", result.Steps[0].ActionKind);
        Assert.Empty(sandbox.RecordedInputEvents);
    }

    [Fact]
    public async Task Replay_RunsScreenshotStep_AssertionMatchesRecordedScreenshot()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [],
                Kind = "screenshot",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Visual = new TraceVisualDescriptor
                    {
                        Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 },
                    },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = StableScreenshotA, CapturedAt = FrozenNow },
            Assertion = new TraceAssertion { Kind = "visual-match" },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
    }

    [Fact]
    public async Task Replay_RunsScreenshotStep_AssertionMismatchSurfaces()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotB);
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [],
                Kind = "screenshot",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Visual = new TraceVisualDescriptor
                    {
                        Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 },
                    },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = StableScreenshotA, CapturedAt = FrozenNow },
            Assertion = new TraceAssertion { Kind = "visual-match" },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.AssertionMismatch, result.FailedStep!.FailureKind);
    }

    // ------------------------------------------------------------------
    // Multi-step traces: first failure terminates the replay
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_StopsAtFirstFailure_AndDoesNotAttemptSubsequentSteps()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Different"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }),
            TypeEntry(seq: 2, text: "unreached"));

        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Single(result.Steps);
        Assert.Equal(1, result.FailedStep!.Sequence);
        Assert.DoesNotContain(sandbox.RecordedInputEvents, e => e.Type == SandboxInputEventType.Type);
    }

    // ------------------------------------------------------------------
    // ActionFailed: bridge / dispatch throws on input
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_FailsWithActionFailed_WhenBridgeRejectsInput()
    {
        // SynthesizeInputAsync throws — engine must coerce into ActionFailed
        // step result with a diagnostic, not propagate out of ReplayAsync.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        sandbox.OnSynthesizeInput = _ => throw new InvalidOperationException("bridge offline");
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));

        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.ActionFailed, result.FailedStep!.FailureKind);
        Assert.Contains("input dispatch failed", result.FailedStep.Diagnostic);
    }

    // ------------------------------------------------------------------
    // DefaultAssertionVerifier: shared between concurrent replays
    // ------------------------------------------------------------------

    [Fact]
    public async Task DefaultAssertionVerifier_IsParallelSafe_AcrossSharedInstance()
    {
        // Single verifier instance shared by both replays. With the previous
        // mutable-property contract, one replay's recorded screenshot could
        // leak into the other's comparison.
        var verifier = new DefaultAssertionVerifier();
        var bytesA = new byte[] { 0xAA };
        var bytesB = new byte[] { 0xBB };
        var taskA = verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(bytesA),
            assertion: new TraceAssertion { Kind = "visual-match" },
            currentScreenshotPng: bytesA,
            recordedScreenshotPng: bytesA,
            accessibilitySnapshotJson: null,
            ct: CancellationToken.None);
        var taskB = verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(bytesB),
            assertion: new TraceAssertion { Kind = "visual-match" },
            currentScreenshotPng: bytesB,
            recordedScreenshotPng: bytesB,
            accessibilitySnapshotJson: null,
            ct: CancellationToken.None);

        var diagA = await taskA;
        var diagB = await taskB;

        Assert.Null(diagA);
        Assert.Null(diagB);
    }

    [Fact]
    public async Task DefaultAssertionVerifier_ResolvesNamedRecordedScreenshot_ByDetail()
    {
        var map = new Dictionary<string, byte[]?>(StringComparer.Ordinal)
        {
            ["after-checkout"] = StableScreenshotB,
        };
        var verifier = new DefaultAssertionVerifier(map);

        var diag = await verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(StableScreenshotB),
            assertion: new TraceAssertion { Kind = "visual-match", Detail = "after-checkout" },
            currentScreenshotPng: StableScreenshotB,
            recordedScreenshotPng: StableScreenshotA,
            accessibilitySnapshotJson: null,
            ct: CancellationToken.None);

        Assert.Null(diag);
    }

    [Fact]
    public async Task DefaultAssertionVerifier_UnknownKind_ReturnsDiagnostic()
    {
        var verifier = new DefaultAssertionVerifier();
        var diag = await verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(StableScreenshotA),
            assertion: new TraceAssertion { Kind = "screenshot-similar", Detail = "0.95" },
            currentScreenshotPng: StableScreenshotA,
            recordedScreenshotPng: null,
            accessibilitySnapshotJson: null,
            ct: CancellationToken.None);

        Assert.NotNull(diag);
        Assert.Contains("unsupported assertion kind", diag);
    }

    // ------------------------------------------------------------------
    // ReplayOptions validation
    // ------------------------------------------------------------------

    [Fact]
    public void ReplayOptions_Rejects_ZeroSpiralSearchStep()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayOptions { SpiralSearchStep = 0 });
    }

    [Fact]
    public void ReplayOptions_Rejects_ZeroVisualWaitPollInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayOptions { VisualWaitPollInterval = TimeSpan.Zero });
    }

    [Fact]
    public void ReplayOptions_Rejects_NegativeMaxScrollAttempts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayOptions { MaxScrollAttempts = -1 });
    }

    [Fact]
    public void ReplayOptions_Rejects_ZeroStableFrameCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayOptions { StableFrameCount = 0 });
    }

    // ------------------------------------------------------------------
    // Diagnostic sanitization: untrusted screen text cannot inject newlines
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_SanitizesUntrustedAccessibilityTextInDiagnostics()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => null,
        };
        var trace = MakeTrace(ClickEntry(seq: 1,
            region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
            accessibility: new TraceAccessibilityDescriptor
            {
                Role = "button",
                Name = "Sign\nIn\rFAKE-LOG-LINE",
            }));

        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.NotNull(result.FailedStep!.Diagnostic);
        Assert.DoesNotContain('\n', result.FailedStep.Diagnostic);
        Assert.DoesNotContain('\r', result.FailedStep.Diagnostic);
    }

    // ------------------------------------------------------------------
    // Action-kind coverage: double_click, key, events
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_DispatchesDoubleClick_AtLocatedCentre()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("cell", "row-3"),
        };
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [],
                Kind = "double_click",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = new TraceAccessibilityDescriptor { Role = "cell", Name = "row-3" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 100, Y = 100, Width = 40, Height = 40 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        var clicks = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Click).ToList();
        Assert.Equal(2, clicks.Count); // double_click resolves to two clicks
        Assert.All(clicks, c => Assert.Equal(120, c.X));
        Assert.All(clicks, c => Assert.Equal(120, c.Y));
    }

    [Fact]
    public async Task Replay_DispatchesKey_WithoutLocator()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Enter" }],
                Kind = "key",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        var keys = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Key).ToList();
        Assert.Single(keys);
        Assert.Equal("Enter", keys[0].Key);
    }

    [Fact]
    public async Task Replay_RelocatesEventsSequence_PreservingDragOffsets()
    {
        // 'events' sequence with a recorded mouse-down at (100, 100) and a
        // move to (140, 130) — a drag of (40, 30). The recorded anchor centre
        // is at (100, 100); the located centre is at (200, 200). We expect
        // the move to land at (240, 230), not collapsed onto the centre.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("canvas", "viewport"),
        };
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents =
                [
                    new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 100, Y = 100 },
                    new SandboxInputEvent { Type = SandboxInputEventType.Move, X = 140, Y = 130 },
                ],
                Kind = "events",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = new TraceAccessibilityDescriptor { Role = "canvas", Name = "viewport" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 180, Y = 180, Width = 40, Height = 40 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        var clicks = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Click).ToList();
        var moves = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Move).ToList();
        Assert.Single(clicks);
        Assert.Single(moves);
        // Located centre is (200, 200); anchor (the recorded click at (100, 100))
        // shifts to (200, 200), so the move's delta (40, 30) translates to (240, 230).
        Assert.Equal(200, clicks[0].X);
        Assert.Equal(200, clicks[0].Y);
        Assert.Equal(240, moves[0].X);
        Assert.Equal(230, moves[0].Y);
    }

    // ------------------------------------------------------------------
    // Helpers / doubles
    // ------------------------------------------------------------------

    private static ReplayEngine NewEngineFor(ScriptedSandbox sandbox, ILocatorHealer? healer = null)
    {
        var clock = new FakeTimeProvider(FrozenNow);
        var bridge = new ComputerUseBridge(timeProvider: clock);
        // The default ScreenshotStabilityWait would consume real wall-clock waiting for
        // its poll interval; the immediate-settle wait below short-circuits to the current
        // screenshot so the engine stays test-deterministic without driving the fake clock.
        var visualWait = new ImmediateWait();
        return new ReplayEngine(
            bridge,
            visualWait: visualWait,
            healer: healer,
            timeProvider: clock);
    }

    private static SessionTrace MakeTrace(params TraceEntry[] entries) => new()
    {
        TraceFormatVersion = SessionTrace.CurrentVersion,
        Modality = "web-graphical",
        StartedAt = FrozenNow,
        Entries = entries,
    };

    private static TraceEntry ClickEntry(
        int seq,
        TraceBoundingRegion region,
        TraceAccessibilityDescriptor? accessibility,
        byte[]? observationScreenshot = null)
    {
        var cx = region.X + region.Width / 2;
        var cy = region.Y + region.Height / 2;
        return new TraceEntry
        {
            Sequence = seq,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Click, X = cx, Y = cy }],
                Kind = "click",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = accessibility,
                    Visual = new TraceVisualDescriptor { Region = region },
                },
            },
            Observation = new TraceObservation
            {
                ScreenshotPng = observationScreenshot,
                CapturedAt = FrozenNow + TimeSpan.FromMilliseconds(50),
            },
        };
    }

    private static TraceEntry TypeEntry(int seq, string text)
    {
        return new TraceEntry
        {
            Sequence = seq,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Type, Text = text }],
                Kind = "type",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
    }

    private static SandboxAccessibilitySnapshot Accessible(string role, string name) =>
        new() { Role = role, Name = name };

    private sealed class ScriptedSandbox : ISandbox
    {
        public ScriptedSandbox(byte[] defaultScreenshot)
        {
            GetScreenshot = _ => Task.FromResult(defaultScreenshot);
        }

        public string Id { get; } = "scripted-" + Guid.NewGuid().ToString("N");
        public Func<CancellationToken, Task<byte[]>> GetScreenshot { get; set; }
        public Func<int, int, SandboxAccessibilitySnapshot?>? AccessibilityAtPoint { get; set; }
        public string? AccessibilityTreeJson { get; set; }
        public Action<IReadOnlyList<SandboxInputEvent>>? OnSynthesizeInput { get; set; }

        public List<SandboxInputEvent> RecordedInputEvents { get; } = new();

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default) => GetScreenshot(ct);

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
        {
            OnSynthesizeInput?.Invoke(events);
            // Append AFTER the hook so a throwing hook short-circuits before recording.
            RecordedInputEvents.AddRange(events);
            return Task.CompletedTask;
        }

        public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default)
            => Task.FromResult(AccessibilityAtPoint?.Invoke(x, y));

        public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default)
            => Task.FromResult(AccessibilityTreeJson);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubHealer : ILocatorHealer
    {
        private readonly LocatedTarget _heal;
        public int Calls { get; private set; }
        public StubHealer(LocatedTarget heal) => _heal = heal;
        public Task<LocatedTarget?> HealAsync(ISandbox sandbox, TraceEntry entry, ReplayOptions options, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<LocatedTarget?>(_heal);
        }
    }

    private sealed class ImmediateWait : IVisualWait
    {
        public async Task<byte[]?> WaitAsync(ISandbox sandbox, Func<byte[], bool>? predicate, ReplayOptions options, CancellationToken ct)
        {
            byte[]? shot;
            try { shot = await sandbox.GetScreenshotAsync(ct); }
            catch { shot = null; }
            if (predicate is not null && shot is not null && !predicate(shot)) return null;
            return shot;
        }
    }

    private sealed class ImmediateNullWait : IVisualWait
    {
        // Forces the engine's predicate=null call to fail by returning null,
        // exercising the WaitTimeout reporting path.
        public Task<byte[]?> WaitAsync(ISandbox sandbox, Func<byte[], bool>? predicate, ReplayOptions options, CancellationToken ct)
            => Task.FromResult<byte[]?>(null);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
