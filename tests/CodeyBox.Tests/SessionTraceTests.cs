using CodeyBox.Core;
using CodeyBox.ExploratoryTesting;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.Tests;

public sealed class SessionTraceTests
{
    private static readonly byte[] SamplePng =
    [
        137, 80, 78, 71, 13, 10, 26, 10, // PNG signature
    ];

    private static readonly DateTimeOffset FrozenNow = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------
    // Data model — defaults and invariants
    // ------------------------------------------------------------------

    [Fact]
    public void SessionTrace_HasExpectedDefaults()
    {
        var trace = new SessionTrace
        {
            TraceFormatVersion = SessionTrace.CurrentVersion,
            Modality = "web-graphical",
            StartedAt = FrozenNow,
            Entries = [],
        };

        Assert.Equal("1.0", trace.TraceFormatVersion);
        Assert.Equal("web-graphical", trace.Modality);
        Assert.Equal(FrozenNow, trace.StartedAt);
        Assert.Null(trace.EndedAt);
        Assert.Empty(trace.Entries);
        Assert.Null(trace.TargetName);
        Assert.Null(trace.EntryUrl);
    }

    // ------------------------------------------------------------------
    // JSON round-trip
    // ------------------------------------------------------------------

    [Fact]
    public void SessionTrace_RoundTripsThroughJson()
    {
        var original = new SessionTrace
        {
            TraceFormatVersion = SessionTrace.CurrentVersion,
            Modality = "web-graphical",
            StartedAt = FrozenNow,
            EndedAt = FrozenNow + TimeSpan.FromMinutes(5),
            TargetName = "jobtrack",
            EntryUrl = "http://localhost:8080",
            ReadinessScreenshotPng = SamplePng,
            Entries =
            [
                new TraceEntry
                {
                    Sequence = 1,
                    Timestamp = FrozenNow,
                    Action = new TraceAction
                    {
                        InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 100, Y = 200 }],
                        Kind = "click",
                        TargetDescriptor = new TraceTargetDescriptor
                        {
                            Accessibility = new TraceAccessibilityDescriptor
                            {
                                Role = "button",
                                Name = "Login",
                            },
                            Visual = new TraceVisualDescriptor
                            {
                                Region = new TraceBoundingRegion { X = 50, Y = 150, Width = 101, Height = 101 },
                                SourceScreenshotPng = SamplePng,
                            },
                        },
                    },
                    Observation = new TraceObservation
                    {
                        ScreenshotPng = SamplePng,
                        AccessibilitySnapshotJson = "{\"tree\":[]}",
                        CapturedAt = FrozenNow + TimeSpan.FromSeconds(1),
                    },
                },
            ],
        };

        var json = SessionTraceJson.Serialize(original);
        Assert.Contains("\"traceFormatVersion\": \"1.0\"", json);
        Assert.Contains("\"modality\": \"web-graphical\"", json);
        Assert.Contains("\"targetName\": \"jobtrack\"", json);
        Assert.Contains("\"sequence\": 1", json);
        Assert.Contains("\"kind\": \"click\"", json);
        Assert.Contains("\"role\": \"button\"", json);
        Assert.Contains("\"name\": \"Login\"", json);
        Assert.Contains("\"x\": 50", json);
        Assert.Contains("\"y\": 150", json);
        Assert.Contains("\"accessibilitySnapshotJson\"", json);

        var deserialized = SessionTraceJson.Deserialize(json);
        Assert.Equal(original.TraceFormatVersion, deserialized.TraceFormatVersion);
        Assert.Equal(original.Modality, deserialized.Modality);
        Assert.Equal(original.TargetName, deserialized.TargetName);
        Assert.Equal(original.EntryUrl, deserialized.EntryUrl);
        Assert.Equal(original.StartedAt, deserialized.StartedAt);
        Assert.Equal(original.EndedAt, deserialized.EndedAt);
        Assert.NotNull(deserialized.ReadinessScreenshotPng);
        Assert.Equal(SamplePng, deserialized.ReadinessScreenshotPng);
        Assert.Single(deserialized.Entries);

        var entry = deserialized.Entries[0];
        Assert.Equal(1, entry.Sequence);
        Assert.Equal(original.Entries[0].Timestamp, entry.Timestamp);
        Assert.Equal("click", entry.Action.Kind);
        Assert.Single(entry.Action.InputEvents);
        Assert.Equal(SandboxInputEventType.Click, entry.Action.InputEvents[0].Type);
        Assert.Equal(100, entry.Action.InputEvents[0].X);
        Assert.Equal(200, entry.Action.InputEvents[0].Y);
        Assert.Equal("button", entry.Action.TargetDescriptor.Accessibility!.Role);
        Assert.Equal("Login", entry.Action.TargetDescriptor.Accessibility.Name);
        Assert.Equal(50, entry.Action.TargetDescriptor.Visual.Region.X);
        Assert.Equal(150, entry.Action.TargetDescriptor.Visual.Region.Y);
        Assert.Equal(101, entry.Action.TargetDescriptor.Visual.Region.Width);
        Assert.NotNull(entry.Observation.ScreenshotPng);
        Assert.Equal(SamplePng, entry.Observation.ScreenshotPng);
        Assert.Equal("{\"tree\":[]}", entry.Observation.AccessibilitySnapshotJson);
        Assert.Equal(original.Entries[0].Observation.CapturedAt, entry.Observation.CapturedAt);
    }

    [Fact]
    public async Task SessionTrace_RoundTripsThroughFile()
    {
        var trace = new SessionTrace
        {
            TraceFormatVersion = SessionTrace.CurrentVersion,
            Modality = "cli",
            StartedAt = FrozenNow,
            EndedAt = FrozenNow + TimeSpan.FromHours(1),
            Entries = [],
        };

        var path = Path.GetTempFileName();
        try
        {
            await SessionTraceJson.WriteToFileAsync(trace, path);
            var loaded = await SessionTraceJson.ReadFromFileAsync(path);

            Assert.Equal(trace.TraceFormatVersion, loaded.TraceFormatVersion);
            Assert.Equal(trace.Modality, loaded.Modality);
            Assert.Equal(trace.StartedAt, loaded.StartedAt);
            Assert.Equal(trace.EndedAt, loaded.EndedAt);
            Assert.Empty(loaded.Entries);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SessionTrace_Deserialize_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => SessionTraceJson.Deserialize(null!));
    }

    [Fact]
    public void SessionTrace_Serialize_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => SessionTraceJson.Serialize(null!));
    }

    [Fact]
    public void SessionTrace_JsonHandlesNullScreenshotsAndAssertion()
    {
        var trace = new SessionTrace
        {
            TraceFormatVersion = SessionTrace.CurrentVersion,
            Modality = "web-graphical",
            StartedAt = FrozenNow,
            Entries =
            [
                new TraceEntry
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
                    Observation = new TraceObservation
                    {
                        ScreenshotPng = null,
                        CapturedAt = FrozenNow,
                    },
                },
            ],
        };

        var json = SessionTraceJson.Serialize(trace);
        var deserialized = SessionTraceJson.Deserialize(json);

        var entry = deserialized.Entries[0];
        Assert.Null(entry.Observation.ScreenshotPng);
        Assert.Null(entry.Assertion);
        Assert.Null(entry.Action.TargetDescriptor.Accessibility);
        Assert.Null(entry.Action.TargetDescriptor.Visual.SourceScreenshotPng);
        Assert.Null(entry.Observation.AccessibilitySnapshotJson);
    }

    // ------------------------------------------------------------------
    // Recording bridge — trace capture (core actions)
    // ------------------------------------------------------------------

    [Fact]
    public async Task RecordingBridge_CapturesClickActionWithVisualDescriptor()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "click", X = 120, Y = 80 });

        Assert.Single(recorder.Trace.Entries);
        var entry = recorder.Trace.Entries[0];
        Assert.Equal(1, entry.Sequence);
        Assert.Equal("click", entry.Action.Kind);
        Assert.Single(entry.Action.InputEvents);
        Assert.Equal(SandboxInputEventType.Click, entry.Action.InputEvents[0].Type);
        Assert.Equal(120, entry.Action.InputEvents[0].X);
        Assert.Equal(80, entry.Action.InputEvents[0].Y);

        var visual = entry.Action.TargetDescriptor.Visual;
        Assert.Equal(70, visual.Region.X); // 120 - 50
        Assert.Equal(30, visual.Region.Y); // 80 - 50
        Assert.Equal(101, visual.Region.Width);  // 2*50 + 1
        Assert.Equal(101, visual.Region.Height);
        Assert.NotNull(visual.SourceScreenshotPng);
        Assert.Equal(SamplePng, visual.SourceScreenshotPng);

        Assert.NotNull(entry.Observation.ScreenshotPng);
        Assert.Equal(SamplePng, entry.Observation.ScreenshotPng);
        Assert.Null(entry.Assertion);
    }

    [Fact]
    public async Task RecordingBridge_DoubleClickEmitsTwoClickEvents()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "double_click", X = 120, Y = 80 });

        var entry = recorder.Trace.Entries[0];
        Assert.Equal("double_click", entry.Action.Kind);
        Assert.Equal(2, entry.Action.InputEvents.Count);
        Assert.Equal(SandboxInputEventType.Click, entry.Action.InputEvents[0].Type);
        Assert.Equal(120, entry.Action.InputEvents[0].X);
        Assert.Equal(80, entry.Action.InputEvents[0].Y);
        Assert.Equal(SandboxInputEventType.Click, entry.Action.InputEvents[1].Type);
        Assert.Equal(120, entry.Action.InputEvents[1].X);
        Assert.Equal(80, entry.Action.InputEvents[1].Y);
    }

    [Fact]
    public async Task RecordingBridge_AcceptsActionSynonymsAndStoresCanonicalKind()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "left_click", X = 100, Y = 50 });
        var entry1 = recorder.Trace.Entries[0];
        Assert.Equal("click", entry1.Action.Kind);
        Assert.Single(entry1.Action.InputEvents);
        Assert.Equal(SandboxInputEventType.Click, entry1.Action.InputEvents[0].Type);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "mouse_move", X = 200, Y = 300 });
        var entry2 = recorder.Trace.Entries[1];
        Assert.Equal("move", entry2.Action.Kind);
        Assert.Equal(SandboxInputEventType.Move, entry2.Action.InputEvents[0].Type);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "keypress", Key = "Escape" });
        var entry3 = recorder.Trace.Entries[2];
        Assert.Equal("key", entry3.Action.Kind);
        Assert.Equal("Escape", entry3.Action.InputEvents[0].Key);
    }

    [Fact]
    public async Task RecordingBridge_CapturesTypeAction()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "type", Text = "hello world" });

        var entry = recorder.Trace.Entries[0];
        Assert.Equal("type", entry.Action.Kind);
        Assert.Single(entry.Action.InputEvents);
        Assert.Equal(SandboxInputEventType.Type, entry.Action.InputEvents[0].Type);
        Assert.Equal("hello world", entry.Action.InputEvents[0].Text);

        var visual = entry.Action.TargetDescriptor.Visual;
        Assert.Equal(0, visual.Region.Width);  // no coordinates -> zero region
        Assert.Equal(0, visual.Region.Height);
        Assert.NotNull(visual.SourceScreenshotPng);
    }

    [Fact]
    public async Task RecordingBridge_CapturesScreenshotAction()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        var result = await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "screenshot" });

        Assert.Equal(SamplePng, result.ScreenshotPng);
        var entry = recorder.Trace.Entries[0];
        Assert.Equal("screenshot", entry.Action.Kind);
        Assert.Empty(entry.Action.InputEvents);
        Assert.Equal(0, entry.Action.TargetDescriptor.Visual.Region.Width);
        Assert.Null(entry.Action.TargetDescriptor.Visual.SourceScreenshotPng);
        Assert.Equal(SamplePng, entry.Observation.ScreenshotPng);
    }

    [Fact]
    public async Task RecordingBridge_CapturesScrollAction()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "scroll", ScrollY = 3 });

        var entry = recorder.Trace.Entries[0];
        Assert.Equal("scroll", entry.Action.Kind);
        Assert.Single(entry.Action.InputEvents);
        Assert.Equal(SandboxInputEventType.Scroll, entry.Action.InputEvents[0].Type);
        Assert.Equal(3, entry.Action.InputEvents[0].Y);
    }

    [Fact]
    public async Task RecordingBridge_ScrollFallsBackToXWhenScrollXNull()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "scroll", X = 200, ScrollX = null });

        var entry = recorder.Trace.Entries[0];
        Assert.Equal("scroll", entry.Action.Kind);
        Assert.Equal(200, entry.Action.InputEvents[0].X); // fallback from X
        Assert.Null(entry.Action.InputEvents[0].Y);
    }

    [Fact]
    public async Task RecordingBridge_CapturesKeyAction()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "key", Key = "Return" });

        var entry = recorder.Trace.Entries[0];
        Assert.Equal("key", entry.Action.Kind);
        Assert.Single(entry.Action.InputEvents);
        Assert.Equal(SandboxInputEventType.Key, entry.Action.InputEvents[0].Type);
        Assert.Equal("Return", entry.Action.InputEvents[0].Key);
    }

    [Fact]
    public async Task RecordingBridge_CapturesEventsAction()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        var events = new SandboxInputEvent[]
        {
            new() { Type = SandboxInputEventType.Move, X = 10, Y = 20 },
            new() { Type = SandboxInputEventType.Click, X = 10, Y = 20 },
        };
        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "events", Events = events });

        var entry = recorder.Trace.Entries[0];
        Assert.Equal("events", entry.Action.Kind);
        Assert.Equal(2, entry.Action.InputEvents.Count);
        Assert.Equal(SandboxInputEventType.Move, entry.Action.InputEvents[0].Type);
        Assert.Equal(SandboxInputEventType.Click, entry.Action.InputEvents[1].Type);

        events[0] = new SandboxInputEvent { Type = SandboxInputEventType.Type, Text = "mutated" };
        Assert.Equal(SandboxInputEventType.Move, entry.Action.InputEvents[0].Type);
    }

    [Fact]
    public async Task RecordingBridge_CapturesMultipleActionsInSequence()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "move", X = 100, Y = 50 });
        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "click", X = 100, Y = 50 });
        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "type", Text = "test" });
        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "keypress", Key = "Return" });

        Assert.Equal(4, recorder.Trace.Entries.Count);
        Assert.Equal(1, recorder.Trace.Entries[0].Sequence);
        Assert.Equal(2, recorder.Trace.Entries[1].Sequence);
        Assert.Equal(3, recorder.Trace.Entries[2].Sequence);
        Assert.Equal(4, recorder.Trace.Entries[3].Sequence);
    }

    [Fact]
    public async Task RecordingBridge_PropagatesInnerBridgeResult()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        var result = await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "screenshot" });

        Assert.NotNull(result);
        Assert.Equal(SamplePng, result.ScreenshotPng);
        Assert.Equal("screenshot", result.Message);
    }

    [Fact]
    public async Task RecordingBridge_OverrideCropRadius()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var options = new RecordingComputerUseBridgeOptions { TargetCropRadius = 25 };
        var recorder = new RecordingComputerUseBridge(inner, timeProvider, options);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "move", X = 100, Y = 100 });

        var visual = recorder.Trace.Entries[0].Action.TargetDescriptor.Visual;
        Assert.Equal(75, visual.Region.X);   // 100 - 25
        Assert.Equal(75, visual.Region.Y);
        Assert.Equal(51, visual.Region.Width);  // 2*25 + 1
        Assert.Equal(51, visual.Region.Height);
    }

    [Fact]
    public async Task RecordingBridge_OverrideModality()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var options = new RecordingComputerUseBridgeOptions { Modality = "cli" };
        var recorder = new RecordingComputerUseBridge(inner, timeProvider, options);

        Assert.Equal("cli", recorder.Trace.Modality);
    }

    [Fact]
    public async Task RecordingBridge_ClampsRegionToZero()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "click", X = 10, Y = 5 });

        var visual = recorder.Trace.Entries[0].Action.TargetDescriptor.Visual;
        Assert.Equal(0, visual.Region.X); // clamped
        Assert.Equal(0, visual.Region.Y); // clamped
    }

    [Fact]
    public async Task RecordingBridge_HandlesScreenshotFailureGracefully()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new ThrowingScreenshotSandbox(new InvalidOperationException("display gone"));
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "click", X = 50, Y = 50 });

        var entry = recorder.Trace.Entries[0];
        Assert.Equal("click", entry.Action.Kind);
        Assert.Null(entry.Action.TargetDescriptor.Visual.SourceScreenshotPng);
        Assert.Null(entry.Observation.ScreenshotPng);
    }

    // ------------------------------------------------------------------
    // Recording bridge — metadata and lifecycle
    // ------------------------------------------------------------------

    [Fact]
    public void RecordingBridge_SetMetadata_UpdatesTrace()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        recorder.SetMetadata(
            targetName: "jobtrack",
            entryUrl: "http://localhost:8080",
            readinessScreenshotPng: SamplePng);

        Assert.Equal("jobtrack", recorder.Trace.TargetName);
        Assert.Equal("http://localhost:8080", recorder.Trace.EntryUrl);
        Assert.Equal(SamplePng, recorder.Trace.ReadinessScreenshotPng);
    }

    [Fact]
    public void RecordingBridge_SetMetadata_NullPreserversPriorValue()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        recorder.SetMetadata(targetName: "first");
        recorder.SetMetadata(entryUrl: "http://example.com");

        Assert.Equal("first", recorder.Trace.TargetName);
        Assert.Equal("http://example.com", recorder.Trace.EntryUrl);
    }

    [Fact]
    public async Task RecordingBridge_EndTrace_SetsEndedAtAndPreservesEntries()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "screenshot" });
        Assert.Null(recorder.Trace.EndedAt);
        Assert.Single(recorder.Trace.Entries);

        recorder.EndTrace();
        Assert.NotNull(recorder.Trace.EndedAt);
        Assert.Equal(FrozenNow, recorder.Trace.EndedAt);
        Assert.Single(recorder.Trace.Entries);
    }

    // ------------------------------------------------------------------
    // Recording bridge — accessibility capture
    // ------------------------------------------------------------------

    [Fact]
    public async Task RecordingBridge_CapturesAccessibilityDescriptorsWhenAvailable()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new AccessibleGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "click", X = 120, Y = 80 });

        var entry = recorder.Trace.Entries[0];
        Assert.NotNull(entry.Action.TargetDescriptor.Accessibility);
        Assert.Equal("button", entry.Action.TargetDescriptor.Accessibility.Role);
        Assert.Equal("Submit", entry.Action.TargetDescriptor.Accessibility.Name);
        Assert.Equal("Submit", entry.Action.TargetDescriptor.Accessibility.Text);
        Assert.NotNull(entry.Observation.AccessibilitySnapshotJson);
        Assert.Equal("{\"tree\":[{\"role\":\"button\"}]}", entry.Observation.AccessibilitySnapshotJson);
    }

    [Fact]
    public async Task RecordingBridge_AccessibilityDefaultsToNullWhenSandboxReturnsNull()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "click", X = 50, Y = 50 });

        var entry = recorder.Trace.Entries[0];
        Assert.Null(entry.Action.TargetDescriptor.Accessibility);
        Assert.Null(entry.Observation.AccessibilitySnapshotJson);
    }

    [Fact]
    public async Task RecordingBridge_TypeActionSkipsAccessibilityPointProbeButCapturesTree()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new AccessibleGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "type", Text = "hello" });

        var entry = recorder.Trace.Entries[0];
        Assert.Null(entry.Action.TargetDescriptor.Accessibility);
        Assert.Equal("{\"tree\":[{\"role\":\"button\"}]}", entry.Observation.AccessibilitySnapshotJson);
    }

    // ------------------------------------------------------------------
    // Test doubles
    // ------------------------------------------------------------------

    private sealed class FrozenTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FrozenTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class RecordingGraphicalSandbox : ISandbox
    {
        private readonly byte[] _screenshot;

        public RecordingGraphicalSandbox(byte[] screenshot) => _screenshot = screenshot;

        public string Id => "trace-test-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
            => Task.FromResult(_screenshot);

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AccessibleGraphicalSandbox : ISandbox
    {
        private readonly byte[] _screenshot;

        public AccessibleGraphicalSandbox(byte[] screenshot) => _screenshot = screenshot;

        public string Id => "accessible-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
            => Task.FromResult(_screenshot);

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default)
            => Task.FromResult<SandboxAccessibilitySnapshot?>(new SandboxAccessibilitySnapshot
            {
                Role = "button",
                Name = "Submit",
                Text = "Submit",
                ElementType = "button",
            });

        public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default)
            => Task.FromResult<string?>("{\"tree\":[{\"role\":\"button\"}]}");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingScreenshotSandbox : ISandbox
    {
        private readonly Exception _exception;

        public ThrowingScreenshotSandbox(Exception exception) => _exception = exception;

        public string Id => "throwing-screenshot-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
            => Task.FromException<byte[]>(_exception);

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
