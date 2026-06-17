using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Verifies a recorded <see cref="TraceAssertion"/> against the current
/// observation of the screen, in real terms (what the user actually sees /
/// what the accessibility tree exposes). Returns null on success, or a
/// human-readable diagnostic on mismatch.
///
/// <para>Treated as a seam so non-web modalities can plug in richer
/// matchers (CLI output match, API response shape, ...). The default
/// implementation handles the three kinds documented on
/// <see cref="TraceAssertion.Kind"/>.</para>
/// </summary>
public interface IAssertionVerifier
{
    /// <summary>
    /// Return null on success; a diagnostic string when the assertion
    /// does not hold against <paramref name="screenshotPng"/> /
    /// <paramref name="accessibilitySnapshotJson"/>.
    /// </summary>
    Task<string?> VerifyAsync(
        ISandbox sandbox,
        TraceAssertion assertion,
        byte[]? screenshotPng,
        string? accessibilitySnapshotJson,
        CancellationToken ct);
}
