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
    // Data model construction
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

    [Fact]
    public void TraceEntry_BindsActionObservationAndOptionalAssertion()
    {
        var entry = new TraceEntry
        {
            Sequence = 1,
            Timestamp = FrozenNow,
            Action = new TraceAction
            {
                InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 100, Y = 200 }],
                Kind = "click",
                TargetDescriptor = new TraceTargetDescriptor
                {
                    Visual = new TraceVisualDescriptor
                    {
                        Region = new TraceBoundingRegion { X = 50, Y = 150, Width = 101, Height = 101 },
                    },
                },
            },
            Observation = new TraceObservation
            {
                ScreenshotPng = SamplePng,
                CapturedAt = FrozenNow,
            },
            Assertion = new TraceAssertion
            {
                Kind = "visual-match",
                Detail = "expected button state change",
            },
        };

        Assert.Equal(1, entry.Sequence);
        Assert.Single(entry.Action.InputEvents);
        Assert.Equal("click", entry.Action.Kind);
        Assert.Equal(50, entry.Action.TargetDescriptor.Visual.Region.X);
        Assert.Equal(150, entry.Action.TargetDescriptor.Visual.Region.Y);
        Assert.Equal(101, entry.Action.TargetDescriptor.Visual.Region.Width);
        Assert.Equal(SamplePng, entry.Observation.ScreenshotPng);
        Assert.NotNull(entry.Assertion);
        Assert.Equal("visual-match", entry.Assertion.Kind);
    }

    [Fact]
    public void TraceTargetDescriptor_SupportsAccessibilityAndVisual()
    {
        var descriptor = new TraceTargetDescriptor
        {
            Accessibility = new TraceAccessibilityDescriptor
            {
                Role = "button",
                Name = "Submit",
                Text = "Submit",
                ElementType = "button",
            },
            Visual = new TraceVisualDescriptor
            {
                TemplatePng = SamplePng,
                OcrText = "Submit",
                Region = new TraceBoundingRegion { X = 10, Y = 20, Width = 80, Height = 30 },
                SourceScreenshotPng = SamplePng,
            },
        };

        Assert.NotNull(descriptor.Accessibility);
        Assert.Equal("button", descriptor.Accessibility.Role);
        Assert.Equal("Submit", descriptor.Accessibility.Name);
        Assert.NotNull(descriptor.Visual.TemplatePng);
        Assert.Equal("Submit", descriptor.Visual.OcrText);
    }

    [Fact]
    public void TraceTargetDescriptor_WorksWithVisualOnly()
    {
        var descriptor = new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor
            {
                Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 100, Height = 100 },
            },
        };

        Assert.Null(descriptor.Accessibility);
        Assert.NotNull(descriptor.Visual);
        Assert.Null(descriptor.Visual.TemplatePng);
        Assert.Null(descriptor.Visual.OcrText);
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

        var deserialized = SessionTraceJson.Deserialize(json);
        Assert.Equal(original.TraceFormatVersion, deserialized.TraceFormatVersion);
        Assert.Equal(original.Modality, deserialized.Modality);
        Assert.Equal(original.TargetName, deserialized.TargetName);
        Assert.Equal(original.EntryUrl, deserialized.EntryUrl);
        Assert.Single(deserialized.Entries);

        var entry = deserialized.Entries[0];
        Assert.Equal(1, entry.Sequence);
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
    }

    [Fact]
    public async Task SessionTrace_RoundTripsThroughFile()
    {
        var trace = new SessionTrace
        {
            TraceFormatVersion = SessionTrace.CurrentVersion,
            Modality = "cli",
            StartedAt = FrozenNow,
            Entries = [],
        };

        var path = Path.GetTempFileName();
        try
        {
            await SessionTraceJson.WriteToFileAsync(trace, path);
            var loaded = await SessionTraceJson.ReadFromFileAsync(path);

            Assert.Equal(trace.TraceFormatVersion, loaded.TraceFormatVersion);
            Assert.Equal(trace.Modality, loaded.Modality);
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
    }

    // ------------------------------------------------------------------
    // Recording bridge — trace capture
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
        Assert.Equal(0, visual.Region.Width);  // no coordinates → zero region
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

    [Fact]
    public async Task RecordingBridge_SetMetadata_UpdatesTrace()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        recorder.SetMetadata(targetName: "jobtrack", entryUrl: "http://localhost:8080");

        Assert.Equal("jobtrack", recorder.Trace.TargetName);
        Assert.Equal("http://localhost:8080", recorder.Trace.EntryUrl);
    }

    [Fact]
    public async Task RecordingBridge_EndTrace_SetsEndedAt()
    {
        var timeProvider = new FrozenTimeProvider(FrozenNow);
        var sandbox = new RecordingGraphicalSandbox(SamplePng);
        var inner = new ComputerUseBridge(timeProvider: timeProvider);
        var recorder = new RecordingComputerUseBridge(inner, timeProvider);

        await recorder.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "screenshot" });
        Assert.Null(recorder.Trace.EndedAt);

        recorder.EndTrace();
        Assert.NotNull(recorder.Trace.EndedAt);
        Assert.Equal(FrozenNow, recorder.Trace.EndedAt);
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
