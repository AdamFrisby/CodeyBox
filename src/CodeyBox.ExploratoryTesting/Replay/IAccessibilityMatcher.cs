using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Decides what counts as a usable accessibility signature and whether a
/// <see cref="SandboxAccessibilitySnapshot"/> matches a recorded
/// <see cref="TraceAccessibilityDescriptor"/>. Extracted so callers
/// (locator, reachability checker, future verifiers) reason about
/// accessibility identity through a single seam — swapping in a custom
/// matcher changes the policy uniformly without anyone reaching into a
/// sibling concrete type's internals.
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

    /// <summary>
    /// Return true when <paramref name="expected"/> carries at least one
    /// non-empty signal the matcher would use for identity. Callers that need
    /// to know whether to bother probing accessibility at all (the
    /// reachability checker's top-most probe, the locator's gate against
    /// all-null descriptors) ask the matcher rather than the locator so the
    /// "what counts as a signal" decision lives in one place.
    /// </summary>
    bool HasAnyAccessibilitySignal(TraceAccessibilityDescriptor expected);
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
        if (!HasAnyAccessibilitySignal(expected)) return false;
        if (!StringMatches(snap.Role, expected.Role)) return false;
        if (!StringMatches(snap.Name, expected.Name)) return false;
        if (!StringMatches(snap.Text, expected.Text)) return false;
        if (!StringMatches(snap.ElementType, expected.ElementType)) return false;
        return true;
    }

    public bool HasAnyAccessibilitySignal(TraceAccessibilityDescriptor expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return !string.IsNullOrEmpty(expected.Role)
            || !string.IsNullOrEmpty(expected.Name)
            || !string.IsNullOrEmpty(expected.Text)
            || !string.IsNullOrEmpty(expected.ElementType);
    }

    private static bool StringMatches(string? actual, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return true;
        return string.Equals(actual ?? "", expected, StringComparison.Ordinal);
    }
}
