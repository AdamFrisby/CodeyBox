using CodeyBox.Core;
using CodeyBox.Sandbox.Graphical;
using System.Text.Json;

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
/// Recorder-only metadata for one computer-use request. Keep trace policy out of
/// the shared <see cref="ComputerUseRequest"/> DTO, whose job is input synthesis.
/// </summary>
public sealed record RecordingComputerUseMetadata
{
    /// <summary>
    /// True only for deliberate application-wide keyboard shortcuts that should
    /// replay without a target descriptor, such as Escape closing a dialog.
    /// </summary>
    public bool IsGlobalInput { get; init; }
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
        => await ExecuteAsync(sandbox, request, metadata: null, ct).ConfigureAwait(false);

    /// <summary>
    /// Executes <paramref name="request"/> and records it with optional
    /// recorder-scoped metadata. The metadata does not affect the bridge input
    /// dispatch; it only controls trace annotations such as deliberate global
    /// shortcuts.
    /// </summary>
    public async Task<ComputerUseResult> ExecuteAsync(
        ISandbox sandbox,
        ComputerUseRequest request,
        RecordingComputerUseMetadata? metadata,
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

        var isGlobalInput = ShouldRecordGlobalInput(metadata, canonicalAction, events);
        var targetDescriptor = isGlobalInput
            ? BuildEmptyTargetDescriptor(preScreenshot)
            : BuildTargetDescriptor(canonicalAction, events, preScreenshot, preAccessibility);
        UpdateRememberedFocusTarget(canonicalAction, events, targetDescriptor, postAccessibilityJson, postScreenshot);

        var entry = new TraceEntry
        {
            Sequence = Interlocked.Increment(ref _sequence),
            Timestamp = timestamp,
            Action = new TraceAction
            {
                InputEvents = events,
                Kind = canonicalAction,
                TargetDescriptor = targetDescriptor,
                IsGlobalInput = isGlobalInput,
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
                && action is "key" or "type" or "scroll"
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

    private static TraceTargetDescriptor BuildEmptyTargetDescriptor(byte[]? screenshot) => new()
    {
        Visual = new TraceVisualDescriptor
        {
            Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 },
            SourceScreenshotPng = screenshot,
        },
    };

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
        TraceTargetDescriptor targetDescriptor,
        string? postAccessibilityJson,
        byte[]? postScreenshot)
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
            _lastTargetDescriptor = TryBuildFocusedTargetDescriptor(postAccessibilityJson, postScreenshot);
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

    private static bool ShouldRecordGlobalInput(
        RecordingComputerUseMetadata? metadata,
        string action,
        IReadOnlyList<SandboxInputEvent> events)
    {
        if (metadata?.IsGlobalInput != true)
            return false;

        if (action == "key")
            return events.Any(e => e.Type == SandboxInputEventType.Key && !string.IsNullOrWhiteSpace(e.Key));

        return action == "events"
            && events.Count > 0
            && events.All(e => e.Type == SandboxInputEventType.Key && !string.IsNullOrWhiteSpace(e.Key));
    }

    private static TraceTargetDescriptor? TryBuildFocusedTargetDescriptor(
        string? accessibilityJson,
        byte[]? screenshot)
    {
        if (string.IsNullOrWhiteSpace(accessibilityJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(accessibilityJson);
            return TryFindFocusedTarget(doc.RootElement, screenshot);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TraceTargetDescriptor? TryFindFocusedTarget(JsonElement element, byte[]? screenshot)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (IsFocusedNode(element))
            {
                return BuildDescriptorFromAccessibilityNode(element, screenshot);
            }

            foreach (var property in element.EnumerateObject())
            {
                var hit = TryFindFocusedTarget(property.Value, screenshot);
                if (hit is not null) return hit;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var hit = TryFindFocusedTarget(item, screenshot);
                if (hit is not null) return hit;
            }
        }

        return null;
    }

    private static TraceTargetDescriptor BuildDescriptorFromAccessibilityNode(JsonElement node, byte[]? screenshot)
    {
        var accessibility = new TraceAccessibilityDescriptor
        {
            Role = ReadString(node, "role", "Role", "controlType", "type"),
            Name = ReadString(node, "name", "Name", "label", "title", "accessibleName"),
            Text = ReadString(node, "text", "Text", "value", "description"),
            ElementType = ReadString(node, "elementType", "ElementType", "tagName", "className"),
        };
        if (!TryReadBounds(node, out var region))
            region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 };

        return new TraceTargetDescriptor
        {
            Accessibility = accessibility,
            Visual = new TraceVisualDescriptor
            {
                Region = region,
                ClickOffsetX = region.Width > 0 ? region.Width / 2 : null,
                ClickOffsetY = region.Height > 0 ? region.Height / 2 : null,
                SourceScreenshotPng = screenshot,
            },
        };
    }

    private static bool IsFocusedNode(JsonElement node)
    {
        foreach (var name in new[] { "focused", "Focused", "hasFocus", "HasFocus", "has_focus", "isFocused", "is_focused" })
        {
            if (!TryGetProperty(node, name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.True) return true;
            if (property.ValueKind == JsonValueKind.String
                && bool.TryParse(property.GetString(), out var parsed)
                && parsed)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadBounds(JsonElement obj, out TraceBoundingRegion region)
    {
        if (TryReadRectObject(obj, out region)) return true;

        foreach (var name in new[] { "bounds", "Bounds", "rect", "Rect", "boundingBox", "BoundingBox" })
        {
            if (!TryGetProperty(obj, name, out var child)) continue;
            if (child.ValueKind == JsonValueKind.Object && TryReadRectObject(child, out region)) return true;
            if (child.ValueKind == JsonValueKind.Array && TryReadRectArray(child, out region)) return true;
        }

        region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 };
        return false;
    }

    private static bool TryReadRectObject(JsonElement obj, out TraceBoundingRegion region)
    {
        if (TryReadInt(obj, "x", out var x)
            && TryReadInt(obj, "y", out var y)
            && TryReadInt(obj, "width", out var width)
            && TryReadInt(obj, "height", out var height)
            && width > 0
            && height > 0)
        {
            region = new TraceBoundingRegion { X = x, Y = y, Width = width, Height = height };
            return true;
        }

        if (TryReadInt(obj, "left", out var left)
            && TryReadInt(obj, "top", out var top)
            && TryReadInt(obj, "right", out var right)
            && TryReadInt(obj, "bottom", out var bottom)
            && right > left
            && bottom > top)
        {
            region = new TraceBoundingRegion
            {
                X = left,
                Y = top,
                Width = right - left,
                Height = bottom - top,
            };
            return true;
        }

        region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 };
        return false;
    }

    private static bool TryReadRectArray(JsonElement array, out TraceBoundingRegion region)
    {
        if (array.GetArrayLength() < 4)
        {
            region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 };
            return false;
        }

        var values = new int[4];
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (i >= 4) break;
            if (!TryReadInt(item, out values[i]))
            {
                region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 };
                return false;
            }
            i++;
        }

        if (values[2] <= 0 || values[3] <= 0)
        {
            region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 };
            return false;
        }

        region = new TraceBoundingRegion { X = values[0], Y = values[1], Width = values[2], Height = values[3] };
        return true;
    }

    private static bool TryReadInt(JsonElement obj, string name, out int value)
    {
        if (TryGetProperty(obj, name, out var property))
            return TryReadInt(property, out value);

        value = 0;
        return false;
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
            return true;
        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(obj, name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                if (!string.IsNullOrEmpty(value)) return value;
            }
        }
        return null;
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value))
            return true;

        value = default;
        return false;
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
            || normalized.Contains("pagedown", StringComparison.Ordinal);
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
