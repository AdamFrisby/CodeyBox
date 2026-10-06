using System.Runtime.CompilerServices;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.AsanaWorkSyncPlugin;

/// <summary>
/// CodeyBox work-source / work-tracker plugin for Asana. One project, one
/// plugin: this single instance implements <see cref="IWorkSource"/> (inbound:
/// assignment/tag-signalled tasks become work items) and <see
/// cref="IWorkTracker"/> (outbound: progress, questions, commit/PR links,
/// completion) against the Asana REST API <c>/api/1.0</c>.
/// <para>Off unless an operator enables it: the plugin loads only when
/// allowlisted AND named in <c>Plugins:Enabled</c>, and it polls/posts nothing
/// until <c>Enabled=true</c> in its own section.</para>
/// <para>Credentials come from the host credential chain (environment
/// variables populated by the operator's vault agent or container secrets);
/// configuration holds only the variable <em>name</em>. Personal access
/// tokens and OAuth access tokens both travel as
/// <c>Authorization: Bearer</c>. Asana offers no client-credentials grant,
/// so there is no in-process refresh — rotation happens in the host
/// environment (<see cref="AsanaTokenProvider"/>).</para>
/// <para>Asana's distinctive surface is GIDs plus stories: identity is the
/// immutable numeric task <c>gid</c>, polls select only the fields ingestion
/// needs via <c>opt_fields</c> and follow opaque <c>next_page.offset</c>
/// tokens, and every outbound write lands as a story (comment) — completion
/// and custom-field writes happen only for caller-resolved statuses the
/// operator explicitly mapped. This plugin is polling-only: it never creates
/// webhooks, manages membership, or imports a whole workspace — only tasks
/// in explicitly mapped projects carrying the configured signal converge
/// onto work items, keyed by GID.</para>
/// </summary>
[CodeyBoxPlugin(
    id: AsanaWorkSyncOptions.PluginId,
    displayName: "CodeyBox: Asana Work Sync",
    minHostApiVersion: "1.0")]
public sealed class AsanaWorkSyncPlugin
    : IWorkSource, IWorkTracker, IPluginInitializer, IDisposable
{
    private readonly TimeProvider _clock;
    private readonly Func<string, string?> _env;
    private readonly IConfigurationSection? _testConfig;

    private IPluginHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private HttpClient? _http;
    private AsanaTokenProvider? _tokens;
    private AsanaRestClient? _api;
    private readonly bool _ownsHttpClient;
    private readonly object _clientLock = new();
    private bool _disposed;

    /// <summary>
    /// Construction-time timeout for the owned no-redirect client. Never
    /// enforced: <see cref="EnsureClients"/> immediately overrides it with
    /// <see cref="Timeout.InfiniteTimeSpan"/> because per-attempt timeouts
    /// come from the live <c>TimeoutSeconds</c> option. Exists only because
    /// <see cref="CredentialHttp.CreateNoRedirectClient(TimeSpan)"/>
    /// requires a finite value.
    /// </summary>
    private static readonly TimeSpan OwnedClientConstructionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Production constructor. The factory parameter is accepted for host DI
    /// compatibility (the host resolves plugins with an <see
    /// cref="IHttpClientFactory"/> available) but credential-bearing Asana
    /// traffic never uses the factory's default redirect-following handler:
    /// <see cref="EnsureClients"/> builds a dedicated no-redirect client via
    /// <see cref="CredentialHttp.CreateNoRedirectClient(TimeSpan)"/>.
    /// </summary>
    public AsanaWorkSyncPlugin(IHttpClientFactory httpFactory, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);
        _clock = clock ?? TimeProvider.System;
        _env = Environment.GetEnvironmentVariable;
        _ownsHttpClient = true;
    }

    /// <summary>Test constructor: explicit client, config, clock, and environment.</summary>
    internal AsanaWorkSyncPlugin(
        HttpClient http,
        IConfigurationSection config,
        TimeProvider? clock = null,
        Func<string, string?>? env = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _testConfig = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? TimeProvider.System;
        _env = env ?? Environment.GetEnvironmentVariable;
        _ownsHttpClient = false;
    }

    /// <inheritdoc />
    public string Namespace => AsanaWorkSyncOptions.ProviderNamespace;

    /// <summary>
    /// Polling-only source. Asana webhooks require per-resource registration
    /// through the webhook API (membership-affecting lifecycle this plugin
    /// must not perform), so deliveries are unsupported and <see
    /// cref="ParseVerifiedWebhookBody"/> throws. Polling is the complete
    /// intake path.
    /// </summary>
    public WorkSourceCapabilities Capabilities { get; } = new(SupportsWebhooks: false, SupportsPolling: true);

    /// <inheritdoc />
    public WorkTrackerCapabilities TrackerCapabilities => new(CanPostComments: true, CanSetStatus: true);

    WorkTrackerCapabilities IWorkTracker.Capabilities => TrackerCapabilities;

    /// <inheritdoc />
    public WorkSignal RequiredSignal => CurrentOptions().RequiredSignal;

    /// <inheritdoc />
    public async Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _host = context.Host;
        _logger = context.Logger ?? NullLogger.Instance;
        var options = CurrentOptions();
        if (!options.Enabled)
        {
            _logger.LogInformation("Asana work sync is disabled (Enabled=false); polling and posting are no-ops");
            return;
        }
        await ProbeIdentityAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Probes the authenticated identity for observability: reads
    /// <c>GET /users/me</c> and logs the GID and name the credential belongs
    /// to. A failure degrades to a logged warning rather than a startup
    /// failure. The probe is advisory: declared capabilities stay static.
    /// </summary>
    private async Task ProbeIdentityAsync(AsanaWorkSyncOptions options, CancellationToken ct)
    {
        try
        {
            var api = EnsureClients();
            var identity = await api.GetAuthenticatedUserAsync(options, ct).ConfigureAwait(false);
            if (identity is null)
                _logger.LogWarning("Asana /users/me returned no identity; continuing — polling remains the intake path");
            else
                _logger.LogInformation(
                    "Asana work sync enabled against {Base} as {Name} ({Gid})",
                    options.ApiBaseUrl, identity.Name, identity.Gid);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && (ex is AsanaApiException or HttpRequestException or TaskCanceledException or InvalidOperationException))
        {
            _logger.LogWarning(ex, "Asana identity probe failed; continuing — polling still applies");
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
            _logger.LogWarning("Asana poll skipped: no projects are mapped (ProjectMap is empty)");
            yield break;
        }
        var api = EnsureClients();
        var cap = Math.Max(1, options.MaxItemsPerPoll);
        var count = 0;
        foreach (var (asanaProjectGid, codeyBoxProject) in options.ProjectMap)
        {
            ct.ThrowIfCancellationRequested();
            if (count >= cap)
                yield break;
            if (string.IsNullOrWhiteSpace(asanaProjectGid) || string.IsNullOrWhiteSpace(codeyBoxProject))
                continue;
            if (!AsanaGids.IsGid(asanaProjectGid.Trim()))
            {
                _logger.LogWarning(
                    "Asana poll skipped project key '{ProjectKey}': not a numeric GID — map Asana project GIDs, never names",
                    asanaProjectGid);
                continue;
            }
            var projectGid = asanaProjectGid.Trim();
            await foreach (var page in SkipProjectOnQueryFailure(
                api.ListTasksPagedAsync(options, projectGid, ct), projectGid, ct).ConfigureAwait(false))
            {
                foreach (var task in page)
                {
                    ct.ThrowIfCancellationRequested();
                    if (count >= cap)
                        yield break;
                    var candidate = ToCandidate(task, options);
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
    /// cref="AsanaRestClient.ListTasksPagedAsync"/> is an async iterator, so
    /// REST errors surface inside the enumeration — the guard must wrap
    /// <c>MoveNextAsync</c>, not the call that built the iterator — or one
    /// failing project would abort polling for all remaining projects.
    /// Real cancellation propagates.
    /// </summary>
    private async IAsyncEnumerable<IReadOnlyList<AsanaTask>> SkipProjectOnQueryFailure(
        IAsyncEnumerable<IReadOnlyList<AsanaTask>> pages,
        string asanaProjectGid,
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
                && (ex is AsanaApiException or HttpRequestException
                    or InvalidOperationException or TaskCanceledException))
            {
                _logger.LogWarning(ex, "Asana poll skipped project {Project}: query failed", asanaProjectGid);
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
            "Asana work sync is polling-only (SupportsWebhooks=false): it never creates " +
            "webhooks, so there are no verified bodies to parse. Poll for signalled tasks instead.");

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostProgressAsync(
        TrackerProgressUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "asana work sync is disabled");
        var check = this.CheckTracked(update.Namespace, update.ExternalId);
        if (check is not null)
            return check;
        if (!AsanaGids.IsGid(update.ExternalId))
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Asana task GID '{update.ExternalId}' is not a valid numeric GID.");

        // The caller (WorkTrackerService) resolves the declared external
        // status from the operator's state mapping and hands it in via
        // ExternalStatus; an empty status means unmapped — report it, never
        // guess (and never re-map through a second, driftable mapping).
        var status = update.ExternalStatus;
        if (string.IsNullOrEmpty(status))
            return TrackerPostResult.UnmappedFor(update.State);

        var api = EnsureClients();
        var applied = await ApplyStatusAsync(api, options, update.ExternalId, status, ct).ConfigureAwait(false);
        if (applied is not null)
            return applied;

        var body = WorkSyncText.ClipComment(update.Body, update.WorkItemId);
        var duplicate = await AlreadyPostedAsync(api, options, update.ExternalId, update.WorkItemId, body, null, ct)
            .ConfigureAwait(false);
        if (duplicate is true)
            return new TrackerPostResult(TrackerPostOutcome.SkippedDuplicate,
                Detail: $"identical story already present on task '{update.ExternalId}'");
        try
        {
            var storyGid = await api.CreateStoryAsync(options, update.ExternalId, body, ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: storyGid);
        }
        catch (AsanaApiException ex)
        {
            // The status change above already landed: report honestly instead
            // of pretending the whole post failed silently.
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Asana status '{status}' applied to task '{update.ExternalId}' but the story failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostQuestionAsync(
        TrackerQuestionPost post, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "asana work sync is disabled");
        var check = this.CheckTracked(post.Namespace, post.ExternalId);
        if (check is not null)
            return check;
        if (!AsanaGids.IsGid(post.ExternalId))
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Asana task GID '{post.ExternalId}' is not a valid numeric GID.");

        var api = EnsureClients();
        var questionTag = WorkSyncQuestions.TagFor(post.QuestionId);
        var body = WorkSyncText.ClipComment($"{post.Body}\n\n{questionTag}", post.WorkItemId);
        var duplicate = await AlreadyPostedAsync(api, options, post.ExternalId, post.WorkItemId, body, questionTag, ct)
            .ConfigureAwait(false);
        if (duplicate is true)
            return new TrackerPostResult(TrackerPostOutcome.SkippedDuplicate,
                Detail: $"question '{post.QuestionId}' already surfaced on task '{post.ExternalId}'");
        try
        {
            var storyGid = await api.CreateStoryAsync(options, post.ExternalId, body, ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: storyGid);
        }
        catch (AsanaApiException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Asana question post for task '{post.ExternalId}' failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostOutcomeAsync(
        TrackerOutcomeReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "asana work sync is disabled");
        var check = this.CheckTracked(report.Namespace, report.ExternalId);
        if (check is not null)
            return check;
        if (!AsanaGids.IsGid(report.ExternalId))
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Asana task GID '{report.ExternalId}' is not a valid numeric GID.");

        // The caller (WorkTrackerService) resolves the declared external status
        // from the operator's state mapping and hands it in via ExternalStatus.
        // An empty terminal status means unmapped — report it, never guess.
        // Interim commit/PR links carry no status and post as stories only.
        string status = report.ExternalStatus;
        if (report.IsComplete && string.IsNullOrEmpty(status))
        {
            return new TrackerPostResult(TrackerPostOutcome.UnmappedState,
                Detail: "terminal outcome has no declared external status mapping; " +
                    "add one to the source's state mapping or leave the item unsynced");
        }

        var api = EnsureClients();
        if (!string.IsNullOrEmpty(status))
        {
            var applied = await ApplyStatusAsync(api, options, report.ExternalId, status, ct).ConfigureAwait(false);
            if (applied is not null)
                return applied;
        }

        var body = WorkSyncText.ClipComment(report.Body, report.WorkItemId);
        var duplicate = await AlreadyPostedAsync(api, options, report.ExternalId, report.WorkItemId, body, null, ct)
            .ConfigureAwait(false);
        if (duplicate is true)
            return new TrackerPostResult(TrackerPostOutcome.SkippedDuplicate,
                Detail: $"identical story already present on task '{report.ExternalId}'");
        try
        {
            var storyGid = await api.CreateStoryAsync(options, report.ExternalId, body, ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: storyGid);
        }
        catch (AsanaApiException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Asana outcome post for task '{report.ExternalId}' failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsHttpClient)
            _http?.Dispose();
    }

    internal AsanaWorkSyncOptions CurrentOptions()
    {
        var section = _host?.ScopedConfig ?? _testConfig;
        if (section is null)
            return new AsanaWorkSyncOptions();
        var warnings = new List<string>();
        var options = AsanaWorkSyncOptions.FromConfiguration(section, warnings);
        foreach (var warning in warnings)
            _logger.LogWarning("Asana work-sync config: {Warning}", warning);
        return options;
    }

    private AsanaRestClient EnsureClients()
    {
        if (_api is not null)
            return _api;
        lock (_clientLock)
        {
            if (_api is not null)
                return _api;
            if (_http is null)
            {
                // Credential-bearing traffic never follows redirects: the
                // factory's default handler follows up to 50 cross-origin
                // hops and would re-send the bearer token to the redirect
                // target, so this plugin owns a dedicated no-redirect
                // client. Request timeouts are enforced per attempt from
                // the live TimeoutSeconds option (a linked CTS in
                // SendWithRetryAsync), so edits hot-reload; disable the
                // client-level timeout on this owned client. An injected
                // client is never mutated.
                _http = CredentialHttp.CreateNoRedirectClient(OwnedClientConstructionTimeout);
                _http.Timeout = Timeout.InfiniteTimeSpan;
            }
            _tokens = new AsanaTokenProvider(_env);
            _api = new AsanaRestClient(_http, _tokens, _clock);
            return _api;
        }
    }

    /// <summary>
    /// Applies a caller-resolved external status to the task. Returns a
    /// non-null <see cref="TrackerPostResult"/> only when the post must be
    /// reported — null means the change landed and the caller proceeds to
    /// the story. Recognised meanings, and only these: <c>completed</c> /
    /// <c>incomplete</c> (completion toggle) and keys of the operator's
    /// explicit <c>StatusCustomFieldMap</c> (one custom-field enum write).
    /// Anything else is a reported <c>Failed</c> outcome — never guessed.
    /// </summary>
    private async Task<TrackerPostResult?> ApplyStatusAsync(
        AsanaRestClient api,
        AsanaWorkSyncOptions options,
        string taskGid,
        string status,
        CancellationToken ct)
    {
        try
        {
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                await api.SetCompletedAsync(options, taskGid, true, ct).ConfigureAwait(false);
                return null;
            }
            if (string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase))
            {
                await api.SetCompletedAsync(options, taskGid, false, ct).ConfigureAwait(false);
                return null;
            }
            if (options.StatusCustomFieldMap.TryGetValue(status, out var mapping))
            {
                if (!AsanaRestClient.TryParseCustomFieldMapping(mapping, out var fieldGid, out var enumGid))
                    return new TrackerPostResult(TrackerPostOutcome.Failed,
                        Detail: $"Asana custom-field mapping for status '{status}' is not fieldGid:enumGid " +
                            "(both numeric GIDs); fix StatusCustomFieldMap.");
                await api.SetCustomFieldAsync(options, taskGid, fieldGid, enumGid, ct).ConfigureAwait(false);
                return null;
            }
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Asana has no declared meaning for external status '{status}' on task '{taskGid}': " +
                    "map it to 'completed'/'incomplete' or add a StatusCustomFieldMap entry.");
        }
        catch (ArgumentException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Asana cannot express status '{status}' for task '{taskGid}': {ex.Message}");
        }
        catch (AsanaApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Asana task '{taskGid}' not found");
        }
        catch (AsanaApiException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Asana rejected the status change '{status}' for task '{taskGid}': {ex.Message}");
        }
    }

    /// <summary>
    /// Best-effort duplicate detection over the task's recent stories: true
    /// when our marked write is already present, false when it is absent,
    /// null when the story read itself failed (the caller proceeds — a stale
    /// read must not block the authoritative write, and a bad credential
    /// fails on the write itself). A deleted task surfaces as a thrown
    /// <see cref="AsanaApiException"/> so the caller reports
    /// <c>Failed</c> instead of posting nowhere.
    /// </summary>
    /// <param name="questionTag">When set, a story containing both the marker and this tag counts.</param>
    private async Task<bool?> AlreadyPostedAsync(
        AsanaRestClient api,
        AsanaWorkSyncOptions options,
        string taskGid,
        WorkItemId workItemId,
        string body,
        string? questionTag,
        CancellationToken ct)
    {
        IReadOnlyList<AsanaStory> stories;
        try
        {
            stories = await api.ListRecentStoriesAsync(options, taskGid, ct).ConfigureAwait(false);
        }
        catch (AsanaApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new AsanaApiException($"Asana task '{taskGid}' not found", ex.StatusCode);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && (ex is AsanaApiException or HttpRequestException or InvalidOperationException or TaskCanceledException))
        {
            _logger.LogWarning(ex, "Asana duplicate scan failed for task {Task}; proceeding with the write", taskGid);
            return null;
        }
        var marker = WorkSyncLoopGuard.MarkerFor(workItemId);
        foreach (var story in stories)
        {
            if (string.IsNullOrEmpty(story.Text)
                || !story.Text.Contains(marker, StringComparison.Ordinal))
                continue;
            if (questionTag is not null)
            {
                if (story.Text.Contains(questionTag, StringComparison.Ordinal))
                    return true;
            }
            else if (string.Equals(story.Text, body, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private ExternalWorkItem? ToCandidate(AsanaTask task, AsanaWorkSyncOptions options)
    {
        // A multi-homed task (in several projects) ingests under the first
        // mapped project in the task's own project order — deterministic
        // across polls, so overlapping polls converge on one work item while
        // the global GID keeps outbound writes unambiguous.
        string? codeyBoxProject = null;
        foreach (var projectGid in task.ProjectGids)
        {
            if (options.ProjectMap.TryGetValue(projectGid, out var mapped)
                && !string.IsNullOrWhiteSpace(mapped))
            {
                codeyBoxProject = mapped;
                break;
            }
        }
        if (codeyBoxProject is null)
            return null;

        var present = new List<WorkSignal>();
        if (AsanaGids.IsGid(task.AssigneeGid))
            present.Add(new WorkSignal(WorkSignalKind.Assignee, task.AssigneeGid));
        if (!string.IsNullOrWhiteSpace(task.AssigneeName))
            present.Add(new WorkSignal(WorkSignalKind.Assignee, task.AssigneeName));
        foreach (var tagGid in task.TagGids)
            present.Add(new WorkSignal(WorkSignalKind.Label, tagGid));
        foreach (var tagName in task.TagNames)
        {
            if (!string.IsNullOrWhiteSpace(tagName))
                present.Add(new WorkSignal(WorkSignalKind.Label, tagName));
        }
        foreach (var section in task.SectionNames)
        {
            if (!string.IsNullOrWhiteSpace(section))
                present.Add(new WorkSignal(WorkSignalKind.Status, section));
        }
        if (task.Completed)
            present.Add(new WorkSignal(WorkSignalKind.Status, "completed"));

        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = task.Gid,
            ProjectId = new ProjectId(codeyBoxProject),
            Title = string.IsNullOrWhiteSpace(task.Name) ? task.Gid : task.Name,
            Body = WorkSyncText.Truncate(task.Notes, options.MaxIngestedBodyChars),
            PresentSignals = present,
            LastActorLogin = null,
            HasSignal = options.RequiredSignal.IsPresentIn(present),
        };
    }
}
