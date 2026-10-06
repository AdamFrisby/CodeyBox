using System.Runtime.CompilerServices;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.RedmineWorkSyncPlugin;

/// <summary>
/// CodeyBox work-source / work-tracker plugin for Redmine. One project, one
/// plugin: this single instance implements <see cref="IWorkSource"/> (inbound:
/// status/assignee/custom-field-signalled issues become work items) and <see
/// cref="IWorkTracker"/> (outbound: progress, questions, commit/PR links,
/// completion as issue notes plus declared status changes) against the
/// documented Redmine JSON REST API (Redmine 4.x–6.x).
/// <para>Polling only, explicitly: Redmine ships no native issue webhooks on
/// any version, so this plugin declares <c>SupportsWebhooks: false</c> and
/// <c>ParseVerifiedWebhookBody</c> throws <see
/// cref="NotSupportedException"/>. The poll is the source of truth; operator
/// question replies arrive as journal notes and are observed by <see
/// cref="ListQuestionRepliesAsync"/>.</para>
/// <para>Off unless an operator enables it: the plugin loads only when
/// allowlisted AND named in <c>Plugins:Enabled</c>, and it polls/posts nothing
/// until <c>Enabled=true</c> in its own section.</para>
/// <para>Credentials come from the host credential chain (environment
/// variables populated by the operator's vault agent or container secrets);
/// configuration holds only the variable <em>name</em>. The API key travels
/// as <c>X-Redmine-API-Key</c> (header — never a <c>?key=</c> query string)
/// and never appears in logs or error text.</para>
/// <para>State changes honor the Redmine workflow server-side: the declared
/// external status name is resolved to a status id per post via
/// <c>GET /issue_statuses.json</c>, and a transition the workflow disallows
/// surfaces as a reported <c>Failed</c> outcome — never a bypassed write. An
/// unmapped state is reported, never guessed.</para>
/// <para>Note writes are reconciled, not retried: <c>PUT
/// /issues/{id}.json</c> answers with an empty body, so a write of uncertain
/// outcome (timeout, cancellation, transport failure) re-reads the issue
/// journal for the loop-guard marker before any repetition — an uncertain
/// note is never blindly re-PUT.</para>
/// </summary>
[CodeyBoxPlugin(
    id: RedmineWorkSyncOptions.PluginId,
    displayName: "CodeyBox: Redmine Work Sync",
    minHostApiVersion: "1.0")]
public sealed class RedmineWorkSyncPlugin
    : IWorkSource, IWorkTracker, IPluginInitializer, IDisposable
{
    private readonly IHttpClientFactory? _httpFactory;
    private readonly Func<string, string?> _env;
    private readonly IConfigurationSection? _testConfig;

    private IPluginHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private HttpClient? _http;
    private RedmineTokenProvider? _tokens;
    private RedmineRestClient? _api;
    private readonly bool _ownsHttpClient;
    private readonly object _clientLock = new();
    private bool _disposed;

    /// <summary>Production constructor (DI provides the HTTP factory).</summary>
    public RedmineWorkSyncPlugin(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _env = Environment.GetEnvironmentVariable;
        _ownsHttpClient = true;
    }

    /// <summary>Test constructor: explicit client, config, and environment.</summary>
    internal RedmineWorkSyncPlugin(
        HttpClient http,
        IConfigurationSection config,
        Func<string, string?>? env = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _testConfig = config ?? throw new ArgumentNullException(nameof(config));
        _env = env ?? Environment.GetEnvironmentVariable;
        _ownsHttpClient = false;
    }

    /// <inheritdoc />
    public string Namespace => RedmineWorkSyncOptions.ProviderNamespace;

    /// <summary>
    /// Polling only: Redmine ships no native issue webhooks on any version,
    /// so webhook support is honestly declared absent rather than faked.
    /// </summary>
    public WorkSourceCapabilities Capabilities { get; } = new(SupportsWebhooks: false, SupportsPolling: true);

    /// <inheritdoc />
    public WorkTrackerCapabilities TrackerCapabilities => new(CanPostComments: true, CanSetStatus: true);

    WorkTrackerCapabilities IWorkTracker.Capabilities => TrackerCapabilities;

    /// <inheritdoc />
    public WorkSignal RequiredSignal => CurrentOptions().RequiredSignal;

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _host = context.Host;
        _logger = context.Logger ?? NullLogger.Instance;
        var options = CurrentOptions();
        if (!options.Enabled)
        {
            _logger.LogInformation("Redmine work sync is disabled (Enabled=false); polling and posting are no-ops");
            return Task.CompletedTask;
        }
        return ProbeInstanceAsync(options, ct);
    }

    /// <summary>
    /// Probes the instance for observability: counts the statuses in
    /// <c>GET /issue_statuses.json</c>. Redmine exposes no REST version
    /// endpoint, so capability is discovered per-request instead — a missing
    /// or forbidden endpoint degrades to "unknown" rather than a startup
    /// failure.
    /// </summary>
    private async Task ProbeInstanceAsync(RedmineWorkSyncOptions options, CancellationToken ct)
    {
        try
        {
            var api = EnsureClients();
            var count = await api.GetIssueStatusCountAsync(options, ct).ConfigureAwait(false);
            if (count is null)
            {
                _logger.LogWarning(
                    "Redmine instance did not report /issue_statuses.json (low-privilege key or older version); " +
                    "REST capability will be discovered per-request");
            }
            else
            {
                _logger.LogInformation(
                    "Redmine work sync enabled against {Base} ({Count} issue statuses)",
                    options.ApiBaseUrl, count);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && (ex is RedmineApiException or HttpRequestException or TaskCanceledException or InvalidOperationException))
        {
            _logger.LogWarning(ex, "Redmine instance probe failed; continuing — polling still applies");
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ExternalWorkItem> PollAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var options = CurrentOptions();
        if (!options.Enabled)
            yield break;
        if (options.ProjectMap.Count == 0)
        {
            _logger.LogWarning("Redmine poll skipped: no projects are mapped (ProjectMap is empty)");
            yield break;
        }
        var api = EnsureClients();
        var cap = Math.Max(1, options.MaxItemsPerPoll);
        var count = 0;
        foreach (var (redmineIdentifier, codeyBoxProject) in options.ProjectMap)
        {
            ct.ThrowIfCancellationRequested();
            if (count >= cap)
                yield break;
            if (string.IsNullOrWhiteSpace(redmineIdentifier) || string.IsNullOrWhiteSpace(codeyBoxProject))
                continue;
            var pages = api.SearchIssuesPagedAsync(options, redmineIdentifier.Trim(), ct);
            await foreach (var page in SkipProjectOnQueryFailure(pages, redmineIdentifier, ct).ConfigureAwait(false))
            {
                foreach (var issue in page)
                {
                    ct.ThrowIfCancellationRequested();
                    if (count >= cap)
                        yield break;
                    // The project map is keyed by identifier, but the issue
                    // carries its own project: an issue that moved projects
                    // upstream is attributed to where it is, when mapped —
                    // never to a stale filter value.
                    var project = options.ProjectMap.TryGetValue(issue.ProjectIdentifier, out var actual)
                        && !string.IsNullOrWhiteSpace(actual)
                            ? actual
                            : codeyBoxProject;
                    var candidate = ToCandidate(issue, project, options);
                    if (candidate is null)
                        continue;
                    count++;
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>
    /// Enumerates one project's pages, converting an upstream failure raised
    /// mid-enumeration into a logged skip. <see
    /// cref="RedmineRestClient.SearchIssuesPagedAsync"/> is an async
    /// iterator, so REST errors surface inside the enumeration — the guard
    /// must wrap <c>MoveNextAsync</c>, not the call that built the iterator —
    /// or one failing project would abort polling for all remaining projects.
    /// Real cancellation propagates.
    /// </summary>
    private async IAsyncEnumerable<IReadOnlyList<RedmineIssue>> SkipProjectOnQueryFailure(
        IAsyncEnumerable<IReadOnlyList<RedmineIssue>> pages,
        string redmineIdentifier,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var enumerator = pages.GetAsyncEnumerator(ct);
        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                && (ex is RedmineApiException or HttpRequestException
                    or InvalidOperationException or TaskCanceledException))
            {
                _logger.LogWarning(ex, "Redmine poll skipped project {Project}: query failed", redmineIdentifier);
                yield break;
            }
            if (!moved)
                yield break;
            yield return enumerator.Current;
        }
    }

    /// <inheritdoc />
    public ExternalWorkItem? ParseVerifiedWebhookBody(string verifiedBody) =>
        throw new NotSupportedException(
            "Redmine ships no native issue webhooks; this source is polling-only " +
            "(SupportsWebhooks: false). Poll for signalled issues instead.");

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostProgressAsync(
        TrackerProgressUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "redmine work sync is disabled");
        var check = this.CheckTracked(update.Namespace, update.ExternalId);
        if (check is not null)
            return check;

        // The caller (WorkTrackerService) resolves the declared external
        // status from the operator's state mapping and hands it in via
        // ExternalStatus; an empty status means unmapped — report it, never
        // guess (and never re-map through a second, driftable mapping).
        var status = update.ExternalStatus;
        if (string.IsNullOrEmpty(status))
            return TrackerPostResult.UnmappedFor(update.State);

        var api = EnsureClients();
        var resolved = await ResolveStatusAsync(api, options, update.ExternalId, status, ct).ConfigureAwait(false);
        if (resolved.Failure is not null)
            return resolved.Failure;

        return await PutNoteAsync(
            api, options, update.ExternalId, update.WorkItemId,
            WorkSyncText.ClipComment(update.Body, update.WorkItemId),
            resolved.StatusId, $"progress for '{update.ExternalId}'", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostQuestionAsync(
        TrackerQuestionPost post, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "redmine work sync is disabled");
        var check = this.CheckTracked(post.Namespace, post.ExternalId);
        if (check is not null)
            return check;

        var api = EnsureClients();
        var body = WorkSyncText.ClipComment(
            $"{post.Body}\n\n{RedmineNotes.QuestionTag(post.QuestionId)}", post.WorkItemId);
        return await PutNoteAsync(
            api, options, post.ExternalId, post.WorkItemId, body, null,
            $"question '{post.QuestionId}' for '{post.ExternalId}'", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostOutcomeAsync(
        TrackerOutcomeReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "redmine work sync is disabled");
        var check = this.CheckTracked(report.Namespace, report.ExternalId);
        if (check is not null)
            return check;

        // The caller (WorkTrackerService) resolves the declared external status
        // from the operator's state mapping and hands it in via ExternalStatus.
        // An empty terminal status means unmapped — report it, never guess.
        string status = report.ExternalStatus;
        if (report.IsComplete && string.IsNullOrEmpty(status))
        {
            return new TrackerPostResult(TrackerPostOutcome.UnmappedState,
                Detail: "terminal outcome has no declared external status mapping; " +
                    "add one to the source's state mapping or leave the item unsynced");
        }

        var api = EnsureClients();
        int? statusId = null;
        if (!string.IsNullOrEmpty(status))
        {
            var resolved = await ResolveStatusAsync(api, options, report.ExternalId, status, ct).ConfigureAwait(false);
            if (resolved.Failure is not null)
                return resolved.Failure;
            statusId = resolved.StatusId;
        }

        return await PutNoteAsync(
            api, options, report.ExternalId, report.WorkItemId,
            WorkSyncText.ClipComment(report.Body, report.WorkItemId),
            statusId, $"outcome for '{report.ExternalId}'", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Observes operator question replies on the poll path: reads the issue
    /// journal and extracts <c>{questionId}: …</c> replies matched by exact
    /// id against <paramref name="openQuestionIds"/>. Notes carrying the
    /// CodeyBox marker are ours and never parse as replies. Redmine offers
    /// no webhooks, so this — called by the host after polling — is how
    /// answers arrive.
    /// </summary>
    public async Task<IReadOnlyList<(string QuestionId, string Answer)>> ListQuestionRepliesAsync(
        string externalId,
        IReadOnlySet<string> openQuestionIds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(externalId);
        ArgumentNullException.ThrowIfNull(openQuestionIds);
        var options = CurrentOptions();
        if (!options.Enabled || openQuestionIds.Count == 0)
            return [];
        var api = EnsureClients();
        IReadOnlyList<RedmineJournalEntry> journals;
        try
        {
            journals = await api.GetIssueJournalsAsync(options, externalId, ct).ConfigureAwait(false);
        }
        catch (RedmineApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }
        var replies = new List<(string QuestionId, string Answer)>();
        foreach (var journal in journals)
        {
            if (string.IsNullOrWhiteSpace(journal.Notes))
                continue;
            var reply = RedmineNotes.TryExtractQuestionReply(journal.Notes, openQuestionIds);
            if (reply.HasValue)
                replies.Add(reply.Value);
        }
        return replies;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsHttpClient)
            _http?.Dispose();
    }

    internal RedmineWorkSyncOptions CurrentOptions()
    {
        var section = _host?.ScopedConfig ?? _testConfig;
        if (section is null)
            return new RedmineWorkSyncOptions();
        var warnings = new List<string>();
        var options = RedmineWorkSyncOptions.FromConfiguration(section, warnings);
        foreach (var warning in warnings)
            _logger.LogWarning("Redmine work-sync config: {Warning}", warning);
        return options;
    }

    private RedmineRestClient EnsureClients()
    {
        if (_api is not null)
            return _api;
        lock (_clientLock)
        {
            if (_api is not null)
                return _api;
            if (_http is null)
            {
                _http = _httpFactory!.CreateClient("redmine-worksync");
                // Request timeouts are enforced per attempt from the live
                // TimeoutSeconds option (a linked CTS in SendOnceAsync),
                // so edits hot-reload; disable the client-level timeout on
                // this owned client. An injected client is never mutated.
                _http.Timeout = Timeout.InfiniteTimeSpan;
            }
            _tokens = new RedmineTokenProvider(_env);
            _api = new RedmineRestClient(_http, _tokens);
            return _api;
        }
    }

    /// <summary>
    /// Resolves a declared status name to its Redmine id at post time, so a
    /// renamed status fails loudly instead of writing a guessed state. A
    /// non-null <see cref="StatusResolution.Failure"/> carries the result to
    /// report when the name is unknown upstream or the lookup itself fails.
    /// </summary>
    private async Task<StatusResolution> ResolveStatusAsync(
        RedmineRestClient api,
        RedmineWorkSyncOptions options,
        string externalId,
        string status,
        CancellationToken ct)
    {
        int? id;
        try
        {
            id = await api.ResolveStatusIdAsync(options, status, ct).ConfigureAwait(false);
        }
        catch (RedmineApiException ex)
        {
            return new StatusResolution(null, new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Redmine status lookup for '{externalId}' failed: {ex.Message}"));
        }
        if (!id.HasValue)
        {
            return new StatusResolution(null, new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Redmine has no issue status named '{status}' for '{externalId}': " +
                    "declare it in Redmine or fix the state mapping (never guessed)"));
        }
        return new StatusResolution(id.Value, null);
    }

    /// <summary>
    /// PUTs a note (and optional status change) to the issue. Never retried:
    /// Redmine answers updates with an empty body, so a write of uncertain
    /// outcome reconciles the journal for the loop-guard marker before the
    /// caller may repeat it. A note already present is <see
    /// cref="TrackerPostOutcome.SkippedDuplicate"/>, never a duplicate.
    /// </summary>
    private async Task<TrackerPostResult> PutNoteAsync(
        RedmineRestClient api,
        RedmineWorkSyncOptions options,
        string externalId,
        WorkItemId workItemId,
        string body,
        int? statusId,
        string what,
        CancellationToken ct)
    {
        var marker = WorkSyncLoopGuard.MarkerFor(workItemId);
        try
        {
            // Pre-write dedup: the exact body already in the journal means a
            // redelivered post, not new work.
            if (await JournalContainsAsync(api, options, externalId, body, ct).ConfigureAwait(false))
                return new TrackerPostResult(TrackerPostOutcome.SkippedDuplicate,
                    Detail: $"identical note already present on '{externalId}'");
            await api.UpdateIssueAsync(options, externalId, body, statusId, ct).ConfigureAwait(false);
            // PUT answers with an empty body, so confirm the note landed:
            // without this read a silent upstream drop would look posted.
            if (await JournalContainsAsync(api, options, externalId, marker, ct).ConfigureAwait(false))
                return new TrackerPostResult(TrackerPostOutcome.Posted);
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Redmine accepted the {what} but the note is not visible in the journal; " +
                    "it may have been dropped upstream — reconcile before reposting");
        }
        catch (ArgumentException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Redmine cannot post the {what}: {ex.Message}");
        }
        catch (RedmineApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Redmine issue '{externalId}' not found");
        }
        catch (RedmineApiException ex) when (ex.IsStatusRejected)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Redmine rejected the status change for '{externalId}' " +
                    "(unknown status or disallowed workflow transition): " +
                    $"{ex.Message}");
        }
        catch (RedmineApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Redmine refused the {what} for '{externalId}' (authentication/authorization): " +
                    $"{ex.Message}");
        }
        catch (RedmineApiException ex)
        {
            // Any other upstream failure after a PUT is an uncertain outcome:
            // the server may have applied the note before failing. Reconcile
            // the journal rather than reporting a failure that may be wrong.
            return await ReconcileUncertainWriteAsync(
                api, options, externalId, marker, what, ex, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Uncertain outcome: the note may have landed despite the
            // transport failure. Reconcile the journal before reporting —
            // a present marker means Posted, not Failed.
            return await ReconcileUncertainWriteAsync(
                api, options, externalId, marker, what, ex, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reconciles a note write of uncertain outcome: re-reads the journal
    /// for the loop-guard marker. Present means the write landed (Posted);
    /// absent means it did not (Failed) — and the caller must run this same
    /// check again before any repetition, never a blind re-PUT.
    /// </summary>
    private async Task<TrackerPostResult> ReconcileUncertainWriteAsync(
        RedmineRestClient api,
        RedmineWorkSyncOptions options,
        string externalId,
        string marker,
        string what,
        Exception transportFailure,
        CancellationToken ct)
    {
        bool present;
        try
        {
            present = await JournalContainsAsync(api, options, externalId, marker, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && (ex is RedmineApiException or HttpRequestException or TaskCanceledException or InvalidOperationException))
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Redmine post of the {what} has an uncertain outcome " +
                    $"({transportFailure.GetType().Name}) and the journal could not be reconciled: " +
                    $"{transportFailure.Message}. Reconcile the journal before reposting.");
        }
        if (present)
        {
            return new TrackerPostResult(TrackerPostOutcome.Posted,
                Detail: $"note reconciled in the journal after {transportFailure.GetType().Name}");
        }
        return new TrackerPostResult(TrackerPostOutcome.Failed,
            Detail: $"Redmine post of the {what} failed ({transportFailure.GetType().Name}: " +
                $"{transportFailure.Message}); the journal shows no matching note.");
    }

    /// <summary>
    /// True when any journal note on the issue contains
    /// <paramref name="needle"/> (an exact body for pre-write dedup, or the
    /// loop-guard marker for reconciliation). A missing issue reads as
    /// absent here — the caller's 404 handling owns that distinction.
    /// </summary>
    private static async Task<bool> JournalContainsAsync(
        RedmineRestClient api,
        RedmineWorkSyncOptions options,
        string externalId,
        string needle,
        CancellationToken ct)
    {
        IReadOnlyList<RedmineJournalEntry> journals;
        try
        {
            journals = await api.GetIssueJournalsAsync(options, externalId, ct).ConfigureAwait(false);
        }
        catch (RedmineApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        foreach (var journal in journals)
        {
            if (!string.IsNullOrEmpty(journal.Notes)
                && journal.Notes.Contains(needle, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private ExternalWorkItem? ToCandidate(
        RedmineIssue issue, string codeyBoxProject, RedmineWorkSyncOptions options)
    {
        var present = new List<WorkSignal>();
        if (!string.IsNullOrWhiteSpace(issue.StatusName))
            present.Add(new WorkSignal(WorkSignalKind.Status, issue.StatusName));
        if (!string.IsNullOrWhiteSpace(issue.AssigneeName))
            present.Add(new WorkSignal(WorkSignalKind.Assignee, issue.AssigneeName));
        foreach (var customValue in issue.CustomFieldValues)
        {
            if (!string.IsNullOrWhiteSpace(customValue))
                present.Add(new WorkSignal(WorkSignalKind.Label, customValue));
        }

        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = issue.ExternalId,
            ProjectId = new ProjectId(codeyBoxProject),
            Title = string.IsNullOrWhiteSpace(issue.Title) ? issue.ExternalId : issue.Title,
            Body = WorkSyncText.Truncate(issue.Description, options.MaxIngestedBodyChars),
            PresentSignals = present,
            LastActorLogin = issue.AuthorName,
            HasSignal = options.RequiredSignal.IsPresentIn(present),
        };
    }

    /// <summary>Resolved status id, or the failure result to report when resolution failed.</summary>
    private sealed record StatusResolution(int? StatusId, TrackerPostResult? Failure);
}
