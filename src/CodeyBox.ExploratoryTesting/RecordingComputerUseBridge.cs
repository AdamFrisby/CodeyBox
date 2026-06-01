using CodeyBox.Core;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Options for <see cref="RecordingComputerUseBridge"/>.
/// </summary>
public sealed record RecordingComputerUseBridgeOptions
{
    /// <summary>
    /// Half-size of the bounding region around a click/move point,
    /// in pixels. The full region is <c>2 * TargetCropRadius + 1</c>
    /// in each dimension, clamped to non-negative screen coordinates.
    /// </summary>
    public int TargetCropRadius { get; init; } = 50;
}

/// <summary>
/// Decorator that wraps a <see cref="ComputerUseBridge"/> session and journals
/// every real-input action into a structured <see cref="SessionTrace"/>.
/// Before each input action a pre-action screenshot is captured for visual
/// target descriptors; after the action a post-action screenshot is captured
/// as the observation. The trace is available via <see cref="Trace"/> and can
/// be serialised at any point.
/// </summary>
public sealed class RecordingComputerUseBridge
{
    private readonly ComputerUseBridge _inner;
    private readonly TimeProvider _timeProvider;
    private readonly RecordingComputerUseBridgeOptions _options;
    private readonly List<TraceEntry> _entries = [];
    private SessionTrace _trace;
    private int _sequence;

    public RecordingComputerUseBridge(
        ComputerUseBridge inner,
        TimeProvider? timeProvider = null,
        RecordingComputerUseBridgeOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new RecordingComputerUseBridgeOptions();
        _trace = new SessionTrace
        {
            TraceFormatVersion = SessionTrace.CurrentVersion,
            Modality = "web-graphical",
            StartedAt = _timeProvider.GetUtcNow(),
            Entries = _entries,
            TargetName = null,
        };
    }

    /// <summary>
    /// The trace built so far. Entries are appended as actions execute;
    /// the returned instance is a live view so callers can observe progress
    /// or serialise at any time.
    /// </summary>
    public SessionTrace Trace => _trace;

    /// <summary>
    /// Sets trace-level metadata after construction (e.g. from the harness
    /// that launched the session).
    /// </summary>
    public void SetMetadata(
        string? targetName = null,
        string? entryUrl = null,
        byte[]? readinessScreenshotPng = null)
    {
        _trace = _trace with
        {
            TargetName = targetName ?? _trace.TargetName,
            EntryUrl = entryUrl ?? _trace.EntryUrl,
            ReadinessScreenshotPng = readinessScreenshotPng ?? _trace.ReadinessScreenshotPng,
        };
    }

    /// <summary>
    /// Marks the trace as ended. Call once the session is done.
    /// </summary>
    public void EndTrace()
    {
        _trace = _trace with { EndedAt = _timeProvider.GetUtcNow() };
    }

    /// <summary>
    /// Executes <paramref name="request"/> through the inner bridge, capturing
    /// pre/post screenshots and journaling a <see cref="TraceEntry"/>.
    /// </summary>
    public async Task<ComputerUseResult> ExecuteAsync(
        ISandbox sandbox,
        ComputerUseRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(request);

        var action = (request.Action ?? "").Trim().ToLowerInvariant();
        var timestamp = _timeProvider.GetUtcNow();

        byte[]? preScreenshot = null;
        if (action is not "screenshot")
        {
            preScreenshot = await CaptureScreenshotBestEffortAsync(sandbox, ct);
        }

        var result = await _inner.ExecuteAsync(sandbox, request, ct);

        byte[]? postScreenshot;
        if (action is "screenshot")
        {
            postScreenshot = result.ScreenshotPng;
        }
        else
        {
            postScreenshot = await CaptureScreenshotBestEffortAsync(sandbox, ct);
        }

        var targetDescriptor = BuildTargetDescriptor(action, request, preScreenshot);

        var entry = new TraceEntry
        {
            Sequence = Interlocked.Increment(ref _sequence),
            Timestamp = timestamp,
            Action = new TraceAction
            {
                InputEvents = MapToInputEvents(action, request),
                Kind = action,
                TargetDescriptor = targetDescriptor,
            },
            Observation = new TraceObservation
            {
                ScreenshotPng = postScreenshot,
                AccessibilitySnapshotJson = null,
                CapturedAt = _timeProvider.GetUtcNow(),
            },
        };

        lock (_entries)
        {
            _entries.Add(entry);
        }

        return result;
    }

    private static async Task<byte[]?> CaptureScreenshotBestEffortAsync(ISandbox sandbox, CancellationToken ct)
    {
        try
        {
            return await sandbox.GetScreenshotAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private TraceTargetDescriptor BuildTargetDescriptor(
        string action,
        ComputerUseRequest request,
        byte[]? preScreenshot)
    {
        var (cx, cy) = ResolveActionCentre(action, request);

        TraceBoundingRegion region;
        if (cx.HasValue && cy.HasValue)
        {
            var r = _options.TargetCropRadius;
            region = new TraceBoundingRegion
            {
                X = Math.Max(0, cx.Value - r),
                Y = Math.Max(0, cy.Value - r),
                Width = 2 * r + 1,
                Height = 2 * r + 1,
            };
        }
        else
        {
            region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 };
        }

        return new TraceTargetDescriptor
        {
            Visual = new TraceVisualDescriptor
            {
                Region = region,
                SourceScreenshotPng = preScreenshot,
            },
        };
    }

    private static (int? X, int? Y) ResolveActionCentre(string action, ComputerUseRequest request)
    {
        switch (action)
        {
            case "click":
            case "left_click":
            case "double_click":
            case "move":
            case "mouse_move":
                return (request.X, request.Y);
            case "scroll":
                return (request.ScrollX ?? request.X, request.ScrollY ?? request.Y);
            default:
                return (null, null);
        }
    }

    private static IReadOnlyList<SandboxInputEvent> MapToInputEvents(string action, ComputerUseRequest request)
    {
        switch (action)
        {
            case "screenshot":
                return [];

            case "click":
            case "left_click":
                return [new SandboxInputEvent { Type = SandboxInputEventType.Click, X = request.X, Y = request.Y }];

            case "double_click":
                return
                [
                    new SandboxInputEvent { Type = SandboxInputEventType.Click, X = request.X, Y = request.Y },
                    new SandboxInputEvent { Type = SandboxInputEventType.Click, X = request.X, Y = request.Y },
                ];

            case "move":
            case "mouse_move":
                return [new SandboxInputEvent { Type = SandboxInputEventType.Move, X = request.X, Y = request.Y }];

            case "scroll":
                return [new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = request.ScrollX ?? request.X, Y = request.ScrollY ?? request.Y }];

            case "key":
            case "keypress":
                return [new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = request.Key ?? request.Text }];

            case "type":
                return [new SandboxInputEvent { Type = SandboxInputEventType.Type, Text = request.Text }];

            case "events":
                return request.Events ?? [];

            default:
                return [];
        }
    }
}
