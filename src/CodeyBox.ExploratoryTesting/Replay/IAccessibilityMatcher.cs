using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Decides whether a <see cref="SandboxAccessibilitySnapshot"/> matches a
/// recorded <see cref="TraceAccessibilityDescriptor"/>. Extracted so the
/// reachability checker can reason about accessibility identity without
/// reaching into a sibling concrete locator's internals — accessibility
/// matching is a separate concept from element locating, and callers can swap
/// either without coupling them through static helpers.
/// </summary>
public interface IAccessibilityMatcher
{
    /// <summary>
    /// Return true when <paramref name="snap"/> describes the same logical
    /// element as <paramref name="expected"/>. Implementations should fail
    /// closed on an all-null expected descriptor — a recorder bug that emits
    /// no signal must not silently match every probe.
    /// </summary>
    bool Matches(SandboxAccessibilitySnapshot snap, TraceAccessibilityDescriptor expected);
}

/// <summary>
/// Default matcher: ordinal equality on every non-empty field
/// (role / name / text / element-type). Returns false when the recorded
/// descriptor carries no signal at all, so an empty descriptor never matches
/// every probe.
/// </summary>
public sealed class DefaultAccessibilityMatcher : IAccessibilityMatcher
{
    public static DefaultAccessibilityMatcher Instance { get; } = new();

    public bool Matches(SandboxAccessibilitySnapshot snap, TraceAccessibilityDescriptor expected)
    {
        ArgumentNullException.ThrowIfNull(snap);
        ArgumentNullException.ThrowIfNull(expected);
        return AccessibilityElementLocator.Matches(snap, expected);
    }
}
