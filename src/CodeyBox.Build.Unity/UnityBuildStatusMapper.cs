using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Build.Unity;

/// <summary>
/// Pure mapping from untrusted Unity Build Automation payloads to the neutral
/// provider status and evidence. Every string is bounded before retention,
/// secrets are redacted, and anything uncertain yields missing evidence —
/// which the shared gate never counts as a pass. A player/editor compilation
/// or a successful package is never reported as a test pass: tests pass only
/// on an explicit test report with zero failures.
/// </summary>
public static class UnityBuildStatusMapper
{
    public const int MaxArtifactsMapped = 32;
    public const long MaxPlausibleTestCount = 10_000_000;

    public static ExternalBuildProviderStatus Map(
        UnityBuildDetails? details,
        UnityBuildTarget target,
        string requestedCommit,
        string expectedSourceDigest,
        string providerRunId,
        int maxDiagnosticsChars,
        long maxArtifactBytes,
        IReadOnlyList<string> allowedArtifactHosts,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRunId);
        ArgumentNullException.ThrowIfNull(allowedArtifactHosts);
        if (details is null)
            return new ExternalBuildProviderStatus(
                ExternalBuildExecutionPhase.Unknown, null, "empty provider response", observedAt);

        var status = (details.BuildStatus ?? string.Empty).Trim().ToLowerInvariant();
        var cancelState = (details.CancelState ?? string.Empty).Trim().ToLowerInvariant();
        var diagnosticsCap = Math.Max(256, maxDiagnosticsChars);
        var phase = status switch
        {
            "queued" or "scheduled" or "senttobuilder" or "pending" => ExternalBuildExecutionPhase.Queued,
            "started" or "building" or "running" => ExternalBuildExecutionPhase.Running,
            "success" or "succeeded" => ExternalBuildExecutionPhase.Succeeded,
            "failure" or "failed" => ExternalBuildExecutionPhase.Failed,
            "canceled" or "cancelled" => ExternalBuildExecutionPhase.Cancelled,
            _ => ExternalBuildExecutionPhase.Unknown,
        };
        if (phase is ExternalBuildExecutionPhase.Running && cancelState is "pending" or "requested")
            return new ExternalBuildProviderStatus(
                ExternalBuildExecutionPhase.Running, null,
                Detail("cancellation pending: provider has not stopped the build yet", details, diagnosticsCap),
                observedAt);
        if (phase is ExternalBuildExecutionPhase.Queued or ExternalBuildExecutionPhase.Running
            or ExternalBuildExecutionPhase.Unknown)
            return new ExternalBuildProviderStatus(
                phase, null, Detail(phase == ExternalBuildExecutionPhase.Unknown
                    ? $"unrecognized provider status '{Truncate(details.BuildStatus, 64)}'"
                    : null, details, diagnosticsCap), observedAt);
        if (phase == ExternalBuildExecutionPhase.Cancelled)
            return new ExternalBuildProviderStatus(
                phase, null, Detail("provider stopped the build before a verdict; no compile/test/package outcome is claimed", details, diagnosticsCap),
                observedAt);

        if (IsLatestCheckout(details))
            return Terminal(ExternalBuildExecutionPhase.Failed, null,
                $"provider built latest-on-branch instead of the frozen candidate; refusing to adopt", details, observedAt);
        var checkout = (details.CheckoutCommit ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(checkout))
            return Terminal(ExternalBuildExecutionPhase.Failed, null,
                "provider reported no exact checkout commit; frozen-candidate correlation unavailable", details, observedAt);
        if (!string.Equals(checkout, requestedCommit.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            return Terminal(ExternalBuildExecutionPhase.Failed, null,
                "provider checkout does not match the dispatched candidate; refusing substituted evidence", details, observedAt);
        if (details.EditorVersion is not null
            && !string.Equals(details.EditorVersion.Trim(), target.EditorVersion, StringComparison.Ordinal))
            return Terminal(ExternalBuildExecutionPhase.Failed, null,
                $"provider editor '{Truncate(details.EditorVersion, 64)}' does not match approved '{target.EditorVersion}'", details, observedAt);
        if (details.Platform is not null
            && !string.Equals(details.Platform.Trim(), target.Platform, StringComparison.Ordinal))
            return Terminal(ExternalBuildExecutionPhase.Failed, null,
                $"provider platform '{Truncate(details.Platform, 64)}' does not match approved '{target.Platform}'", details, observedAt);

        var failurePhase = (details.FailurePhase ?? string.Empty).Trim().ToLowerInvariant();
        var compile = CompileOutcome(phase, failurePhase);
        var tests = TestOutcome(phase, failurePhase, details.TestReport);
        var digests = ArtifactDigests(details, maxArtifactBytes, allowedArtifactHosts);
        var package = PackageOutcome(phase, failurePhase, digests);
        var detail = BoundedDetail(details, maxDiagnosticsChars);
        var evidence = new ExternalBuildEvidence
        {
            Compile = compile,
            Tests = tests,
            Package = package,
            SourceDigestSha256 = expectedSourceDigest,
            ProviderRunId = providerRunId,
            WorkflowIdentity = target.WorkflowIdentity,
            ApprovedTargetName = target.ApprovedTargetName,
            Toolchain = "unity-editor/" + target.EditorVersion,
            Platform = target.Platform,
            Configuration = target.Configuration,
            ArtifactDigests = digests,
            Authoritative = true,
            CapturedAt = observedAt,
        };
        return Terminal(phase, evidence, detail, details, observedAt);
    }

    private static bool IsLatestCheckout(UnityBuildDetails details) =>
        string.Equals((details.CheckoutMode ?? string.Empty).Trim(), "latest", StringComparison.OrdinalIgnoreCase);

    private static ExternalBuildDimensionOutcome CompileOutcome(
        ExternalBuildExecutionPhase phase, string failurePhase)
    {
        if (phase == ExternalBuildExecutionPhase.Succeeded)
            return ExternalBuildDimensionOutcome.Passed;
        return failurePhase switch
        {
            "test" or "package" or "publish" => ExternalBuildDimensionOutcome.Passed,
            _ => ExternalBuildDimensionOutcome.Failed,
        };
    }

    private static ExternalBuildDimensionOutcome TestOutcome(
        ExternalBuildExecutionPhase phase, string failurePhase, UnityTestReport? report)
    {
        if (report is not null && IsPlausible(report))
        {
            if (report.Failed > 0)
                return ExternalBuildDimensionOutcome.Failed;
            if (report is { Total: > 0, Passed: > 0 })
                return ExternalBuildDimensionOutcome.Passed;
            return ExternalBuildDimensionOutcome.NotRun;
        }
        if (string.Equals(failurePhase, "test", StringComparison.Ordinal))
            return ExternalBuildDimensionOutcome.Failed;
        return ExternalBuildDimensionOutcome.NotRun;
    }

    private static bool IsPlausible(UnityTestReport report) =>
        report.Total is >= 0 and <= MaxPlausibleTestCount
        && report.Passed >= 0 && report.Failed >= 0 && report.Skipped >= 0
        && report.Passed + report.Failed <= report.Total + report.Skipped;

    private static Dictionary<string, string> ArtifactDigests(
        UnityBuildDetails details, long maxArtifactBytes, IReadOnlyList<string> allowedHosts)
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in details.Artifacts.Take(MaxArtifactsMapped + 1))
        {
            if (digests.Count >= MaxArtifactsMapped)
                break;
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > ExternalBuildArtifactGuard.MaxArtifactNameChars)
                continue;
            var normalized = entry.Name.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Contains("..", StringComparison.Ordinal))
                continue;
            if (entry.Size < 0 || entry.Size > maxArtifactBytes)
                continue;
            if (string.IsNullOrWhiteSpace(entry.Sha256) || entry.Sha256.Trim().Length != 64
                || !entry.Sha256.Trim().All(static c => Uri.IsHexDigit(c)))
                continue;
            if (ExternalBuildArtifactGuard.ValidateArtifactUrl(entry.Url, allowedHosts) is not null)
                continue;
            digests.TryAdd(entry.Name, entry.Sha256.Trim().ToLowerInvariant());
        }
        return digests;
    }

    private static ExternalBuildDimensionOutcome PackageOutcome(
        ExternalBuildExecutionPhase phase, string failurePhase, IReadOnlyDictionary<string, string> digests)
    {
        if (digests.Count > 0)
            return ExternalBuildDimensionOutcome.Passed;
        if (string.Equals(failurePhase, "package", StringComparison.Ordinal)
            || string.Equals(failurePhase, "publish", StringComparison.Ordinal))
            return ExternalBuildDimensionOutcome.Failed;
        return ExternalBuildDimensionOutcome.NotRun;
    }

    private static ExternalBuildProviderStatus Terminal(
        ExternalBuildExecutionPhase phase, ExternalBuildEvidence? evidence, string? reason,
        UnityBuildDetails details, DateTimeOffset observedAt)
    {
        if (reason is null && details.QueuedSeconds.HasValue)
            reason = $"provider queue delay {Math.Max(0, details.QueuedSeconds.Value)}s";
        else if (reason is not null && details.QueuedSeconds.HasValue
            && !reason.Contains("queue delay", StringComparison.Ordinal))
            reason += $" | provider queue delay {Math.Max(0, details.QueuedSeconds.Value)}s";
        return new ExternalBuildProviderStatus(phase, evidence, reason, observedAt);
    }

    private static string? Detail(string? reason, UnityBuildDetails details, int maxChars)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(reason))
            parts.Add(reason);
        if (!string.IsNullOrWhiteSpace(details.FailureReason))
            parts.Add(details.FailureReason.Trim());
        if (details.QueuedSeconds.HasValue)
            parts.Add($"provider queue delay {Math.Max(0, details.QueuedSeconds.Value)}s");
        if (parts.Count == 0)
            return null;
        return ExternalBuildArtifactGuard.TruncateBounded(
            ExternalBuildArtifactGuard.Redact(string.Join(" | ", parts)), Math.Max(256, maxChars));
    }

    private static string? BoundedDetail(UnityBuildDetails details, int maxChars)
    {
        var raw = (details.FailureReason ?? string.Empty) + "\n" + (details.LogExcerpt ?? string.Empty);
        raw = raw.Trim();
        string? diagnostics = raw.Length == 0 ? null :
            ExternalBuildArtifactGuard.TruncateBounded(
                ExternalBuildArtifactGuard.Redact(raw), Math.Max(256, maxChars));
        if (details.QueuedSeconds.HasValue)
        {
            var queue = $"provider queue delay {Math.Max(0, details.QueuedSeconds.Value)}s";
            diagnostics = diagnostics is null ? queue : diagnostics + " | " + queue;
        }
        return diagnostics;
    }

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
            return "<empty>";
        var clean = ExternalBuildArtifactGuard.Redact(value);
        return clean.Length <= maxChars ? clean : clean[..maxChars] + "…";
    }
}
