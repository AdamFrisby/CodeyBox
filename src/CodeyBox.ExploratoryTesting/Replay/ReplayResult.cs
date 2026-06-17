namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Outcome of replaying a full <see cref="SessionTrace"/>. <see cref="Passed"/>
/// is true iff every step succeeded, including any post-action assertion. A
/// single failed step terminates the replay and is reported on
/// <see cref="FailedStep"/>.
/// </summary>
public sealed record ReplayResult
{
    public required bool Passed { get; init; }
    public required IReadOnlyList<ReplayStepResult> Steps { get; init; }

    /// <summary>
    /// The first failed step, or null when <see cref="Passed"/> is true.
    /// </summary>
    public ReplayStepResult? FailedStep { get; init; }

    /// <summary>
    /// Total wall-clock the replay consumed (including all visual waits).
    /// </summary>
    public required TimeSpan Duration { get; init; }
}

/// <summary>
/// Outcome of a single replayed <see cref="TraceEntry"/>. The triple
/// (<see cref="Sequence"/>, <see cref="FailureKind"/>, <see cref="Diagnostic"/>)
/// plus <see cref="DiagnosticScreenshotPng"/> is the precise failure report
/// the acceptance criteria call out: which step, why, and the screenshot.
/// </summary>
public sealed record ReplayStepResult
{
    /// <summary>
    /// Sequence number copied from the recorded <see cref="TraceEntry.Sequence"/>
    /// so step failure messages line up with the trace artefact.
    /// </summary>
    public required int Sequence { get; init; }

    /// <summary>
    /// Canonical action kind from the recorded entry (<c>click</c>,
    /// <c>type</c>, ...). Surfaced for diagnostic clarity.
    /// </summary>
    public required string ActionKind { get; init; }

    /// <summary>True when the step's action, reachability, and assertion all succeeded.</summary>
    public required bool Passed { get; init; }

    /// <summary>
    /// Failure kind when <see cref="Passed"/> is false. Null on success.
    /// </summary>
    public ReplayFailureKind? FailureKind { get; init; }

    /// <summary>
    /// Human-readable diagnostic: which step, why, surfacing locator /
    /// assertion details. Null on success.
    /// </summary>
    public string? Diagnostic { get; init; }

    /// <summary>
    /// Screenshot captured at the moment of failure (current screen),
    /// or null when no screenshot could be obtained. Attached so failed
    /// reports can show what the engine actually saw.
    /// </summary>
    public byte[]? DiagnosticScreenshotPng { get; init; }

    /// <summary>
    /// The located target, when a locator hit was found before failure.
    /// Null when the failure was <see cref="ReplayFailureKind.NotFound"/>
    /// or when no locator step ran (e.g. <c>screenshot</c> action).
    /// </summary>
    public LocatedTarget? LocatedTarget { get; init; }
}

/// <summary>
/// The bounds + provenance of a re-located target on the current screen.
/// Returned by <see cref="IElementLocator.LocateAsync"/> and consumed by the
/// engine to drive real input. The <see cref="CenterX"/> / <see cref="CenterY"/>
/// pair is the engine's click target; <see cref="Region"/> describes the
/// element's full bounds so reachability checks can reason about it.
/// </summary>
public sealed record LocatedTarget
{
    public required int CenterX { get; init; }
    public required int CenterY { get; init; }
    public required TraceBoundingRegion Region { get; init; }

    /// <summary>
    /// Provenance: how the target was re-located. One of
    /// <c>accessibility-point</c>, <c>accessibility-spiral</c>,
    /// <c>visual-template</c>, <c>recorded-region</c>.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Locator-supplied confidence in [0, 1]. The default accessibility
    /// locator returns 1.0 on an exact role/name match and 0.5 on a
    /// fall-back to the recorded region.
    /// </summary>
    public required double Confidence { get; init; }
}
