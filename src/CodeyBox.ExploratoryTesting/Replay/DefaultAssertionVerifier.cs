using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Default <see cref="IAssertionVerifier"/>. Handles the three assertion
/// kinds the recorder emits today:
///
/// <list type="bullet">
///   <item><c>visual-match</c>: PNG byte-equality between the recorded
///   observation screenshot and the current screenshot. <c>Detail</c> may
///   name a screenshot in the named-recordings map; when unset, the engine's
///   per-step recorded screenshot (threaded through VerifyAsync) is the
///   comparison target.</item>
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
///
/// <para>The verifier itself is stateless: the recorded screenshot flows
/// through <see cref="VerifyAsync"/> as a parameter, never as a mutable
/// property. A single instance is safe to share across concurrent replays.</para>
/// </summary>
public sealed class DefaultAssertionVerifier : IAssertionVerifier
{
    private readonly IReadOnlyDictionary<string, byte[]> _recordedScreenshots;
    private readonly IScreenshotComparer _screenshotComparer;

    public DefaultAssertionVerifier()
        : this(
            recordedScreenshots: new Dictionary<string, byte[]>(StringComparer.Ordinal),
            screenshotComparer: ExactBytesScreenshotComparer.Instance)
    {
    }

    /// <summary>
    /// Creates a verifier that can resolve <c>visual-match</c> assertions
    /// against recorded screenshots keyed by the assertion's
    /// <see cref="TraceAssertion.Detail"/>. When the assertion has no
    /// <c>Detail</c> the per-step recorded screenshot passed to
    /// <see cref="VerifyAsync"/> is the comparison target; when
    /// <c>Detail</c> is set but the map does not contain that key, the
    /// verifier returns a configuration-error diagnostic instead of silently
    /// falling back to the per-step screenshot (which would compare against
    /// the wrong reference image).
    /// </summary>
    public DefaultAssertionVerifier(IReadOnlyDictionary<string, byte[]> recordedScreenshots)
        : this(recordedScreenshots, ExactBytesScreenshotComparer.Instance)
    {
    }

    /// <summary>
    /// Like the dictionary-only constructor but accepts a custom
    /// <see cref="IScreenshotComparer"/> — wire in a perceptual-diff or
    /// tolerance-window comparator for production renders where PNG bytes
    /// are non-deterministic across runs / hosts.
    /// </summary>
    public DefaultAssertionVerifier(
        IReadOnlyDictionary<string, byte[]> recordedScreenshots,
        IScreenshotComparer screenshotComparer)
    {
        ArgumentNullException.ThrowIfNull(recordedScreenshots);
        ArgumentNullException.ThrowIfNull(screenshotComparer);
        _recordedScreenshots = recordedScreenshots;
        _screenshotComparer = screenshotComparer;
    }

    public Task<string?> VerifyAsync(
        ISandbox sandbox,
        TraceAssertion assertion,
        byte[]? currentScreenshotPng,
        byte[]? recordedScreenshotPng,
        string? accessibilitySnapshotJson,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(assertion);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(VerifyCore(assertion, currentScreenshotPng, recordedScreenshotPng, accessibilitySnapshotJson));
    }

    private string? VerifyCore(
        TraceAssertion assertion,
        byte[]? currentScreenshotPng,
        byte[]? recordedScreenshotPng,
        string? accessibilitySnapshotJson)
    {
        switch (assertion.Kind)
        {
            case "visual-match":
                {
                    var resolution = ResolveExpectedScreenshot(assertion, recordedScreenshotPng);
                    if (resolution.MissingNamedKey)
                        return $"visual-match assertion references unknown named recording '{DiagnosticText.Sanitize(assertion.Detail)}'";
                    if (resolution.Bytes is null)
                        return "visual-match assertion has no recorded screenshot to compare against";
                    if (currentScreenshotPng is null)
                        return "visual-match assertion: current observation has no screenshot";
                    var verdict = _screenshotComparer.Compare(resolution.Bytes, currentScreenshotPng);
                    return verdict.Matches
                        ? null
                        : verdict.Diagnostic ?? $"visual-match assertion: current screenshot ({currentScreenshotPng.Length} bytes) differs from recorded ({resolution.Bytes.Length} bytes)";
                }
            case "text-contains":
                {
                    if (string.IsNullOrEmpty(assertion.Detail))
                        return "text-contains assertion has no Detail (expected text)";
                    if (string.IsNullOrEmpty(accessibilitySnapshotJson))
                        return $"text-contains assertion expected '{DiagnosticText.Sanitize(assertion.Detail)}' but accessibility tree is empty";
                    return accessibilitySnapshotJson.Contains(assertion.Detail, StringComparison.Ordinal)
                        ? null
                        : $"text-contains assertion expected '{DiagnosticText.Sanitize(assertion.Detail)}' but no match in accessibility tree";
                }
            case "element-present":
                {
                    if (string.IsNullOrEmpty(assertion.Detail))
                        return "element-present assertion has no Detail (expected element marker)";
                    if (string.IsNullOrEmpty(accessibilitySnapshotJson))
                        return $"element-present assertion expected '{DiagnosticText.Sanitize(assertion.Detail)}' but accessibility tree is empty";
                    return accessibilitySnapshotJson.Contains(assertion.Detail, StringComparison.Ordinal)
                        ? null
                        : $"element-present assertion expected '{DiagnosticText.Sanitize(assertion.Detail)}' but no match in accessibility tree";
                }
            default:
                return $"unsupported assertion kind '{DiagnosticText.Sanitize(assertion.Kind)}'";
        }
    }

    private ScreenshotResolution ResolveExpectedScreenshot(TraceAssertion assertion, byte[]? recordedScreenshotPng)
    {
        if (string.IsNullOrEmpty(assertion.Detail))
            return new ScreenshotResolution(recordedScreenshotPng, MissingNamedKey: false);

        // Detail names a specific recording: insist on a hit so a typo'd key
        // surfaces as a configuration error rather than silently comparing
        // against the per-step recorded screenshot (which would be the wrong
        // reference image for an assertion that explicitly named a different
        // one).
        if (_recordedScreenshots.TryGetValue(assertion.Detail, out var named))
            return new ScreenshotResolution(named, MissingNamedKey: false);

        return new ScreenshotResolution(Bytes: null, MissingNamedKey: true);
    }

    private readonly record struct ScreenshotResolution(byte[]? Bytes, bool MissingNamedKey);
}
