using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// Single-pass GitHub audit check-run publisher: create-or-reconcile by
/// <c>external_id</c>, then bounded annotation batches with ambiguous-write
/// reconciliation.
///
/// Reconciliation rules (GitHub <c>external_id</c> is correlation, not an
/// idempotency key):
/// <list type="bullet">
/// <item>Before creating, list check runs for the exact SHA and reuse the
/// newest run whose <c>external_id</c> matches — a lost create response,
/// restart, or concurrent delivery converges instead of duplicating.</item>
/// <item>After an ambiguous create failure (timeout/transport, where the write
/// may have landed), list again: adopt on match, otherwise rethrow for
/// bounded service-level retry.</item>
/// <item>Annotation batches are treated as appends. After an ambiguous batch
/// failure, re-read the run's annotations and send only fingerprints not yet
/// present. When the re-read itself fails, stop and return
/// <c>BatchUncertain=true</c> with a visibly disclosing summary rather than
/// blindly duplicating batches.</item>
/// </list>
/// </summary>
public sealed class GitHubAuditCheckPublisher
{
    private readonly GitHubCheckRunsClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public GitHubAuditCheckPublisher(
        GitHubCheckRunsClient client,
        TimeProvider? time = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    public async Task<AuditCheckPublicationResult> PublishAsync(
        AuditCheckPublicationRequest request,
        AuditCheckPublicationOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _client.ValidateRepositoryMatch(request);
        AuditCheckPayloadBuilder.ValidateHeadSha(request.HeadSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CheckName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExternalId);
        if (request.ExternalId.Length > 255)
            throw new AuditCheckValidationException("ExternalId exceeds the 255-character forge limit.");
        ValidateDetailsUrl(request.DetailsUrl);

        var (annotations, omitted) = AuditCheckPayloadBuilder.BuildAnnotations(request.Findings, options);
        var (batches, overflow) = AuditCheckPayloadBuilder.ChunkBatches(annotations, options);
        var totalOmitted = omitted.Count + overflow;

        var status = AuditCheckConclusionMapper.ToStatusString(request.Lifecycle);
        var conclusion = AuditCheckConclusionMapper.ToConclusionString(
            request.Lifecycle, request.Verdict, request.UnavailabilityReason);
        var now = _time.GetUtcNow();

        // Reconcile-before-create: a previous pass may have created the run
        // and lost the response. external_id is our correlation handle.
        var existing = await FindByExternalIdAsync(request, ct).ConfigureAwait(false);
        GitHubCheckRun run;
        var published = 0;
        var batchesSent = 0;
        var uncertain = false;

        if (existing is not null)
        {
            run = existing;
            _log.LogInformation(
                "Reusing existing check run {CheckRunId} for external_id {ExternalId} instead of creating a duplicate",
                run.Id, request.ExternalId);
        }
        else
        {
            var firstBatch = batches.Count > 0 ? ToRequestBatch(batches[0]) : null;
            var summary = AuditCheckPayloadBuilder.BuildSummary(request, omitted, overflow, false, options);
            var createBody = new GitHubCreateCheckRunRequest(
                request.CheckName,
                request.HeadSha,
                status,
                request.ExternalId,
                new GitHubCheckRunOutput(AuditCheckPayloadBuilder.BuildTitle(request), summary, firstBatch),
                conclusion,
                now.ToString("O"),
                request.Lifecycle == AuditCheckLifecycle.Completed ? now.ToString("O") : null,
                request.DetailsUrl);
            try
            {
                run = await _client.CreateCheckRunAsync(createBody, ct).ConfigureAwait(false);
            }
            catch (AuditCheckRateLimitedException)
            {
                // A 429/rate-limit response proves the write did NOT land, but a
                // concurrent delivery may still have created the run — one
                // bounded reconcile read before surfacing the retry with its
                // original Retry-After hint intact.
                var reconciled = await ReconcileAfterAmbiguousCreateAsync(request, ct).ConfigureAwait(false);
                if (reconciled is not null)
                    run = reconciled;
                else
                    throw;
            }
            catch (AuditCheckTransientException)
            {
                var reconciled = await ReconcileAfterAmbiguousCreateAsync(request, ct).ConfigureAwait(false);
                if (reconciled is not null)
                    run = reconciled;
                else
                    throw;
            }
            if (firstBatch is not null)
            {
                published += batches[0].Count;
                batchesSent++;
            }
        }

        // Remaining batches, appended one forge call at a time.
        for (var i = batchesSent; i < batches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var batch = batches[i];
            try
            {
                var summary = AuditCheckPayloadBuilder.BuildSummary(request, omitted, overflow, uncertain, options);
                await _client.UpdateCheckRunAsync(run.Id, new GitHubUpdateCheckRunRequest(
                    Output: new GitHubCheckRunOutput(
                        AuditCheckPayloadBuilder.BuildTitle(request), summary, ToRequestBatch(batch)),
                    DetailsUrl: request.DetailsUrl), ct).ConfigureAwait(false);
                published += batch.Count;
                batchesSent++;
            }
            catch (AuditCheckRateLimitedException)
            {
                // Rate-limit responses prove the batch did not land: propagate
                // for bounded service-level backoff without guessing.
                throw;
            }
            catch (AuditCheckTransientException) when (!ct.IsCancellationRequested)
            {
                // Ambiguous: the batch may have landed. Re-read and send only
                // what is missing; surface uncertainty when the re-read fails.
                var reconciled = await TryReconcileBatchAsync(run.Id, batches, i, published, ct).ConfigureAwait(false);
                if (reconciled is null)
                {
                    uncertain = true;
                    break;
                }
                published = reconciled.Published;
                batchesSent = reconciled.BatchesSent;
                i = reconciled.NextIndex - 1;
            }
        }

        if (uncertain)
        {
            // One best-effort disclosure update (no annotations, so nothing can
            // duplicate). Its own failure is swallowed: the result flag is the
            // durable signal, and the service persists it.
            try
            {
                var summary = AuditCheckPayloadBuilder.BuildSummary(request, omitted, overflow, true, options);
                await _client.UpdateCheckRunAsync(run.Id, new GitHubUpdateCheckRunRequest(
                    Output: new GitHubCheckRunOutput(AuditCheckPayloadBuilder.BuildTitle(request), summary),
                    DetailsUrl: request.DetailsUrl), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuditCheckPublicationException or OperationCanceledException)
            {
                _ = ex;
            }
        }

        return new AuditCheckPublicationResult
        {
            CheckRunId = run.Id,
            HtmlUrl = run.HtmlUrl,
            Status = status,
            Conclusion = conclusion,
            AnnotationsPublished = published,
            AnnotationsOmitted = totalOmitted,
            BatchesSent = batchesSent,
            BatchUncertain = uncertain,
        };
    }

    private async Task<GitHubCheckRun?> FindByExternalIdAsync(
        AuditCheckPublicationRequest request,
        CancellationToken ct)
    {
        var runs = await _client.ListCheckRunsForRefAsync(request.HeadSha, request.CheckName, ct)
            .ConfigureAwait(false);
        GitHubCheckRun? newest = null;
        foreach (var run in runs)
        {
            if (!string.Equals(run.ExternalId, request.ExternalId, StringComparison.Ordinal))
                continue;
            if (newest is null || run.Id > newest.Id)
                newest = run;
        }
        return newest;
    }

    private async Task<GitHubCheckRun?> ReconcileAfterAmbiguousCreateAsync(
        AuditCheckPublicationRequest request,
        CancellationToken ct)
    {
        try
        {
            return await FindByExternalIdAsync(request, ct).ConfigureAwait(false);
        }
        catch (AuditCheckPublicationException)
        {
            return null;
        }
    }

    private sealed record BatchReconcile(int Published, int BatchesSent, int NextIndex);

    private async Task<BatchReconcile?> TryReconcileBatchAsync(
        long checkRunId,
        IReadOnlyList<IReadOnlyList<AuditCheckAnnotation>> batches,
        int failedIndex,
        int publishedSoFar,
        CancellationToken ct)
    {
        IReadOnlyList<GitHubCheckAnnotationItem> present;
        try
        {
            present = await _client.ListAnnotationsAsync(checkRunId, ct).ConfigureAwait(false);
        }
        catch (AuditCheckPublicationException)
        {
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var presentPrints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in present)
            presentPrints.Add(Fingerprint(item.Path, item.StartLine, item.EndLine, item.Title));

        var published = publishedSoFar;
        var batchesSent = failedIndex;
        for (var i = failedIndex; i < batches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var missing = batches[i]
                .Where(a => !presentPrints.Contains(Fingerprint(a.Path, a.StartLine, a.EndLine, a.Title)))
                .ToList();
            if (missing.Count == 0)
            {
                // Proven present on the forge by the re-read: safe to count
                // without resending.
                published += batches[i].Count;
                batchesSent++;
                continue;
            }
            try
            {
                await _client.UpdateCheckRunAsync(checkRunId, new GitHubUpdateCheckRunRequest(
                    Output: new GitHubCheckRunOutput(
                        Title: $"CodeyBox audit annotations (reconciled batch {i + 1})",
                        Summary: "Retrying only annotations not already present on the check run.",
                        Annotations: ToRequestBatch(missing))), ct).ConfigureAwait(false);
                published += missing.Count;
                batchesSent++;
                foreach (var a in missing)
                    presentPrints.Add(Fingerprint(a.Path, a.StartLine, a.EndLine, a.Title));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is AuditCheckPublicationException or OperationCanceledException)
            {
                // A second failure while reconciling stays uncertain: never
                // advance past an unsent batch as if it had landed. The result
                // flag plus the disclosure update carry the gap visibly.
                return null;
            }
        }
        return new BatchReconcile(published, batchesSent, batches.Count);
    }

    private static string Fingerprint(string? path, int start, int end, string? title) =>
        $"{path ?? string.Empty}\n{start}\n{end}\n{title ?? string.Empty}";

    private static IReadOnlyList<GitHubCheckAnnotationRequest> ToRequestBatch(
        IReadOnlyList<AuditCheckAnnotation> batch)
    {
        var items = new List<GitHubCheckAnnotationRequest>(batch.Count);
        foreach (var a in batch)
        {
            if (a.StartLine <= 0 || a.EndLine < a.StartLine)
                throw new AuditCheckValidationException(
                    $"Annotation for '{a.Path}' has an invalid line range; refusing to send it.");
            items.Add(new GitHubCheckAnnotationRequest(a.Path, a.StartLine, a.EndLine, a.Level, a.Title, a.Message));
        }
        return items;
    }

    private static void ValidateDetailsUrl(string? detailsUrl)
    {
        if (string.IsNullOrWhiteSpace(detailsUrl))
            return;
        if (!Uri.TryCreate(detailsUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new AuditCheckValidationException(
                "DetailsUrl must be an absolute http(s) URL so the complete report stays reachable " +
                "from the forge; relative links would silently strand readers.");
    }
}
