using CodeyBox.Core;

namespace CodeyBox.Orchestrator.WorkSync;

/// <summary>
/// Drives an <see cref="IWorkTracker"/>: posts progress on meaningful state
/// transitions, surfaces open <see cref="WorkItemQuestion"/>s upstream and
/// accepts replies as answers, reports resulting commits / pull requests and
/// terminal completion or failure. Every attempt — post, skip, or failure —
/// is recorded in the <see cref="IWorkSyncRecordStore"/> audit trail.
/// <para>An upstream failure never fails a work item: sync problems are
/// recorded and returned, and the work stands on its own.</para>
/// </summary>
public sealed class WorkTrackerService
{
    private const int MaxOutcomeChars = 4000;
    private const int MaxAnswerChars = 4000;

    private readonly IWorkTracker _tracker;
    private readonly IWorkSyncRecordStore _records;
    private readonly IWorkItemQuestionStore? _questions;
    private readonly Func<WorkSyncOptions> _options;
    private readonly Func<WorkStateMapping> _mapping;
    private readonly Func<WorkItemId, CancellationToken, Task<WorkItem?>>? _workItemLookup;

    /// <param name="tracker">Provider outbound implementation.</param>
    /// <param name="records">Cross-system audit trail.</param>
    /// <param name="questions">Question store for surfacing/answering. Null disables question sync.</param>
    /// <param name="options">Hot-reloadable knobs; read per call. Defaults to disabled.</param>
    /// <param name="mapping">Operator-declared state mapping; read per call so reloads apply.</param>
    /// <param name="workItemLookup">Resolves items for answer-record attribution. Null falls back to the tracker's namespace.</param>
    public WorkTrackerService(
        IWorkTracker tracker,
        IWorkSyncRecordStore records,
        IWorkItemQuestionStore? questions = null,
        Func<WorkSyncOptions>? options = null,
        Func<WorkStateMapping>? mapping = null,
        Func<WorkItemId, CancellationToken, Task<WorkItem?>>? workItemLookup = null)
    {
        _tracker = tracker;
        _records = records;
        _questions = questions;
        _options = options ?? (() => new WorkSyncOptions());
        _mapping = mapping ?? (() => WorkStateMapping.Empty);
        _workItemLookup = workItemLookup;
    }

    /// <summary>
    /// Reports the item's current state. Posts only when the declared
    /// external status changed since the last successful post — a progress
    /// comment must not trigger ingestion or another update, and chatter
    /// invites exactly that. A state with no mapping is recorded as unmapped
    /// and never guessed.
    /// </summary>
    public async Task<TrackerPostResult> ReportStateAsync(
        WorkItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_options().Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "work sync is disabled");

        if (!TryExternalId(item, out var externalId))
            return new TrackerPostResult(TrackerPostOutcome.NotTracked, Detail: $"no external id under '{_tracker.Namespace}'");

        var map = _mapping();
        if (!map.TryMap(item.State, out var status) || string.IsNullOrEmpty(status))
        {
            var unmapped = new UnmappedWorkItemState(item.State);
            await _records.RecordAsync(new WorkSyncRecord
            {
                WorkItemId = item.Id,
                Namespace = _tracker.Namespace,
                ExternalId = externalId,
                Kind = WorkSyncRecordKind.UnmappedState,
                Succeeded = true,
                Detail = unmapped.Describe(),
            }, ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.UnmappedState, Detail: unmapped.Describe());
        }

        var last = await _records.LastPostedStatusAsync(item.Id, _tracker.Namespace, ct).ConfigureAwait(false);
        if (string.Equals(last, status, StringComparison.Ordinal))
            return new TrackerPostResult(TrackerPostOutcome.SkippedDuplicate, Detail: $"status unchanged ({status})");

        if (!_tracker.Capabilities.CanPostComments && !_tracker.Capabilities.CanSetStatus)
        {
            await RecordAsync(item.Id, externalId, WorkSyncRecordKind.SyncFailed, status, null, false,
                "tracker declares no usable capability (cannot post comments or set status)", ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Unsupported, Detail: "tracker cannot post comments or set status");
        }

        var body = WorkSyncLoopGuard.Mark(BuildProgressBody(item, status), item.Id);
        TrackerPostResult result;
        try
        {
            result = await _tracker.PostProgressAsync(new TrackerProgressUpdate
            {
                WorkItemId = item.Id,
                Namespace = _tracker.Namespace,
                ExternalId = externalId,
                State = item.State,
                ExternalStatus = status,
                Body = body,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await FailedAsync(item.Id, externalId, status,
                $"progress post threw {ex.GetType().Name}: {ex.Message}", ct).ConfigureAwait(false);
        }

        if (result.Outcome == TrackerPostOutcome.Posted)
            await RecordAsync(item.Id, externalId, WorkSyncRecordKind.Progress, status, result.RemoteId, true, null, ct).ConfigureAwait(false);
        else if (result.Outcome == TrackerPostOutcome.Failed)
            await RecordAsync(item.Id, externalId, WorkSyncRecordKind.SyncFailed, status, null, false, result.Detail, ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Surfaces open questions as upstream comments. A tracker that cannot
    /// post comments reports <see cref="TrackerPostOutcome.Unsupported"/> —
    /// it never silently drops a question.
    /// </summary>
    public async Task<IReadOnlyList<TrackerPostResult>> SyncQuestionsAsync(
        WorkItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_questions is null || !_options().Enabled)
            return [];
        if (!TryExternalId(item, out var externalId))
            return [new TrackerPostResult(TrackerPostOutcome.NotTracked, Detail: $"no external id under '{_tracker.Namespace}'")];

        var open = (await _questions.ListByWorkItemAsync(item.Id.ToString(), ct).ConfigureAwait(false))
            .Where(q => string.Equals(q.State, "open", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (open.Count == 0)
            return [];

        var posted = await PostedQuestionIdsAsync(item.Id, ct).ConfigureAwait(false);
        var results = new List<TrackerPostResult>();
        foreach (var question in open)
        {
            if (posted.Contains(question.QuestionId))
            {
                results.Add(new TrackerPostResult(TrackerPostOutcome.SkippedDuplicate, Detail: question.QuestionId));
                continue;
            }

            if (!_tracker.Capabilities.CanPostComments)
            {
                var unsupported = new TrackerPostResult(TrackerPostOutcome.Unsupported,
                    Detail: $"tracker cannot post comments; question '{question.QuestionId}' not surfaced");
                await RecordAsync(item.Id, externalId, WorkSyncRecordKind.Question, null, null, false,
                    unsupported.Detail, ct).ConfigureAwait(false);
                results.Add(unsupported);
                continue;
            }

            var body = WorkSyncLoopGuard.Mark(BuildQuestionBody(question), item.Id);
            TrackerPostResult post;
            try
            {
                post = await _tracker.PostQuestionAsync(new TrackerQuestionPost
                {
                    WorkItemId = item.Id,
                    Namespace = _tracker.Namespace,
                    ExternalId = externalId,
                    QuestionId = question.QuestionId,
                    Body = body,
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                post = new TrackerPostResult(TrackerPostOutcome.Failed,
                    Detail: $"question post threw {ex.GetType().Name}: {ex.Message}");
            }

            await RecordAsync(item.Id, externalId, WorkSyncRecordKind.Question, null, post.RemoteId,
                post.Outcome == TrackerPostOutcome.Posted, post.Outcome == TrackerPostOutcome.Posted ? question.QuestionId : post.Detail, ct)
                .ConfigureAwait(false);
            results.Add(post);
        }

        return results;
    }

    /// <summary>
    /// Accepts an upstream reply as the answer to a surfaced question.
    /// Providers call this when they observe a reply to a question comment.
    /// Returns false when the question is unknown or already resolved.
    /// </summary>
    public async Task<bool> AcceptExternalAnswerAsync(
        WorkItemId workItemId,
        string questionId,
        string answer,
        string? answeredBy,
        CancellationToken ct = default)
    {
        if (_questions is null)
            return false;
        if (string.IsNullOrWhiteSpace(questionId) || string.IsNullOrWhiteSpace(answer))
            throw new ArgumentException("questionId and answer must not be empty");

        var question = await _questions.GetAsync(workItemId.ToString(), questionId, ct).ConfigureAwait(false);
        if (question is null
            || !string.Equals(question.State, "open", StringComparison.OrdinalIgnoreCase))
            return false;

        var clipped = answer.Trim();
        if (clipped.Length > MaxAnswerChars)
            clipped = clipped[..MaxAnswerChars];

        await _questions.AnswerAsync(workItemId.ToString(), questionId, clipped, answeredBy, ct).ConfigureAwait(false);

        var (ns, eid) = await ResolveAttributionAsync(workItemId, ct).ConfigureAwait(false);
        await _records.RecordAsync(new WorkSyncRecord
        {
            WorkItemId = workItemId,
            Namespace = ns,
            ExternalId = eid,
            Kind = WorkSyncRecordKind.Answer,
            Succeeded = true,
            Detail = questionId,
        }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Reports resulting commit SHAs and pull request links (interim), or
    /// terminal completion/failure. Completion requires a declared mapping
    /// for the item's state — an unmapped terminal state is reported as
    /// unmapped, never guessed. Upstream failure leaves the item unaffected.
    /// </summary>
    public async Task<TrackerPostResult> ReportOutcomeAsync(
        WorkItem item,
        bool succeeded,
        string summary,
        IReadOnlyList<string>? commitShas = null,
        string? pullRequestUrl = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_options().Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "work sync is disabled");
        if (!TryExternalId(item, out var externalId))
            return new TrackerPostResult(TrackerPostOutcome.NotTracked, Detail: $"no external id under '{_tracker.Namespace}'");

        var isComplete = WorkItemStates.IsTerminal(item.State);
        string status = string.Empty;
        if (isComplete)
        {
            if (!_mapping().TryMap(item.State, out var mapped) || string.IsNullOrEmpty(mapped))
            {
                var unmapped = new UnmappedWorkItemState(item.State);
                await RecordAsync(item.Id, externalId, WorkSyncRecordKind.UnmappedState, null, null, true,
                    unmapped.Describe(), ct).ConfigureAwait(false);
                return new TrackerPostResult(TrackerPostOutcome.UnmappedState, Detail: unmapped.Describe());
            }

            status = mapped;
        }
        else if (_mapping().TryMap(item.State, out var interim) && !string.IsNullOrEmpty(interim))
        {
            status = interim;
        }

        if (!_tracker.Capabilities.CanPostComments)
            return new TrackerPostResult(TrackerPostOutcome.Unsupported, Detail: "tracker cannot post comments");

        var body = WorkSyncLoopGuard.Mark(
            BuildOutcomeBody(item, succeeded, summary, commitShas ?? [], pullRequestUrl), item.Id);
        TrackerPostResult result;
        try
        {
            result = await _tracker.PostOutcomeAsync(new TrackerOutcomeReport
            {
                WorkItemId = item.Id,
                Namespace = _tracker.Namespace,
                ExternalId = externalId,
                IsComplete = isComplete,
                Succeeded = succeeded,
                ExternalStatus = status,
                Body = body,
                CommitShas = commitShas ?? [],
                PullRequestUrl = pullRequestUrl,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await FailedAsync(item.Id, externalId, status,
                $"outcome post threw {ex.GetType().Name}: {ex.Message}", ct).ConfigureAwait(false);
        }

        var kind = isComplete ? WorkSyncRecordKind.Completion : WorkSyncRecordKind.Links;
        if (result.Outcome == TrackerPostOutcome.Posted)
            await RecordAsync(item.Id, externalId, kind, status, result.RemoteId, true, null, ct).ConfigureAwait(false);
        else if (result.Outcome == TrackerPostOutcome.Failed)
            await RecordAsync(item.Id, externalId, WorkSyncRecordKind.SyncFailed, status, null, false, result.Detail, ct).ConfigureAwait(false);

        return result;
    }

    private bool TryExternalId(WorkItem item, out string externalId)
    {
        if (item.ExternalIds.TryGetValue(_tracker.Namespace, out var found) && !string.IsNullOrEmpty(found))
        {
            externalId = found;
            return true;
        }

        externalId = string.Empty;
        return false;
    }

    private async Task<HashSet<string>> PostedQuestionIdsAsync(WorkItemId id, CancellationToken ct)
    {
        var records = await _records.ListByWorkItemAsync(id, ct).ConfigureAwait(false);
        return new HashSet<string>(records
            .Where(r => r.Kind == WorkSyncRecordKind.Question && r.Succeeded && r.Detail is not null
                && string.Equals(r.Namespace, _tracker.Namespace, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Detail!),
            StringComparer.Ordinal);
    }

    private async Task<TrackerPostResult> FailedAsync(
        WorkItemId id, string externalId, string? status, string detail, CancellationToken ct)
    {
        await RecordAsync(id, externalId, WorkSyncRecordKind.SyncFailed, status, null, false, detail, ct).ConfigureAwait(false);
        return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: detail);
    }

    private Task RecordAsync(
        WorkItemId id, string externalId, WorkSyncRecordKind kind, string? status,
        string? remoteId, bool succeeded, string? detail, CancellationToken ct) =>
        _records.RecordAsync(new WorkSyncRecord
        {
            WorkItemId = id,
            Namespace = _tracker.Namespace,
            ExternalId = externalId,
            Kind = kind,
            ExternalStatus = status,
            RemoteId = remoteId,
            Succeeded = succeeded,
            Detail = detail,
        }, ct);

    /// <summary>
    /// Resolves the namespace/external-id pair for an answer record from the
    /// work item store when a lookup is provided; otherwise attributes to the
    /// tracker's namespace.
    /// </summary>
    private async Task<(string Namespace, string ExternalId)> ResolveAttributionAsync(
        WorkItemId workItemId, CancellationToken ct)
    {
        if (_workItemLookup is not null)
        {
            var item = await _workItemLookup(workItemId, ct).ConfigureAwait(false);
            if (item is not null && item.ExternalIds.TryGetValue(_tracker.Namespace, out var eid))
                return (_tracker.Namespace, eid);
        }

        return (_tracker.Namespace, string.Empty);
    }

    internal static string BuildProgressBody(WorkItem item, string status) =>
        $"CodeyBox update: '{item.Title}' is now {item.State} (external status: {status}).";

    internal static string BuildQuestionBody(WorkItemQuestion question) =>
        $"CodeyBox question [{question.QuestionId}]: {question.QuestionText}";

    internal static string BuildOutcomeBody(
        WorkItem item,
        bool succeeded,
        string summary,
        IReadOnlyList<string> commitShas,
        string? pullRequestUrl)
    {
        // Summary originates from agent output: truncate, never echo stack traces.
        var clipped = Truncate(summary.Trim(), MaxOutcomeChars);
        var verdict = succeeded ? "completed successfully" : "failed";
        var links = new List<string>();
        links.AddRange(commitShas.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => $"commit {s.Trim()}"));
        if (!string.IsNullOrWhiteSpace(pullRequestUrl))
            links.Add($"pull request {pullRequestUrl.Trim()}");
        var linkLine = links.Count == 0 ? string.Empty : $"\nLinks: {string.Join(", ", links)}";
        var errorLine = !succeeded && !string.IsNullOrWhiteSpace(item.LastError)
            ? $"\nDetail: {Truncate(item.LastError.Trim(), MaxOutcomeChars)}"
            : string.Empty;
        return $"CodeyBox update: '{item.Title}' {verdict}.\n{clipped}{linkLine}{errorLine}";
    }

    internal static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];
}
