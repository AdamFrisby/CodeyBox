using System;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Default <see cref="IJobTrackTestCaseExporter"/>. For a work item on a project
/// that opted into JobTrack export, lists the item's test cases, maps each onto
/// the JobTrack import contract, and upserts them via <see cref="IJobTrackTestCaseClient"/>.
///
/// <para><b>Best-effort.</b> A per-case failure is retried (bounded, backed off)
/// and then counted, never rethrown — a propagation failure must not fail the
/// owning work item. Only cancellation propagates. <b>Idempotent:</b> JobTrack
/// upserts on the case id, so re-export updates in place instead of duplicating.</para>
///
/// <para>The env reader is injected so token resolution is deterministic in tests
/// without touching the process environment; production wires the ambient reader.</para>
/// </summary>
public sealed class JobTrackTestCaseExporter : IJobTrackTestCaseExporter
{
    private readonly ITestCaseStore _store;
    private readonly IJobTrackTestCaseClient _client;
    private readonly ILogger<JobTrackTestCaseExporter> _log;
    private readonly Func<string, string?> _environment;

    public JobTrackTestCaseExporter(
        ITestCaseStore store,
        IJobTrackTestCaseClient client,
        ILogger<JobTrackTestCaseExporter>? log = null,
        Func<string, string?>? environment = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _log = log ?? NullLogger<JobTrackTestCaseExporter>.Instance;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public async Task<JobTrackExportSummary> ExportForWorkItemAsync(
        WorkItem item, Project project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(project);

        var cfg = project.JobTrackExport;
        if (!cfg.Enabled)
            return JobTrackExportSummary.Skipped(JobTrackExportStatus.Disabled);

        if (!item.ExternalIds.TryGetValue(cfg.ExternalIdNamespace, out var sourceTaskId)
            || string.IsNullOrWhiteSpace(sourceTaskId))
        {
            return JobTrackExportSummary.Skipped(
                JobTrackExportStatus.NoJobTrackId,
                $"work item carries no '{cfg.ExternalIdNamespace}' external id");
        }

        if (!JobTrackExportEndpointResolver.TryResolve(cfg, _environment, out var endpoint, out var error))
        {
            _log.LogWarning(
                "JobTrack export skipped for work item {WorkItemId}: {Error}", item.Id, error);
            return JobTrackExportSummary.Skipped(JobTrackExportStatus.Misconfigured, error);
        }

        var exported = 0;
        var failed = 0;
        await foreach (var testCase in _store.ListByWorkItemAsync(item.Id.ToString(), ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            JobTrackTestCaseImport import;
            try
            {
                import = JobTrackTestCaseMapper.ToImport(testCase, sourceTaskId, cfg.DefaultSurfaceArea);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(
                    ex, "JobTrack export skipped test case {TestCaseId} for work item {WorkItemId}: mapping failed",
                    testCase.Id, item.Id);
                failed++;
                continue;
            }

            if (await PushWithRetryAsync(endpoint!, import, cfg, item.Id, ct).ConfigureAwait(false))
                exported++;
            else
                failed++;
        }

        _log.LogInformation(
            "JobTrack export for work item {WorkItemId} (task {SourceTaskId}): {Exported} exported, {Failed} failed",
            item.Id, sourceTaskId, exported, failed);

        return new JobTrackExportSummary
        {
            Status = JobTrackExportStatus.Completed,
            Exported = exported,
            Failed = failed,
        };
    }

    /// <summary>
    /// Upserts one case with a bounded, backed-off retry. Returns true on success,
    /// false once <see cref="ProjectJobTrackExport.MaxAttempts"/> is exhausted.
    /// Cancellation propagates; every other exception is retried then swallowed.
    /// </summary>
    private async Task<bool> PushWithRetryAsync(
        JobTrackExportEndpoint endpoint,
        JobTrackTestCaseImport import,
        ProjectJobTrackExport cfg,
        WorkItemId workItemId,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, cfg.MaxAttempts);
        Exception? last = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _client.UpsertAsync(endpoint, import, ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < maxAttempts)
                {
                    var delay = cfg.RetryBaseDelay * attempt;
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }

        _log.LogWarning(
            last,
            "JobTrack export failed for test case {TestCaseId} (work item {WorkItemId}) after {Attempts} attempt(s); item unaffected",
            import.ExternalSourceId, workItemId, maxAttempts);
        return false;
    }
}
