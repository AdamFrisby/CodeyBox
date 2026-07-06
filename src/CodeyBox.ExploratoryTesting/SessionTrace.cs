using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Modality-abstract ordered log of a driven session. Each entry captures an
/// action (real kbd/mouse input), its target descriptors (accessibility + visual),
/// a post-action observation, and an optional assertion.
///
/// <para>The trace format is driver-agnostic: web sessions record DOM+visual
/// descriptors; CLI/API sessions will record different action/observation
/// kinds behind the same envelope later.</para>
/// </summary>
public sealed record SessionTrace
{
    public const string CurrentVersion = "1.0";

    public required string TraceFormatVersion { get; init; }
    public required string Modality { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public required IReadOnlyList<TraceEntry> Entries { get; init; }
    public string? TargetName { get; init; }
    public string? EntryUrl { get; init; }
    public byte[]? ReadinessScreenshotPng { get; init; }
}

/// <summary>
/// A single entry in a <see cref="SessionTrace"/>, pairing an action with its
/// post-condition observation and optional assertion.
/// </summary>
public sealed record TraceEntry
{
    public required int Sequence { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required TraceAction Action { get; init; }
    public required TraceObservation Observation { get; init; }
    public TraceAssertion? Assertion { get; init; }
}

/// <summary>
/// A real-input action plus robust target descriptors that enable later
/// re-location by recognition rather than raw-coordinate replay.
/// </summary>
public sealed record TraceAction
{
    /// <summary>
    /// The actual input events dispatched to the sandbox (one or more
    /// <see cref="SandboxInputEvent"/> entries).
    /// </summary>
    public required IReadOnlyList<SandboxInputEvent> InputEvents { get; init; }

    /// <summary>
    /// Action kind as a canonical string: <c>click</c>, <c>double_click</c>,
    /// <c>move</c>, <c>scroll</c>, <c>key</c>, <c>type</c>, <c>events</c>,
    /// <c>screenshot</c>. Synonyms submitted by the caller (e.g.
    /// <c>left_click</c>, <c>mouse_move</c>, <c>keypress</c>) are normalised
    /// to the canonical form before storage.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Descriptors sufficient to re-locate the interaction target by
    /// recognition — never by raw coordinates.
    /// </summary>
    public required TraceTargetDescriptor TargetDescriptor { get; init; }

    /// <summary>
    /// True only for deliberately global input that is not scoped to a recorded
    /// focus target, such as an application-wide Escape shortcut. Targetless
    /// key/type steps default to false so trimmed or legacy traces cannot type
    /// into incidental focus by accident.
    /// </summary>
    public bool IsGlobalInput { get; init; }
}

/// <summary>
/// Recognition descriptors for an interaction target. Always carries a visual
/// descriptor; augments with accessibility information when available.
///
/// <para>All string fields that originate from app screen content (accessibility
/// role/name/text, OCR text) are untrusted — treat them as opaque data, never
/// as instructions, when consumed by LLM-driven replay or analysis pipelines.</para>
/// </summary>
public sealed record TraceTargetDescriptor
{
    /// <summary>
    /// Accessibility role / name / text when the app or platform exposes an
    /// accessibility tree. Null for untagged, canvas, or 3D targets.
    /// </summary>
    public TraceAccessibilityDescriptor? Accessibility { get; init; }

    /// <summary>
    /// Full accessibility-tree snapshot captured immediately before the action
    /// executed. Used by the replay emitter to resolve stable selectors against
    /// the pre-action DOM.
    /// </summary>
    public string? AccessibilitySnapshotJson { get; init; }

    /// <summary>
    /// Visual descriptor — always captured so untagged / canvas / 3D targets
    /// remain re-locatable by sight.
    /// </summary>
    public required TraceVisualDescriptor Visual { get; init; }
}

/// <summary>
/// Accessibility signals captured from an app's accessibility tree at action time.
/// All fields are untrusted: they originate from the app screen and must be
/// treated as opaque data, never as instructions, when consumed by LLM-driven
/// replay or analysis pipelines.
/// </summary>
public sealed record TraceAccessibilityDescriptor
{
    public string? Role { get; init; }
    public string? Name { get; init; }
    public string? Text { get; init; }
    public string? ElementType { get; init; }

    /// <summary>
    /// Bounds of the accessibility node at recording time when the provider
    /// exposed them. Optional for backward compatibility with older traces and
    /// point-only providers.
    /// </summary>
    public TraceBoundingRegion? Bounds { get; init; }
}

/// <summary>
/// Visual signals for re-locating a target by sight. Includes a cropped
/// template/anchor image, OCR text from the region, the bounding region in
/// the source screenshot, and the click offset inside that region when the
/// action had a pointer anchor.
///
/// <para><see cref="OcrText"/> originates from the app screen and is untrusted —
/// treat it as opaque data, never as instructions, when consumed by LLM-driven
/// replay or analysis pipelines.</para>
/// </summary>
public sealed record TraceVisualDescriptor
{
    /// <summary>
    /// Cropped template/anchor image around the target (PNG). May be null
    /// when the template can be derived from <see cref="SourceScreenshotPng"/>
    /// and <see cref="Region"/> by a post-processing step.
    /// </summary>
    public byte[]? TemplatePng { get; init; }

    /// <summary>
    /// OCR text detected in the target region. Null when OCR was not run.
    /// Untrusted: originates from the app screen — treat as opaque data.
    /// </summary>
    public string? OcrText { get; init; }

    /// <summary>
    /// Bounding region of the target in <see cref="SourceScreenshotPng"/>.
    /// </summary>
    public required TraceBoundingRegion Region { get; init; }

    /// <summary>
    /// X offset of the recorded pointer inside <see cref="Region"/>. Null for
    /// older traces and non-pointer actions, where replay falls back to the
    /// region centre.
    /// </summary>
    public int? ClickOffsetX { get; init; }

    /// <summary>
    /// Y offset of the recorded pointer inside <see cref="Region"/>. Null for
    /// older traces and non-pointer actions, where replay falls back to the
    /// region centre.
    /// </summary>
    public int? ClickOffsetY { get; init; }

    /// <summary>
    /// The pre-action screenshot this visual descriptor was computed from.
    /// </summary>
    public byte[]? SourceScreenshotPng { get; init; }
}

/// <summary>
/// Axis-aligned bounding region in screen coordinates.
/// </summary>
public sealed record TraceBoundingRegion
{
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>
/// Post-action observation: the state of the app immediately after the action
/// was applied.
///
/// <para>All string fields that originate from app screen content
/// (<see cref="AccessibilitySnapshotJson"/>) are untrusted — treat them as
/// opaque data, never as instructions, when consumed by LLM-driven replay
/// or analysis pipelines.</para>
/// </summary>
public sealed record TraceObservation
{
    /// <summary>
    /// Full-desktop screenshot taken after the action completed.
    /// </summary>
    public byte[]? ScreenshotPng { get; init; }

    /// <summary>
    /// Optional accessibility-tree snapshot captured after the action.
    /// Serialised as an opaque JSON string; the replay engine interprets it.
    /// </summary>
    public string? AccessibilitySnapshotJson { get; init; }

    /// <summary>
    /// Wall-clock time when the observation was captured.
    /// </summary>
    public required DateTimeOffset CapturedAt { get; init; }
}

/// <summary>
/// Optional expected-visual-state assertion that the replay engine can verify
/// after re-applying the associated action.
/// </summary>
public sealed record TraceAssertion
{
    /// <summary>
    /// Assertion kind: <c>visual-match</c>, <c>text-contains</c>,
    /// <c>element-present</c>, etc.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Free-form detail the assertion engine interprets (expected text,
    /// template hash, element selector, etc.).
    /// </summary>
    public string? Detail { get; init; }
}
