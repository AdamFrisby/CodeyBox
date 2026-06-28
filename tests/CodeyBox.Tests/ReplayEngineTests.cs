using CodeyBox.Core;
using CodeyBox.ExploratoryTesting;
using CodeyBox.ExploratoryTesting.Replay;
using CodeyBox.Sandbox.Graphical;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

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
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("list", "tasks"),
        };
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
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("list", "tasks"),
        };
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
            bridgeFactory: () => new ComputerUseBridge(timeProvider: clock),
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
        var engine = new ReplayEngine();

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

    [Fact]
    public async Task Replay_RunsInParallel_ForcesOverlappingAssertionVerification()
    {
        const int replayCount = 4;
        var verifier = new BarrierAssertionVerifier(replayCount);
        var engine = new ReplayEngine(assertions: verifier);

        var tasks = Enumerable.Range(0, replayCount).Select(async i =>
        {
            var recorded = new byte[] { 0x42, (byte)i };
            var sandbox = new ScriptedSandbox(recorded)
            {
                AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
            };
            var entry = ClickEntry(
                seq: 1,
                region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" },
                observationScreenshot: recorded) with
            {
                Assertion = new TraceAssertion { Kind = "visual-match" },
            };
            return await engine.ReplayAsync(sandbox, MakeTrace(entry));
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Passed, r.FailedStep?.Diagnostic));
        Assert.True(verifier.MaxActive > 1, "verifier calls must overlap to prove replay parallel safety");
    }

    // ------------------------------------------------------------------
    // Screenshot-step replay (no locator path)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_RunsScreenshotStep_WithoutLocator()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("list", "tasks"),
        };
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

    [Fact]
    public async Task Replay_ScreenshotStep_WaitsForStableExpectedFrameBeforeAssertion()
    {
        var frames = new Queue<byte[]>(new[]
        {
            StableScreenshotA,
            StableScreenshotB,
            StableScreenshotB,
        });
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        sandbox.GetScreenshot = _ => Task.FromResult(frames.Count > 0 ? frames.Dequeue() : StableScreenshotB);
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
            Observation = new TraceObservation { ScreenshotPng = StableScreenshotB, CapturedAt = FrozenNow },
            Assertion = new TraceAssertion { Kind = "visual-match" },
        };
        var trace = MakeTrace(entry);
        var clock = new FakeTimeProvider(FrozenNow);
        var engine = new ReplayEngine(
            bridgeFactory: () => new ComputerUseBridge(timeProvider: clock),
            visualWait: new ScreenshotStabilityWait(clock),
            timeProvider: clock);
        var options = new ReplayOptions
        {
            VisualWaitPollInterval = TimeSpan.FromMilliseconds(50),
            VisualWaitTimeout = TimeSpan.FromSeconds(10),
            StableFrameCount = 2,
        };

        var task = engine.ReplayAsync(sandbox, trace, options);
        for (var i = 0; i < 10 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(60));
            await Task.Yield();
        }
        var result = await task;

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
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
        var comparer = new BlockingScreenshotComparer(expectedCalls: 2);
        var verifier = new DefaultAssertionVerifier(
            recordedScreenshots: new Dictionary<string, byte[]>(StringComparer.Ordinal),
            screenshotComparer: comparer);
        var bytesA = new byte[] { 0xAA };
        var bytesB = new byte[] { 0xBB };
        var taskA = Task.Run(() => verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(bytesA),
            assertion: new TraceAssertion { Kind = "visual-match" },
            currentScreenshotPng: bytesA,
            recordedScreenshotPng: bytesA,
            accessibilitySnapshotJson: null,
            ct: CancellationToken.None));
        var taskB = Task.Run(() => verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(bytesB),
            assertion: new TraceAssertion { Kind = "visual-match" },
            currentScreenshotPng: bytesB,
            recordedScreenshotPng: bytesB,
            accessibilitySnapshotJson: null,
            ct: CancellationToken.None));

        var diagA = await taskA;
        var diagB = await taskB;

        Assert.Null(diagA);
        Assert.Null(diagB);
        Assert.True(comparer.MaxActive > 1, "comparer calls must overlap to prove shared verifier safety");
    }

    [Fact]
    public async Task DefaultAssertionVerifier_ResolvesNamedRecordedScreenshot_ByDetail()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal)
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
    public void ReplayOptions_Rejects_ZeroRingSearchStep()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayOptions { RingSearchStep = 0 });
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

    [Fact]
    public void ReplayOptions_Rejects_OneStableFrameCount()
    {
        // Lower bound is 2: the wait requires at least one prior frame for
        // the equality comparison, so 1 is functionally impossible.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayOptions { StableFrameCount = 1 });
    }

    [Fact]
    public void ReplayOptions_Rejects_ZeroScreenWidth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayOptions { ScreenWidth = 0 });
    }

    [Fact]
    public void ReplayOptions_Rejects_ZeroScreenHeight()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayOptions { ScreenHeight = 0 });
    }

    [Fact]
    public void ReplayOptions_Rejects_ZeroScrollStep()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayOptions { ScrollStep = 0 });
    }

    [Fact]
    public void ReplayOptions_Rejects_ZeroVisualWaitTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayOptions { VisualWaitTimeout = TimeSpan.Zero });
    }

    [Fact]
    public void ReplayOptions_Rejects_NegativeRingSearchRadius()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayOptions { RingSearchRadius = -1 });
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
    public async Task Replay_DispatchesKey_AfterTargetRecognition()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("textbox", "search"),
        };
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
                    Accessibility = new TraceAccessibilityDescriptor { Role = "textbox", Name = "search" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 10, Y = 20, Width = 100, Height = 20 } },
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
    // Action-kind coverage: scroll, move, empty trace
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_DispatchesScroll_UsesScrollAxesNotXY()
    {
        // Confirms BuildScrollRequest forwards Scroll-typed event magnitudes
        // on ScrollY (NOT X/Y), so the bridge's "two-axis scroll rejected"
        // validator is not tripped by recorded centre coordinates.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("list", "tasks"),
        };
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Scroll, Y = 3 }],
                Kind = "scroll",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = new TraceAccessibilityDescriptor { Role = "list", Name = "tasks" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 10, Y = 10, Width = 300, Height = 300 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        var scrolls = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Scroll).ToList();
        Assert.Single(scrolls);
        // ScrollY=3, no X coordinate spilled in.
        Assert.Null(scrolls[0].X);
        Assert.Equal(3, scrolls[0].Y);
    }

    [Fact]
    public async Task Replay_DispatchesScroll_DropsSmallerAxisWhenRecordingHasTwoAxes()
    {
        // Defensive normalisation: a malformed recording that emitted a
        // two-axis scroll (e.g. X=2, Y=5) must be normalised to a single
        // axis so the bridge's "two-axis scroll rejected" validator is not
        // tripped.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("list", "tasks"),
        };
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = 2, Y = 5 }],
                Kind = "scroll",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = new TraceAccessibilityDescriptor { Role = "list", Name = "tasks" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 10, Y = 10, Width = 300, Height = 300 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        var scrolls = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Scroll).ToList();
        Assert.Single(scrolls);
        Assert.Null(scrolls[0].X);
        Assert.Equal(5, scrolls[0].Y); // larger-magnitude axis (vertical) wins
    }

    [Fact]
    public async Task Replay_DispatchesScroll_UsesHorizontalAxisWhenOnlyXRecorded()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("list", "tasks"),
        };
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = -7 }],
                Kind = "scroll",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = new TraceAccessibilityDescriptor { Role = "list", Name = "tasks" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 10, Y = 10, Width = 300, Height = 300 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, MakeTrace(entry));

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        var scroll = Assert.Single(sandbox.RecordedInputEvents, e => e.Type == SandboxInputEventType.Scroll);
        Assert.Equal(-7, scroll.X);
        Assert.Null(scroll.Y);
    }

    [Fact]
    public async Task Replay_DispatchesScroll_VerticalWinsWhenTwoAxesTie()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("list", "tasks"),
        };
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = 5, Y = -5 }],
                Kind = "scroll",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = new TraceAccessibilityDescriptor { Role = "list", Name = "tasks" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 10, Y = 10, Width = 300, Height = 300 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, MakeTrace(entry));

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        var scroll = Assert.Single(sandbox.RecordedInputEvents, e => e.Type == SandboxInputEventType.Scroll);
        Assert.Null(scroll.X);
        Assert.Equal(-5, scroll.Y);
    }

    [Fact]
    public async Task Replay_DispatchesScroll_SkipsNonScrollEvents()
    {
        // Defence-in-depth: if the recording's first event is not a Scroll
        // (a malformed sequence), the builder must skip it and pull
        // magnitude from a real Scroll event downstream, NOT push pixel
        // coords from the click event as scroll units.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("list", "tasks"),
        };
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents =
                [
                    new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 500, Y = 800 },
                    new SandboxInputEvent { Type = SandboxInputEventType.Scroll, Y = 4 },
                ],
                Kind = "scroll",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = new TraceAccessibilityDescriptor { Role = "list", Name = "tasks" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 10, Y = 10, Width = 300, Height = 300 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        var scrolls = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Scroll).ToList();
        Assert.Single(scrolls);
        Assert.Equal(4, scrolls[0].Y);
        Assert.Null(scrolls[0].X);
    }

    [Fact]
    public async Task Replay_DispatchesMove_AtLocatedCentre()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("slider", "volume"),
        };
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [],
                Kind = "move",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = new TraceAccessibilityDescriptor { Role = "slider", Name = "volume" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 200, Y = 300, Width = 60, Height = 20 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        var moves = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Move).ToList();
        Assert.Single(moves);
        Assert.Equal(230, moves[0].X); // 200 + 60/2
        Assert.Equal(310, moves[0].Y); // 300 + 20/2
    }

    [Fact]
    public async Task Replay_OverEmptyTrace_PassesWithZeroSteps()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var trace = MakeTrace();
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed);
        Assert.Empty(result.Steps);
        Assert.Null(result.FailedStep);
    }

    // ------------------------------------------------------------------
    // Locator coverage: ring fallback + all-null descriptor guard
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_LocatorFindsTargetViaRingScan_WhenCentreMisses()
    {
        // Point probe at recorded centre (170, 90) misses; the ring scan
        // finds a match at (178, 90) — within the default 24-pixel radius
        // at the 8-pixel step. Located centre must be the ring hit, source
        // must be "accessibility-ring", confidence 0.85.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (x, y) =>
            {
                if (x == 178 && y == 90) return Accessible("button", "Login");
                return null;
            },
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        var located = result.Steps[0].LocatedTarget;
        Assert.NotNull(located);
        Assert.Equal(178, located.CenterX);
        Assert.Equal(90, located.CenterY);
        Assert.Equal("accessibility-ring", located.Source);
        Assert.Equal(0.85, located.Confidence);
    }

    [Fact]
    public async Task VisualSignatureLocator_ReturnsHit_WhenCurrentScreenMatchesRecorded()
    {
        // The non-accessibility fallback locator: when the recorder
        // captured a SourceScreenshotPng and the current screen is
        // byte-identical, the locator returns the recorded centre with
        // source = "visual-signature". This is the "ELSE visual / OCR /
        // template" leg of the brief's recognition contract — the rest of
        // the chain falls through to NotFound.
        var recordedSource = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6, 7 };
        var sandbox = new ScriptedSandbox(recordedSource);
        var descriptor = new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 100, Y = 200, Width = 50, Height = 40 },
                SourceScreenshotPng = recordedSource,
            },
        };

        var locator = new VisualSignatureElementLocator();
        var hit = await locator.LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Equal(125, hit.CenterX); // 100 + 50/2
        Assert.Equal(220, hit.CenterY); // 200 + 40/2
        Assert.Equal("visual-signature", hit.Source);
    }

    [Fact]
    public async Task VisualSignatureLocator_ReturnsNull_WhenCurrentScreenDiffers()
    {
        // Tight gate: any byte difference between the recorded and current
        // screens defeats the locator. Conservative by design — we never
        // trust recorded coordinates against a changed visual.
        var sandbox = new ScriptedSandbox(StableScreenshotB);
        var descriptor = new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 100, Y = 200, Width = 50, Height = 40 },
                SourceScreenshotPng = StableScreenshotA,
            },
        };

        var locator = new VisualSignatureElementLocator();
        var hit = await locator.LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.Null(hit);
    }

    [Fact]
    public async Task VisualSignatureLocator_ReturnsNull_WhenNoRecordedSource()
    {
        // No SourceScreenshotPng = no visual-identity check possible.
        // Locator refuses to fall back to raw-coordinate trust.
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var descriptor = new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 100, Y = 200, Width = 50, Height = 40 },
                SourceScreenshotPng = null,
            },
        };

        var locator = new VisualSignatureElementLocator();
        var hit = await locator.LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.Null(hit);
    }

    [Fact]
    public async Task CompositeLocator_PrefersFirstSuccessfulInnerLocator()
    {
        // Confirms the composite chain returns the first non-null hit and
        // skips later locators — the engine's default wiring is
        // accessibility-first, visual-signature-second; the order matters.
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var descriptor = new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 1, Y = 2, Width = 3, Height = 4 } },
        };

        var firstCalls = 0;
        var secondCalls = 0;
        var first = new ScriptedLocator(_ =>
        {
            firstCalls++;
            return new LocatedTarget
            {
                CenterX = 99,
                CenterY = 99,
                Region = descriptor.Visual.Region,
                Source = "stub-first",
                Confidence = 1.0,
            };
        });
        var second = new ScriptedLocator(_ => { secondCalls++; return null; });
        var composite = new CompositeElementLocator(first, second);

        var hit = await composite.LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Equal("stub-first", hit.Source);
        Assert.Equal(1, firstCalls);
        Assert.Equal(0, secondCalls);
    }

    [Fact]
    public async Task Locator_RejectsAllNullAccessibilityDescriptor()
    {
        // Defends against the false-positive bug class where a recorder
        // emits an empty accessibility descriptor and the locator silently
        // matches whatever element the sandbox returns at the recorded
        // centre. The locator must refuse rather than match every probe.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Anything"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { /* all fields null */ }));
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.NotFound, result.FailedStep!.FailureKind);
    }

    // ------------------------------------------------------------------
    // RelocateEvents fallbacks: no pointer anchor + CollapseToCentre
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_RelocatesEventsSequence_WithNoPointerAnchor_PreservesKeyboardEvents()
    {
        // No Click/Move event carries coords (only a Key event), so there is
        // no pointer geometry to relocate. Keyboard events flow through after
        // target recognition and reachability have succeeded.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("textbox", "search"),
        };
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Tab" }],
                Kind = "events",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = new TraceAccessibilityDescriptor { Role = "textbox", Name = "search" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 180, Y = 180, Width = 40, Height = 40 } },
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
        Assert.Equal("Tab", keys[0].Key);
    }

    [Fact]
    public async Task Replay_RelocatesEventsSequence_CollapsesClickEventsOntoLocatedCentre()
    {
        // Healer returns a LocatedTarget for an event sequence where:
        //   - no event carries coords (all Key/Type events)
        //   - descriptor region has zero width (FindAnchor returns null)
        // Engine falls back to CollapseToCentre, which pins any Click/Move
        // event without coords to the located centre — defensive even
        // though no such event exists here.
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var healer = new StubHealer(new LocatedTarget
        {
            CenterX = 500,
            CenterY = 400,
            Region = new TraceBoundingRegion { X = 500, Y = 400, Width = 0, Height = 0 },
            Source = "healer-vision",
            Confidence = 0.9,
        });
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents =
                [
                    new SandboxInputEvent { Type = SandboxInputEventType.Click /* no X/Y */ },
                    new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Enter" },
                ],
                Kind = "events",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox, healer: healer);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        var clicks = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Click).ToList();
        Assert.Single(clicks);
        Assert.Equal(500, clicks[0].X); // collapsed onto located centre
        Assert.Equal(400, clicks[0].Y);
    }

    // ------------------------------------------------------------------
    // Reachability: scroll-then-success + thrown-checker → ActionFailed
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_ScrollsThenRelocates_WhenTargetMovesIntoView()
    {
        // Recorded centre is off-screen (Y=900 in 800-tall viewport). On the
        // second locate (after one scroll attempt), the sandbox simulates a
        // post-scroll layout where the target now sits in-viewport at
        // (220, 600). Engine must re-locate via the locator, NOT extrapolate
        // pre-scroll coordinates, and the step must succeed.
        var locateCalls = 0;
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        sandbox.AccessibilityAtPoint = (x, y) =>
        {
            // Pre-scroll: the target answers at the original Y=910 point.
            // Post-scroll: relocation finds it at Y=600 (the relocate
            // probe queries the recorded centre first).
            if (locateCalls < 2 && x == 220 && y == 910) { locateCalls++; return Accessible("button", "Submit"); }
            if (locateCalls >= 2 && x == 220 && y == 600) return Accessible("button", "Submit");
            // Reachability's post-scroll top-most probe lands at the new
            // located centre too.
            return null;
        };

        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 200, Y = 900, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Submit" }));

        // Use a custom locator double so the second locator call returns a
        // different (in-viewport) hit, simulating the post-scroll layout.
        var locator = new ScriptedLocator(target =>
        {
            locateCalls++;
            if (locateCalls == 1)
            {
                return new LocatedTarget
                {
                    CenterX = 220,
                    CenterY = 910,
                    Region = target.Visual.Region,
                    Source = "accessibility-point",
                    Confidence = 1.0,
                };
            }
            return new LocatedTarget
            {
                CenterX = 220,
                CenterY = 600,
                Region = target.Visual.Region,
                Source = "accessibility-point",
                Confidence = 1.0,
            };
        });
        sandbox.AccessibilityAtPoint = (x, y) =>
        {
            if (x == 220 && y == 600) return Accessible("button", "Submit");
            return null;
        };
        var clock = new FakeTimeProvider(FrozenNow);
        var engine = new ReplayEngine(
            bridgeFactory: () => new ComputerUseBridge(timeProvider: clock),
            locator: locator,
            visualWait: new ImmediateWait(),
            timeProvider: clock);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        // Engine drove a real click at the relocated centre, NOT at the
        // pre-scroll Y=910.
        var clicks = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Click).ToList();
        Assert.Single(clicks);
        Assert.Equal(220, clicks[0].X);
        Assert.Equal(600, clicks[0].Y);
        // Engine issued at least one scroll while bringing the target into view.
        Assert.Contains(sandbox.RecordedInputEvents, e => e.Type == SandboxInputEventType.Scroll);
    }

    [Fact]
    public async Task Replay_FailsWithActionFailed_WhenReachabilityCheckerThrows()
    {
        // A custom reachability checker that throws — must be coerced into
        // an ActionFailed step result, NOT leak out of ReplayAsync.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));
        var clock = new FakeTimeProvider(FrozenNow);
        var engine = new ReplayEngine(
            bridgeFactory: () => new ComputerUseBridge(timeProvider: clock),
            reachability: new ThrowingReachability(new InvalidOperationException("checker offline")),
            visualWait: new ImmediateWait(),
            timeProvider: clock);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.ActionFailed, result.FailedStep!.FailureKind);
        Assert.Contains("reachability check failed", result.FailedStep.Diagnostic);
        Assert.Contains("checker offline", result.FailedStep.Diagnostic);
    }

    [Fact]
    public async Task Replay_FailsWithOccluded_WhenAccessibilityProbeReturnsNullAtLocatedCentre()
    {
        // Visible-leg of the reachability check: an accessibility-tagged
        // target that no longer answers at the located centre is treated
        // as invisible (display:none / opacity:0 / removed-from-tree).
        var calls = 0;
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        sandbox.AccessibilityAtPoint = (_, _) =>
        {
            calls++;
            // First call (locator point probe) succeeds; second call
            // (reachability top-most probe) returns null — element vanished.
            return calls == 1 ? Accessible("button", "Login") : null;
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.Occluded, result.FailedStep!.FailureKind);
        Assert.Contains("no longer visible", result.FailedStep.Diagnostic);
    }

    // ------------------------------------------------------------------
    // RunScreenshotStepAsync: waits for a checkpoint frame
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_FailsWithWaitTimeout_WhenScreenshotWaitCannotCapture()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        sandbox.GetScreenshot = _ => Task.FromException<byte[]>(new InvalidOperationException("framebuffer torn"));
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
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.WaitTimeout, result.FailedStep!.FailureKind);
        Assert.Contains("did not settle", result.FailedStep.Diagnostic);
    }

    // ------------------------------------------------------------------
    // ScreenshotStabilityWait: predicate short-circuit + capture-exception swallow
    // ------------------------------------------------------------------

    [Fact]
    public async Task VisualWait_ShortCircuitsOnPredicateMatch_BeforeStability()
    {
        // First captured frame already satisfies the predicate — the wait
        // must return it immediately without waiting for stability.
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        sandbox.GetScreenshot = _ => Task.FromResult(new byte[] { 0xCA, 0xFE });

        var clock = new FakeTimeProvider(FrozenNow);
        var wait = new ScreenshotStabilityWait(clock);
        var options = new ReplayOptions
        {
            VisualWaitPollInterval = TimeSpan.FromMilliseconds(50),
            VisualWaitTimeout = TimeSpan.FromSeconds(10),
            StableFrameCount = 2,
        };

        var settled = await wait.WaitAsync(
            sandbox,
            predicate: current => current.Length == 2 && current[0] == 0xCA,
            options,
            CancellationToken.None);

        Assert.NotNull(settled);
        Assert.Equal(new byte[] { 0xCA, 0xFE }, settled);
    }

    [Fact]
    public async Task VisualWait_SwallowsScreenshotException_TreatedAsNoFrame()
    {
        // Sandbox throws on the first few polls (e.g. transient framebuffer
        // hiccup), then recovers and emits two equal frames. The wait must
        // not surface the exception — it must absorb the failure and
        // converge on the eventual stability.
        var calls = 0;
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        sandbox.GetScreenshot = _ =>
        {
            calls++;
            if (calls <= 2) return Task.FromException<byte[]>(new InvalidOperationException("framebuffer hiccup"));
            return Task.FromResult(new byte[] { 7, 7 });
        };

        var clock = new FakeTimeProvider(FrozenNow);
        var wait = new ScreenshotStabilityWait(clock);
        var options = new ReplayOptions
        {
            VisualWaitPollInterval = TimeSpan.FromMilliseconds(50),
            VisualWaitTimeout = TimeSpan.FromSeconds(10),
            StableFrameCount = 2,
        };

        var task = wait.WaitAsync(sandbox, predicate: null, options, CancellationToken.None);
        for (var i = 0; i < 20 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(60));
            await Task.Yield();
        }
        var settled = await task;

        Assert.NotNull(settled);
        Assert.Equal(new byte[] { 7, 7 }, settled);
    }

    // ------------------------------------------------------------------
    // DefaultAssertionVerifier: success / null-input / typo'd named recording
    // ------------------------------------------------------------------

    [Fact]
    public async Task DefaultAssertionVerifier_ElementPresent_SuccessPath()
    {
        // Element-present's success branch: substring present in
        // accessibility-tree JSON → returns null (no diagnostic).
        var verifier = new DefaultAssertionVerifier();
        var diag = await verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(StableScreenshotA),
            assertion: new TraceAssertion { Kind = "element-present", Detail = "checkout-banner" },
            currentScreenshotPng: StableScreenshotA,
            recordedScreenshotPng: null,
            accessibilitySnapshotJson: "{\"tree\":[{\"id\":\"checkout-banner\"}]}",
            ct: CancellationToken.None);

        Assert.Null(diag);
    }

    [Fact]
    public async Task DefaultAssertionVerifier_VisualMatch_NullRecorded_ReportsNoRecorded()
    {
        var verifier = new DefaultAssertionVerifier();
        var diag = await verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(StableScreenshotA),
            assertion: new TraceAssertion { Kind = "visual-match" },
            currentScreenshotPng: StableScreenshotA,
            recordedScreenshotPng: null,
            accessibilitySnapshotJson: null,
            ct: CancellationToken.None);

        Assert.NotNull(diag);
        Assert.Contains("no recorded screenshot", diag);
    }

    [Fact]
    public async Task DefaultAssertionVerifier_VisualMatch_NullCurrent_ReportsNoCurrent()
    {
        var verifier = new DefaultAssertionVerifier();
        var diag = await verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(StableScreenshotA),
            assertion: new TraceAssertion { Kind = "visual-match" },
            currentScreenshotPng: null,
            recordedScreenshotPng: StableScreenshotA,
            accessibilitySnapshotJson: null,
            ct: CancellationToken.None);

        Assert.NotNull(diag);
        Assert.Contains("current observation has no screenshot", diag);
    }

    [Fact]
    public async Task DefaultAssertionVerifier_VisualMatch_NamedRecordingMissing_ReportsConfigError()
    {
        // Typo'd named recording: Detail set but the key isn't in the map.
        // Must surface as a configuration-error diagnostic rather than
        // silently falling back to the per-step recorded screenshot (which
        // would compare against the wrong reference).
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["after-checkout"] = StableScreenshotB,
        };
        var verifier = new DefaultAssertionVerifier(map);

        var diag = await verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(StableScreenshotB),
            assertion: new TraceAssertion { Kind = "visual-match", Detail = "aftercheckout" /* typo */ },
            currentScreenshotPng: StableScreenshotB,
            recordedScreenshotPng: StableScreenshotB,
            accessibilitySnapshotJson: null,
            ct: CancellationToken.None);

        Assert.NotNull(diag);
        Assert.Contains("unknown named recording", diag);
        Assert.Contains("aftercheckout", diag);
    }

    [Fact]
    public async Task DefaultAssertionVerifier_TextContains_EmptyDetail_ReportsConfigError()
    {
        var verifier = new DefaultAssertionVerifier();
        var diag = await verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(StableScreenshotA),
            assertion: new TraceAssertion { Kind = "text-contains" /* Detail null */ },
            currentScreenshotPng: StableScreenshotA,
            recordedScreenshotPng: null,
            accessibilitySnapshotJson: "{\"tree\":[]}",
            ct: CancellationToken.None);

        Assert.NotNull(diag);
        Assert.Contains("text-contains assertion has no Detail", diag);
    }

    [Fact]
    public async Task DefaultAssertionVerifier_TextContains_EmptyTree_ReportsConfigError()
    {
        var verifier = new DefaultAssertionVerifier();
        var diag = await verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(StableScreenshotA),
            assertion: new TraceAssertion { Kind = "text-contains", Detail = "Welcome" },
            currentScreenshotPng: StableScreenshotA,
            recordedScreenshotPng: null,
            accessibilitySnapshotJson: null,
            ct: CancellationToken.None);

        Assert.NotNull(diag);
        Assert.Contains("accessibility tree is empty", diag);
    }

    [Fact]
    public async Task DefaultAssertionVerifier_AcceptsCustomScreenshotComparer()
    {
        // Confirms the IScreenshotComparer seam: a custom comparer that
        // accepts any two non-empty PNGs as a match overrides the default
        // byte-equality behaviour.
        var verifier = new DefaultAssertionVerifier(
            recordedScreenshots: new Dictionary<string, byte[]>(StringComparer.Ordinal),
            screenshotComparer: new AlwaysMatchScreenshotComparer());

        var diag = await verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(StableScreenshotA),
            assertion: new TraceAssertion { Kind = "visual-match" },
            currentScreenshotPng: StableScreenshotA,
            recordedScreenshotPng: StableScreenshotB,
            accessibilitySnapshotJson: null,
            ct: CancellationToken.None);

        Assert.Null(diag);
    }

    [Fact]
    public async Task DefaultAssertionVerifier_SurfacesCustomScreenshotMismatchDiagnostic()
    {
        var verifier = new DefaultAssertionVerifier(
            recordedScreenshots: new Dictionary<string, byte[]>(StringComparer.Ordinal),
            screenshotComparer: new AlwaysMismatchScreenshotComparer("perceptual delta too high"));

        var diag = await verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(StableScreenshotA),
            assertion: new TraceAssertion { Kind = "visual-match" },
            currentScreenshotPng: StableScreenshotA,
            recordedScreenshotPng: StableScreenshotB,
            accessibilitySnapshotJson: null,
            ct: CancellationToken.None);

        Assert.Equal("perceptual delta too high", diag);
    }

    [Fact]
    public async Task DefaultAssertionVerifier_ElementPresent_EmptyDetail_ReportsConfigError()
    {
        var verifier = new DefaultAssertionVerifier();
        var diag = await verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(StableScreenshotA),
            assertion: new TraceAssertion { Kind = "element-present", Detail = "" },
            currentScreenshotPng: StableScreenshotA,
            recordedScreenshotPng: null,
            accessibilitySnapshotJson: "{\"tree\":[]}",
            ct: CancellationToken.None);

        Assert.NotNull(diag);
        Assert.Contains("element-present assertion has no Detail", diag);
    }

    [Fact]
    public async Task DefaultAssertionVerifier_ElementPresent_EmptyTree_ReportsConfigError()
    {
        var verifier = new DefaultAssertionVerifier();
        var diag = await verifier.VerifyAsync(
            sandbox: new ScriptedSandbox(StableScreenshotA),
            assertion: new TraceAssertion { Kind = "element-present", Detail = "checkout-banner" },
            currentScreenshotPng: StableScreenshotA,
            recordedScreenshotPng: null,
            accessibilitySnapshotJson: null,
            ct: CancellationToken.None);

        Assert.NotNull(diag);
        Assert.Contains("accessibility tree is empty", diag);
    }

    [Fact]
    public void ExactBytesScreenshotComparer_RejectsLengthMismatch()
    {
        var result = ExactBytesScreenshotComparer.Instance.Compare([1, 2, 3], [1, 2, 3, 4]);

        Assert.False(result.Matches);
    }

    // ------------------------------------------------------------------
    // Real-wait predicate-as-hint contract: a non-matching predicate must
    // still return the stable frame so the engine can run the verifier
    // ------------------------------------------------------------------

    [Fact]
    public async Task VisualWait_ReturnsStableFrame_WhenPredicateNeverMatches()
    {
        // Production regression cover: prior to the fix, the wait gated its
        // stability return on `predicate is null`, so a visual-match
        // assertion whose recorded screenshot differed from the live one
        // surfaced as WaitTimeout instead of the AssertionMismatch the
        // verifier would have produced. Pin the documented "early-stop hint,
        // not a hard gate" contract by exercising the real wait with a
        // predicate that intentionally never matches.
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var frames = new Queue<byte[]>(new[]
        {
            new byte[] { 1, 1 },
            new byte[] { 2, 2 },
            new byte[] { 9, 9 },
            new byte[] { 9, 9 },
        });
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

        var task = wait.WaitAsync(
            sandbox,
            predicate: _ => false,
            options,
            CancellationToken.None);
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
    public async Task Replay_VisualMatchMismatch_SurfacesAssertionMismatch_UnderRealWait()
    {
        // End-to-end pin for the same fix: wire the engine with the real
        // ScreenshotStabilityWait (not the ImmediateWait double) and a
        // visual-match assertion whose recorded screenshot differs from the
        // live one. The wait must converge on stability and hand the frame
        // to the verifier so the engine reports AssertionMismatch, not
        // WaitTimeout.
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

        var clock = new FakeTimeProvider(FrozenNow);
        var engine = new ReplayEngine(
            bridgeFactory: () => new ComputerUseBridge(timeProvider: clock),
            visualWait: new ScreenshotStabilityWait(clock),
            timeProvider: clock);
        var options = new ReplayOptions
        {
            VisualWaitPollInterval = TimeSpan.FromMilliseconds(50),
            VisualWaitTimeout = TimeSpan.FromSeconds(10),
            StableFrameCount = 2,
        };

        var task = engine.ReplayAsync(sandbox, trace, options);
        for (var i = 0; i < 10 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(60));
            await Task.Yield();
        }
        var result = await task;

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.AssertionMismatch, result.FailedStep!.FailureKind);
    }

    [Fact]
    public async Task Replay_VisualMatchSuccess_UnderRealWait_AndPerceptualComparer()
    {
        // Custom comparer accepts any two non-empty PNGs; even with the
        // engine's byte-equality early-stop predicate never matching
        // (live=B, recorded=A), the wait stabilises and hands the frame to
        // the verifier — which accepts via the perceptual comparator. This
        // pins the comparer-seam fix: a perceptual comparer is no longer
        // defeated by the predicate / stability gating interaction.
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

        var clock = new FakeTimeProvider(FrozenNow);
        var verifier = new DefaultAssertionVerifier(
            recordedScreenshots: new Dictionary<string, byte[]>(StringComparer.Ordinal),
            screenshotComparer: new AlwaysMatchScreenshotComparer());
        var engine = new ReplayEngine(
            bridgeFactory: () => new ComputerUseBridge(timeProvider: clock),
            visualWait: new ScreenshotStabilityWait(clock),
            assertions: verifier,
            timeProvider: clock);
        var options = new ReplayOptions
        {
            VisualWaitPollInterval = TimeSpan.FromMilliseconds(50),
            VisualWaitTimeout = TimeSpan.FromSeconds(10),
            StableFrameCount = 2,
        };

        var task = engine.ReplayAsync(sandbox, trace, options);
        for (var i = 0; i < 10 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(60));
            await Task.Yield();
        }
        var result = await task;

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
    }

    [Fact]
    public async Task VisualWait_NeverSettles_ReturnsNull_Exactly_AtTimeout()
    {
        // Tighter off-by-one cover for the stability count: the wait must
        // require StableFrameCount consecutive equal frames, not fewer. A
        // queue of 3 distinct frames followed by a single repeated final
        // frame would satisfy `stable >= StableFrameCount` (the broken
        // off-by-one) but not the correct `stable + 1 >= StableFrameCount`.
        var frames = new Queue<byte[]>(new[]
        {
            new byte[] { 1, 1 },
            new byte[] { 2, 2 },
            new byte[] { 3, 3 },
        });
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        sandbox.GetScreenshot = _ => Task.FromResult(frames.Count > 0
            ? frames.Dequeue()
            : new byte[] { (byte)(Environment.TickCount & 0x7F), 0 });

        var clock = new FakeTimeProvider(FrozenNow);
        var wait = new ScreenshotStabilityWait(clock);
        var options = new ReplayOptions
        {
            VisualWaitPollInterval = TimeSpan.FromMilliseconds(50),
            VisualWaitTimeout = TimeSpan.FromMilliseconds(200),
            StableFrameCount = 3, // need at least 3 consecutive identical frames
        };

        var task = wait.WaitAsync(sandbox, predicate: null, options, CancellationToken.None);
        for (var i = 0; i < 30 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(60));
            await Task.Yield();
        }
        var settled = await task;

        Assert.Null(settled);
    }

    // ------------------------------------------------------------------
    // Coercion contracts: throws in dispatch / verifier / scroll-shape must
    // surface as structured ReplayStepResult, not escape ReplayAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_FailsWithActionFailed_OnUnsupportedActionKind()
    {
        // BuildRequestForReplay throws NotSupportedException for unknown
        // action kinds; the dispatch try/catch must coerce it into a
        // structured ActionFailed step result, not let it abort ReplayAsync.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [],
                Kind = "drag", // not in the dispatch switch — engine must coerce
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = new TraceAccessibilityDescriptor { Role = "button", Name = "Login" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.ActionFailed, result.FailedStep!.FailureKind);
        Assert.Contains("drag", result.FailedStep.Diagnostic);
    }

    [Fact]
    public async Task Replay_FailsWithAssertionMismatch_WhenAssertionVerifierThrows()
    {
        // ReplayStepAsync's verifier-throws branch — coerced to
        // AssertionMismatch with a "verifier threw" diagnostic instead of
        // leaking out of ReplayAsync.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        var entry = ClickEntry(seq: 1,
            region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
            accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" });
        entry = entry with { Assertion = new TraceAssertion { Kind = "visual-match" } };
        var trace = MakeTrace(entry);
        var clock = new FakeTimeProvider(FrozenNow);
        var engine = new ReplayEngine(
            bridgeFactory: () => new ComputerUseBridge(timeProvider: clock),
            visualWait: new ImmediateWait(),
            assertions: new ThrowingAssertionVerifier(new InvalidOperationException("verifier offline")),
            timeProvider: clock);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.AssertionMismatch, result.FailedStep!.FailureKind);
        Assert.Contains("verifier threw", result.FailedStep.Diagnostic);
        Assert.Contains("verifier offline", result.FailedStep.Diagnostic);
    }

    [Fact]
    public async Task Replay_FailsWithAssertionMismatch_WhenScreenshotStepVerifierThrows()
    {
        // RunScreenshotStepAsync's verifier-throws branch — same coercion
        // contract as ReplayStepAsync.
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
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = StableScreenshotA, CapturedAt = FrozenNow },
            Assertion = new TraceAssertion { Kind = "visual-match" },
        };
        var trace = MakeTrace(entry);
        var clock = new FakeTimeProvider(FrozenNow);
        var engine = new ReplayEngine(
            bridgeFactory: () => new ComputerUseBridge(timeProvider: clock),
            visualWait: new ImmediateWait(),
            assertions: new ThrowingAssertionVerifier(new InvalidOperationException("screenshot verifier offline")),
            timeProvider: clock);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.AssertionMismatch, result.FailedStep!.FailureKind);
        Assert.Contains("verifier threw", result.FailedStep.Diagnostic);
        Assert.Contains("(screenshot)", result.FailedStep.Diagnostic);
    }

    [Fact]
    public async Task Replay_FailsWithActionFailed_OnMalformedScrollRecording_NoScrollEvent()
    {
        // A scroll action with no Scroll-typed event in its InputEvents is a
        // recorder bug. Must surface a precise "malformed recording"
        // diagnostic, not the bridge validator's generic "non-zero X or Y"
        // wording.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("list", "tasks"),
        };
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 100, Y = 100 }],
                Kind = "scroll",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = new TraceAccessibilityDescriptor { Role = "list", Name = "tasks" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 10, Y = 10, Width = 300, Height = 300 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.ActionFailed, result.FailedStep!.FailureKind);
        Assert.Contains("malformed recording", result.FailedStep.Diagnostic);
        Assert.Contains("no SandboxInputEvent of Type=Scroll", result.FailedStep.Diagnostic);
    }

    [Fact]
    public async Task Replay_FailsWithActionFailed_OnMalformedScrollRecording_ZeroMagnitude()
    {
        // A Scroll event with both axes zero is dispatch-shaped but
        // semantically a recorder bug. Must surface the precise diagnostic.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("list", "tasks"),
        };
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = 0, Y = 0 }],
                Kind = "scroll",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = new TraceAccessibilityDescriptor { Role = "list", Name = "tasks" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 10, Y = 10, Width = 300, Height = 300 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
        var trace = MakeTrace(entry);
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.ActionFailed, result.FailedStep!.FailureKind);
        Assert.Contains("zero magnitude", result.FailedStep.Diagnostic);
    }

    // ------------------------------------------------------------------
    // Coverage gaps: healer-miss, ring radius boundary, horizontal scroll,
    // probe-throws → Reachable, cancellation propagation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_FailsWithNotFound_WhenLocatorMissAndHealerAlsoReturnsNull()
    {
        // Healer is wired but also returns null — the engine must still
        // surface NotFound, not crash on the second null check.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Different"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));
        var healer = new NullHealer();
        var engine = NewEngineFor(sandbox, healer: healer);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.NotFound, result.FailedStep!.FailureKind);
        Assert.Equal(1, healer.Calls);
        // No input was dispatched.
        Assert.Empty(sandbox.RecordedInputEvents);
    }

    [Fact]
    public async Task Locator_WithRingSearchRadiusZero_DisablesRingScan()
    {
        // Boundary: RingSearchRadius=0 means the ring loop body never runs.
        // The locator still honours the centre probe, but any miss has no
        // recovery; without a ring hit it returns null.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (x, y) =>
            {
                // Only a nearby (not-centre) point answers — would be a ring
                // hit at the default radius, but zero radius disables it.
                if (x == 178 && y == 90) return Accessible("button", "Login");
                return null;
            },
        };
        var descriptor = new TraceTargetDescriptor
        {
            Accessibility = new TraceAccessibilityDescriptor { Role = "button", Name = "Login" },
            Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 } },
        };
        var options = new ReplayOptions { RingSearchRadius = 0 };
        var locator = new AccessibilityElementLocator();
        var hit = await locator.LocateAsync(sandbox, descriptor, options, CancellationToken.None);

        Assert.Null(hit);
    }

    [Fact]
    public async Task Reachability_ScrollsHorizontally_WhenTargetIsOffScreenOnXAxis()
    {
        // Horizontal-scroll branch of ResolveScrollDelta. A target with
        // X >= ScreenWidth must trigger a horizontal scroll (ScrollX), not a
        // vertical one (ScrollY).
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Right"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 1500, Y = 100, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Right" }));
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.OffScreen, result.FailedStep!.FailureKind);
        var scrolls = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Scroll).ToList();
        Assert.NotEmpty(scrolls);
        // Every emitted scroll must be horizontal — never vertical for an
        // X-off-screen target. ScrollX is dispatched via the X field on the
        // SandboxInputEvent (the bridge collapses ScrollX/Y onto X/Y at
        // event-translation time).
        Assert.All(scrolls, s =>
        {
            Assert.NotNull(s.X);
            Assert.True((s.X ?? 0) > 0, "rightward scroll must carry a POSITIVE X magnitude");
            Assert.True((s.Y ?? 0) == 0, "horizontal-only scroll must not carry a Y magnitude");
        });
    }

    [Fact]
    public async Task Reachability_ScrollsUpward_WhenTargetIsAboveViewport()
    {
        // Upward-scroll branch of ResolveScrollDelta (t.CenterY < 0). A
        // target with a recorded centre above Y=0 must trigger a vertical
        // scroll with a NEGATIVE ScrollY magnitude, not a positive one — a
        // bug like dropping the unary minus would silently regress traces
        // whose recorded centre is above the viewport (e.g. a reload /
        // hash-route restore that scrolled the page back to the top).
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Top"),
        };
        // Region centre = (220, -90): X is in viewport, Y is above.
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 200, Y = -100, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Top" }));
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.OffScreen, result.FailedStep!.FailureKind);
        var scrolls = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Scroll).ToList();
        Assert.NotEmpty(scrolls);
        // Every emitted scroll must be vertical AND negative (upward).
        Assert.All(scrolls, s =>
        {
            Assert.NotNull(s.Y);
            Assert.True((s.Y ?? 0) < 0, "upward scroll must carry a NEGATIVE Y magnitude");
            Assert.True((s.X ?? 0) == 0, "vertical-only scroll must not carry an X magnitude");
        });
    }

    [Fact]
    public async Task Reachability_ScrollsLeftward_WhenTargetIsLeftOfViewport()
    {
        // Leftward-scroll branch of ResolveScrollDelta (t.CenterX < 0). A
        // target with a recorded centre left of X=0 must trigger a horizontal
        // scroll with a NEGATIVE ScrollX magnitude. Mirrors the upward
        // branch and protects against the same drop-the-sign regression on
        // the X axis.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Left"),
        };
        // Region centre = (-80, 110): Y is in viewport, X is left of it.
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = -100, Y = 100, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Left" }));
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.OffScreen, result.FailedStep!.FailureKind);
        var scrolls = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Scroll).ToList();
        Assert.NotEmpty(scrolls);
        // Every emitted scroll must be horizontal AND negative (leftward).
        Assert.All(scrolls, s =>
        {
            Assert.NotNull(s.X);
            Assert.True((s.X ?? 0) < 0, "leftward scroll must carry a NEGATIVE X magnitude");
            Assert.True((s.Y ?? 0) == 0, "horizontal-only scroll must not carry a Y magnitude");
        });
    }

    [Fact]
    public async Task Reachability_WithZeroScrollAttempts_FailsOffScreenWithoutScrolling()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Below"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 200, Y = 900, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Below" }));
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace, new ReplayOptions { MaxScrollAttempts = 0 });

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.OffScreen, result.FailedStep!.FailureKind);
        Assert.DoesNotContain(sandbox.RecordedInputEvents, e => e.Type == SandboxInputEventType.Scroll);
    }

    [Fact]
    public async Task Reachability_FailsWithLayoutReflowed_WhenLocatorMissesTwiceAfterScroll()
    {
        // ReachabilityChecker.cs:114-123 emits a distinct OffScreen outcome
        // with a 'locator could not re-find the target ... (layout likely
        // reflowed)' diagnostic when the post-scroll re-locate returns null
        // on two consecutive attempts. The existing
        // Replay_FailsWithOffScreen_WhenTargetNeverScrollsIntoView test
        // keeps the locator hitting at the unchanged recorded centre, so
        // consecutiveRelocateMisses stays at 0 and that branch is never
        // entered — this test specifically arranges the re-locate to fail.
        //
        // Setup: the AccessibilityAtPoint hook returns a matching element on
        // the FIRST probe (the engine's initial Locate) and null on every
        // probe after that (each post-scroll re-locate). The recorded centre
        // is far below the viewport so ring offsets are filtered out by the
        // locator's InScreen guard — guaranteeing the re-locate returns null
        // rather than picking up an in-viewport ring hit.
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var probeCalls = 0;
        sandbox.AccessibilityAtPoint = (_, _) =>
        {
            probeCalls++;
            return probeCalls == 1 ? Accessible("button", "Login") : null;
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
        // Distinct diagnostic wording — distinguishes 'genuinely off-screen
        // after MaxScrollAttempts' from 'locator stopped recognising the
        // target after the scroll.'
        Assert.Contains("layout likely reflowed", result.FailedStep.Diagnostic);
        Assert.Contains("locator could not re-find", result.FailedStep.Diagnostic);
        // Exactly two scrolls — the consecutive-miss bail trips on
        // attempt=1, well before MaxScrollAttempts (default 3) would be
        // reached. A regression that flipped >= 2 to >= 3 (or fell through
        // to the generic OffScreen branch) would record more scrolls.
        var scrolls = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Scroll).ToList();
        Assert.Equal(2, scrolls.Count);
    }

    [Fact]
    public async Task Replay_DrivesClick_WhenDescriptorIsVisualOnly_AndNoAccessibleOverlayIsTopMost()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var probeCalls = 0;
        sandbox.AccessibilityAtPoint = (_, _) =>
        {
            probeCalls++;
            return null;
        };
        var trace = MakeTrace(new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 170, Y = 90 }],
                Kind = "click",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    // No accessibility signature — visual-signature is the
                    // only locator that can resolve this descriptor.
                    Accessibility = null,
                    Visual = new TraceVisualDescriptor
                    {
                        Region = new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                        // Strict pixel-equal source — the locator returns the
                        // recorded centre because the current screen matches
                        // byte-for-byte.
                        SourceScreenshotPng = StableScreenshotA,
                    },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        });

        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        Assert.Equal(1, probeCalls);
        // The located target came from the visual-signature locator, not
        // from accessibility recognition.
        Assert.NotNull(result.Steps[0].LocatedTarget);
        Assert.Equal("visual-signature", result.Steps[0].LocatedTarget!.Source);
        // Real input was dispatched at the located visual-signature centre
        // — the engine drove a real click, not a synthetic selector dispatch.
        var clicks = sandbox.RecordedInputEvents.Where(e => e.Type == SandboxInputEventType.Click).ToList();
        Assert.Single(clicks);
        Assert.Equal(170, clicks[0].X);
        Assert.Equal(90, clicks[0].Y);
    }

    [Fact]
    public async Task Replay_FailsWithOccluded_WhenVisualOnlyTargetHasAccessibleOverlay()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("dialog", "overlay"),
        };
        var trace = MakeTrace(new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 170, Y = 90 }],
                Kind = "click",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Accessibility = null,
                    Visual = new TraceVisualDescriptor
                    {
                        Region = new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                        SourceScreenshotPng = StableScreenshotA,
                    },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        });
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.Occluded, result.FailedStep!.FailureKind);
        Assert.Contains("visual-only target", result.FailedStep.Diagnostic);
        Assert.Empty(sandbox.RecordedInputEvents);
    }

    [Fact]
    public async Task Reachability_TreatsAccessibilityProbeThrow_AsOccluded()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var probeCalls = 0;
        sandbox.AccessibilityAtPoint = (_, _) =>
        {
            probeCalls++;
            // First call (locator point probe) succeeds; second call
            // (reachability top-most probe) throws.
            if (probeCalls == 1) return Accessible("button", "Login");
            throw new InvalidOperationException("probe IPC blip");
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.Occluded, result.FailedStep!.FailureKind);
        Assert.Contains("top-most accessibility probe failed", result.FailedStep.Diagnostic);
        Assert.Empty(sandbox.RecordedInputEvents);
    }

    [Fact]
    public async Task Replay_RethrowsOperationCanceledException_OnCancelledToken()
    {
        // The engine's catch blocks must let OperationCanceledException
        // through (not coerce it into a structured failure). A pre-cancelled
        // token therefore propagates instead of returning a step result.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));
        var engine = NewEngineFor(sandbox);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.ReplayAsync(sandbox, trace, ct: cts.Token));
    }

    [Fact]
    public async Task ScreenshotStabilityWait_RethrowsOperationCanceledException()
    {
        // The wait's screenshot-exception branch must NOT swallow
        // OperationCanceledException — a cancelled token has to propagate so
        // the engine can in turn rethrow it instead of polling on past the
        // cancel point.
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        sandbox.GetScreenshot = ct => Task.FromException<byte[]>(new OperationCanceledException(ct));
        var clock = new FakeTimeProvider(FrozenNow);
        var wait = new ScreenshotStabilityWait(clock);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => wait.WaitAsync(sandbox, predicate: null, new ReplayOptions(), cts.Token));
    }

    // ------------------------------------------------------------------
    // Diagnostic boundaries: text truncation, named-recording predicate
    // bypass, custom matcher
    // ------------------------------------------------------------------

    [Fact]
    public async Task DiagnosticText_TruncatesLongInputs_WithEllipsis()
    {
        // Pin DiagnosticText.Sanitize's truncation boundary. A 400-char Name
        // surfaced through a NotFound diagnostic must be sanitised AND
        // truncated, not echoed verbatim.
        var longName = new string('X', 400);
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => null,
        };
        var trace = MakeTrace(ClickEntry(seq: 1,
            region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
            accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = longName }));
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.False(result.Passed);
        var diag = result.FailedStep!.Diagnostic!;
        Assert.DoesNotContain(longName, diag); // truncated, not echoed verbatim
        Assert.Contains("…", diag);
    }

    [Fact]
    public async Task Replay_NamedRecordingVisualMatch_FallsBackToVerifierResolution()
    {
        // Named-recording visual-match: assertion.Detail set => engine's
        // BuildExpectedStatePredicate returns null => no early-stop. The
        // wait converges on stability and the verifier resolves the named
        // recording from its map. Pins the "Detail-non-empty branch" of
        // BuildExpectedStatePredicate against a future regression that
        // would mis-target the per-step recorded screenshot.
        var sandbox = new ScriptedSandbox(StableScreenshotB)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        var frames = new Queue<byte[]>(new[]
        {
            StableScreenshotA,
            StableScreenshotB,
            StableScreenshotB,
        });
        sandbox.GetScreenshot = _ => Task.FromResult(frames.Count > 0 ? frames.Dequeue() : StableScreenshotB);
        var entry = ClickEntry(seq: 1,
            region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
            accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" },
            observationScreenshot: StableScreenshotA);
        entry = entry with { Assertion = new TraceAssertion { Kind = "visual-match", Detail = "after-checkout" } };
        var trace = MakeTrace(entry);

        var clock = new FakeTimeProvider(FrozenNow);
        var verifier = new DefaultAssertionVerifier(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            // Named recording matches the LIVE screen — engine must compare
            // current against this, not against entry.Observation.ScreenshotPng.
            ["after-checkout"] = StableScreenshotB,
        });
        var engine = new ReplayEngine(
            bridgeFactory: () => new ComputerUseBridge(timeProvider: clock),
            visualWait: new ScreenshotStabilityWait(clock),
            assertions: verifier,
            timeProvider: clock);
        var options = new ReplayOptions
        {
            VisualWaitPollInterval = TimeSpan.FromMilliseconds(50),
            VisualWaitTimeout = TimeSpan.FromSeconds(10),
            StableFrameCount = 2,
        };

        var task = engine.ReplayAsync(sandbox, trace, options);
        for (var i = 0; i < 10 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(60));
            await Task.Yield();
        }
        var result = await task;

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
    }

    [Fact]
    public async Task ReplayEngine_AcceptsCustomAccessibilityMatcher()
    {
        // Pins the new matcher ctor seam: a custom IAccessibilityMatcher
        // wired through the engine must change the matching policy of the
        // default locator AND the default reachability checker without the
        // caller having to wire either component explicitly.
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            // Sandbox returns "Different" role/name; default matcher would
            // refuse, but the custom matcher below trusts the role only.
            AccessibilityAtPoint = (_, _) => Accessible("button", "Different"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));

        var clock = new FakeTimeProvider(FrozenNow);
        var engine = new ReplayEngine(
            bridgeFactory: () => new ComputerUseBridge(timeProvider: clock),
            visualWait: new ImmediateWait(),
            accessibilityMatcher: new RoleOnlyAccessibilityMatcher(),
            timeProvider: clock);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
    }

    [Fact]
    public void DefaultAccessibilityMatcher_UsesTextAndElementTypeSignals()
    {
        var matcher = DefaultAccessibilityMatcher.Instance;
        var expected = new TraceAccessibilityDescriptor
        {
            Text = "Invoice #123",
            ElementType = "button",
        };

        Assert.True(matcher.Matches(
            new SandboxAccessibilitySnapshot { Text = "Invoice #123", ElementType = "button" },
            expected));
        Assert.False(matcher.Matches(
            new SandboxAccessibilitySnapshot { Text = "Invoice #123", ElementType = "link" },
            expected));
        Assert.False(matcher.Matches(
            new SandboxAccessibilitySnapshot { Text = "Invoice #999", ElementType = "button" },
            expected));
    }

    [Fact]
    public async Task Replay_ElementPresentAssertion_DiagnosesWhenAccessibilityTreeFetchThrows()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
            GetAccessibilityTree = _ => Task.FromException<string?>(new InvalidOperationException("tree IPC failed")),
        };
        var entry = ClickEntry(seq: 1,
            region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
            accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }) with
        {
            Assertion = new TraceAssertion { Kind = "element-present", Detail = "checkout-banner" },
        };
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, MakeTrace(entry));

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.AssertionMismatch, result.FailedStep!.FailureKind);
        Assert.Contains("accessibility tree is empty", result.FailedStep.Diagnostic);
    }

    [Fact]
    public async Task ReplayEngine_PublicReplayRejectsNullArguments()
    {
        var engine = new ReplayEngine();
        var recipe = MinimalRecipe();
        var trace = MakeTrace();
        var harness = new ScriptedHarness(new ScriptedSandbox(StableScreenshotA), new ComputerUseBridge());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => engine.ReplayAsync(null!, recipe, trace));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => engine.ReplayAsync(harness, null!, trace));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => engine.ReplayAsync(harness, recipe, null!));
    }

    [Fact]
    public async Task ReplayEngine_InternalReplayRejectsNullArguments()
    {
        var engine = new ReplayEngine();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => engine.ReplayAsync(null!, MakeTrace()));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => engine.ReplayAsync(new ScriptedSandbox(StableScreenshotA), null!));
    }

    [Fact]
    public async Task Replay_UsesHarnessSession_AndDisposesIt()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        var harness = new ScriptedHarness(sandbox, new ComputerUseBridge());
        var recipe = MinimalRecipe();
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Login" }));
        var engine = new ReplayEngine(visualWait: new ImmediateWait());

        var result = await engine.ReplayAsync(harness, recipe, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        Assert.Equal(1, harness.LaunchCalls);
        Assert.Same(recipe, harness.LastRecipe);
        Assert.True(sandbox.Disposed);
        Assert.Contains(sandbox.RecordedInputEvents, e => e.Type == SandboxInputEventType.Click);
    }

    [Fact]
    public async Task Replay_HealerCanReplaceDescriptor_ForReachabilityAndArtifactRewrite()
    {
        var original = new TraceAccessibilityDescriptor { Role = "button", Name = "Old" };
        var healedDescriptor = new TraceTargetDescriptor
        {
            Accessibility = new TraceAccessibilityDescriptor { Role = "button", Name = "New" },
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 280, Y = 180, Width = 40, Height = 40 },
            },
        };
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "New"),
        };
        var healer = new StubHealer(
            new LocatedTarget
            {
                CenterX = 300,
                CenterY = 200,
                Region = healedDescriptor.Visual.Region,
                Source = "healer-vision",
                Confidence = 0.9,
            },
            healedDescriptor);
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
                accessibility: original));
        var engine = NewEngineFor(sandbox, healer);

        var result = await engine.ReplayAsync(sandbox, trace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        Assert.Equal(1, healer.Calls);
    }

    [Fact]
    public async Task AccessibilityLocator_FindsMovedTarget_FromAccessibilityTreeBounds()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityTreeJson = """
                {"nodes":[{"role":"button","name":"Login","bounds":{"x":400,"y":300,"width":80,"height":30}}]}
                """,
            AccessibilityAtPoint = (_, _) => null,
        };
        var descriptor = new TraceTargetDescriptor
        {
            Accessibility = new TraceAccessibilityDescriptor { Role = "button", Name = "Login" },
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 10, Y = 10, Width = 20, Height = 20 },
            },
        };
        var locator = new AccessibilityElementLocator();

        var hit = await locator.LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Equal(440, hit.CenterX);
        Assert.Equal(315, hit.CenterY);
        Assert.Equal("accessibility-tree", hit.Source);
    }

    [Fact]
    public async Task Replay_OffScreenTreeHit_ReachesReachabilityInsteadOfNotFound()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityTreeJson = """
                {"role":"button","name":"Submit","bounds":{"x":200,"y":900,"width":40,"height":20}}
                """,
            AccessibilityAtPoint = (_, _) => Accessible("button", "Submit"),
        };
        var trace = MakeTrace(
            ClickEntry(seq: 1, region: new TraceBoundingRegion { X = 10, Y = 10, Width = 20, Height = 20 },
                accessibility: new TraceAccessibilityDescriptor { Role = "button", Name = "Submit" }));
        var engine = NewEngineFor(sandbox);

        var result = await engine.ReplayAsync(sandbox, trace, new ReplayOptions { MaxScrollAttempts = 0 });

        Assert.False(result.Passed);
        Assert.Equal(ReplayFailureKind.OffScreen, result.FailedStep!.FailureKind);
        Assert.Contains("outside viewport", result.FailedStep.Diagnostic);
    }

    [Fact]
    public async Task AccessibilityLocator_ContinuesRingSearch_WhenPointProbeThrows()
    {
        var calls = 0;
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (x, y) =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("point probe failed");
                return x == 178 && y == 90 ? Accessible("button", "Login") : null;
            },
        };
        var descriptor = new TraceTargetDescriptor
        {
            Accessibility = new TraceAccessibilityDescriptor { Role = "button", Name = "Login" },
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 },
            },
        };

        var hit = await new AccessibilityElementLocator()
            .LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Equal("accessibility-ring", hit.Source);
        Assert.Equal(178, hit.CenterX);
    }

    [Fact]
    public async Task AccessibilityLocator_ReturnsNull_WhenAccessibilityDescriptorHasZeroRegionAndNoTreeBounds()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            AccessibilityAtPoint = (_, _) => Accessible("button", "Login"),
        };
        var descriptor = new TraceTargetDescriptor
        {
            Accessibility = new TraceAccessibilityDescriptor { Role = "button", Name = "Login" },
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 },
            },
        };

        var hit = await new AccessibilityElementLocator()
            .LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.Null(hit);
    }

    [Fact]
    public async Task VisualSignatureLocator_FindsMovedTarget_FromTemplatePng()
    {
        var green = BuildRgbPng(1, 1, [0, 255, 0]);
        var current = BuildRgbPng(3, 1, [255, 0, 0, 0, 0, 255, 0, 255, 0]);
        var sandbox = new ScriptedSandbox(current);
        var descriptor = new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor
            {
                TemplatePng = green,
                Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 1, Height = 1 },
            },
        };

        var hit = await new VisualSignatureElementLocator()
            .LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Equal("visual-template", hit.Source);
        Assert.Equal(2, hit.CenterX);
    }

    [Fact]
    public async Task VisualSignatureLocator_FindsMovedTarget_FromSourceCrop()
    {
        var source = BuildRgbPng(3, 1, [255, 0, 0, 0, 255, 0, 0, 0, 255]);
        var current = BuildRgbPng(5, 1, [0, 0, 255, 255, 0, 0, 0, 0, 255, 255, 255, 255, 0, 255, 0]);
        var sandbox = new ScriptedSandbox(current);
        var descriptor = new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 1, Y = 0, Width = 1, Height = 1 },
                SourceScreenshotPng = source,
            },
        };

        var hit = await new VisualSignatureElementLocator()
            .LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Equal("visual-source-crop", hit.Source);
        Assert.Equal(4, hit.CenterX);
    }

    [Fact]
    public async Task VisualSignatureLocator_UsesOcrTextTreeFallback()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA)
        {
            GetAccessibilityTree = _ => Task.FromResult<string?>(
                """
                {
                  "children": [
                    { "role": "button", "text": "Checkout", "bounds": { "x": 180, "y": 90, "width": 120, "height": 30 } }
                  ]
                }
                """),
        };
        var descriptor = new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 20, Y = 30, Width = 100, Height = 20 },
                OcrText = "Checkout",
            },
        };

        var hit = await new VisualSignatureElementLocator()
            .LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Equal("visual-ocr-tree", hit.Source);
        Assert.Equal(240, hit.CenterX);
        Assert.Equal(105, hit.CenterY);
    }

    [Fact]
    public async Task VisualSignatureLocator_UsesOcrTextPayloadFallback()
    {
        var current = Encoding.UTF8.GetBytes("screen payload with Checkout visible");
        var sandbox = new ScriptedSandbox(current);
        var descriptor = new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 20, Y = 30, Width = 100, Height = 20 },
                OcrText = "Checkout",
            },
        };

        var hit = await new VisualSignatureElementLocator()
            .LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Equal("visual-ocr-payload", hit.Source);
        Assert.Equal(70, hit.CenterX);
    }

    [Fact]
    public async Task VisualSignatureLocator_ReturnsNull_WhenSourceScreenshotIsEmpty()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var descriptor = new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 100, Y = 200, Width = 50, Height = 40 },
                SourceScreenshotPng = [],
            },
        };

        var hit = await new VisualSignatureElementLocator()
            .LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.Null(hit);
    }

    [Fact]
    public async Task VisualSignatureLocator_ReturnsNull_WhenCurrentAndRecordedSourcesHaveDifferentLengths()
    {
        var sandbox = new ScriptedSandbox([1, 2, 3, 4, 5]);
        var descriptor = new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 100, Y = 200, Width = 50, Height = 40 },
                SourceScreenshotPng = [1, 2, 3],
            },
        };

        var hit = await new VisualSignatureElementLocator()
            .LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.Null(hit);
    }

    // ------------------------------------------------------------------
    // CompositeElementLocator constructor validation
    // ------------------------------------------------------------------

    [Fact]
    public void CompositeLocator_Rejects_EmptyLocatorArray()
    {
        Assert.Throws<ArgumentException>(() => new CompositeElementLocator(Array.Empty<IElementLocator>()));
    }

    [Fact]
    public void CompositeLocator_Rejects_NullInnerLocator()
    {
        Assert.Throws<ArgumentException>(() => new CompositeElementLocator(
            new AccessibilityElementLocator(), null!));
    }

    // ------------------------------------------------------------------
    // VisualSignatureElementLocator: zero region + screenshot exception
    // ------------------------------------------------------------------

    [Fact]
    public async Task VisualSignatureLocator_ReturnsNull_OnZeroRegion()
    {
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        var descriptor = new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 },
                SourceScreenshotPng = StableScreenshotA,
            },
        };

        var locator = new VisualSignatureElementLocator();
        var hit = await locator.LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.Null(hit);
    }

    [Fact]
    public async Task VisualSignatureLocator_ReturnsNull_WhenScreenshotThrows()
    {
        // Defends against a regression that would trust the recorded centre
        // on a thrown screenshot (silent stale-coordinate trust).
        var sandbox = new ScriptedSandbox(StableScreenshotA);
        sandbox.GetScreenshot = _ => Task.FromException<byte[]>(new InvalidOperationException("framebuffer torn"));
        var descriptor = new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 100, Y = 200, Width = 50, Height = 40 },
                SourceScreenshotPng = StableScreenshotA,
            },
        };

        var locator = new VisualSignatureElementLocator();
        var hit = await locator.LocateAsync(sandbox, descriptor, new ReplayOptions(), CancellationToken.None);

        Assert.Null(hit);
    }

    // ------------------------------------------------------------------
    // Helpers / doubles
    // ------------------------------------------------------------------

    private static ReplayEngine NewEngineFor(ScriptedSandbox sandbox, ILocatorHealer? healer = null)
    {
        var clock = new FakeTimeProvider(FrozenNow);
        // The default ScreenshotStabilityWait would consume real wall-clock waiting for
        // its poll interval; the immediate-settle wait below short-circuits to the current
        // screenshot so the engine stays test-deterministic without driving the fake clock.
        var visualWait = new ImmediateWait();
        return new ReplayEngine(
            bridgeFactory: () => new ComputerUseBridge(timeProvider: clock),
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
                    Accessibility = new TraceAccessibilityDescriptor { Role = "button", Name = "Login" },
                    Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 150, Y = 80, Width = 40, Height = 20 } },
                },
            },
            Observation = new TraceObservation { ScreenshotPng = null, CapturedAt = FrozenNow },
        };
    }

    private static SandboxAccessibilitySnapshot Accessible(string role, string name) =>
        new() { Role = role, Name = name };

    private static WebAppRecipe MinimalRecipe() => new()
    {
        TargetName = "jobtrack",
        RunCommand = new RecipeStep { Command = ["dotnet", "run"] },
        EntryUrl = "http://localhost:5000",
        BrowserCommand = ["firefox", "$URL"],
    };

    private static byte[] BuildRgbPng(int width, int height, byte[] rgb)
    {
        if (rgb.Length != width * height * 3)
            throw new ArgumentException("RGB payload length does not match dimensions.", nameof(rgb));

        var scanlines = new byte[(width * 3 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (width * 3 + 1);
            scanlines[rowStart] = 0;
            Array.Copy(rgb, y * width * 3, scanlines, rowStart + 1, width * 3);
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WritePngChunk(png, "IHDR", ihdr);
        WritePngChunk(png, "IDAT", CompressZlib(scanlines));
        WritePngChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte[] CompressZlib(byte[] data)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }
        return compressed.ToArray();
    }

    private static void WritePngChunk(Stream png, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        png.Write(Encoding.ASCII.GetBytes(type));
        png.Write(data);
        png.Write([0, 0, 0, 0]);
    }

    private sealed class ScriptedSandbox : ISandbox
    {
        public ScriptedSandbox(byte[] defaultScreenshot)
        {
            GetScreenshot = _ => Task.FromResult(defaultScreenshot);
        }

        public string Id { get; } = "scripted-" + Guid.NewGuid().ToString("N");
        public Func<CancellationToken, Task<byte[]>> GetScreenshot { get; set; }
        public Func<int, int, SandboxAccessibilitySnapshot?>? AccessibilityAtPoint { get; set; }
        public Func<CancellationToken, Task<string?>>? GetAccessibilityTree { get; set; }
        public string? AccessibilityTreeJson { get; set; }
        public Action<IReadOnlyList<SandboxInputEvent>>? OnSynthesizeInput { get; set; }
        public bool Disposed { get; private set; }

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
            => GetAccessibilityTree?.Invoke(ct) ?? Task.FromResult(AccessibilityTreeJson);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubHealer : ILocatorHealer
    {
        private readonly LocatedTarget _heal;
        private readonly TraceTargetDescriptor? _updatedDescriptor;
        public int Calls { get; private set; }
        public StubHealer(LocatedTarget heal, TraceTargetDescriptor? updatedDescriptor = null)
        {
            _heal = heal;
            _updatedDescriptor = updatedDescriptor;
        }
        public Task<LocatorHealResult?> HealAsync(ISandbox sandbox, TraceEntry entry, ReplayOptions options, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<LocatorHealResult?>(new LocatorHealResult
            {
                Target = _heal,
                UpdatedDescriptor = _updatedDescriptor,
            });
        }
    }

    private sealed class ImmediateWait : IVisualWait
    {
        // Mirrors the real ScreenshotStabilityWait contract: a non-null
        // predicate is an early-stop hint, not a hard gate — even when the
        // predicate never matches, the wait returns the settled frame so the
        // engine can run the assertion verifier and produce a precise
        // AssertionMismatch diagnostic instead of a misleading WaitTimeout.
        public async Task<byte[]?> WaitAsync(ISandbox sandbox, Func<byte[], bool>? predicate, ReplayOptions options, CancellationToken ct)
        {
            _ = predicate;
            try { return await sandbox.GetScreenshotAsync(ct); }
            catch { return null; }
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

    private sealed class ScriptedLocator : IElementLocator
    {
        private readonly Func<TraceTargetDescriptor, LocatedTarget?> _produce;
        public ScriptedLocator(Func<TraceTargetDescriptor, LocatedTarget?> produce) => _produce = produce;
        public Task<LocatedTarget?> LocateAsync(
            ISandbox sandbox, TraceTargetDescriptor descriptor, ReplayOptions options, CancellationToken ct)
            => Task.FromResult(_produce(descriptor));
    }

    private sealed class ThrowingReachability : IReachabilityChecker
    {
        private readonly Exception _toThrow;
        public ThrowingReachability(Exception toThrow) => _toThrow = toThrow;
        public Task<ReachabilityOutcome> EnsureReachableAsync(
            ISandbox sandbox, LocatedTarget target, TraceTargetDescriptor descriptor,
            ReplayOptions options, CancellationToken ct)
            => throw _toThrow;
    }

    private sealed class AlwaysMatchScreenshotComparer : IScreenshotComparer
    {
        public ScreenshotComparison Compare(byte[] recorded, byte[] current)
            => new ScreenshotComparison(true);
    }

    private sealed class AlwaysMismatchScreenshotComparer : IScreenshotComparer
    {
        private readonly string _diagnostic;
        public AlwaysMismatchScreenshotComparer(string diagnostic) => _diagnostic = diagnostic;
        public ScreenshotComparison Compare(byte[] recorded, byte[] current)
            => new ScreenshotComparison(false, _diagnostic);
    }

    private sealed class BlockingScreenshotComparer : IScreenshotComparer
    {
        private readonly int _expectedCalls;
        private readonly ManualResetEventSlim _release = new(false);
        private int _entered;
        private int _active;
        private int _maxActive;

        public BlockingScreenshotComparer(int expectedCalls) => _expectedCalls = expectedCalls;
        public int MaxActive => _maxActive;

        public ScreenshotComparison Compare(byte[] recorded, byte[] current)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMax(active);
            if (Interlocked.Increment(ref _entered) == _expectedCalls)
                _release.Set();
            Assert.True(_release.Wait(TimeSpan.FromSeconds(5)), "timed out waiting for comparer overlap");
            Interlocked.Decrement(ref _active);
            return new ScreenshotComparison(recorded.AsSpan().SequenceEqual(current));
        }

        private void UpdateMax(int active)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maxActive);
                if (active <= observed) return;
                if (Interlocked.CompareExchange(ref _maxActive, active, observed) == observed) return;
            }
        }
    }

    private sealed class BarrierAssertionVerifier : IAssertionVerifier
    {
        private readonly int _expectedCalls;
        private readonly TaskCompletionSource _allEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;
        private int _active;
        private int _maxActive;

        public BarrierAssertionVerifier(int expectedCalls) => _expectedCalls = expectedCalls;
        public int MaxActive => _maxActive;

        public async Task<string?> VerifyAsync(
            ISandbox sandbox,
            TraceAssertion assertion,
            byte[]? currentScreenshotPng,
            byte[]? recordedScreenshotPng,
            string? accessibilitySnapshotJson,
            CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMax(active);
            if (Interlocked.Increment(ref _entered) == _expectedCalls)
                _allEntered.TrySetResult();

            await _allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Interlocked.Decrement(ref _active);
            return null;
        }

        private void UpdateMax(int active)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maxActive);
                if (active <= observed) return;
                if (Interlocked.CompareExchange(ref _maxActive, active, observed) == observed) return;
            }
        }
    }

    private sealed class NullHealer : ILocatorHealer
    {
        public int Calls { get; private set; }
        public Task<LocatorHealResult?> HealAsync(ISandbox sandbox, TraceEntry entry, ReplayOptions options, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<LocatorHealResult?>(null);
        }
    }

    private sealed class ThrowingAssertionVerifier : IAssertionVerifier
    {
        private readonly Exception _toThrow;
        public ThrowingAssertionVerifier(Exception toThrow) => _toThrow = toThrow;
        public Task<string?> VerifyAsync(
            ISandbox sandbox,
            TraceAssertion assertion,
            byte[]? currentScreenshotPng,
            byte[]? recordedScreenshotPng,
            string? accessibilitySnapshotJson,
            CancellationToken ct)
            => throw _toThrow;
    }

    private sealed class RoleOnlyAccessibilityMatcher : IAccessibilityMatcher
    {
        public bool Matches(SandboxAccessibilitySnapshot snap, TraceAccessibilityDescriptor expected)
            => string.Equals(snap.Role, expected.Role, StringComparison.Ordinal);

        public bool HasAnyAccessibilitySignal(TraceAccessibilityDescriptor expected)
            => !string.IsNullOrEmpty(expected.Role);
    }

    private sealed class ScriptedHarness : IAppUnderTestHarness
    {
        private readonly ScriptedSandbox _sandbox;
        private readonly ComputerUseBridge _bridge;

        public ScriptedHarness(ScriptedSandbox sandbox, ComputerUseBridge bridge)
        {
            _sandbox = sandbox;
            _bridge = bridge;
        }

        public int LaunchCalls { get; private set; }
        public WebAppRecipe? LastRecipe { get; private set; }

        public Task<AppUnderTestSession> LaunchAsync(WebAppRecipe recipe, CancellationToken ct = default)
        {
            LaunchCalls++;
            LastRecipe = recipe;
            return Task.FromResult(new AppUnderTestSession(_sandbox, _bridge, recipe.EntryUrl, StableScreenshotA));
        }
    }
}
