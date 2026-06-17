using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Default <see cref="IAssertionVerifier"/>. Handles the three assertion
/// kinds the recorder emits today:
///
/// <list type="bullet">
///   <item><c>visual-match</c>: PNG byte-equality between the recorded
///   observation screenshot and the current screenshot. <c>Detail</c> is
///   ignored — the recorded screenshot is the source of truth.</item>
///   <item><c>text-contains</c>: <c>Detail</c> must occur as an Ordinal
///   substring of the current accessibility-tree JSON. Untrusted text is
///   treated as opaque data per the trace's security contract.</item>
///   <item><c>element-present</c>: <c>Detail</c> must occur as an Ordinal
///   substring of the current accessibility-tree JSON. Loose by design —
///   richer matchers belong in custom verifiers for richer accessibility
///   shapes.</item>
/// </list>
///
/// <para>Unknown assertion kinds fail with a clear diagnostic so a recording
/// from a newer recorder doesn't silently no-op.</para>
/// </summary>
public sealed class DefaultAssertionVerifier : IAssertionVerifier
{
    private readonly IReadOnlyDictionary<string, byte[]?> _recordedScreenshots;

    public DefaultAssertionVerifier() : this(new Dictionary<string, byte[]?>(StringComparer.Ordinal))
    {
    }

    /// <summary>
    /// Creates a verifier that can resolve <c>visual-match</c> assertions
    /// against recorded screenshots keyed by the assertion's
    /// <see cref="TraceAssertion.Detail"/>. When the map is empty, the
    /// engine's per-step recorded screenshot is the comparison target.
    /// </summary>
    public DefaultAssertionVerifier(IReadOnlyDictionary<string, byte[]?> recordedScreenshots)
    {
        ArgumentNullException.ThrowIfNull(recordedScreenshots);
        _recordedScreenshots = recordedScreenshots;
    }

    /// <summary>
    /// The per-step recorded screenshot the engine wants compared on a
    /// <c>visual-match</c> assertion. Set by <see cref="ReplayEngine"/>
    /// before each step so the verifier doesn't need to know the trace
    /// structure.
    /// </summary>
    public byte[]? CurrentRecordedScreenshot { get; set; }

    public Task<string?> VerifyAsync(
        ISandbox sandbox,
        TraceAssertion assertion,
        byte[]? screenshotPng,
        string? accessibilitySnapshotJson,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        _ = sandbox; _ = ct;

        return Task.FromResult(VerifyCore(assertion, screenshotPng, accessibilitySnapshotJson));
    }

    private string? VerifyCore(TraceAssertion assertion, byte[]? screenshotPng, string? accessibilitySnapshotJson)
    {
        switch (assertion.Kind)
        {
            case "visual-match":
                {
                    var expected = ResolveExpectedScreenshot(assertion);
                    if (expected is null)
                        return "visual-match assertion has no recorded screenshot to compare against";
                    if (screenshotPng is null)
                        return "visual-match assertion: current observation has no screenshot";
                    return ScreenshotsEqual(expected, screenshotPng)
                        ? null
                        : $"visual-match assertion: current screenshot ({screenshotPng.Length} bytes) differs from recorded ({expected.Length} bytes)";
                }
            case "text-contains":
                {
                    if (string.IsNullOrEmpty(assertion.Detail))
                        return "text-contains assertion has no Detail (expected text)";
                    if (string.IsNullOrEmpty(accessibilitySnapshotJson))
                        return $"text-contains assertion expected '{assertion.Detail}' but accessibility tree is empty";
                    return accessibilitySnapshotJson.Contains(assertion.Detail, StringComparison.Ordinal)
                        ? null
                        : $"text-contains assertion expected '{assertion.Detail}' but no match in accessibility tree";
                }
            case "element-present":
                {
                    if (string.IsNullOrEmpty(assertion.Detail))
                        return "element-present assertion has no Detail (expected element marker)";
                    if (string.IsNullOrEmpty(accessibilitySnapshotJson))
                        return $"element-present assertion expected '{assertion.Detail}' but accessibility tree is empty";
                    return accessibilitySnapshotJson.Contains(assertion.Detail, StringComparison.Ordinal)
                        ? null
                        : $"element-present assertion expected '{assertion.Detail}' but no match in accessibility tree";
                }
            default:
                return $"unsupported assertion kind '{assertion.Kind}'";
        }
    }

    private byte[]? ResolveExpectedScreenshot(TraceAssertion assertion)
    {
        if (!string.IsNullOrEmpty(assertion.Detail) &&
            _recordedScreenshots.TryGetValue(assertion.Detail, out var named) && named is not null)
        {
            return named;
        }
        return CurrentRecordedScreenshot;
    }

    private static bool ScreenshotsEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
    }
}
