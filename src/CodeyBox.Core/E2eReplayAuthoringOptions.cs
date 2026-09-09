using System;

namespace CodeyBox.Core;

/// <summary>
/// Config bound from <c>CodeyBox:E2eReplayAuthoring</c>. Drives the
/// post-implementation gate that makes every declared e2e-replay test case end
/// up with a committed replay that re-runs green on the cheap-CPU E2E pool.
///
/// <para>The gate authors a missing (or broken) replay with a <b>cheap</b>
/// model — never the coding fleet's frontier agents — verifies it via the E2E
/// execution infrastructure, and blocks the work item when a declared case
/// cannot be made green. All values here are operational knobs, hot-reloadable
/// through <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>.</para>
/// </summary>
public sealed class E2eReplayAuthoringOptions
{
    /// <summary>
    /// Master switch. When false the gate is a complete no-op: it neither
    /// authors nor verifies nor blocks, and declared e2e cases pass straight
    /// through audit. Default false; opt in per deployment once a cheap-model
    /// authoring driver and the E2E execution pool are wired.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// How many times a committed replay that verifies red (a broken replay —
    /// the "UI changed" case) is re-authored before the gate gives up and
    /// blocks. 0 disables re-authoring (a first red verdict blocks). Bounded by
    /// <see cref="MaxAllowedReauthorAttempts"/> so a config typo cannot loop the
    /// cheap-model author unboundedly. Default 1.
    /// </summary>
    public int MaxReauthorAttempts { get; set; } = 1;

    /// <summary>
    /// Cheap model id the authoring driver uses. Must satisfy the cheap-model
    /// allowlist (haiku / flash class) — the driver rejects a frontier id so
    /// authoring can never burn coding quota. Metadata for audit trails; the
    /// driver is the enforcement point.
    /// </summary>
    public string AuthorModelId { get; set; } = "claude-haiku-4-5-20251001";

    /// <summary>
    /// Wall-clock cap the verifier waits for a single enqueued replay run to
    /// reach a terminal status on the E2E pool. On expiry the verifier cancels
    /// the run and reports a red (blocking) verdict — an unverifiable replay is
    /// never treated as green. Default 20 minutes (above the pool's default
    /// 15-minute per-run cap so a genuine slow run isn't clipped early).
    /// </summary>
    public TimeSpan VerificationTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Cadence at which the verifier polls the run store for a terminal status.
    /// Default 2 seconds.
    /// </summary>
    public TimeSpan VerificationPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Floor for <see cref="MaxReauthorAttempts"/>.</summary>
    public const int MinReauthorAttempts = 0;

    /// <summary>Ceiling for <see cref="MaxReauthorAttempts"/>.</summary>
    public const int MaxAllowedReauthorAttempts = 10;
}
