using CodeyBox.Audit;
using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Fail-closed backstop for parked human deployment reviews. Periodically
/// scans for undecided reviews past their deadline and, for each: tears the
/// held deployment down (bounded by the recipe's max lifetime — a forgotten
/// review must not hold a VM), marks the review expired, dismisses the
/// backing question as "expired unreviewed", and re-queues the item so the
/// audit loop resumes and records the blocking expiry finding through the
/// normal rework path. Silence never becomes an implicit pass.
///
/// <para>All dependencies are optional: unwired compositions no-op. Per-item
/// failures are caught and logged so one poisoned review cannot kill the
/// sweep. Reviews that already carry a verdict are never touched — the
/// resume path owns them.</para>
/// </summary>
public sealed class HumanDeploymentReviewSweeper : BackgroundService
{
    private readonly IHumanDeploymentReviewStore? _reviews;
    private readonly IDeploymentManager? _deployments;
    private readonly IWorkItemStore? _store;
    private readonly IWorkItemQuestionStore? _questions;
    private readonly ITaskQueue? _queue;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly Func<HumanDeploymentReviewOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<HumanDeploymentReviewSweeper> _log;

    public HumanDeploymentReviewSweeper(
        IHumanDeploymentReviewStore? reviews = null,
        IDeploymentManager? deployments = null,
        IWorkItemStore? store = null,
        IWorkItemQuestionStore? questions = null,
        ITaskQueue? queue = null,
        IWebhookDispatcher? webhooks = null,
        Func<HumanDeploymentReviewOptions>? options = null,
        TimeProvider? clock = null,
        ILogger<HumanDeploymentReviewSweeper>? log = null)
    {
        _reviews = reviews;
        _deployments = deployments;
        _store = store;
        _questions = questions;
        _queue = queue;
        _webhooks = webhooks;
        _options = options ?? (() => new HumanDeploymentReviewOptions());
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<HumanDeploymentReviewSweeper>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = _options().SweepInterval;
            if (interval <= TimeSpan.Zero)
                return;
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Expires every undecided review past its deadline. Deterministic entry
    /// point for tests (the background loop above just calls it on a timer).
    /// </summary>
    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        if (_reviews is null)
            return 0;
        var now = _clock.GetUtcNow();
        var expired = await _reviews.ListExpiredPendingAsync(now, ct).ConfigureAwait(false);
        var count = 0;
        foreach (var review in expired)
        {
            try
            {
                if (await ExpireAsync(review, now, ct).ConfigureAwait(false))
                    count++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(
                    ex,
                    "Human-review sweeper failed to expire review for work item {WorkItemId} iteration {Iteration}",
                    review.WorkItemId, review.Iteration);
            }
        }

        return count;
    }

    private async Task<bool> ExpireAsync(HumanDeploymentReview review, DateTimeOffset now, CancellationToken ct)
    {
        if (_reviews is null)
            return false;
        try
        {
            return await HumanReviewExpiry.ExpireAsync(
                _reviews,
                _deployments,
                _store,
                _questions,
                _queue,
                _webhooks,
                review,
                now,
                _log,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                ex,
                "Human-review sweeper failed to expire review for work item {WorkItemId} iteration {Iteration}",
                review.WorkItemId, review.Iteration);
            return false;
        }
    }
}

/// <summary>Structured payload for the resume webhook after a review expiry.</summary>
public sealed record HumanReviewExpiredDetails(
    string WorkItemId,
    int Iteration,
    string DeploymentId,
    DateTimeOffset ExpiredAt);
