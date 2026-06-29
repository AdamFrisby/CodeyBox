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

    /// <summary>
    /// Modality label recorded in the trace. Defaults to <c>"web-graphical"</c>
    /// for the web pilot; CLI/API recorders should override.
    /// </summary>
    public string Modality { get; init; } = "web-graphical";
}

/// <summary>
/// Decorator that wraps a <see cref="ComputerUseBridge"/> session and journals
/// every real-input action into a structured <see cref="SessionTrace"/>.
/// Before each input action a pre-action screenshot is captured for visual
/// target descriptors; after the action a post-action screenshot is captured
/// as the observation. The trace is available via <see cref="Trace"/> and can
/// be serialised at any point.
///
/// <para>Threading: this class is designed for a single producer (one driver
/// per session). <see cref="ExecuteAsync"/>, <see cref="SetMetadata"/>, and
/// <see cref="EndTrace"/> must not be called concurrently. The
/// <see cref="Trace"/> property returns a live view whose
/// <see cref="SessionTrace.Entries"/> may be read after <see cref="EndTrace"/>
/// without external synchronisation; concurrent reading during
/// <see cref="ExecuteAsync"/> requires caller-supplied serialisation.</para>
/// </summary>
public sealed class RecordingComputerUseBridge
{
    private readonly ComputerUseBridge _inner;
    private readonly TimeProvider _timeProvider;
    private readonly RecordingComputerUseBridgeOptions _options;
    private readonly List<TraceEntry> _entries = [];
    private SessionTrace _trace;
    private TraceTargetDescriptor? _lastTargetDescriptor;
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
            Modality = _options.Modality,
            StartedAt = _timeProvider.GetUtcNow(),
            Entries = _entries,
            TargetName = null,
        };
    }

    /// <summary>
    /// The trace built so far. Entries are appended as actions execute;
    /// the returned instance is a live view. Callers should read
    /// <see cref="SessionTrace.Entries"/> only after <see cref="EndTrace"/>
    /// unless they supply their own synchronisation.
    /// </summary>
    public SessionTrace Trace => _trace;

    /// <summary>
    /// Sets trace-level metadata after construction (e.g. from the harness
    /// that launched the session). Each non-null value overwrites the
    /// corresponding field; passing null for a field leaves the prior value
    /// in place.
    /// </summary>
    public void SetMetadata(
        string? targetName = null,
        string? entryUrl = null,
        byte[]? readinessScreenshotPng = null)
    {
        lock (_entries)
        {
            _trace = _trace with
            {
                TargetName = targetName ?? _trace.TargetName,
                EntryUrl = entryUrl ?? _trace.EntryUrl,
                ReadinessScreenshotPng = readinessScreenshotPng ?? _trace.ReadinessScreenshotPng,
            };
        }
    }

    /// <summary>
    /// Marks the trace as ended. Call once the session is done.
    /// Preserves all recorded entries.
    /// </summary>
    public void EndTrace()
    {
        lock (_entries)
        {
            _trace = _trace with { EndedAt = _timeProvider.GetUtcNow() };
        }
    }

    /// <summary>
    /// Executes <paramref name="request"/> through the inner bridge, capturing
    /// pre/post screenshots and journaling a <see cref="TraceEntry"/>.
    /// Action kind and events are resolved through
    /// <see cref="ComputerUseBridge.ResolveInputEvents"/> so the trace records
    /// the same events the inner bridge dispatches.
    /// </summary>
    public async Task<ComputerUseResult> ExecuteAsync(
        ISandbox sandbox,
        ComputerUseRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(request);

        var (events, canonicalAction) = ComputerUseBridge.ResolveInputEvents(request);
        var isScreenshot = canonicalAction is "screenshot";
        var timestamp = _timeProvider.GetUtcNow();

        byte[]? preScreenshot = null;
        if (!isScreenshot)
        {
            preScreenshot = await CaptureScreenshotBestEffortAsync(sandbox, ct).ConfigureAwait(false);
        }

        (int? cx, int? cy) = ResolveActionCentre(canonicalAction, events);
        TraceAccessibilityDescriptor? preAccessibility = null;
        if (cx.HasValue && cy.HasValue)
        {
            var snap = await CaptureAccessibilityBestEffortAsync(sandbox, cx.Value, cy.Value, ct).ConfigureAwait(false);
            if (snap != null)
            {
                preAccessibility = new TraceAccessibilityDescriptor
                {
                    Role = snap.Role,
                    Name = snap.Name,
                    Text = snap.Text,
                    ElementType = snap.ElementType,
                };
            }
        }

        var result = await _inner.ExecuteAsync(sandbox, request, ct).ConfigureAwait(false);

        byte[]? postScreenshot;
        if (isScreenshot)
        {
            postScreenshot = result.ScreenshotPng;
        }
        else
        {
            postScreenshot = await CaptureScreenshotBestEffortAsync(sandbox, ct).ConfigureAwait(false);
        }

        string? postAccessibilityJson = await CaptureAccessibilityTreeBestEffortAsync(sandbox, ct).ConfigureAwait(false);

        var targetDescriptor = BuildTargetDescriptor(canonicalAction, events, preScreenshot, preAccessibility);
        UpdateRememberedFocusTarget(canonicalAction, events, targetDescriptor);

        var entry = new TraceEntry
        {
            Sequence = Interlocked.Increment(ref _sequence),
            Timestamp = timestamp,
            Action = new TraceAction
            {
                InputEvents = events,
                Kind = canonicalAction,
                TargetDescriptor = targetDescriptor,
            },
            Observation = new TraceObservation
            {
                ScreenshotPng = postScreenshot,
                AccessibilitySnapshotJson = postAccessibilityJson,
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
            return await sandbox.GetScreenshotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<TraceAccessibilityDescriptor?> CaptureAccessibilityBestEffortAsync(
        ISandbox sandbox, int x, int y, CancellationToken ct)
    {
        try
        {
            var snap = await sandbox.GetAccessibilityAtPointAsync(x, y, ct).ConfigureAwait(false);
            if (snap is null) return null;
            return new TraceAccessibilityDescriptor
            {
                Role = snap.Role,
                Name = snap.Name,
                Text = snap.Text,
                ElementType = snap.ElementType,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string?> CaptureAccessibilityTreeBestEffortAsync(ISandbox sandbox, CancellationToken ct)
    {
        try
        {
            return await sandbox.GetAccessibilityTreeJsonAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private TraceTargetDescriptor BuildTargetDescriptor(
        string action,
        SandboxInputEvent[] events,
        byte[]? preScreenshot,
        TraceAccessibilityDescriptor? preAccessibility)
    {
        var (cx, cy) = ResolveActionCentre(action, events);

        TraceBoundingRegion region;
        int? clickOffsetX = null;
        int? clickOffsetY = null;
        if (cx.HasValue && cy.HasValue)
        {
            var r = _options.TargetCropRadius;
            var clampedX = Math.Max(0, cx.Value - r);
            var clampedY = Math.Max(0, cy.Value - r);
            var width = 2 * r + 1;
            var height = 2 * r + 1;
            if (TryReadPngDimensions(preScreenshot, out var screenWidth, out var screenHeight))
            {
                var right = Math.Min(screenWidth - 1, cx.Value + r);
                var bottom = Math.Min(screenHeight - 1, cy.Value + r);
                width = Math.Max(1, right - clampedX + 1);
                height = Math.Max(1, bottom - clampedY + 1);
            }
            clickOffsetX = cx.Value - clampedX;
            clickOffsetY = cy.Value - clampedY;
            region = new TraceBoundingRegion
            {
                X = clampedX,
                Y = clampedY,
                Width = width,
                Height = height,
            };
        }
        else
        {
            if (_lastTargetDescriptor is { } last
                && action is "key" or "type"
                && HasUsableTargetDescriptor(last))
            {
                return last with
                {
                    Visual = last.Visual with
                    {
                        SourceScreenshotPng = preScreenshot ?? last.Visual.SourceScreenshotPng,
                    },
                };
            }

            region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 };
        }

        return new TraceTargetDescriptor
        {
            Accessibility = preAccessibility,
            Visual = new TraceVisualDescriptor
            {
                Region = region,
                ClickOffsetX = clickOffsetX,
                ClickOffsetY = clickOffsetY,
                SourceScreenshotPng = preScreenshot,
            },
        };
    }

    private static (int? X, int? Y) ResolveActionCentre(string action, SandboxInputEvent[] events)
    {
        if (events.Length > 0 && action is "click" or "double_click" or "move")
        {
            var first = events[0];
            return (first.X, first.Y);
        }
        if (action is "events")
        {
            foreach (var evt in events)
            {
                if (evt.Type is SandboxInputEventType.Click or SandboxInputEventType.Move
                    && evt.X.HasValue
                    && evt.Y.HasValue)
                    return (evt.X, evt.Y);
            }
        }
        return (null, null);
    }

    private void UpdateRememberedFocusTarget(
        string action,
        IReadOnlyList<SandboxInputEvent> events,
        TraceTargetDescriptor targetDescriptor)
    {
        if (ActionSetsPointerFocus(action, events))
        {
            _lastTargetDescriptor = HasUsableTargetDescriptor(targetDescriptor)
                ? targetDescriptor
                : null;
            return;
        }

        if (ActionMayMoveKeyboardFocus(action, events))
        {
            _lastTargetDescriptor = null;
        }
    }

    private static bool ActionSetsPointerFocus(string action, IReadOnlyList<SandboxInputEvent> events)
    {
        if (action is "click" or "double_click") return true;
        if (action is not "events") return false;
        return events.Any(e =>
            e.Type == SandboxInputEventType.Click
            && e.X.HasValue
            && e.Y.HasValue);
    }

    private static bool ActionMayMoveKeyboardFocus(string action, IReadOnlyList<SandboxInputEvent> events)
    {
        if (action is not ("key" or "events")) return false;
        return events.Any(e => e.Type == SandboxInputEventType.Key && KeyMayMoveFocus(e.Key));
    }

    private static bool KeyMayMoveFocus(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var normalized = key.Trim().ToLowerInvariant();
        return normalized.Contains("tab", StringComparison.Ordinal)
            || normalized.Contains("enter", StringComparison.Ordinal)
            || normalized.Contains("return", StringComparison.Ordinal)
            || normalized.Contains("escape", StringComparison.Ordinal)
            || normalized == "esc"
            || normalized.Contains("arrow", StringComparison.Ordinal)
            || normalized is "up" or "down" or "left" or "right"
            || normalized.Contains("pageup", StringComparison.Ordinal)
            || normalized.Contains("pagedown", StringComparison.Ordinal)
            || normalized.Contains("ctrl", StringComparison.Ordinal)
            || normalized.Contains("control", StringComparison.Ordinal)
            || normalized.Contains("alt", StringComparison.Ordinal)
            || normalized.Contains("meta", StringComparison.Ordinal)
            || normalized.Contains("cmd", StringComparison.Ordinal)
            || normalized.Contains("super", StringComparison.Ordinal);
    }

    private static bool TryReadPngDimensions(byte[]? png, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (png is null || png.Length < 24) return false;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!png.AsSpan(0, signature.Length).SequenceEqual(signature)) return false;
        if (png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R')
            return false;

        width = ReadBigEndianInt32(png.AsSpan(16, 4));
        height = ReadBigEndianInt32(png.AsSpan(20, 4));
        return width > 0 && height > 0;
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> bytes)
        => (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    private static bool HasUsableTargetDescriptor(TraceTargetDescriptor descriptor)
    {
        var region = descriptor.Visual.Region;
        if (region.Width > 0 && region.Height > 0) return true;
        var acc = descriptor.Accessibility;
        return acc is not null
            && (!string.IsNullOrEmpty(acc.Role)
                || !string.IsNullOrEmpty(acc.Name)
                || !string.IsNullOrEmpty(acc.Text)
                || !string.IsNullOrEmpty(acc.ElementType));
    }
}
