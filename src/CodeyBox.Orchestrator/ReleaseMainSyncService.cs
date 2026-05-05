using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Background service that periodically merges the default branch (main) into
/// each open release branch. The merge is delegated to the project's configured
/// <see cref="IUpstreamRemote"/> so the operation is forge-agnostic.
///
/// <para>Frequency is controlled per-project by
/// <see cref="ProjectReleaseConfig.AutoSyncMainInterval"/> (default 12 h). This
/// service wakes every 5 minutes and runs releases that are due; the per-release
/// timer is tracked in-memory (resets on restart, which forces a sync).</para>
///
/// <para>On merge conflict the service emits a <c>release.sync_conflict</c>
/// webhook and logs a warning. It does <em>not</em> auto-resolve conflicts — a
/// human must fix the conflict and push to the release branch.</para>
/// </summary>
public sealed class ReleaseMainSyncService : BackgroundService
{
    private readonly IReleaseStore _releases;
    private readonly IProjectRepository _projects;
    private readonly IWebhookDispatcher _webhooks;
    private readonly IUpstreamRemoteFactory _upstreamFactory;
    private readonly ILogger<ReleaseMainSyncService> _log;

    // In-memory last-sync clock per release. Intentionally resets on service
    // restart so every open release gets a sync on the first sweep after startup.
    private readonly Dictionary<ReleaseId, DateTimeOffset> _lastSyncAt = new();

    public ReleaseMainSyncService(
        IReleaseStore releases,
        IProjectRepository projects,
        IWebhookDispatcher webhooks,
        IUpstreamRemoteFactory upstreamFactory,
        ILogger<ReleaseMainSyncService> log)
    {
        _releases = releases;
        _projects = projects;
        _webhooks = webhooks;
        _upstreamFactory = upstreamFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            await RunSweepAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunSweepForTestAsync(CancellationToken ct) => await RunSweepAsync(ct);

    private async Task RunSweepAsync(CancellationToken ct)
    {
        IReadOnlyList<Release> openReleases;
        try
        {
            openReleases = await _releases.ListAsync(state: ReleaseState.Open, ct: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ReleaseMainSync: failed to list open releases; sweep skipped");
            return;
        }

        foreach (var release in openReleases)
        {
            ct.ThrowIfCancellationRequested();

            if (release.BranchName is null) continue;

            Project? project;
            try { project = await _projects.GetAsync(release.ProjectId, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ReleaseMainSync: could not load project for release {Id}; skipping", release.Id);
                continue;
            }

            if (project is null || !project.ReleaseConfig.Enabled) continue;

            var interval = project.ReleaseConfig.AutoSyncMainInterval;
            if (interval is null) continue; // auto-sync disabled for this project

            if (_lastSyncAt.TryGetValue(release.Id, out var lastSync) &&
                DateTimeOffset.UtcNow - lastSync < interval.Value)
                continue;

            await SyncReleaseAsync(release, project, ct);
        }
    }

    private async Task SyncReleaseAsync(Release release, Project project, CancellationToken ct)
    {
        var mainBranch = project.DefaultBaseBranch ?? "main";
        _log.LogInformation(
            "ReleaseMainSync: merging '{Main}' into release branch '{Branch}' for release {Id}",
            mainBranch, release.BranchName, release.Id);

        try
        {
            var upstream = _upstreamFactory.Create(project);
            var merged = await upstream.TryMergeUpstreamBranchAsync(release.BranchName!, mainBranch, ct);

            // Always update last-sync time so the service doesn't retry every 5 min on conflict.
            _lastSyncAt[release.Id] = DateTimeOffset.UtcNow;

            if (merged)
            {
                _log.LogInformation(
                    "ReleaseMainSync: merged '{Main}' → '{Branch}' for release {Id}",
                    mainBranch, release.BranchName, release.Id);
            }
            else
            {
                _log.LogWarning(
                    "ReleaseMainSync: merge conflict merging '{Main}' → '{Branch}' for release {Id}; human intervention required",
                    mainBranch, release.BranchName, release.Id);

                await _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "release.sync_conflict",
                    Release = release,
                    Project = project,
                    Details = new { sourceBranch = mainBranch, targetBranch = release.BranchName },
                }, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ReleaseMainSync: sync failed for release {Id}; will retry next sweep", release.Id);
            // Do not update _lastSyncAt so the next sweep retries.
        }
    }
}
