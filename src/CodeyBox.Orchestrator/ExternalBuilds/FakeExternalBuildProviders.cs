using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Orchestrator.ExternalBuilds;

/// <summary>
/// Deterministic fake providers used by tests and documentation to represent
/// the two follow-on consumer shapes — a Git-based build service and an
/// artifact-snapshot build service — through the REAL production
/// <see cref="IExternalBuildProvider"/> path (no mock-call assertions).
/// Never enabled in running configuration; never contacts real providers.
/// </summary>
public abstract class FakeExternalBuildProviderBase : IExternalBuildProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FakeRun> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _requestRuns = new(StringComparer.Ordinal);
    private int _sequence;
    private int _submitCalls;

    protected FakeExternalBuildProviderBase(string providerId, bool git, bool snapshot)
    {
        ProviderId = providerId;
        SupportsGitPublication = git;
        SupportsSnapshotUpload = snapshot;
    }

    public string ProviderId { get; }
    public bool SupportsGitPublication { get; }
    public bool SupportsSnapshotUpload { get; }

    /// <summary>When true, the next submit reports uncertain (timeout) without accepting.</summary>
    public bool FailNextSubmitUncertain { get; set; }

    /// <summary>When true, submits throw a transient error (retryable).</summary>
    public bool FailNextSubmitTransient { get; set; }

    public int SubmitCalls { get { lock (_gate) return _submitCalls; } }

    /// <summary>Scripted terminal evidence per run; defaults to passing evidence.</summary>
    public Func<ExternalBuildRecord, ExternalBuildEvidence>? EvidenceFactory { get; set; }

    /// <summary>Polls before the run reports terminal evidence. Default 2.</summary>
    public int PollsToTerminal { get; set; } = 2;

    public Task<ExternalBuildSubmitResult> SubmitAsync(
        ExternalBuildRecord intent, ExternalBuildSubmitInput input, CancellationToken ct)
    {
        lock (_gate)
        {
            _submitCalls++;
            if (!string.IsNullOrWhiteSpace(intent.RequestId) && _requestRuns.TryGetValue(intent.RequestId, out var dupRun))
                return Task.FromResult(new ExternalBuildSubmitResult(true, dupRun, null));
            if (FailNextSubmitTransient)
            {
                FailNextSubmitTransient = false;
                throw new InvalidOperationException("fake transient provider fault");
            }
            if (FailNextSubmitUncertain)
            {
                FailNextSubmitUncertain = false;
                return Task.FromResult(new ExternalBuildSubmitResult(false, null, "timeout before acceptance", Uncertain: true));
            }
            if (SupportsGitPublication && input.CandidateRef is null && input.Snapshot is not null && !SupportsSnapshotUpload)
                return Task.FromResult(new ExternalBuildSubmitResult(false, null, "snapshot not supported by git-only provider"));
            var runId = $"{ProviderId}-run-{++_sequence}";
            _runs[runId] = new FakeRun(intent.Source.SourceDigestSha256, intent.Id, PollsToTerminal);
            if (!string.IsNullOrWhiteSpace(intent.RequestId))
                _requestRuns[intent.RequestId] = runId;
            return Task.FromResult(new ExternalBuildSubmitResult(true, runId, null));
        }
    }

    public Task<ExternalBuildProviderStatus> GetStatusAsync(string providerRunId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(providerRunId, out var run))
                return Task.FromResult(new ExternalBuildProviderStatus(
                    ExternalBuildExecutionPhase.Unknown, null, "unknown run", DateTimeOffset.UtcNow));
            if (run.Cancelled)
                return Task.FromResult(new ExternalBuildProviderStatus(
                    ExternalBuildExecutionPhase.Cancelled, null, "cancelled", DateTimeOffset.UtcNow));
            run.Polls++;
            if (run.Polls < run.PollsToTerminal)
                return Task.FromResult(new ExternalBuildProviderStatus(
                    run.Polls <= 1 ? ExternalBuildExecutionPhase.Queued : ExternalBuildExecutionPhase.Running,
                    null, null, DateTimeOffset.UtcNow));
            var evidence = EvidenceFactory is not null && run.Intent is not null
                ? EvidenceFactory(run.Intent)
                : PassingEvidence(providerRunId, run.SourceDigest);
            return Task.FromResult(new ExternalBuildProviderStatus(
                ExternalBuildExecutionPhase.Succeeded, evidence, null, DateTimeOffset.UtcNow));
        }
    }

    public Task<ExternalBuildCancelResult> CancelAsync(string providerRunId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(providerRunId, out var run))
                return Task.FromResult(new ExternalBuildCancelResult(false, "unknown run"));
            if (run.TerminalDelivered)
                return Task.FromResult(new ExternalBuildCancelResult(false, "already terminal"));
            run.Cancelled = true;
            return Task.FromResult(new ExternalBuildCancelResult(true, "cancel confirmed"));
        }
    }

    public Task<IReadOnlyList<ExternalBuildArtifactRef>> ListArtifactsAsync(string providerRunId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(providerRunId, out _))
                return Task.FromResult<IReadOnlyList<ExternalBuildArtifactRef>>([]);
            var digest = Core.ExternalBuilds.ExternalBuildProvenance.DigestText(providerRunId + ":pkg");
            return Task.FromResult<IReadOnlyList<ExternalBuildArtifactRef>>(
                [new ExternalBuildArtifactRef("package.zip", 128, digest, "application/zip")]);
        }
    }

    public Task<ExternalBuildArtifactPayload> ReadArtifactAsync(string providerRunId, string artifactName, CancellationToken ct)
    {
        if (!string.Equals(artifactName, "package.zip", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unknown artifact '{artifactName}'.");
        var content = System.Text.Encoding.UTF8.GetBytes("fake-package:" + providerRunId);
        return Task.FromResult(new ExternalBuildArtifactPayload(
            artifactName, content, "application/zip",
            Core.ExternalBuilds.ExternalBuildProvenance.DigestBytes(content)));
    }

    public void MarkTerminalDelivered(string providerRunId)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(providerRunId, out var run)) run.TerminalDelivered = true;
        }
    }

    public bool WasCancelled(string providerRunId)
    {
        lock (_gate) return _runs.TryGetValue(providerRunId, out var run) && run.Cancelled;
    }

    private static ExternalBuildEvidence PassingEvidence(string runId, string sourceDigest) => new()
    {
        Compile = ExternalBuildDimensionOutcome.Passed,
        Tests = ExternalBuildDimensionOutcome.Passed,
        Package = ExternalBuildDimensionOutcome.Passed,
        SourceDigestSha256 = sourceDigest,
        ProviderRunId = runId,
        WorkflowIdentity = "fake-workflow@pinned",
        ApprovedTargetName = "fake-target",
        Toolchain = "fake-toolchain-1",
        Platform = "fake-platform-1",
        Configuration = "release",
        ArtifactDigests = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["package.zip"] = Core.ExternalBuilds.ExternalBuildProvenance.DigestText(runId + ":pkg"),
        },
        Authoritative = true,
        CapturedAt = DateTimeOffset.UtcNow,
    };

    private sealed class FakeRun(string sourceDigest, string buildId, int pollsToTerminal)
    {
        public string SourceDigest { get; } = sourceDigest;
        public string BuildId { get; } = buildId;
        public int PollsToTerminal { get; } = pollsToTerminal;
        public int Polls { get; set; }
        public bool Cancelled { get; set; }
        public bool TerminalDelivered { get; set; }
        public ExternalBuildRecord? Intent { get; set; }
    }

    internal void AttachIntent(string providerRunId, ExternalBuildRecord intent)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(providerRunId, out var run)) run.Intent = intent;
        }
    }
}

/// <summary>Fake Git-based build service: requires a candidate ref.</summary>
public sealed class FakeGitBuildProvider : FakeExternalBuildProviderBase
{
    public FakeGitBuildProvider() : base("fake-git", git: true, snapshot: false) { }
}

/// <summary>Fake artifact-snapshot build service: accepts uploaded snapshots.</summary>
public sealed class FakeSnapshotBuildProvider : FakeExternalBuildProviderBase
{
    public FakeSnapshotBuildProvider() : base("fake-snapshot", git: false, snapshot: true) { }
}
