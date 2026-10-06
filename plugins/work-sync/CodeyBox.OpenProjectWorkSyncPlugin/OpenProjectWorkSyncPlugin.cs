using System.Runtime.CompilerServices;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.OpenProjectWorkSyncPlugin;

/// <summary>
/// CodeyBox work-source / work-tracker plugin for OpenProject. One project,
/// one plugin: this single instance implements <see cref="IWorkSource"/>
/// (inbound: assignee/status-signalled work packages become work items) and
/// <see cref="IWorkTracker"/> (outbound: progress, questions, commit/PR
/// links, completion) against the OpenProject REST API v3 (HAL+JSON).
/// <para>Off unless an operator enables it: the plugin loads only when
/// allowlisted AND named in <c>Plugins:Enabled</c>, and it polls/posts nothing
/// until <c>Enabled=true</c> in its own section.</para>
/// <para>Credentials come from the host credential chain (environment
/// variables populated by the operator's vault agent or container secrets);
/// configuration holds only the variable <em>name</em>. The API token travels
/// as <c>Authorization: Bearer</c> per the official API introduction.</para>
/// <para>OpenProject's distinctive surface is optimistic locking: every
/// status write carries the <c>lockVersion</c> from the immediately-preceding
/// read, and the status href applied is resolved from the host's own
/// <c>GET /api/v3/statuses</c> at the moment of the write — an unmapped or
/// host-unknown status is reported, never guessed. A 409 means the package
/// changed under us: the plugin re-reads and reconciles before any single
/// bounded repeat, and every comment write is deduplicated by exact-body
/// activity match.</para>
/// <para>Polling only: OpenProject exposes no webhook-registration REST API
/// consumed here and the plugin opens no live connection, so
/// <see cref="IWorkSource.Capabilities"/> honestly declares polling without
/// webhooks and <see cref="IWorkSource.ParseVerifiedWebhookBody"/> throws
/// <see cref="NotSupportedException"/>.</para>
/// </summary>
[CodeyBoxPlugin(
    id: OpenProjectWorkSyncOptions.PluginId,
    displayName: "CodeyBox: OpenProject Work Sync",
    minHostApiVersion: "1.0")]
public sealed class OpenProjectWorkSyncPlugin
    : IWorkSource, IWorkTracker, IPluginInitializer, IDisposable
{
    private readonly IHttpClientFactory? _httpFactory;
    private readonly Func<string, string?> _env;
    private readonly IConfigurationSection? _testConfig;

    private IPluginHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private HttpClient? _http;
    private OpenProjectTokenProvider? _tokens;
    private OpenProjectRestClient? _api;
    private readonly bool _ownsHttpClient;
    private readonly object _clientLock = new();
    private bool _disposed;

    /// <summary>Production constructor (DI provides the HTTP factory).</summary>
    public OpenProjectWorkSyncPlugin(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _env = Environment.GetEnvironmentVariable;
        _ownsHttpClient = true;
    }

    /// <summary>Test constructor: explicit client, config, and environment.</summary>
    internal OpenProjectWorkSyncPlugin(
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
    public string Namespace => OpenProjectWorkSyncOptions.ProviderNamespace;

    /// <summary>
    /// Polling only: webhook registration and live connections are out of
    /// scope, so webhook support is honestly declared absent.
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
            _logger.LogInformation("OpenProject work sync is disabled (Enabled=false); polling and posting are no-ops");
            return Task.CompletedTask;
        }
        return ProbeInstanceAsync(options, ct);
    }

    /// <summary>
    /// Probes the instance for observability: lists the admin-defined
    /// statuses the host-resolved mapping matches against and logs how many
    /// were found. An unreachable or forbidden endpoint degrades to a warning
    /// rather than a startup failure — polling remains the fallback path and
    /// per-post failures still surface. The probe is advisory: declared
    /// capabilities stay static because listing, reading, patching (with
    /// <c>lockVersion</c>), and activity comments are uniform across the
    /// <c>/api/v3</c> surface this adapter is pinned to.
    /// </summary>
    private async Task ProbeInstanceAsync(OpenProjectWorkSyncOptions options, CancellationToken ct)
    {
        try
        {
            var api = EnsureClients();
            var statuses = await api.ListStatusesAsync(options, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "OpenProject work sync enabled against {Base} (API v3, OpenProject {MinVersion}+; {StatusCount} statuses known to host mapping)",
                options.ApiBaseUrl,
                OpenProjectWorkSyncOptions.MinSupportedVersion,
                statuses.Count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && (ex is OpenProjectApiException or HttpRequestException or TaskCanceledException or InvalidOperationException))
        {
            _logger.LogWarning(ex, "OpenProject instance probe failed; continuing — polling still applies");
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
            _logger.LogWarning("OpenProject poll skipped: no projects are mapped (ProjectMap is empty)");
            yield break;
        }
        if (options.SignalKind == WorkSignalKind.Label)
        {
            // OpenProject work packages carry no labels or tags: a
            // label-kind signal can never be present. Warn loudly instead of
            // silently ingesting nothing all day.
            _logger.LogWarning(
                "OpenProject poll: SignalKind=Label never matches (work packages have no labels); " +
                "use Assignee (service-account user id) or Status");
        }
        var api = EnsureClients();
        var cap = Math.Max(1, options.MaxItemsPerPoll);
        var count = 0;
        foreach (var (projectKey, codeyBoxProject) in options.ProjectMap)
        {
            ct.ThrowIfCancellationRequested();
            if (count >= cap)
                yield break;
            if (string.IsNullOrWhiteSpace(projectKey) || string.IsNullOrWhiteSpace(codeyBoxProject))
                continue;
            IAsyncEnumerable<IReadOnlyList<OpenProjectWorkPackage>> pages;
            try
            {
                pages = api.ListProjectWorkPackagesPagedAsync(options, projectKey, ct);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "OpenProject poll skipped project {Project}", projectKey);
                continue;
            }
            await foreach (var page in SkipProjectOnQueryFailure(pages, projectKey, ct).ConfigureAwait(false))
            {
                foreach (var package in page)
                {
                    ct.ThrowIfCancellationRequested();
                    if (count >= cap)
                        yield break;
                    var candidate = ToCandidate(package, codeyBoxProject, options);
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
    /// cref="OpenProjectRestClient.ListProjectWorkPackagesPagedAsync"/> is an
    /// async iterator, so REST errors surface inside the enumeration — the
    /// guard must wrap <c>MoveNextAsync</c>, not the call that built the
    /// iterator — or one failing project would abort polling for all
    /// remaining projects. Real cancellation propagates.
    /// </summary>
    private async IAsyncEnumerable<IReadOnlyList<OpenProjectWorkPackage>> SkipProjectOnQueryFailure(
        IAsyncEnumerable<IReadOnlyList<OpenProjectWorkPackage>> pages,
        string projectKey,
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
                && (ex is OpenProjectApiException or HttpRequestException
                    or InvalidOperationException or TaskCanceledException))
            {
                _logger.LogWarning(ex, "OpenProject poll skipped project {Project}: query failed", projectKey);
                yield break;
            }
            if (!moved)
                yield break;
            yield return enumerator.Current;
        }
    }

    /// <summary>
    /// Webhooks are not part of this integration (no webhook registration or
    /// live connection is managed here), so there is no verified body to
    /// parse. Always throws.
    /// </summary>
    /// <exception cref="NotSupportedException">Always — polling is the only delivery path.</exception>
    public ExternalWorkItem? ParseVerifiedWebhookBody(string verifiedBody) =>
        throw new NotSupportedException(
            "OpenProject work sync is polling-only; webhook deliveries are not supported.");

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostProgressAsync(
        TrackerProgressUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "openproject work sync is disabled");
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
        var body = WorkSyncText.ClipComment(update.Body, update.WorkItemId);
        var (applied, statusFailure) = await ApplyStatusAsync(
            api, options, update.ExternalId, status, ct).ConfigureAwait(false);
        if (statusFailure is not null)
            return statusFailure;

        return await PostCommentIdempotentAsync(
            api, options, update.ExternalId, body, statusApplied: applied, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostQuestionAsync(
        TrackerQuestionPost post, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "openproject work sync is disabled");
        var check = this.CheckTracked(post.Namespace, post.ExternalId);
        if (check is not null)
            return check;

        var api = EnsureClients();
        var body = WorkSyncText.ClipComment(
            $"{post.Body}\n\n{WorkSyncQuestions.TagFor(post.QuestionId)}", post.WorkItemId);
        return await PostCommentIdempotentAsync(
            api, options, post.ExternalId, body, statusApplied: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostOutcomeAsync(
        TrackerOutcomeReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "openproject work sync is disabled");
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
        var applied = false;
        if (!string.IsNullOrEmpty(status))
        {
            TrackerPostResult? statusFailure;
            (applied, statusFailure) = await ApplyStatusAsync(
                api, options, report.ExternalId, status, ct).ConfigureAwait(false);
            if (statusFailure is not null)
                return statusFailure;
        }

        var body = WorkSyncText.ClipComment(report.Body, report.WorkItemId);
        return await PostCommentIdempotentAsync(
            api, options, report.ExternalId, body, statusApplied: applied, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsHttpClient)
            _http?.Dispose();
    }

    internal OpenProjectWorkSyncOptions CurrentOptions()
    {
        var section = _host?.ScopedConfig ?? _testConfig;
        if (section is null)
            return new OpenProjectWorkSyncOptions();
        var warnings = new List<string>();
        var options = OpenProjectWorkSyncOptions.FromConfiguration(section, warnings);
        foreach (var warning in warnings)
            _logger.LogWarning("OpenProject work-sync config: {Warning}", warning);
        return options;
    }

    private OpenProjectRestClient EnsureClients()
    {
        if (_api is not null)
            return _api;
        lock (_clientLock)
        {
            if (_api is not null)
                return _api;
            if (_http is null)
            {
                _http = _httpFactory!.CreateClient("openproject-worksync");
                // Request timeouts are enforced per request from the live
                // TimeoutSeconds option (a linked CTS in SendDocumentAsync),
                // so edits hot-reload; disable the client-level timeout on
                // this owned client. An injected client is never mutated.
                _http.Timeout = Timeout.InfiniteTimeSpan;
            }
            _tokens = new OpenProjectTokenProvider(_env);
            _api = new OpenProjectRestClient(_http, _tokens);
            return _api;
        }
    }

    /// <summary>
    /// Applies a declared status value through the versioned PATCH interface.
    /// Returns the applied flag plus a non-null failure only when the post
    /// must be reported as failed — a null failure means the status now
    /// holds (or already held) the declared value and the caller proceeds to
    /// the comment.
    /// <para>Ambiguous outcomes (lock conflict, transport failure after the
    /// request was sent) are reconciled by re-reading before any repeat, and
    /// at most one repeat is ever issued — with the fresh
    /// <c>lockVersion</c>, never the stale one.</para>
    /// </summary>
    private async Task<(bool Applied, TrackerPostResult? Failure)> ApplyStatusAsync(
        OpenProjectRestClient api,
        OpenProjectWorkSyncOptions options,
        string externalId,
        string status,
        CancellationToken ct)
    {
        OpenProjectWorkPackage? current;
        try
        {
            current = await api.GetWorkPackageAsync(options, externalId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            return (false, Failed($"OpenProject status read for '{externalId}' failed: {Short(ex)}"));
        }
        if (ct.IsCancellationRequested)
            return (false, Failed($"OpenProject status write for '{externalId}' cancelled."));
        if (current is null)
            return (false, Failed($"OpenProject work package '{externalId}' not found"));

        // Already there: the write is a no-op. The caller still posts the
        // comment (progress text is new), but records no status change.
        if (string.Equals(current.StatusName, status, StringComparison.OrdinalIgnoreCase))
            return (false, null);

        string? href;
        try
        {
            href = await ResolveStatusHrefAsync(api, options, status, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            return (false, Failed($"OpenProject status list for '{externalId}' failed: {Short(ex)}"));
        }
        if (href is null)
            return (false, Failed(
                $"OpenProject has no status named '{status}' for work package '{externalId}': " +
                "declare a host-known status name in the state mapping"));
        if (current.LockVersion is null)
            return (false, Failed(
                $"OpenProject work package '{externalId}' reports no lockVersion; refusing a blind write"));

        var patch = await TryPatchAsync(api, options, externalId, href, current.LockVersion.Value, ct)
            .ConfigureAwait(false);
        if (patch.Applied)
            return (true, null);
        if (patch.Retryable)
        {
            // Reconcile: re-read before any repeat. Converged means someone
            // (possibly our own ambiguous attempt) already applied it.
            var reread = await TryReadAsync(api, options, externalId, ct).ConfigureAwait(false);
            if (reread is not null
                && string.Equals(reread.StatusName, status, StringComparison.OrdinalIgnoreCase))
                return (true, null);
            if (reread?.LockVersion is not null)
            {
                var retry = await TryPatchAsync(api, options, externalId, href, reread.LockVersion.Value, ct)
                    .ConfigureAwait(false);
                if (retry.Applied)
                    return (true, null);
                if (retry.Retryable)
                    return (false, Failed(
                        $"OpenProject work package '{externalId}' changed concurrently; " +
                        $"status '{status}' not applied to avoid stomping the newer change"));
                return (false, Failed(retry.Detail ?? $"OpenProject rejected the status '{status}' for '{externalId}'"));
            }
            return (false, Failed(
                $"OpenProject status write for '{externalId}' was ambiguous and the work package " +
                "could not be re-read to reconcile; nothing was repeated"));
        }
        return (false, Failed(patch.Detail ?? $"OpenProject rejected the status '{status}' for '{externalId}'"));
    }

    /// <summary>
    /// Posts a comment idempotently: an exact-body activity already present
    /// (from a retried post or overlapping sync) is reused, never duplicated.
    /// An ambiguous transport failure is reconciled by re-scanning before the
    /// single bounded repeat. When neither the status (already held) nor the
    /// comment (already posted) changed anything, the outcome is
    /// <c>SkippedDuplicate</c>; when the status was applied but the comment
    /// already existed, the post honestly reports <c>Posted</c> with the
    /// existing activity id.
    /// </summary>
    private async Task<TrackerPostResult> PostCommentIdempotentAsync(
        OpenProjectRestClient api,
        OpenProjectWorkSyncOptions options,
        string externalId,
        string body,
        bool statusApplied,
        CancellationToken ct)
    {
        string? duplicate;
        try
        {
            duplicate = await api.FindActivityByExactCommentAsync(
                options, externalId, body, Math.Max(10, options.MaxActivitiesScanned), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            return Failed(
                $"OpenProject comment check for '{externalId}' failed: {Short(ex)}" +
                (statusApplied ? $" (status was applied; the comment is unknown)" : string.Empty));
        }
        if (duplicate is not null)
            return statusApplied
                ? new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: duplicate)
                : new TrackerPostResult(TrackerPostOutcome.SkippedDuplicate,
                    Detail: $"identical comment already posted as activity {duplicate}");

        string? activityId;
        try
        {
            activityId = await api.CreateActivityAsync(options, externalId, body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            // Ambiguous: the POST may have landed despite the failure.
            // Reconcile by re-scanning before any repeat.
            var landed = await TryFindAsync(api, options, externalId, body, ct).ConfigureAwait(false);
            if (landed is not null)
                return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: landed);
            activityId = await TryCreateAsync(api, options, externalId, body, ct).ConfigureAwait(false);
            if (activityId is null)
                return Failed(
                    $"OpenProject comment post for '{externalId}' failed: {Short(ex)}" +
                    (statusApplied ? " (status was applied; the comment is unknown)" : string.Empty));
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: activityId);
        }
        return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: activityId);
    }

    private async Task<string?> TryFindAsync(
        OpenProjectRestClient api,
        OpenProjectWorkSyncOptions options,
        string externalId,
        string body,
        CancellationToken ct)
    {
        try
        {
            return await api.FindActivityByExactCommentAsync(
                options, externalId, body, Math.Max(10, options.MaxActivitiesScanned), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<string?> TryCreateAsync(
        OpenProjectRestClient api,
        OpenProjectWorkSyncOptions options,
        string externalId,
        string body,
        CancellationToken ct)
    {
        try
        {
            return await api.CreateActivityAsync(options, externalId, body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<OpenProjectWorkPackage?> TryReadAsync(
        OpenProjectRestClient api,
        OpenProjectWorkSyncOptions options,
        string externalId,
        CancellationToken ct)
    {
        try
        {
            return await api.GetWorkPackageAsync(options, externalId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<PatchAttempt> TryPatchAsync(
        OpenProjectRestClient api,
        OpenProjectWorkSyncOptions options,
        string externalId,
        string href,
        int lockVersion,
        CancellationToken ct)
    {
        try
        {
            await api.PatchWorkPackageStatusAsync(options, externalId, href, lockVersion, ct)
                .ConfigureAwait(false);
            return new PatchAttempt(Applied: true, Retryable: false, Detail: null);
        }
        catch (OpenProjectApiException ex) when (ex.IsNotFound)
        {
            return new PatchAttempt(
                Applied: false, Retryable: false,
                Detail: $"OpenProject work package '{externalId}' not found");
        }
        catch (OpenProjectApiException ex) when (ex.IsLockConflict)
        {
            return new PatchAttempt(Applied: false, Retryable: true, Detail: null);
        }
        catch (OpenProjectApiException ex) when (ex.IsAuthFailure)
        {
            return new PatchAttempt(
                Applied: false, Retryable: false,
                Detail: $"OpenProject authentication rejected for '{externalId}' ({Short(ex)})");
        }
        catch (OpenProjectApiException ex)
        {
            return new PatchAttempt(
                Applied: false, Retryable: false,
                Detail: $"OpenProject rejected the status for '{externalId}': {Short(ex)}");
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            // Transport failure after the request was sent: possibly applied.
            return new PatchAttempt(Applied: false, Retryable: true, Detail: null);
        }
    }

    /// <summary>
    /// Resolves the declared status name to the host-supplied status href by
    /// exact name match (ordinal-ignore-case). Returns null when the host
    /// knows no such status — the caller reports it, never a guessed href.
    /// </summary>
    private static async Task<string?> ResolveStatusHrefAsync(
        OpenProjectRestClient api,
        OpenProjectWorkSyncOptions options,
        string status,
        CancellationToken ct)
    {
        var statuses = await api.ListStatusesAsync(options, ct).ConfigureAwait(false);
        foreach (var entry in statuses)
        {
            if (string.Equals(entry.Name, status, StringComparison.OrdinalIgnoreCase))
                return entry.Href;
        }
        return null;
    }

    private ExternalWorkItem? ToCandidate(
        OpenProjectWorkPackage package, string codeyBoxProject, OpenProjectWorkSyncOptions options)
    {
        var present = new List<WorkSignal>();
        // Only immutable identifiers are ever emitted: the numeric assignee
        // user id and the admin-defined status name. Display names are
        // user-editable free text and never matched.
        if (!string.IsNullOrWhiteSpace(package.AssigneeUserId))
            present.Add(new WorkSignal(WorkSignalKind.Assignee, package.AssigneeUserId));
        if (!string.IsNullOrWhiteSpace(package.StatusName))
            present.Add(new WorkSignal(WorkSignalKind.Status, package.StatusName));

        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = package.Id,
            ProjectId = new ProjectId(codeyBoxProject),
            Title = string.IsNullOrWhiteSpace(package.Subject) ? package.Id : package.Subject,
            Body = WorkSyncText.Truncate(package.DescriptionRaw, options.MaxIngestedBodyChars),
            PresentSignals = present,
            LastActorLogin = null,
            HasSignal = options.RequiredSignal.IsPresentIn(present),
        };
    }

    /// <summary>
    /// True for failures raised by the transport or the upstream API. Operator
    /// misconfiguration (<see cref="InvalidOperationException"/>: unset
    /// <c>ApiBaseUrl</c>, missing credential, cleartext URL) is deliberately
    /// NOT included — it propagates loudly instead of becoming a <c>Failed</c>
    /// outcome that looks like a transient upstream problem.
    /// </summary>
    private static bool IsUpstreamFailure(Exception ex) =>
        ex is OpenProjectApiException or HttpRequestException or TaskCanceledException;

    private static string Short(Exception ex) => ex switch
    {
        OpenProjectApiException api => api.Message,
        HttpRequestException http => $"request failed: {http.Message}",
        TaskCanceledException => "request timed out",
        _ => ex.Message,
    };

    private static TrackerPostResult Failed(string detail) =>
        new(TrackerPostOutcome.Failed, Detail: detail);

    /// <summary>Outcome of one status PATCH attempt.</summary>
    /// <param name="Applied">True when the status now holds.</param>
    /// <param name="Retryable">True only for ambiguous outcomes (lock conflict
    /// or transport failure after send): the caller re-reads and reconciles
    /// before any single repeat.</param>
    /// <param name="Detail">Failure detail when not applied and not retryable.</param>
    private sealed record PatchAttempt(bool Applied, bool Retryable, string? Detail);
}
