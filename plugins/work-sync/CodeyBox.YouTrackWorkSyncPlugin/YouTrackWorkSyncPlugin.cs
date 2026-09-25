using System.Runtime.CompilerServices;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.YouTrackWorkSyncPlugin;

/// <summary>
/// CodeyBox work-source / work-tracker plugin for YouTrack. One project, one
/// plugin: this single instance implements <see cref="IWorkSource"/> (inbound:
/// tag/assignee/state-signalled issues become work items) and <see
/// cref="IWorkTracker"/> (outbound: progress, questions, commit/PR links,
/// completion) against the YouTrack REST API, which is identical on
/// YouTrack Cloud and self-hosted Server.
/// <para>Off unless an operator enables it: the plugin loads only when
/// allowlisted AND named in <c>Plugins:Enabled</c>, and it polls/posts nothing
/// until <c>Enabled=true</c> in its own section.</para>
/// <para>Credentials come from the host credential chain (environment
/// variables populated by the operator's vault agent or container secrets);
/// configuration holds only the variable <em>names</em>. Permanent tokens
/// travel as <c>Authorization: Bearer</c>; Hub OAuth2 access tokens
/// (client-credentials grant) are refreshed as part of the integration
/// (<see cref="YouTrackTokenProvider"/>).</para>
/// <para>YouTrack's distinctive surface is its command syntax: state changes
/// are commands (<c>{State} {In Progress}</c>) applied through
/// <c>POST /api/commands</c>, not field writes. Project and field names are
/// per-instance configurable, so the field names are operator declarations,
/// and the state value applied is the caller-resolved <see
/// cref="TrackerProgressUpdate.ExternalStatus"/>/<see
/// cref="TrackerOutcomeReport.ExternalStatus"/> — an unmapped state is
/// reported, never guessed, and a command the instance rejects is a reported
/// <c>Failed</c> outcome.</para>
/// <para>Webhooks: YouTrack delivers them through the Webhook Triggers app,
/// which is configured in the YouTrack UI (there is no webhook-registration
/// REST API on any YouTrack version, so registration cannot be managed from
/// here). Deliveries are authenticated by the app's shared token in the
/// configured header; the plugin verifies it before parsing and ignores its
/// own writes via the loop-guard marker and service logins.</para>
/// </summary>
[CodeyBoxPlugin(
    id: YouTrackWorkSyncOptions.PluginId,
    displayName: "CodeyBox: YouTrack Work Sync",
    minHostApiVersion: "1.0")]
public sealed class YouTrackWorkSyncPlugin
    : IWorkSource, IWorkTracker, IPluginInitializer, IDisposable
{
    private readonly IHttpClientFactory? _httpFactory;
    private readonly TimeProvider _clock;
    private readonly Func<string, string?> _env;
    private readonly IConfigurationSection? _testConfig;

    private IPluginHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private HttpClient? _http;
    private YouTrackTokenProvider? _tokens;
    private YouTrackRestClient? _api;
    private readonly bool _ownsHttpClient;
    private readonly object _clientLock = new();
    private bool _disposed;

    /// <summary>Production constructor (DI provides the HTTP factory).</summary>
    public YouTrackWorkSyncPlugin(IHttpClientFactory httpFactory, TimeProvider? clock = null)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _clock = clock ?? TimeProvider.System;
        _env = Environment.GetEnvironmentVariable;
        _ownsHttpClient = true;
    }

    /// <summary>Test constructor: explicit client, config, clock, and environment.</summary>
    internal YouTrackWorkSyncPlugin(
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
    public string Namespace => YouTrackWorkSyncOptions.ProviderNamespace;

    /// <inheritdoc />
    public WorkSourceCapabilities Capabilities { get; } = new(SupportsWebhooks: true, SupportsPolling: true);

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
            _logger.LogInformation("YouTrack work sync is disabled (Enabled=false); polling and posting are no-ops");
            return Task.CompletedTask;
        }
        return ProbeInstanceAsync(options, ct);
    }

    /// <summary>
    /// Probes the instance for observability: reads <c>GET /api/config</c>
    /// for the version/build the instance reports and logs it. Older Server
    /// versions and low-privilege tokens may not answer the endpoint — that
    /// degrades to a logged "unknown" rather than a startup failure, and
    /// polling remains the fallback path. The probe is advisory: declared
    /// capabilities stay static because webhooks, polling, comments, and
    /// commands are uniformly available on the <c>/api</c> surface.
    /// </summary>
    private async Task ProbeInstanceAsync(YouTrackWorkSyncOptions options, CancellationToken ct)
    {
        try
        {
            var api = EnsureClients();
            var info = await api.GetInstanceInfoAsync(options, ct).ConfigureAwait(false);
            if (info is null)
            {
                _logger.LogWarning(
                    "YouTrack instance did not report /api/config (older Server or low-privilege token); " +
                    "REST capability will be discovered per-request");
            }
            else
            {
                _logger.LogInformation(
                    "YouTrack work sync enabled against {Base} (version {Version}, build {Build})",
                    options.ApiBaseUrl,
                    string.IsNullOrWhiteSpace(info.Version) ? "unknown" : info.Version,
                    string.IsNullOrWhiteSpace(info.Build) ? "unknown" : info.Build);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && (ex is YouTrackApiException or HttpRequestException or TaskCanceledException or InvalidOperationException))
        {
            _logger.LogWarning(ex, "YouTrack instance probe failed; continuing — polling and webhooks still apply");
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
            _logger.LogWarning("YouTrack poll skipped: no projects are mapped (ProjectMap is empty)");
            yield break;
        }
        var api = EnsureClients();
        var cap = Math.Max(1, options.MaxItemsPerPoll);
        var count = 0;
        foreach (var (youTrackProjectKey, codeyBoxProject) in options.ProjectMap)
        {
            ct.ThrowIfCancellationRequested();
            if (count >= cap)
                yield break;
            if (string.IsNullOrWhiteSpace(youTrackProjectKey) || string.IsNullOrWhiteSpace(codeyBoxProject))
                continue;
            IAsyncEnumerable<IReadOnlyList<YouTrackIssue>> pages;
            try
            {
                // A project key that cannot be quoted safely is a config
                // error: skip it loudly rather than emitting a mangled query.
                _ = YouTrackRestClient.QuoteQueryValue(youTrackProjectKey);
                pages = api.SearchIssuesPagedAsync(options, youTrackProjectKey, ct);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "YouTrack poll skipped project {Project}: key cannot be expressed in a query", youTrackProjectKey);
                continue;
            }
            await foreach (var page in SkipProjectOnQueryFailure(pages, youTrackProjectKey, ct).ConfigureAwait(false))
            {
                foreach (var issue in page)
                {
                    ct.ThrowIfCancellationRequested();
                    if (count >= cap)
                        yield break;
                    var candidate = ToCandidate(issue, codeyBoxProject, options);
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
    /// cref="YouTrackRestClient.SearchIssuesPagedAsync"/> is an async
    /// iterator, so REST errors surface inside the enumeration — the guard
    /// must wrap <c>MoveNextAsync</c>, not the call that built the iterator —
    /// or one failing project would abort polling for all remaining projects.
    /// Real cancellation propagates.
    /// </summary>
    private async IAsyncEnumerable<IReadOnlyList<YouTrackIssue>> SkipProjectOnQueryFailure(
        IAsyncEnumerable<IReadOnlyList<YouTrackIssue>> pages,
        string youTrackProjectKey,
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
                && (ex is YouTrackApiException or HttpRequestException
                    or InvalidOperationException or TaskCanceledException))
            {
                _logger.LogWarning(ex, "YouTrack poll skipped project {Project}: query failed", youTrackProjectKey);
                yield break;
            }
            if (!moved)
                yield break;
            yield return enumerator.Current;
        }
    }

    /// <inheritdoc />
    public ExternalWorkItem? ParseVerifiedWebhookBody(string verifiedBody)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifiedBody);
        var options = CurrentOptions();
        var parsed = YouTrackWebhook.Parse(verifiedBody, options);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.IssueId))
            return null;

        if (parsed.Kind == YouTrackWebhookEventKind.Comment)
        {
            // Comments never ingest directly: only a question-id reply prefix can
            // answer a surfaced question, and content alone never triggers work.
            // A comment whose project is unmapped cannot be attributed to a
            // work item — drop it rather than guess a project.
            var commentProject = ProjectFor(parsed.ProjectKey, options);
            if (commentProject is null)
                return null;
            return new ExternalWorkItem
            {
                Namespace = Namespace,
                ExternalId = parsed.IssueId,
                ProjectId = new ProjectId(commentProject),
                Title = parsed.IssueId,
                Body = WorkSyncText.Truncate(parsed.Body, options.MaxIngestedBodyChars),
                PresentSignals = [],
                LastActorLogin = parsed.ActorLogin,
                HasSignal = false,
            };
        }

        if (parsed.Kind != YouTrackWebhookEventKind.Issue
            || !options.ProjectMap.TryGetValue(parsed.ProjectKey, out var codeyBoxProject)
            || string.IsNullOrWhiteSpace(codeyBoxProject))
            return null;

        var present = ToWorkSignals(parsed.PresentSignals);
        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = parsed.IssueId,
            ProjectId = new ProjectId(codeyBoxProject),
            Title = string.IsNullOrWhiteSpace(parsed.Title) ? parsed.IssueId : parsed.Title,
            Body = WorkSyncText.Truncate(parsed.Body, options.MaxIngestedBodyChars),
            PresentSignals = present,
            LastActorLogin = parsed.ActorLogin,
            HasSignal = options.RequiredSignal.IsPresentIn(present),
        };
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostProgressAsync(
        TrackerProgressUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "youtrack work sync is disabled");
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
        var applied = await ApplyStateAsync(api, options, update.ExternalId, status, ct).ConfigureAwait(false);
        if (applied is not null)
            return applied;

        try
        {
            var commentId = await api.CreateCommentAsync(
                options, update.ExternalId,
                WorkSyncText.ClipComment(update.Body, update.WorkItemId), ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
        }
        catch (YouTrackApiException ex)
        {
            // The state command above already landed: report honestly instead of
            // pretending the whole post failed silently.
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"YouTrack command '{status}' applied to '{update.ExternalId}' but the comment failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostQuestionAsync(
        TrackerQuestionPost post, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "youtrack work sync is disabled");
        var check = this.CheckTracked(post.Namespace, post.ExternalId);
        if (check is not null)
            return check;

        var api = EnsureClients();
        try
        {
            var body = WorkSyncText.ClipComment(
                $"{post.Body}\n\n{YouTrackWebhook.QuestionTag(post.QuestionId)}", post.WorkItemId);
            var commentId = await api.CreateCommentAsync(options, post.ExternalId, body, ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
        }
        catch (YouTrackApiException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"YouTrack question post for '{post.ExternalId}' failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostOutcomeAsync(
        TrackerOutcomeReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "youtrack work sync is disabled");
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
        if (!string.IsNullOrEmpty(status))
        {
            var applied = await ApplyStateAsync(api, options, report.ExternalId, status, ct).ConfigureAwait(false);
            if (applied is not null)
                return applied;
        }

        try
        {
            var commentId = await api.CreateCommentAsync(
                options, report.ExternalId,
                WorkSyncText.ClipComment(report.Body, report.WorkItemId), ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
        }
        catch (YouTrackApiException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"YouTrack outcome post for '{report.ExternalId}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Verifies a webhook delivery by comparing the value of the configured
    /// <see cref="YouTrackWorkSyncOptions.WebhookTokenHeader"/> request header
    /// against the shared token from the credential chain (constant-time).
    /// The hosting endpoint MUST call this over the delivery BEFORE calling
    /// <c>ParseVerifiedWebhookBody</c>.
    /// </summary>
    /// <param name="presentedToken">Header value sent by the Webhook Triggers app.</param>
    public bool VerifyWebhookDelivery(string? presentedToken)
    {
        var options = CurrentOptions();
        return YouTrackWebhook.VerifyDelivery(
            presentedToken, _env(options.WebhookSecretEnvVar) ?? string.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _tokens?.Dispose();
        if (_ownsHttpClient)
            _http?.Dispose();
    }

    internal YouTrackWorkSyncOptions CurrentOptions()
    {
        var section = _host?.ScopedConfig ?? _testConfig;
        if (section is null)
            return new YouTrackWorkSyncOptions();
        var warnings = new List<string>();
        var options = YouTrackWorkSyncOptions.FromConfiguration(section, warnings);
        foreach (var warning in warnings)
            _logger.LogWarning("YouTrack work-sync config: {Warning}", warning);
        return options;
    }

    private YouTrackRestClient EnsureClients()
    {
        if (_api is not null)
            return _api;
        lock (_clientLock)
        {
            if (_api is not null)
                return _api;
            if (_http is null)
            {
                _http = _httpFactory!.CreateClient("youtrack-worksync");
                // Request timeouts are enforced per request from the live
                // TimeoutSeconds option (a linked CTS in SendDocumentAsync),
                // so edits hot-reload; disable the client-level timeout on
                // this owned client. An injected client is never mutated.
                _http.Timeout = Timeout.InfiniteTimeSpan;
            }
            _tokens = new YouTrackTokenProvider(_http, _env, _clock);
            _api = new YouTrackRestClient(_http, _tokens);
            return _api;
        }
    }

    /// <summary>
    /// Applies a declared state value through the command interface. Returns
    /// a non-null <see cref="TrackerPostResult"/> only when the post must be
    /// reported as failed — null means the command landed and the caller
    /// proceeds to comment.
    /// </summary>
    private async Task<TrackerPostResult?> ApplyStateAsync(
        YouTrackRestClient api,
        YouTrackWorkSyncOptions options,
        string externalId,
        string status,
        CancellationToken ct)
    {
        try
        {
            // The command is built and quoted at the sink: a value the
            // command language cannot express throws ArgumentException
            // before anything is sent — a refused write, never a mangled
            // command.
            await api.ApplyCommandAsync(
                options, externalId, options.StateFieldName, status, ct).ConfigureAwait(false);
            return null;
        }
        catch (ArgumentException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"YouTrack cannot express '{status}' as a command for '{externalId}': {ex.Message}");
        }
        catch (YouTrackApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"YouTrack issue '{externalId}' not found");
        }
        catch (YouTrackApiException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"YouTrack rejected the state command '{status}' for '{externalId}': {ex.Message}");
        }
    }

    private ExternalWorkItem? ToCandidate(
        YouTrackIssue issue, string codeyBoxProject, YouTrackWorkSyncOptions options)
    {
        var present = new List<WorkSignal>();
        foreach (var tag in issue.TagNames)
        {
            if (!string.IsNullOrWhiteSpace(tag))
                present.Add(new WorkSignal(WorkSignalKind.Label, tag));
        }
        foreach (var assignee in issue.AssigneeLogins)
        {
            if (!string.IsNullOrWhiteSpace(assignee))
                present.Add(new WorkSignal(WorkSignalKind.Assignee, assignee));
        }
        if (!string.IsNullOrWhiteSpace(issue.StateName))
            present.Add(new WorkSignal(WorkSignalKind.Status, issue.StateName));

        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = issue.IdReadable,
            ProjectId = new ProjectId(codeyBoxProject),
            Title = string.IsNullOrWhiteSpace(issue.Title) ? issue.IdReadable : issue.Title,
            Body = WorkSyncText.Truncate(issue.Description, options.MaxIngestedBodyChars),
            PresentSignals = present,
            LastActorLogin = issue.LastActorLogin,
            HasSignal = options.RequiredSignal.IsPresentIn(present),
        };
    }

    private static IReadOnlyList<WorkSignal> ToWorkSignals(
        IReadOnlyList<YouTrackSignalDatum> data)
    {
        var signals = new List<WorkSignal>(data.Count);
        foreach (var datum in data)
        {
            if (!string.IsNullOrWhiteSpace(datum.Value))
                signals.Add(new WorkSignal(datum.Kind, datum.Value));
        }
        return signals;
    }

    /// <summary>
    /// Maps a webhook project key to its operator-declared CodeyBox project.
    /// Null when unmapped — the same "skipped, never guessed" rule as issues.
    /// </summary>
    private static string? ProjectFor(string projectKey, YouTrackWorkSyncOptions options) =>
        options.ProjectMap.TryGetValue(projectKey, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
            ? mapped
            : null;
}
