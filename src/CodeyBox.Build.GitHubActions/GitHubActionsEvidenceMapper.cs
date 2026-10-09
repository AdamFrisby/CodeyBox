using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Build.GitHubActions;

/// <summary>
/// Approved test-report JSON schema carried inside the report artifact zip.
/// Minimal and toolchain-neutral on purpose: counts plus the producing
/// framework name. Example: <c>{"version":1,"framework":"dotnet-test","passed":41,"failed":0,"skipped":2}</c>.
/// </summary>
public sealed record GitHubActionsTestReport(
    int Passed, int Failed, int Skipped, string Framework);

/// <summary>Provider-level outcome of mapping one completed run.</summary>
public enum GitHubActionsRunOutcome
{
    Succeeded = 0,
    Failed = 1,
    Cancelled = 2,
}

/// <summary>Mapped result: neutral evidence plus the bounded detail string.</summary>
public sealed record GitHubActionsMappedEvidence(
    GitHubActionsRunOutcome Outcome,
    ExternalBuildEvidence? Evidence,
    string Detail);

/// <summary>
/// Pure mapping from GitHub run/job/report observations to neutral
/// framework evidence. All provider input is untrusted: repository,
/// workflow, fork/event, and exact-checkout substitution is rejected before
/// any conclusion is trusted, every terminal conclusion is handled, and a
/// workflow success without explicit approved test-report evidence never
/// counts tests as passed.
/// </summary>
public static class GitHubActionsEvidenceMapper
{
    private const int MaxReportTotal = 10_000_000;

    /// <summary>
    /// Parses approved report JSON. Malformed, versioned-wrong, negative, or
    /// oversized payloads throw typed unavailable — never a silent pass.
    /// </summary>
    public static GitHubActionsTestReport ParseReport(byte[] jsonBytes, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(jsonBytes);
        if (jsonBytes.LongLength > maxBytes)
            throw new GitHubActionsEvidenceUnavailableException(
                $"test report is {jsonBytes.LongLength} bytes, exceeding the {maxBytes}-byte cap");
        if (jsonBytes.Length == 0)
            throw new GitHubActionsEvidenceUnavailableException("test report is empty");
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonBytes);
        }
        catch (JsonException ex)
        {
            throw new GitHubActionsEvidenceUnavailableException("test report is malformed JSON", ex);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new GitHubActionsEvidenceUnavailableException("test report root must be an object");
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != 1)
                throw new GitHubActionsEvidenceUnavailableException("test report version must be 1");
            var framework = root.TryGetProperty("framework", out var fw) && fw.ValueKind == JsonValueKind.String
                ? fw.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(framework) || framework.Length > 128)
                throw new GitHubActionsEvidenceUnavailableException("test report framework must be named");
            var passed = ReadCount(root, "passed");
            var failed = ReadCount(root, "failed");
            var skipped = ReadCount(root, "skipped");
            if ((long)passed + failed + skipped > MaxReportTotal)
                throw new GitHubActionsEvidenceUnavailableException("test report counts exceed plausible bounds");
            return new GitHubActionsTestReport(passed, failed, skipped, framework.Trim());
        }
    }

    private static int ReadCount(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number
            || !el.TryGetInt32(out var value) || value < 0)
            throw new GitHubActionsEvidenceUnavailableException($"test report '{name}' must be a non-negative integer");
        return value;
    }

    /// <summary>
    /// Extracts the exact report file from an artifact zip without
    /// extracting anything else. Traversal, symlink, entry-count, and
    /// expansion-ratio guards run through the shared framework policy BEFORE
    /// buffering; the report entry itself is size-checked before AND during
    /// the copy. Nothing downloaded is ever executed.
    /// </summary>
    public static byte[] ExtractReportFile(
        byte[] zipBytes,
        string reportFileName,
        ExternalBuildOptions frameworkOptions,
        long maxReportBytes)
    {
        ArgumentNullException.ThrowIfNull(zipBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportFileName);
        ArgumentNullException.ThrowIfNull(frameworkOptions);
        if (zipBytes.LongLength > frameworkOptions.MaxArtifactBytes)
            throw new GitHubActionsEvidenceUnavailableException(
                $"report artifact is {zipBytes.LongLength} bytes, exceeding the {frameworkOptions.MaxArtifactBytes}-byte ingestion cap");
        using var stream = new MemoryStream(zipBytes, writable: false);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            throw new GitHubActionsEvidenceUnavailableException("report artifact is not a readable archive", ex);
        }
        using (archive)
        {
            var tuples = new List<(string Path, long CompressedSize, long UncompressedSize, bool IsSymlink)>();
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.Length == 0)
                    continue;
                tuples.Add((entry.FullName, Math.Max(0, entry.CompressedLength), Math.Max(0, entry.Length), IsSymlink(entry)));
            }
            var guardError = ExternalBuildArtifactGuard.ValidateArchiveEntries(tuples, frameworkOptions);
            if (guardError is not null)
                throw new GitHubActionsEvidenceUnavailableException("report archive rejected: " + guardError);
            var match = archive.Entries.FirstOrDefault(
                entry => string.Equals(entry.FullName, reportFileName, StringComparison.Ordinal));
            if (match is null)
                throw new GitHubActionsEvidenceUnavailableException(
                    $"report file '{reportFileName}' is not in the artifact archive");
            if (match.Length > maxReportBytes)
                throw new GitHubActionsEvidenceUnavailableException(
                    $"report file is {match.Length} bytes, exceeding the {maxReportBytes}-byte cap");
            using var entryStream = match.Open();
            using var buffered = new MemoryStream();
            var remaining = maxReportBytes + 1;
            var chunk = new byte[8192];
            int read;
            while ((read = entryStream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining))) > 0)
            {
                remaining -= read;
                if (remaining < 0)
                    throw new GitHubActionsEvidenceUnavailableException("report file exceeds the byte cap while reading");
                buffered.Write(chunk, 0, read);
            }
            return buffered.ToArray();
        }
    }

    private static bool IsSymlink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int SymlinkType = 0xA000;
        return ((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == SymlinkType;
    }

    /// <summary>
    /// Maps one completed run plus its jobs to neutral evidence. Returns
    /// Failed (never pass) for every substitution, missing/skipped-report,
    /// or non-success conclusion; Cancelled only for provider-confirmed
    /// cancellation. Callers must only invoke this for completed runs.
    /// </summary>
    public static GitHubActionsMappedEvidence MapCompletedRun(
        GitHubActionsRun run,
        IReadOnlyList<GitHubActionsJob> jobs,
        GitHubActionsBinding binding,
        GitHubActionsWorkflowApproval approval,
        GitHubActionsTestReport? report,
        IReadOnlyDictionary<string, string> packageDigests,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(packageDigests);

        var substitution = CheckSubstitution(run, binding, approval);
        if (substitution is not null)
            return new GitHubActionsMappedEvidence(GitHubActionsRunOutcome.Failed, null, substitution);

        if (IsCancelled(run.Conclusion))
            return new GitHubActionsMappedEvidence(
                GitHubActionsRunOutcome.Cancelled, null, "provider confirms cancellation");
        if (!string.Equals(run.Conclusion, GitHubActionsConclusions.Success, StringComparison.OrdinalIgnoreCase))
            return new GitHubActionsMappedEvidence(
                GitHubActionsRunOutcome.Failed, null,
                $"terminal conclusion '{run.Conclusion ?? "none"}' is not authoritative success");

        var compile = MapCompile(jobs, approval);
        if (compile is not null)
            return new GitHubActionsMappedEvidence(GitHubActionsRunOutcome.Failed, null, compile);

        var tests = MapTests(jobs, approval, report);
        if (tests.Error is not null)
            return new GitHubActionsMappedEvidence(GitHubActionsRunOutcome.Failed, null, tests.Error);

        var package = packageDigests.Count > 0
            ? ExternalBuildDimensionOutcome.Passed
            : ExternalBuildDimensionOutcome.NotRun;
        var evidence = new ExternalBuildEvidence
        {
            Compile = ExternalBuildDimensionOutcome.Passed,
            Tests = tests.Outcome,
            Package = package,
            SourceDigestSha256 = binding.SourceDigest,
            ProviderRunId = run.Id.ToString(CultureInfo.InvariantCulture),
            WorkflowIdentity = $"{approval.Owner}/{approval.Repository}/{approval.WorkflowPath}@{approval.Toolchain}",
            ApprovedTargetName = binding.ApprovedTargetName,
            Toolchain = approval.Toolchain,
            Platform = approval.Platform,
            Configuration = approval.Configuration,
            ArtifactDigests = new Dictionary<string, string>(packageDigests, StringComparer.Ordinal),
            Authoritative = true,
            CapturedAt = now,
        };
        return new GitHubActionsMappedEvidence(
            GitHubActionsRunOutcome.Succeeded, evidence,
            $"authoritative workflow evidence for run {run.Id} attempt {run.RunAttempt}");
    }

    /// <summary>
    /// Substitution guards, evaluated before any conclusion is trusted. A
    /// matching mutable ref, a success badge, or "the latest run" is never
    /// proof: only the exact repository, workflow file, non-fork event, and
    /// pinned checkout sha pass.
    /// </summary>
    public static string? CheckSubstitution(
        GitHubActionsRun run,
        GitHubActionsBinding binding,
        GitHubActionsWorkflowApproval approval)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(approval);
        var expectedRepo = approval.Owner + "/" + approval.Repository;
        if (!string.Equals(run.BaseRepository, expectedRepo, StringComparison.OrdinalIgnoreCase))
            return $"run substitution rejected: repository '{run.BaseRepository}' is not the approved '{expectedRepo}'";
        if (!string.Equals(run.WorkflowPath, approval.WorkflowPath, StringComparison.Ordinal))
            return $"run substitution rejected: workflow '{run.WorkflowPath}' is not the approved '{approval.WorkflowPath}'";
        if (!string.Equals(run.Event, "workflow_dispatch", StringComparison.OrdinalIgnoreCase))
            return $"run substitution rejected: event '{run.Event}' is not an approved dispatch";
        if (!string.Equals(run.HeadRepository, run.BaseRepository, StringComparison.OrdinalIgnoreCase))
            return $"run substitution rejected: head repository '{run.HeadRepository}' is a fork of '{run.BaseRepository}'";
        var expectedSha = binding.ExpectedMergeSha ?? binding.ExpectedHeadSha;
        if (!string.Equals(run.HeadSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            return "checkout mismatch: run checked out '"
                + run.HeadSha + "' but the frozen candidate requires '" + expectedSha
                + "'; mutable-ref or latest-run proof is rejected";
        return null;
    }

    private static bool IsCancelled(string? conclusion) =>
        string.Equals(conclusion, GitHubActionsConclusions.Cancelled, StringComparison.OrdinalIgnoreCase);

    private static string? MapCompile(
        IReadOnlyList<GitHubActionsJob> jobs, GitHubActionsWorkflowApproval approval)
    {
        foreach (var required in approval.CompileJobNames)
        {
            GitHubActionsJob? job = null;
            foreach (var candidate in jobs)
            {
                if (string.Equals(candidate.Name, required, StringComparison.Ordinal))
                {
                    job = candidate;
                    break;
                }
            }
            if (job is null)
                return $"required compile job '{required}' did not report; refusing silent pass";
            if (string.Equals(job.Conclusion, GitHubActionsConclusions.Skipped, StringComparison.OrdinalIgnoreCase))
                return $"required compile job '{required}' was skipped; missing evidence never counts as pass";
            if (!string.Equals(job.Conclusion, GitHubActionsConclusions.Success, StringComparison.OrdinalIgnoreCase))
                return $"required compile job '{required}' concluded '{job.Conclusion ?? job.Status}'";
        }
        return null;
    }

    private static (ExternalBuildDimensionOutcome Outcome, string? Error) MapTests(
        IReadOnlyList<GitHubActionsJob> jobs,
        GitHubActionsWorkflowApproval approval,
        GitHubActionsTestReport? report)
    {
        if (approval.RequireTestReport)
        {
            if (report is null)
                return (ExternalBuildDimensionOutcome.NotRun,
                    "test report missing: workflow success does not imply tests ran; explicit approved report evidence is required");
            if (report.Failed > 0)
                return (ExternalBuildDimensionOutcome.NotRun,
                    $"tests failed: {report.Failed} failed, {report.Passed} passed, {report.Skipped} skipped ({report.Framework})");
            if (report.Passed == 0)
                return (ExternalBuildDimensionOutcome.NotRun,
                    "test report shows zero passed tests; absent tests never count as pass");
            return (ExternalBuildDimensionOutcome.Passed, null);
        }
        if (approval.TestJobNames.Count == 0)
            return (ExternalBuildDimensionOutcome.NotRun, null);
        foreach (var required in approval.TestJobNames)
        {
            GitHubActionsJob? job = null;
            foreach (var candidate in jobs)
            {
                if (string.Equals(candidate.Name, required, StringComparison.Ordinal))
                {
                    job = candidate;
                    break;
                }
            }
            if (job is null || string.Equals(job.Conclusion, GitHubActionsConclusions.Skipped, StringComparison.OrdinalIgnoreCase))
                return (ExternalBuildDimensionOutcome.NotRun, null);
            if (!string.Equals(job.Conclusion, GitHubActionsConclusions.Success, StringComparison.OrdinalIgnoreCase))
                return (ExternalBuildDimensionOutcome.Failed,
                    $"test job '{required}' concluded '{job.Conclusion ?? job.Status}'");
        }
        return (ExternalBuildDimensionOutcome.Passed, null);
    }
}
