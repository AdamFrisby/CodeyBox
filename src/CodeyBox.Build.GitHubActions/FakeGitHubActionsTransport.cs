using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Build.GitHubActions;

/// <summary>
/// Deterministic fake GitHub Actions transport. Scripts runs, jobs,
/// artifacts, and failure modes through the REAL provider, mapper, and
/// framework paths — tests assert on produced state and evidence, never on
/// mock calls. Never contacts real providers; never reads real logs.
/// Polls advance run state (each <c>GetRunAsync</c> is one tick), so tests
/// stay deterministic without clocks or sleeps.
/// </summary>
public sealed class FakeGitHubActionsTransport : IGitHubActionsTransport
{
    private readonly object _gate = new();
    private readonly Dictionary<long, FakeRun> _runs = new();
    private readonly Dictionary<string, long> _runByCorrelation = new(StringComparer.Ordinal);
    private long _sequence = 1000;
    private int _dispatchCalls;

    /// <summary>When true, dispatch answers uncertain (as real 204s do) without a run id.</summary>
    public bool DispatchUncertain { get; set; }

    /// <summary>When true, the next dispatch throws a transient fault (retryable).</summary>
    public bool FailNextDispatchTransient { get; set; }

    /// <summary>When true, the next dispatch throws a rate-limit with <see cref="RateLimitedRetryAfter"/>.</summary>
    public bool FailNextDispatchRateLimited { get; set; }

    /// <summary>When true, the next poll (get/list) throws a rate-limit.</summary>
    public bool FailNextPollRateLimited { get; set; }

    /// <summary>When true, the next call throws an auth failure.</summary>
    public bool FailNextAuth { get; set; }

    /// <summary>Retry-after hint carried by the next injected rate-limit. Default 30s.</summary>
    public TimeSpan RateLimitedRetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>GetRun calls before a fresh run reports completed. Default 2.</summary>
    public int PollsToCompleted { get; set; } = 2;

    /// <summary>Terminal conclusion for fresh runs. Default success.</summary>
    public string TerminalConclusion { get; set; } = GitHubActionsConclusions.Success;

    /// <summary>
    /// Head-sha override per correlation id, simulating a workflow that
    /// checked out something other than the requested candidate.
    /// </summary>
    public Dictionary<string, string> HeadShaOverrides { get; } = new(StringComparer.Ordinal);

    /// <summary>Conclusion override per correlation id.</summary>
    public Dictionary<string, string> ConclusionOverrides { get; } = new(StringComparer.Ordinal);

    /// <summary>Scripted jobs per correlation id (defaults to build+test success).</summary>
    public Func<string, List<GitHubActionsJob>>? JobsFactory { get; set; }

    /// <summary>
    /// Scripted test report per correlation id. Null means the report
    /// artifact carries no report file (the missing-report path). Default:
    /// a passing report.
    /// </summary>
    public Func<string, GitHubActionsTestReport?>? ReportFactory { get; set; }

    /// <summary>When true, the report artifact zip is malformed bytes.</summary>
    public bool CorruptReportArchive { get; set; }

    /// <summary>When true, the report JSON is padded past the adapter report cap.</summary>
    public bool OversizeReport { get; set; }

    /// <summary>When true, report-artifact downloads throw an auth failure.</summary>
    public bool ReportDownloadAuthFailure { get; set; }

    /// <summary>When true, the report artifact is listed as expired.</summary>
    public bool ReportArtifactExpired { get; set; }

    public int DispatchCalls { get { lock (_gate) return _dispatchCalls; } }

    public const string ReportArtifactName = "codeybox-reports";
    public const string PackageArtifactName = "package-app.zip";

    public Task<GitHubActionsDispatchOutcome> DispatchAsync(
        GitHubActionsDispatchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _dispatchCalls++;
            if (FailNextDispatchTransient)
            {
                FailNextDispatchTransient = false;
                throw new GitHubActionsTransientException("fake transient dispatch fault");
            }
            if (FailNextDispatchRateLimited)
            {
                FailNextDispatchRateLimited = false;
                throw new ExternalBuildRateLimitedException(
                    "fake dispatch throttled", RateLimitedRetryAfter);
            }
            if (FailNextAuth)
            {
                FailNextAuth = false;
                throw new GitHubActionsAuthException("fake dispatch credential rejected (401)");
            }
            var key = CorrelationKey(request.Owner, request.Repository, request.WorkflowPath, request.RequestCorrelationId);
            if (_runByCorrelation.TryGetValue(key, out var existing))
                return Task.FromResult(new GitHubActionsDispatchOutcome(
                    true, existing.ToString(CultureInfo.InvariantCulture), null, false, 204));
            var id = ++_sequence;
            var headSha = HeadShaOverrides.TryGetValue(request.RequestCorrelationId, out var sha)
                ? sha : request.ExpectedHeadSha;
            var conclusion = ConclusionOverrides.TryGetValue(request.RequestCorrelationId, out var conc)
                ? conc : TerminalConclusion;
            var run = new FakeRun(
                id, request.Owner, request.Repository, request.WorkflowPath,
                request.RequestCorrelationId, request.CandidateRef, headSha, conclusion);
            _runs[id] = run;
            _runByCorrelation[key] = id;
            if (DispatchUncertain)
                return Task.FromResult(new GitHubActionsDispatchOutcome(
                    false, null, "accepted; run id withheld (204 semantics)", true, 204));
            return Task.FromResult(new GitHubActionsDispatchOutcome(
                true, id.ToString(CultureInfo.InvariantCulture), null, false, 204));
        }
    }

    public Task<GitHubActionsRun?> FindRunByCorrelationAsync(
        string owner, string repository, string workflowPath,
        string correlationId, string? headBranch, DateTimeOffset? createdAfter, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowPollFaultsLocked();
            var key = CorrelationKey(owner, repository, workflowPath, correlationId);
            if (!_runByCorrelation.TryGetValue(key, out var id) || !_runs.TryGetValue(id, out var run))
                return Task.FromResult<GitHubActionsRun?>(null);
            return Task.FromResult<GitHubActionsRun?>(ApplyConclusionOverrideLocked(run, run.ToRun(PollsToCompleted)));
        }
    }

    public Task<GitHubActionsRun?> GetRunAsync(string owner, string repository, long runId, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowPollFaultsLocked();
            if (!_runs.TryGetValue(runId, out var run)
                || !string.Equals(run.Owner, owner, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(run.Repository, repository, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<GitHubActionsRun?>(null);
            run.Polls++;
            return Task.FromResult<GitHubActionsRun?>(ApplyConclusionOverrideLocked(run, run.ToRun(PollsToCompleted)));
        }
    }

    public Task<IReadOnlyList<GitHubActionsJob>> ListJobsAsync(
        string owner, string repository, long runId, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowPollFaultsLocked();
            if (!_runs.TryGetValue(runId, out var run))
                return Task.FromResult<IReadOnlyList<GitHubActionsJob>>([]);
            if (JobsFactory is not null)
                return Task.FromResult<IReadOnlyList<GitHubActionsJob>>(JobsFactory(run.CorrelationId));
            return Task.FromResult<IReadOnlyList<GitHubActionsJob>>([
                new GitHubActionsJob(1, "build", GitHubActionsStatuses.Completed, GitHubActionsConclusions.Success),
                new GitHubActionsJob(2, "test", GitHubActionsStatuses.Completed, GitHubActionsConclusions.Success),
            ]);
        }
    }

    public Task CancelRunAsync(string owner, string repository, long runId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (FailNextAuth)
            {
                FailNextAuth = false;
                throw new GitHubActionsAuthException("fake cancel credential rejected (403)");
            }
            if (!_runs.TryGetValue(runId, out var run))
                throw new GitHubActionsValidationException($"fake run {runId} does not exist (404)");
            if (!run.IsTerminal(PollsToCompleted))
                run.Cancelled = true;
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<GitHubActionsArtifactInfo>> ListArtifactsAsync(
        string owner, string repository, long runId, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowPollFaultsLocked();
            if (!_runs.TryGetValue(runId, out _))
                return Task.FromResult<IReadOnlyList<GitHubActionsArtifactInfo>>([]);
            return Task.FromResult<IReadOnlyList<GitHubActionsArtifactInfo>>([
                new GitHubActionsArtifactInfo(
                    11, ReportArtifactName, 4096, ReportArtifactExpired,
                    $"https://api.github.com/fake/{runId}/artifacts/11", DateTimeOffset.UtcNow.AddHours(1)),
                new GitHubActionsArtifactInfo(
                    12, PackageArtifactName, 64, false,
                    $"https://api.github.com/fake/{runId}/artifacts/12", DateTimeOffset.UtcNow.AddHours(1)),
            ]);
        }
    }

    public Task<byte[]> DownloadArtifactAsync(GitHubActionsArtifactInfo artifact, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        lock (_gate)
        {
            if (ReportDownloadAuthFailure && string.Equals(artifact.Name, ReportArtifactName, StringComparison.Ordinal))
                throw new GitHubActionsAuthException("fake artifact download forbidden (403)");
            if (artifact.Expired)
                throw new GitHubActionsEvidenceUnavailableException($"artifact '{artifact.Name}' expired");
            byte[] content;
            if (string.Equals(artifact.Name, ReportArtifactName, StringComparison.Ordinal))
            {
                if (CorruptReportArchive)
                {
                    content = [0x50, 0x4B, 0x03, 0x00, 0xFF, 0xFF];
                }
                else
                {
                    var correlation = FindCorrelationForArtifactLocked(artifact);
                    var report = ReportFactory is not null ? ReportFactory(correlation) : DefaultReport();
                    content = report is null
                        ? throw new GitHubActionsEvidenceUnavailableException($"artifact '{artifact.Name}' has no report file")
                        : BuildReportZip(report, OversizeReport);
                }
            }
            else
            {
                content = BuildPackageZip(artifact.Name);
            }
            if (content.LongLength > maxBytes)
                throw new GitHubActionsEvidenceUnavailableException(
                    $"artifact '{artifact.Name}' is {content.LongLength} bytes, exceeding the {maxBytes}-byte cap");
            return Task.FromResult(content);
        }
    }

    /// <summary>Run id correlated to an id, for adoption in tests. Null on a miss.</summary>
    public string? GetRunId(string owner, string repository, string workflowPath, string correlationId)
    {
        lock (_gate)
        {
            var key = CorrelationKey(owner, repository, workflowPath, correlationId);
            return _runByCorrelation.TryGetValue(key, out var id)
                ? id.ToString(CultureInfo.InvariantCulture) : null;
        }
    }

    /// <summary>Simulates a rerun: bumps the attempt counter, resetting progress.</summary>
    public void Rerun(string correlationId)
    {
        lock (_gate)
        {
            foreach (var run in _runs.Values)
            {
                if (string.Equals(run.CorrelationId, correlationId, StringComparison.Ordinal))
                {
                    run.Attempt++;
                    run.Polls = 0;
                    run.Cancelled = false;
                }
            }
        }
    }

    /// <summary>
    /// Re-points a correlated run at a different head sha, simulating a
    /// workflow that checked out something other than the requested
    /// candidate (checkout-mismatch path).
    /// </summary>
    public void SetHeadSha(string correlationId, string headSha)
    {
        lock (_gate)
        {
            foreach (var run in _runs.Values)
            {
                if (string.Equals(run.CorrelationId, correlationId, StringComparison.Ordinal))
                    run.HeadSha = headSha;
            }
        }
    }

    /// <summary>Directly sets a run's reported attempt (obsolete-attempt racing).</summary>
    public void SetAttempt(string correlationId, int attempt)
    {
        lock (_gate)
        {
            foreach (var run in _runs.Values)
            {
                if (string.Equals(run.CorrelationId, correlationId, StringComparison.Ordinal))
                    run.Attempt = attempt;
            }
        }
    }

    /// <summary>
    /// Spoofs what the provider reports for a correlated run (workflow
    /// path, head repository, fork flag), simulating substitution attacks
    /// the adapter must reject by exact match.
    /// </summary>
    public void SpoofRun(
        string correlationId,
        string? workflowPath = null,
        string? headRepository = null,
        bool? fork = null)
    {
        lock (_gate)
        {
            foreach (var run in _runs.Values)
            {
                if (!string.Equals(run.CorrelationId, correlationId, StringComparison.Ordinal))
                    continue;
                if (workflowPath is not null)
                    run.ReportedWorkflowPath = workflowPath;
                if (headRepository is not null)
                    run.ReportedHeadRepository = headRepository;
                if (fork.HasValue)
                    run.ReportedFork = fork.Value;
            }
        }
    }

    /// <summary>Directly completes a run with a conclusion (late-completion racing).</summary>
    public void Complete(string correlationId, string conclusion)
    {
        lock (_gate)
        {
            foreach (var run in _runs.Values)
            {
                if (string.Equals(run.CorrelationId, correlationId, StringComparison.Ordinal))
                {
                    run.Conclusion = conclusion;
                    run.Polls = PollsToCompleted;
                }
            }
        }
    }

    private GitHubActionsRun ApplyConclusionOverrideLocked(FakeRun run, GitHubActionsRun reported) =>
        ConclusionOverrides.TryGetValue(run.CorrelationId, out var forced)
            ? reported with { Conclusion = forced }
            : reported;

    private void ThrowPollFaultsLocked()
    {
        if (FailNextPollRateLimited)
        {
            FailNextPollRateLimited = false;
            throw new ExternalBuildRateLimitedException(
                "fake poll throttled", RateLimitedRetryAfter);
        }
        if (FailNextAuth)
        {
            FailNextAuth = false;
            throw new GitHubActionsAuthException("fake poll credential rejected (401)");
        }
    }

    private string FindCorrelationForArtifactLocked(GitHubActionsArtifactInfo artifact)
    {
        foreach (var run in _runs.Values)
        {
            if (artifact.ArchiveUrl.Contains(
                run.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                return run.CorrelationId;
        }
        return string.Empty;
    }

    private static string CorrelationKey(string owner, string repository, string workflowPath, string correlationId) =>
        owner + "/" + repository + "/" + workflowPath + "\0" + correlationId;

    private static GitHubActionsTestReport DefaultReport() => new(41, 0, 2, "fake-harness");

    public static byte[] BuildReportZip(GitHubActionsTestReport report, bool oversize = false)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["version"] = 1,
            ["framework"] = report.Framework,
            ["passed"] = report.Passed,
            ["failed"] = report.Failed,
            ["skipped"] = report.Skipped,
        });
        if (oversize)
            json = json[..^1] + $",\"pad\":\"{IncompressibleFiller(3 * 1024 * 1024)}\"}}";
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("codeybox-test-report.json");
            // No BOM: the schema is strict UTF-8 JSON and the parser rejects
            // anything that is not a value start.
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(json);
        }
        return stream.ToArray();
    }

    /// <summary>Deterministic incompressible filler (seeded PRNG, no live randomness).</summary>
    private static string IncompressibleFiller(int chars)
    {
        var rng = new Random(42);
        var builder = new StringBuilder(chars);
        for (var i = 0; i < chars; i++)
            builder.Append((char)('a' + rng.Next(26)));
        return builder.ToString();
    }

    public static byte[] BuildPackageZip(string artifactName)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry(artifactName + ".bin");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("fake-package-bytes:" + artifactName);
        }
        return stream.ToArray();
    }

    private sealed class FakeRun(
        long id, string owner, string repository, string workflowPath,
        string correlationId, string candidateRef, string headSha, string conclusion)
    {
        public long Id { get; } = id;
        public string Owner { get; } = owner;
        public string Repository { get; } = repository;
        public string WorkflowPath { get; } = workflowPath;
        public string CorrelationId { get; } = correlationId;
        public string CandidateRef { get; } = candidateRef;
        public string HeadSha { get; set; } = headSha;
        public string Conclusion { get; set; } = conclusion;
        public string ReportedWorkflowPath { get; set; } = workflowPath;
        public string ReportedHeadRepository { get; set; } = owner + "/" + repository;
        public bool ReportedFork { get; set; }
        public int Polls { get; set; }
        public int Attempt { get; set; } = 1;
        public bool Cancelled { get; set; }

        public bool IsTerminal(int pollsToCompleted) => Cancelled || Polls >= pollsToCompleted;

        public GitHubActionsRun ToRun(int pollsToCompleted)
        {
            string status;
            string? conclusion = null;
            if (Cancelled)
            {
                status = GitHubActionsStatuses.Completed;
                conclusion = GitHubActionsConclusions.Cancelled;
            }
            else if (Polls >= pollsToCompleted)
            {
                status = GitHubActionsStatuses.Completed;
                conclusion = Conclusion;
            }
            else
            {
                status = Polls <= 0 ? GitHubActionsStatuses.Queued : GitHubActionsStatuses.InProgress;
            }
            var branch = CandidateRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? CandidateRef["refs/heads/".Length..]
                : CandidateRef;
            return new GitHubActionsRun(
                Id, Attempt, status, conclusion, HeadSha, branch,
                ReportedFork ? "someone/" + Repository : ReportedHeadRepository,
                Owner + "/" + Repository,
                ReportedWorkflowPath, "workflow_dispatch",
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow);
        }
    }
}
