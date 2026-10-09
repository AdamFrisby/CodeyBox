using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// One-off startup reconciliation for stranded upstream pushes: shortly after
/// boot, scans for work items parked by the owned-branch lease guard (or
/// otherwise settled with an open PR on their own branch) and surfaces each
/// candidate with the operator endpoint that re-drives it. Detection only —
/// nothing is pushed, rewritten, or re-dispatched here; the operator re-drive
/// carries the authorization the lease guard requires.
/// </summary>
public sealed class StaleUpstreamRedriveDetector : BackgroundService
{
    private readonly UpstreamRedriveService _redrive;
    private readonly ILogger<StaleUpstreamRedriveDetector> _log;
    private readonly TimeProvider _time;

    public StaleUpstreamRedriveDetector(
        UpstreamRedriveService redrive,
        ILogger<StaleUpstreamRedriveDetector> log,
        TimeProvider? time = null)
    {
        _redrive = redrive;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), _time, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        IReadOnlyList<UpstreamRedriveService.UpstreamRedriveCandidate> candidates;
        try
        {
            candidates = await _redrive.ListCandidatesAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Startup upstream re-drive scan failed; on-demand endpoint remains available");
            return;
        }

        if (candidates.Count == 0)
        {
            _log.LogInformation("Startup upstream re-drive scan: no stranded items with open PRs on owned branches");
            return;
        }

        foreach (var candidate in candidates)
        {
            _log.LogWarning(
                "Stranded upstream push: work item {Id} ('{Title}') is {State} with open PR #{Pr} ({Url}) on owned branch '{Branch}' (PR head {HeadSha}, last pushed {LastPushed}). " +
                "Re-drive with POST /workitems/{Id}/redrive-upstream after confirming no third party pushed to the branch; " +
                "pass {{\"confirmOwnership\": true}} when the branch has no recorded push or PR. Reason: {Reason}",
                candidate.WorkItemId, candidate.Title, candidate.State,
                candidate.PullRequestNumber?.ToString() ?? "(none)", candidate.PullRequestUrl ?? "(none)", candidate.WorkBranch,
                candidate.PullRequestHeadSha ?? "(none)", candidate.LastPushedWorkBranchSha ?? "(unrecorded)",
                candidate.WorkItemId, candidate.Reason);
        }
    }
}
