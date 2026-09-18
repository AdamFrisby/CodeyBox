namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Recognises the benign AppArmor teardown race that can fail an Incus VM
/// stop: teardown asks <c>apparmor_parser</c> to unload the instance profile
/// after it has already been unloaded, so the parser reports
/// <c>Profile doesn't exist</c>. That message is the desired end state — the
/// profile is gone — not a failure. Callers must still verify the instance
/// reached its goal state (stopped or absent) before treating the operation
/// as successful; this helper only matches the failure signature, it never
/// proves the outcome.
/// </summary>
internal static class IncusBenignTeardown
{
    /// <summary>
    /// Marker for the AppArmor profile management tool in Incus CLI stderr.
    /// </summary>
    public const string ApparmorParserMarker = "apparmor_parser";

    /// <summary>
    /// Marker for the already-unloaded end state in <c>apparmor_parser</c>
    /// stderr. Matched exactly: a differently-worded profile error is a real
    /// failure and must stay reported.
    /// </summary>
    public const string ProfileDoesNotExistMarker = "Profile doesn't exist";

    /// <summary>
    /// True when <paramref name="exception"/> (or any exception it wraps) is
    /// the benign already-unloaded AppArmor profile teardown described above.
    /// Both markers must be present: neither an unrelated
    /// <c>apparmor_parser</c> failure nor an unrelated "doesn't exist" error
    /// may be absorbed.
    /// </summary>
    public static bool IsBenignApparmorProfileTeardown(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (!string.IsNullOrEmpty(message)
                && message.Contains(ApparmorParserMarker, StringComparison.Ordinal)
                && message.Contains(ProfileDoesNotExistMarker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
