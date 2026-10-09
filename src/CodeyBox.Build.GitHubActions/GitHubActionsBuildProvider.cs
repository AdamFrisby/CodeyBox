using System.Globalization;
using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Build.GitHubActions;

/// <summary>
/// GitHub Actions execution/evidence adapter behind the neutral
/// <see cref="IExternalBuildProvider"/> contract (CBX-NEXT-103). Core stays
/// language/ecosystem-neutral: every GitHub or toolchain-specific notion
/// (workflow dispatch, run attempts, job conclusions, report zips) lives
/// here. The framework keeps owning sandbox tools, park/resume, durable
/// ownership/completion, budgets, cancellation racing, and artifact
/// security — this adapter never re-implements them.
///
/// Dispatch/adopt semantics: the host publishes the frozen snapshot as a
/// scoped temporary candidate branch; the adapter dispatches (or adopts)
/// the approved workflow for that exact ref and pins the expected checkout
/// sha. Verification compares the run's actual <c>head_sha</c> with exact
/// equality — a matching mutable ref, a success badge, or the latest run is
/// never proof. No caller-supplied inputs are forwarded to workflows; only
/// correlation and sha pins travel as dispatch inputs.
/// </summary>
public sealed class GitHubActionsBuildProvider : IExternalBuildProvider
{
    public const string HeadShaParameter = "github.head_sha";
    public const string MergeShaParameter = "github.merge_sha";

    private readonly IGitHubActionsTransport _transport;
    private readonly IGitHubActionsBindingStore _bindings;
    private readonly Func<GitHubActionsExternalBuildOptions> _options;
    private readonly Func<ExternalBuildOptions> _frameworkOptions;
    private readonly TimeProvider _clock;

    public GitHubActionsBuildProvider(
        IGitHubActionsTransport transport,
        IGitHubActionsBindingStore bindings,
        Func<GitHubActionsExternalBuildOptions> options,
        Func<ExternalBuildOptions> frameworkOptions,
        TimeProvider? clock = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _frameworkOptions = frameworkOptions ?? throw new ArgumentNullException(nameof(frameworkOptions));
        _clock = clock ?? TimeProvider.System;
    }

    public string ProviderId => GitHubActionsExternalBuildOptions.ProviderId;
    public bool SupportsGitPublication => true;
    public bool SupportsSnapshotUpload => false;

    public async Task<ExternalBuildSubmitResult> SubmitAsync(
        ExternalBuildRecord intent, ExternalBuildSubmitInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(input);
        var opts = _options();
        if (!opts.Enabled)
            throw new ExternalBuildNotEnabledException();
        if (!opts.ApprovedWorkflows.TryGetValue(intent.Target.TargetId, out var approval))
            throw new ExternalBuildTargetNotApprovedException(
                intent.Target.TargetId + " (no approved GitHub workflow)");

        var candidateRef = input.CandidateRef ?? intent.Source.CandidateRef;
        if (string.IsNullOrWhiteSpace(candidateRef))
            throw new ExternalBuildInvalidRequestException(
                "GitHub Actions requires a published candidate ref; snapshot upload is not supported.");
        var refError = GitHubActionsCandidateRefPolicy.ValidateForDispatch(candidateRef, opts.CandidateRefPrefix);
        if (refError is not null)
            throw new ExternalBuildInvalidRequestException(refError);

        var prior = await _bindings.GetByRequestAsync(intent.RequestId, ct).ConfigureAwait(false);
        var expectedHead = input.Parameters.TryGetValue(HeadShaParameter, out var headParam)
            ? headParam : prior?.ExpectedHeadSha;
        if (!GitHubActionsCandidateRefPolicy.IsPlausibleSha(expectedHead))
            throw new ExternalBuildInvalidRequestException(
                $"Dispatch input '{HeadShaParameter}' must be the 40-hex commit sha of the published candidate; "
                + "without it the adapter cannot verify the workflow checked out the requested candidate.");
        var expectedMerge = input.Parameters.TryGetValue(MergeShaParameter, out var mergeParam)
            ? mergeParam : prior?.ExpectedMergeSha;
        if (expectedMerge is not null && !GitHubActionsCandidateRefPolicy.IsPlausibleSha(expectedMerge))
            throw new ExternalBuildInvalidRequestException(
                $"Dispatch input '{MergeShaParameter}' must be a 40-hex commit sha when supplied.");

        var now = _clock.GetUtcNow();
        var binding = new GitHubActionsBinding(
            intent.Id, intent.RequestId, intent.Target.TargetId, intent.Target.TargetId,
            approval.Owner, approval.Repository, approval.WorkflowPath,
            candidateRef, expectedHead!, expectedMerge,
            intent.Source.SourceDigestSha256, prior?.RunId, prior?.Attempt ?? 1,
            prior?.TerminalOutcome, prior?.TerminalEvidence, now);
        await _bindings.SaveAsync(binding, ct).ConfigureAwait(false);

        var dispatchRef = GitHubActionsCandidateRefPolicy.DeriveDispatchRef(candidateRef);
        var request = new GitHubActionsDispatchRequest(
            approval.Owner, approval.Repository, approval.WorkflowPath, dispatchRef,
            intent.RequestId, candidateRef, expectedHead!, expectedMerge,
            new Dictionary<string, string>(StringComparer.Ordinal));
        // Transport rate/auth/transient faults propagate: the framework keeps
        // the durable uncertain intent and reconciles by correlation before
        // any retry, so no duplicate paid run follows. Cancellation propagates.
        var outcome = await _transport.DispatchAsync(request, ct).ConfigureAwait(false);
        if (outcome.RunId is not null
            && (prior is null || !string.Equals(prior.RunId, outcome.RunId, StringComparison.Ordinal)))
        {
            // A fresh provider run resets attempt tracking; a redelivered
            // duplicate of the known run keeps it (obsolete-attempt guard).
            binding = binding with
            {
                RunId = outcome.RunId,
                Attempt = 1,
                TerminalOutcome = null,
                TerminalEvidence = null,
                UpdatedAt = _clock.GetUtcNow(),
            };
            await _bindings.SaveAsync(binding, ct).ConfigureAwait(false);
        }
        else if (outcome.RunId is not null)
        {
            binding = binding with { RunId = outcome.RunId, UpdatedAt = _clock.GetUtcNow() };
            await _bindings.SaveAsync(binding, ct).ConfigureAwait(false);
        }
        if (outcome.Accepted && !string.IsNullOrWhiteSpace(outcome.RunId))
            return new ExternalBuildSubmitResult(true, outcome.RunId, null);
        return new ExternalBuildSubmitResult(false, null, outcome.Error ?? "dispatch accepted without a run id", Uncertain: true);
    }

    public async Task<ExternalBuildProviderStatus> GetStatusAsync(string providerRunId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRunId);
        if (providerRunId.StartsWith("request:", StringComparison.Ordinal))
            return await ProbeCorrelationAsync(providerRunId["request:".Length..], ct).ConfigureAwait(false);

        if (!long.TryParse(providerRunId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var runId))
            throw new ArgumentException("Provider run id must be numeric or 'request:'.", nameof(providerRunId));
        var binding = await _bindings.GetByRunAsync(providerRunId, ct).ConfigureAwait(false);
        if (binding is null)
            return Unknown("no adapter binding for this run; refusing to vouch without pinned checkout truth");
        var opts = _options();
        if (!opts.ApprovedWorkflows.TryGetValue(binding.TargetId, out var approval))
            return TerminalFailed("workflow approval for this target was withdrawn; failing closed");
        GitHubActionsRun? run;
        try
        {
            run = await _transport.GetRunAsync(binding.Owner, binding.Repository, runId, ct).ConfigureAwait(false);
        }
        catch (GitHubActionsAuthException ex)
        {
            throw new GitHubActionsAuthException(
                $"poll of run {providerRunId} is blocked on credentials; refusing silent pass ({ex.Message})", ex);
        }
        if (run is null)
            return Unknown($"run {providerRunId} not found on the provider");

        var substitution = GitHubActionsEvidenceMapper.CheckSubstitution(run, binding, approval);
        if (substitution is not null)
            return TerminalFailed(substitution);

        if (run.RunAttempt != binding.Attempt)
        {
            if (run.RunAttempt > binding.Attempt)
            {
                binding = binding with
                {
                    Attempt = run.RunAttempt,
                    TerminalOutcome = null,
                    TerminalEvidence = null,
                    UpdatedAt = _clock.GetUtcNow(),
                };
                await _bindings.SaveAsync(binding, ct).ConfigureAwait(false);
            }
            else if (binding.TerminalOutcome == GitHubActionsRunOutcome.Succeeded && binding.TerminalEvidence is not null)
            {
                return Succeeded(binding.TerminalEvidence, "replaying cached evidence; obsolete attempt ignored");
            }
            else
            {
                return Active(run, "obsolete run attempt observed; awaiting the latest attempt");
            }
        }
        else if (binding.TerminalOutcome is not null)
        {
            return binding.TerminalOutcome switch
            {
                GitHubActionsRunOutcome.Succeeded when binding.TerminalEvidence is not null =>
                    Succeeded(binding.TerminalEvidence, "replaying cached evidence for a duplicate poll"),
                GitHubActionsRunOutcome.Cancelled =>
                    CancelledStatus("provider confirms cancellation"),
                _ => TerminalFailed("cached terminal failure; duplicate poll replays the same outcome"),
            };
        }

        if (!run.IsTerminalStatus)
            return Active(run, null);

        if (run.Conclusion is not null
            && string.Equals(run.Conclusion, GitHubActionsConclusions.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            binding = binding with
            {
                TerminalOutcome = GitHubActionsRunOutcome.Cancelled,
                UpdatedAt = _clock.GetUtcNow(),
            };
            await _bindings.SaveAsync(binding, ct).ConfigureAwait(false);
            return CancelledStatus("provider confirms cancellation");
        }

        return await MapTerminalAsync(binding, approval, run, ct).ConfigureAwait(false);
    }

    public async Task<ExternalBuildCancelResult> CancelAsync(string providerRunId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRunId);
        if (providerRunId.StartsWith("request:", StringComparison.Ordinal))
        {
            var correlated = await CorrelateAsync(providerRunId["request:".Length..], ct).ConfigureAwait(false);
            if (correlated is null)
                return new ExternalBuildCancelResult(false, "no provider run correlated yet; nothing to cancel remotely");
            providerRunId = correlated.RunId;
        }
        if (!long.TryParse(providerRunId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var runId))
            throw new ArgumentException("Provider run id must be numeric or 'request:'.", nameof(providerRunId));
        var binding = await _bindings.GetByRunAsync(providerRunId, ct).ConfigureAwait(false);
        if (binding is null)
            return new ExternalBuildCancelResult(false, "no adapter binding; refusing to address an arbitrary run");
        var run = await _transport.GetRunAsync(binding.Owner, binding.Repository, runId, ct).ConfigureAwait(false);
        if (run is null)
            return new ExternalBuildCancelResult(false, "unknown run");
        if (run.IsTerminalStatus)
        {
            if (run.Conclusion is not null
                && string.Equals(run.Conclusion, GitHubActionsConclusions.Success, StringComparison.OrdinalIgnoreCase))
                return new ExternalBuildCancelResult(false, "run already completed successfully; late completion wins over cancel");
            if (run.Conclusion is not null
                && string.Equals(run.Conclusion, GitHubActionsConclusions.Cancelled, StringComparison.OrdinalIgnoreCase))
                return new ExternalBuildCancelResult(true, "run already cancelled");
            return new ExternalBuildCancelResult(false, $"run already terminal ({run.Conclusion ?? "unknown"})");
        }
        await _transport.CancelRunAsync(binding.Owner, binding.Repository, runId, ct).ConfigureAwait(false);
        var after = await _transport.GetRunAsync(binding.Owner, binding.Repository, runId, ct).ConfigureAwait(false);
        if (after is not null && after.IsTerminalStatus
            && after.Conclusion is not null
            && string.Equals(after.Conclusion, GitHubActionsConclusions.Cancelled, StringComparison.OrdinalIgnoreCase))
            return new ExternalBuildCancelResult(true, "cancel confirmed by the provider");
        if (after is not null && after.IsTerminalStatus)
            return new ExternalBuildCancelResult(false, "run completed before cancel landed; late completion wins");
        return new ExternalBuildCancelResult(false, "cancel requested; provider still reports the run active");
    }

    public async Task<IReadOnlyList<ExternalBuildArtifactRef>> ListArtifactsAsync(
        string providerRunId, CancellationToken ct)
    {
        if (!long.TryParse(providerRunId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var runId))
            return [];
        var binding = await _bindings.GetByRunAsync(providerRunId, ct).ConfigureAwait(false);
        if (binding is null)
            return [];
        var framework = _frameworkOptions();
        var infos = await _transport.ListArtifactsAsync(binding.Owner, binding.Repository, runId, ct).ConfigureAwait(false);
        if (infos.Count > framework.MaxArtifactsPerBuild)
            throw new InvalidOperationException(
                $"Provider listed {infos.Count} artifacts, exceeding the {framework.MaxArtifactsPerBuild} cap; refusing unbounded ingestion.");
        var refs = new List<ExternalBuildArtifactRef>();
        foreach (var info in infos)
        {
            if (info.Expired || info.SizeBytes > framework.MaxArtifactBytes)
                continue;
            var bytes = await _transport.DownloadArtifactAsync(info, framework.MaxArtifactBytes, ct).ConfigureAwait(false);
            var digest = ExternalBuildProvenance.DigestBytes(bytes);
            var candidate = new ExternalBuildArtifactRef(info.Name, bytes.Length, digest, MediaTypeFor(info.Name));
            if (ExternalBuildArtifactGuard.ValidateRef(candidate, framework) is null)
                refs.Add(candidate);
        }
        return refs;
    }

    public async Task<ExternalBuildArtifactPayload> ReadArtifactAsync(
        string providerRunId, string artifactName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifactName)
            || artifactName.Length > ExternalBuildArtifactGuard.MaxArtifactNameChars)
            throw new ArgumentException("Artifact name missing or too long.", nameof(artifactName));
        var normalized = artifactName.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Artifact name escapes its directory.", nameof(artifactName));
        if (!long.TryParse(providerRunId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var runId))
            throw new InvalidOperationException("Build has no provider run yet.");
        var binding = await _bindings.GetByRunAsync(providerRunId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No adapter binding for this run.");
        var framework = _frameworkOptions();
        var infos = await _transport.ListArtifactsAsync(binding.Owner, binding.Repository, runId, ct).ConfigureAwait(false);
        GitHubActionsArtifactInfo? listed = null;
        foreach (var info in infos)
        {
            if (string.Equals(info.Name, artifactName, StringComparison.Ordinal))
            {
                listed = info;
                break;
            }
        }
        if (listed is null)
            throw new InvalidOperationException($"Artifact '{artifactName}' is not listed for this run.");
        if (listed.Expired)
            throw new InvalidOperationException($"Artifact '{artifactName}' expired; expired artifacts never count as pass.");
        if (listed.SizeBytes > framework.MaxArtifactBytes)
            throw new InvalidOperationException(
                $"Artifact '{artifactName}' reports {listed.SizeBytes} bytes, exceeding the {framework.MaxArtifactBytes}-byte cap; refusing to buffer.");
        var bytes = await _transport.DownloadArtifactAsync(listed, framework.MaxArtifactBytes, ct).ConfigureAwait(false);
        var payload = new ExternalBuildArtifactPayload(
            artifactName, bytes, MediaTypeFor(artifactName), ExternalBuildProvenance.DigestBytes(bytes));
        var error = ExternalBuildArtifactGuard.ValidatePayload(payload, framework);
        if (error is not null)
            throw new InvalidOperationException("Artifact rejected: " + error);
        return payload;
    }

    private async Task<ExternalBuildProviderStatus> ProbeCorrelationAsync(string correlationId, CancellationToken ct)
    {
        var correlated = await CorrelateAsync(correlationId, ct).ConfigureAwait(false);
        if (correlated is null)
            return Unknown("dispatch accepted but no run is discoverable yet");
        // The framework treats a non-unknown probe on an unadopted intent as
        // adoption-required; the host adopts the exact discovered run id.
        return new ExternalBuildProviderStatus(
            ExternalBuildExecutionPhase.Queued, null,
            $"run {correlated.RunId} discovered by correlation; adopt this exact run id, never the latest run",
            _clock.GetUtcNow());
    }

    private sealed record CorrelatedRun(string RunId, int Attempt);

    private async Task<CorrelatedRun?> CorrelateAsync(string correlationId, CancellationToken ct)
    {
        var binding = await _bindings.GetByRequestAsync(correlationId, ct).ConfigureAwait(false);
        if (binding is null)
            return null;
        var opts = _options();
        if (!opts.ApprovedWorkflows.TryGetValue(binding.TargetId, out var approval))
            return null;
        var branch = GitHubActionsCandidateRefPolicy.DeriveDispatchRef(binding.CandidateRef);
        var run = await _transport.FindRunByCorrelationAsync(
            binding.Owner, binding.Repository, binding.WorkflowPath, correlationId,
            string.IsNullOrEmpty(branch) ? null : branch,
            binding.UpdatedAt.AddMinutes(-5), ct).ConfigureAwait(false);
        if (run is null)
            return null;
        var runId = run.Id.ToString(CultureInfo.InvariantCulture);
        binding = binding with
        {
            RunId = runId,
            Attempt = run.RunAttempt,
            UpdatedAt = _clock.GetUtcNow(),
        };
        await _bindings.SaveAsync(binding, ct).ConfigureAwait(false);
        return new CorrelatedRun(runId, run.RunAttempt);
    }

    private async Task<ExternalBuildProviderStatus> MapTerminalAsync(
        GitHubActionsBinding binding,
        GitHubActionsWorkflowApproval approval,
        GitHubActionsRun run,
        CancellationToken ct)
    {
        var framework = _frameworkOptions();
        var adapterOpts = _options();
        var now = _clock.GetUtcNow();
        var jobs = await _transport.ListJobsAsync(binding.Owner, binding.Repository, run.Id, ct).ConfigureAwait(false);

        GitHubActionsTestReport? report = null;
        if (approval.RequireTestReport)
        {
            var reportError = await TryLoadReportAsync(binding, approval, run.Id, framework, adapterOpts.MaxReportBytes, ct)
                .ConfigureAwait(false);
            if (reportError.Error is not null)
                return TerminalFailed(reportError.Error);
            report = reportError.Report;
        }

        var packages = new Dictionary<string, string>(StringComparer.Ordinal);
        var packageNotes = new List<string>();
        var artifacts = await _transport.ListArtifactsAsync(binding.Owner, binding.Repository, run.Id, ct).ConfigureAwait(false);
        foreach (var artifact in artifacts)
        {
            if (!IsPackageArtifact(artifact.Name, approval))
                continue;
            if (artifact.Expired)
            {
                packageNotes.Add($"'{artifact.Name}' expired");
                continue;
            }
            if (artifact.SizeBytes > framework.MaxArtifactBytes)
            {
                packageNotes.Add($"'{artifact.Name}' exceeds the ingestion cap");
                continue;
            }
            try
            {
                var bytes = await _transport.DownloadArtifactAsync(artifact, framework.MaxArtifactBytes, ct).ConfigureAwait(false);
                packages[artifact.Name] = ExternalBuildProvenance.DigestBytes(bytes);
            }
            catch (GitHubActionsEvidenceUnavailableException ex)
            {
                packageNotes.Add($"'{artifact.Name}' unavailable: {ex.Reason}");
            }
        }

        GitHubActionsMappedEvidence mapped;
        try
        {
            mapped = GitHubActionsEvidenceMapper.MapCompletedRun(run, jobs, binding, approval, report, packages, now);
        }
        catch (GitHubActionsEvidenceUnavailableException ex)
        {
            return TerminalFailed(Bound($"insufficient evidence: {ex.Reason}", framework));
        }
        if (mapped.Outcome == GitHubActionsRunOutcome.Succeeded && mapped.Evidence is not null)
        {
            var saved = binding with
            {
                TerminalOutcome = GitHubActionsRunOutcome.Succeeded,
                TerminalEvidence = mapped.Evidence,
                UpdatedAt = now,
            };
            await _bindings.SaveAsync(saved, ct).ConfigureAwait(false);
            return Succeeded(mapped.Evidence, Bound(mapped.Detail, framework));
        }
        if (mapped.Outcome == GitHubActionsRunOutcome.Cancelled)
            return CancelledStatus(Bound(mapped.Detail, framework));
        var note = packageNotes.Count > 0 ? " package notes: " + string.Join("; ", packageNotes) : string.Empty;
        return TerminalFailed(Bound(mapped.Detail + note + "; " + SummarizeJobs(jobs), framework));
    }

    private async Task<(GitHubActionsTestReport? Report, string? Error)> TryLoadReportAsync(
        GitHubActionsBinding binding,
        GitHubActionsWorkflowApproval approval,
        long runId,
        ExternalBuildOptions framework,
        long maxReportBytes,
        CancellationToken ct)
    {
        var artifacts = await _transport.ListArtifactsAsync(binding.Owner, binding.Repository, runId, ct).ConfigureAwait(false);
        GitHubActionsArtifactInfo? reportArtifact = null;
        foreach (var artifact in artifacts)
        {
            if (string.Equals(artifact.Name, approval.TestReportArtifactName, StringComparison.Ordinal))
            {
                reportArtifact = artifact;
                break;
            }
        }
        if (reportArtifact is null)
            return (null, Bound(
                "test report missing: workflow success does not imply tests ran; explicit approved report evidence is required",
                framework));
        if (reportArtifact.Expired)
            return (null, Bound(
                $"test report artifact '{reportArtifact.Name}' expired; expired artifacts never count as pass",
                framework));
        byte[] zip;
        try
        {
            zip = await _transport.DownloadArtifactAsync(reportArtifact, framework.MaxArtifactBytes, ct).ConfigureAwait(false);
        }
        catch (GitHubActionsEvidenceUnavailableException ex)
        {
            return (null, Bound($"test report unavailable: {ex.Reason}; build success does not imply tests ran", framework));
        }
        try
        {
            var json = GitHubActionsEvidenceMapper.ExtractReportFile(
                zip, approval.TestReportArtifact, framework, maxReportBytes);
            return (GitHubActionsEvidenceMapper.ParseReport(json, maxReportBytes), null);
        }
        catch (GitHubActionsEvidenceUnavailableException ex)
        {
            return (null, Bound($"test report rejected: {ex.Reason}", framework));
        }
    }

    private static bool IsPackageArtifact(string name, GitHubActionsWorkflowApproval approval)
    {
        foreach (var prefix in approval.PackageArtifactPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string MediaTypeFor(string name) =>
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "application/zip"
        : name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json"
        : "application/octet-stream";

    private string SummarizeJobs(IReadOnlyList<GitHubActionsJob> jobs)
    {
        var parts = new List<string>();
        foreach (var job in jobs)
            parts.Add($"{job.Name}={job.Conclusion ?? job.Status}");
        return "jobs: " + string.Join(", ", parts);
    }

    private string Bound(string detail, ExternalBuildOptions framework) =>
        ExternalBuildArtifactGuard.TruncateBounded(
            ExternalBuildArtifactGuard.Redact(detail), framework.MaxDiagnosticsChars);

    private ExternalBuildProviderStatus Unknown(string detail) =>
        new(ExternalBuildExecutionPhase.Unknown, null, detail, _clock.GetUtcNow());

    private ExternalBuildProviderStatus Active(GitHubActionsRun run, string? detail) =>
        new(run.Status == GitHubActionsStatuses.Queued
            ? ExternalBuildExecutionPhase.Queued : ExternalBuildExecutionPhase.Running,
            null, detail, _clock.GetUtcNow());

    private ExternalBuildProviderStatus TerminalFailed(string detail) =>
        new(ExternalBuildExecutionPhase.Failed, null, detail, _clock.GetUtcNow());

    private ExternalBuildProviderStatus Succeeded(ExternalBuildEvidence evidence, string detail) =>
        new(ExternalBuildExecutionPhase.Succeeded, evidence, detail, _clock.GetUtcNow());

    private ExternalBuildProviderStatus CancelledStatus(string detail) =>
        new(ExternalBuildExecutionPhase.Cancelled, null, detail, _clock.GetUtcNow());
}
