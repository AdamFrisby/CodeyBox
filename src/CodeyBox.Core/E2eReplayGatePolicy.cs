using System;

namespace CodeyBox.Core;

/// <summary>
/// What a single declared e2e-replay test case needs before it can pass the
/// post-implementation replay gate.
/// </summary>
public enum E2eReplayCaseNeed
{
    /// <summary>Not a live, declared e2e-replay case (wrong automation kind, or archived) — the gate ignores it.</summary>
    NotApplicable,

    /// <summary>A declared e2e case with no committed replay artifact — the cheap-model author must produce one.</summary>
    NeedsAuthoring,

    /// <summary>
    /// A declared e2e case that carries a committed replay whose most-recent
    /// run is not a known pass — it must be re-verified (and re-authored if it
    /// verifies red).
    /// </summary>
    NeedsVerification,

    /// <summary>A declared e2e case whose committed replay's last run passed — nothing to do.</summary>
    Satisfied,
}

/// <summary>
/// Pure classification of a <see cref="TestCase"/> for the replay-authoring
/// gate. Kept side-effect-free so the "which cases need work / which block"
/// decision is trivially testable without any store, author, or executor.
/// </summary>
public static class E2eReplayGatePolicy
{
    /// <summary>A declared e2e case is a non-archived case whose automation kind is <see cref="AutomationKind.E2eReplay"/>.</summary>
    public static bool IsDeclaredE2eCase(TestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        return testCase.AutomationKind == AutomationKind.E2eReplay && !testCase.IsArchived;
    }

    /// <summary>A committed replay is a non-blank <see cref="TestCase.ExecutableArtifactJson"/>.</summary>
    public static bool HasCommittedReplay(TestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        return !string.IsNullOrWhiteSpace(testCase.ExecutableArtifactJson);
    }

    /// <summary>
    /// A committed replay is "known green" only when it exists AND its most
    /// recent run passed. The gate resets <see cref="TestCase.LastRunPassed"/>
    /// whenever it rewrites the artifact, so a stale pass can never mask a
    /// freshly-authored (unverified) replay.
    /// </summary>
    public static bool IsKnownGreen(TestCase testCase)
        => HasCommittedReplay(testCase) && testCase.LastRunPassed == true;

    /// <summary>Classifies a single test case for the gate.</summary>
    public static E2eReplayCaseNeed Classify(TestCase testCase)
    {
        if (!IsDeclaredE2eCase(testCase))
            return E2eReplayCaseNeed.NotApplicable;
        if (!HasCommittedReplay(testCase))
            return E2eReplayCaseNeed.NeedsAuthoring;
        return IsKnownGreen(testCase)
            ? E2eReplayCaseNeed.Satisfied
            : E2eReplayCaseNeed.NeedsVerification;
    }
}
