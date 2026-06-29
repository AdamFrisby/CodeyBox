using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CodeyBox.Core;

/// <summary>
/// Deterministic replay artifact for an end-to-end test case. Persisted as JSON
/// in <see cref="TestCase.ExecutableArtifactJson"/> when
/// <see cref="AutomationKind"/> is <see cref="AutomationKind.E2eReplay"/>.
///
/// <para>The runtime evaluates steps and assertions sequentially against a
/// freshly-cloned sandbox VM (with the app under test pre-baked into the
/// baseline image). The artifact is pure data — there is no LLM in the
/// replay loop. The cheap-model selector-repair fallback the brief mentions
/// is a future addition; the runtime exposes the seam (<see cref="E2eRunResult.FailedStepIndex"/>)
/// without acting on it.</para>
/// </summary>
public sealed record E2eReplayArtifact
{
    /// <summary>Optional human-readable label echoed into the run result.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Optional readiness probe run before any step. When the probe fails the
    /// whole run is recorded as <see cref="E2eRunStatus.Error"/> ("app under
    /// test never came up") rather than <see cref="E2eRunStatus.Failed"/> so the
    /// dashboard distinguishes flake-from-infra-issue vs. real assertion fail.
    /// </summary>
    [JsonPropertyName("readiness")]
    public E2eReadinessProbe? Readiness { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<E2eReplayStep> Steps { get; init; } = [];

    [JsonPropertyName("assertions")]
    public IReadOnlyList<E2eReplayAssertion> Assertions { get; init; } = [];
}

public sealed record E2eReadinessProbe
{
    /// <summary>HTTP(S) URL the runtime probes with its built-in readiness command.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>Legacy command shape. New artifacts must use <see cref="Url"/>.</summary>
    [JsonPropertyName("argv")]
    public IReadOnlyList<string> Argv { get; init; } = [];

    /// <summary>Maximum number of probe attempts; defaults to 30.</summary>
    [JsonPropertyName("maxAttempts")]
    public int MaxAttempts { get; init; } = 30;

    /// <summary>Delay (ms) between probe attempts; defaults to 1000.</summary>
    [JsonPropertyName("delayMs")]
    public int DelayMs { get; init; } = 1000;
}

public sealed record E2eReplayStep
{
    /// <summary>Recorded browser/app action, for example <c>navigate</c>, <c>click</c>, or <c>fill</c>.</summary>
    [JsonPropertyName("action")]
    public string? Action { get; init; }

    /// <summary>Selector used by selector-targeted actions.</summary>
    [JsonPropertyName("selector")]
    public string? Selector { get; init; }

    /// <summary>URL or other action target.</summary>
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    /// <summary>Value typed/selected by input-oriented actions.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>Legacy command shape. New artifacts must use action/selector/target fields.</summary>
    [JsonPropertyName("argv")]
    public IReadOnlyList<string> Argv { get; init; } = [];

    /// <summary>Optional stdin payload; null = no stdin.</summary>
    [JsonPropertyName("stdin")]
    public string? Stdin { get; init; }

    /// <summary>Working directory inside the sandbox; null defers to the sandbox default (/work).</summary>
    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// When true, a non-zero exit fails the step (and the run). When false the
    /// runtime records the exit code but continues — useful for fire-and-forget
    /// setup steps. Defaults to true (strict).
    /// </summary>
    [JsonPropertyName("failOnNonZeroExit")]
    public bool FailOnNonZeroExit { get; init; } = true;

    /// <summary>Optional fixed delay (ms) inserted after the step completes.</summary>
    [JsonPropertyName("delayAfterMs")]
    public int? DelayAfterMs { get; init; }
}

/// <summary>
/// Assertion shape consumed by the deterministic replay driver. New artifacts
/// use selector/target/value fields; legacy argv expectations are retained only
/// so old data can be rejected with a clear validation error instead of being
/// executed as artifact-controlled shell commands.
/// </summary>
public sealed record E2eReplayAssertion
{
    /// <summary>Assertion kind, for example <c>selectorVisible</c> or <c>urlContains</c>.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("selector")]
    public string? Selector { get; init; }

    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>Legacy command shape. New artifacts must use kind/selector/target/value fields.</summary>
    [JsonPropertyName("argv")]
    public IReadOnlyList<string> Argv { get; init; } = [];

    /// <summary>Expected exit code; defaults to 0.</summary>
    [JsonPropertyName("expectExitCode")]
    public int ExpectExitCode { get; init; }

    /// <summary>Optional substring that must appear in stdout (case-sensitive).</summary>
    [JsonPropertyName("expectStdoutContains")]
    public string? ExpectStdoutContains { get; init; }

    /// <summary>Optional substring that must NOT appear in stdout.</summary>
    [JsonPropertyName("expectStdoutNotContains")]
    public string? ExpectStdoutNotContains { get; init; }

    /// <summary>Optional human-readable description for failure reporting.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>Lifecycle of a single replay execution.</summary>
public enum E2eRunStatus
{
    Queued = 0,
    Running = 1,
    Passed = 2,
    Failed = 3,
    Error = 4,
    Canceled = 5,
}

/// <summary>
/// One execution of a test case's replay artifact against a freshly-cloned
/// pool VM. Many runs of the same case may exist over time — each one is its
/// own row.
/// </summary>
public sealed record E2eRun
{
    public required string Id { get; init; }
    public required string TestCaseId { get; init; }
    public required E2eRunStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Free-form result text; expected to be a serialized <see cref="E2eRunResult"/> for terminal states.</summary>
    public string? Result { get; init; }

    /// <summary>Identifier of the pool node that picked the run up (sandbox VM id, remote host id, etc.).</summary>
    public string? SandboxId { get; init; }

    /// <summary>Optional batch correlation id so callers can aggregate fan-outs.</summary>
    public string? BatchId { get; init; }
}

/// <summary>
/// Terminal outcome of a single replay. Stored serialized in <see cref="E2eRun.Result"/>.
/// Kept in Core so callers, the dispatcher, the API, and the tests share the shape.
/// </summary>
public sealed record E2eRunResult
{
    [JsonPropertyName("passed")]
    public required bool Passed { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    /// <summary>Index of the first failing step or assertion (0-based); null on pass.</summary>
    [JsonPropertyName("failedStepIndex")]
    public int? FailedStepIndex { get; init; }

    [JsonPropertyName("failureKind")]
    public string? FailureKind { get; init; }

    [JsonPropertyName("stepResults")]
    public IReadOnlyList<E2eStepResult> StepResults { get; init; } = [];

    [JsonPropertyName("assertionResults")]
    public IReadOnlyList<E2eAssertionResult> AssertionResults { get; init; } = [];

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }
}

public sealed record E2eStepResult
{
    [JsonPropertyName("exitCode")]
    public int ExitCode { get; init; }

    [JsonPropertyName("stdoutTail")]
    public string StdoutTail { get; init; } = string.Empty;

    [JsonPropertyName("stderrTail")]
    public string StderrTail { get; init; } = string.Empty;

    [JsonPropertyName("passed")]
    public bool Passed { get; init; }
}

public sealed record E2eAssertionResult
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("passed")]
    public bool Passed { get; init; }

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;
}
