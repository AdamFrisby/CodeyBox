using System.Runtime.CompilerServices;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.PlaneWorkSyncPlugin;

/// <summary>
/// CodeyBox work-source / work-tracker plugin for Plane. One project, one
/// plugin: this single instance implements <see cref="IWorkSource"/> (inbound:
/// label/assignee/status-signalled work items become work items) and
/// <see cref="IWorkTracker"/> (outbound: progress, questions, commit/PR links,
/// completion) against the Plane REST API.
/// <para>Off unless an operator enables it: the plugin loads only when
/// allowlisted AND named in <c>Plugins:Enabled</c>, and it polls/posts nothing
/// until <c>Enabled=true</c> in its own section.</para>
/// <para>Credentials come from the host credential chain (environment
/// variables populated by the operator's vault agent or container secrets);
/// configuration holds only the variable <em>names</em>. Static personal access
/// tokens travel as <c>X-API-Key</c>; OAuth access tokens are refreshed as part
/// of the integration (<see cref="PlaneTokenProvider"/>) and travel as
/// <c>Authorization: Bearer</c>.</para>
/// <para>On Plane's agent-integration support: installing CodeyBox as a Plane
/// agent (an OAuth app with mentions enabled) creates a bot user operators can
/// @mention. Mentions arrive as comment <em>content</em>, which can never
/// trigger ingestion — so the ingestion signal stays label/assignee/status.
/// The bot's user id doubles as the <c>Assignee</c> signal value and as a
/// <c>ServiceLogins</c> entry for the loop guard.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PlaneWorkSyncOptions.PluginId,
    displayName: "CodeyBox: Plane Work Sync",
    minHostApiVersion: "1.0")]
public sealed class PlaneWorkSyncPlugin
    : IWorkSource, IWorkTracker, IPluginInitializer, IDisposable
{
    private readonly IHttpClientFactory? _httpFactory;
    private readonly TimeProvider _clock;
    private readonly Func<string, string?> _env;
    private readonly IConfigurationSection? _testConfig;

    private IPluginHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private HttpClient? _http;
    private PlaneTokenProvider? _tokens;
    private PlaneRestClient? _api;
    private readonly bool _ownsHttpClient;
    private readonly object _clientLock = new();
    private bool _disposed;

    private readonly Dictionary<string, string> _uuidCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _projectIdentifierCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    /// <summary>Production constructor (DI provides the HTTP factory).</summary>
    public PlaneWorkSyncPlugin(IHttpClientFactory httpFactory, TimeProvider? clock = null)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _clock = clock ?? TimeProvider.System;
        _env = Environment.GetEnvironmentVariable;
        _ownsHttpClient = true;
    }

    /// <summary>Test constructor: explicit client, config, clock, and environment.</summary>
    internal PlaneWorkSyncPlugin(
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
    public string Namespace => PlaneWorkSyncOptions.ProviderNamespace;

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
        EnsureClients();
        var options = CurrentOptions();
        if (!options.Enabled)
        {
            _logger.LogInformation("Plane work sync is disabled (Enabled=false); polling and posting are no-ops");
            return Task.CompletedTask;
        }
        if (options.ManageWebhooks)
        {
            return EnsureWebhookAsync(options, ct);
        }
        return Task.CompletedTask;
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
            _logger.LogWarning("Plane poll skipped: no projects are mapped (ProjectMap is empty)");
            yield break;
        }
        var api = EnsureClients();
        var cap = Math.Max(1, options.MaxItemsPerPoll);
        var count = 0;
        foreach (var (planeProjectId, codeyBoxProject) in options.ProjectMap)
        {
            ct.ThrowIfCancellationRequested();
            if (count >= cap)
                yield break;
            if (string.IsNullOrWhiteSpace(planeProjectId) || string.IsNullOrWhiteSpace(codeyBoxProject))
                continue;
            string identifier;
            try
            {
                identifier = await GetProjectIdentifierAsync(api, options, planeProjectId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PlaneApiException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Plane poll skipped project {Project}: cannot read project", planeProjectId);
                continue;
            }
            IAsyncEnumerable<IReadOnlyList<PlaneIssue>> pages;
            try
            {
                pages = api.ListIssuesPagedAsync(options, planeProjectId, identifier, ct);
            }
            catch (Exception ex) when (ex is PlaneApiException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Plane poll skipped project {Project}: list failed", planeProjectId);
                continue;
            }
            await foreach (var page in pages.ConfigureAwait(false))
            {
                foreach (var issue in page)
                {
                    ct.ThrowIfCancellationRequested();
                    if (count >= cap)
                        yield break;
                    var candidate = ToCandidate(issue, codeyBoxProject, options);
                    if (candidate is null)
                        continue;
                    if (!string.Equals(candidate.Namespace, Namespace, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"poll yielded namespace '{candidate.Namespace}' from source '{Namespace}'");
                    count++;
                    yield return candidate;
                }
            }
        }
    }

    /// <inheritdoc />
    public ExternalWorkItem? ParseVerifiedWebhookBody(string verifiedBody)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifiedBody);
        var options = CurrentOptions();
        var parsed = PlaneWebhook.Parse(verifiedBody, options.ProjectMap);
        if (parsed is null)
            return null;

        if (parsed.Kind == PlaneWebhookEventKind.Comment)
        {
            // Comments never ingest directly: only a question-id reply prefix can
            // answer a surfaced question, and content alone never triggers work.
            // A missing key still yields a candidate so the host can attribute
            // loop-guard and reply handling; the ingestion gate skips it.
            return new ExternalWorkItem
            {
                Namespace = Namespace,
                ExternalId = string.IsNullOrWhiteSpace(parsed.Key) ? "plane-comment" : parsed.Key,
                ProjectId = new ProjectId(
                    parsed.CodeyBoxProjectId ?? FirstConfiguredProject(options)),
                Title = string.IsNullOrWhiteSpace(parsed.Key) ? "plane-comment" : parsed.Key,
                Body = WorkSyncText.Truncate(parsed.Body, options.MaxIngestedBodyChars),
                PresentSignals = [],
                LastActorLogin = parsed.ActorLogin,
                HasSignal = false,
            };
        }

        if (parsed.Kind != PlaneWebhookEventKind.Issue
            || string.IsNullOrWhiteSpace(parsed.Key)
            || string.IsNullOrWhiteSpace(parsed.CodeyBoxProjectId))
            return null;
        var present = ToWorkSignals(parsed.PresentSignals);
        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = parsed.Key,
            ProjectId = new ProjectId(parsed.CodeyBoxProjectId),
            Title = parsed.Title,
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
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "plane work sync is disabled");
        var check = this.CheckTracked(update.Namespace, update.ExternalId);
        if (check is not null)
            return check;

        var mapping = WorkStateMapping.ParseOrEmpty(options.StateMapping, _logger, "Plane");
        if (!mapping.TryMap(update.State, out var status) || string.IsNullOrEmpty(status))
            return TrackerPostResult.UnmappedFor(update.State);

        var target = await ResolveTargetAsync(options, update.ExternalId, ct).ConfigureAwait(false);
        if (target.FailureDetail is not null || string.IsNullOrEmpty(target.IssueUuid))
            return target.ToResult(update.ExternalId);
        var (api, projectId, issueUuid) = target;

        try
        {
            var stateId = await ResolveStateIdAsync(api, options, projectId, status, ct).ConfigureAwait(false);
            if (stateId is null)
                return new TrackerPostResult(TrackerPostOutcome.Failed,
                    Detail: $"Plane state '{status}' not found on issue '{update.ExternalId}'");
            await api.UpdateIssueStateAsync(options, projectId, issueUuid, stateId, ct).ConfigureAwait(false);
        }
        catch (PlaneApiException ex) when (ex.IsCapabilityGap)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Plane instance cannot move state for '{update.ExternalId}': {ex.Message}");
        }

        try
        {
            var commentId = await api.CreateCommentAsync(
                options, projectId, issueUuid, WorkSyncText.ClipComment(update.Body, update.WorkItemId), ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
        }
        catch (PlaneApiException ex) when (ex.IsCapabilityGap)
        {
            // The state move above already landed: report honestly instead of
            // pretending the whole post failed silently.
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Plane instance cannot post comments for '{update.ExternalId}': {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostQuestionAsync(
        TrackerQuestionPost post, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "plane work sync is disabled");
        var check = this.CheckTracked(post.Namespace, post.ExternalId);
        if (check is not null)
            return check;

        var target = await ResolveTargetAsync(options, post.ExternalId, ct).ConfigureAwait(false);
        if (target.FailureDetail is not null || string.IsNullOrEmpty(target.IssueUuid))
            return target.ToResult(post.ExternalId);
        var (api, projectId, issueUuid) = target;

        try
        {
            var body = WorkSyncText.ClipComment($"{post.Body}\n\n{PlaneWebhook.QuestionTag(post.QuestionId)}", post.WorkItemId);
            var commentId = await api.CreateCommentAsync(options, projectId, issueUuid, body, ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
        }
        catch (PlaneApiException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Plane question post for '{post.ExternalId}' failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostOutcomeAsync(
        TrackerOutcomeReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "plane work sync is disabled");
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

        var target = await ResolveTargetAsync(options, report.ExternalId, ct).ConfigureAwait(false);
        if (target.FailureDetail is not null || string.IsNullOrEmpty(target.IssueUuid))
            return target.ToResult(report.ExternalId);
        var (api, projectId, issueUuid) = target;

        if (!string.IsNullOrEmpty(status))
        {
            try
            {
                var stateId = await ResolveStateIdAsync(api, options, projectId, status, ct).ConfigureAwait(false);
                if (stateId is null)
                    return new TrackerPostResult(TrackerPostOutcome.Failed,
                        Detail: $"Plane state '{status}' not found on issue '{report.ExternalId}'");
                await api.UpdateIssueStateAsync(options, projectId, issueUuid, stateId, ct).ConfigureAwait(false);
            }
            catch (PlaneApiException ex) when (ex.IsCapabilityGap)
            {
                return new TrackerPostResult(TrackerPostOutcome.Failed,
                    Detail: $"Plane instance cannot move state for '{report.ExternalId}': {ex.Message}");
            }
        }

        try
        {
            var commentId = await api.CreateCommentAsync(
                options, projectId, issueUuid, WorkSyncText.ClipComment(report.Body, report.WorkItemId), ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
        }
        catch (PlaneApiException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Plane outcome post for '{report.ExternalId}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Best-effort webhook registration: keeps an active registration for
    /// <see cref="PlaneWorkSyncOptions.WebhookUrl"/>, recreating it when
    /// absent. Plane webhooks are workspace-level and normally created in the
    /// UI — when the instance lacks the webhooks API the plugin logs and
    /// continues with polling rather than erroring. Never assumes a prior
    /// registration persists.
    /// </summary>
    public async Task EnsureWebhookAsync(PlaneWorkSyncOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ManageWebhooks || string.IsNullOrWhiteSpace(options.WebhookUrl))
            return;
        ValidateWebhookUrl(options.WebhookUrl);
        var secret = _env(options.WebhookSecretEnvVar);
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogError(
                "Plane webhook management is on but env var '{Var}' is empty; skipping registration",
                options.WebhookSecretEnvVar);
            return;
        }
        var api = EnsureClients();
        try
        {
            var existing = await api.ListWebhooksAsync(options, ct).ConfigureAwait(false);
            var match = existing.FirstOrDefault(w =>
                string.Equals(NormalizeUrl(w.Url), NormalizeUrl(options.WebhookUrl), StringComparison.OrdinalIgnoreCase));
            if (match is not null && match.Enabled)
            {
                _logger.LogInformation("Plane webhook already registered at {Url}", options.WebhookUrl);
                return;
            }
            if (match is not null)
                await api.DeleteWebhookAsync(options, match.Id, ct).ConfigureAwait(false);
            var id = await api.CreateWebhookAsync(options, options.WebhookUrl, secret, ct).ConfigureAwait(false);
            _logger.LogInformation("Plane webhook registered at {Url} (id {Id})", options.WebhookUrl, id);
        }
        catch (Exception ex) when (ex is PlaneApiException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Plane webhook registration unavailable; continuing with polling");
        }
    }

    /// <summary>Removes the registration for <paramref name="webhookUrl"/>. Unknown URLs report false.</summary>
    public async Task<bool> RemoveWebhookAsync(
        PlaneWorkSyncOptions options, string webhookUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookUrl);
        var api = EnsureClients();
        try
        {
            var existing = await api.ListWebhooksAsync(options, ct).ConfigureAwait(false);
            var match = existing.FirstOrDefault(w =>
                string.Equals(NormalizeUrl(w.Url), NormalizeUrl(webhookUrl), StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return false;
            return await api.DeleteWebhookAsync(options, match.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaneApiException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Plane webhook removal unavailable; assuming already absent");
            return false;
        }
    }

    /// <summary>
    /// Verifies a Plane webhook delivery over the raw bytes. Thin wrapper over
    /// <see cref="PlaneWebhook.VerifySignature"/> reading the secret from the
    /// configured env var, for hosting endpoints.
    /// </summary>
    public bool VerifyWebhookDelivery(byte[] rawBody, string? signatureHeader)
    {
        var options = CurrentOptions();
        return PlaneWebhook.VerifySignature(rawBody, signatureHeader, _env(options.WebhookSecretEnvVar) ?? string.Empty);
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

    internal PlaneWorkSyncOptions CurrentOptions()
    {
        var section = _host?.ScopedConfig ?? _testConfig;
        return section is null
            ? new PlaneWorkSyncOptions()
            : PlaneWorkSyncOptions.FromConfiguration(section);
    }

    private PlaneRestClient EnsureClients()
    {
        if (_api is not null)
            return _api;
        lock (_clientLock)
        {
            if (_api is not null)
                return _api;
            _http ??= _httpFactory!.CreateClient("plane-worksync");
            _http.Timeout = TimeSpan.FromSeconds(CurrentOptions().TimeoutSeconds);
            _tokens = new PlaneTokenProvider(_http, _env, _clock);
            _api = new PlaneRestClient(_http, _tokens);
            return _api;
        }
    }

    private ExternalWorkItem? ToCandidate(PlaneIssue issue, string codeyBoxProject, PlaneWorkSyncOptions options)
    {
        if (string.IsNullOrWhiteSpace(issue.Key))
            return null;

        var present = new List<WorkSignal>();
        foreach (var label in issue.LabelNames)
        {
            if (!string.IsNullOrWhiteSpace(label))
                present.Add(new WorkSignal(WorkSignalKind.Label, label));
        }
        foreach (var assignee in issue.AssigneeLogins)
        {
            if (!string.IsNullOrWhiteSpace(assignee))
                present.Add(new WorkSignal(WorkSignalKind.Assignee, assignee));
        }
        if (!string.IsNullOrWhiteSpace(issue.StateName))
            present.Add(new WorkSignal(WorkSignalKind.Status, issue.StateName));
        if (!string.IsNullOrWhiteSpace(issue.StateGroup))
            present.Add(new WorkSignal(WorkSignalKind.Status, issue.StateGroup));

        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = issue.Key,
            ProjectId = new ProjectId(codeyBoxProject),
            Title = issue.Title,
            Body = WorkSyncText.Truncate(issue.Description, options.MaxIngestedBodyChars),
            PresentSignals = present,
            LastActorLogin = issue.LastActorLogin,
            HasSignal = options.RequiredSignal.IsPresentIn(present),
        };
    }

    private static IReadOnlyList<WorkSignal> ToWorkSignals(
        IReadOnlyList<PlaneWebhook.PlaneSignals.SignalDatum> data)
    {
        var signals = new List<WorkSignal>(data.Count);
        foreach (var datum in data)
        {
            if (Enum.TryParse<WorkSignalKind>(datum.Kind, ignoreCase: true, out var kind)
                && !string.IsNullOrWhiteSpace(datum.Value))
                signals.Add(new WorkSignal(kind, datum.Value));
        }
        return signals;
    }

    /// <summary>
    /// Resolves a human key (<c>WEB-123</c>) to the project UUID + issue UUID pair
    /// mutations need, by matching the key's project prefix against cached project
    /// identifiers and scanning that project's issues. Always returns a target:
    /// check <see cref="PlaneTarget.FailureDetail"/> (or an empty
    /// <see cref="PlaneTarget.IssueUuid"/>) before mutating.
    /// </summary>
    private async Task<PlaneTarget> ResolveTargetAsync(
        PlaneWorkSyncOptions options, string key, CancellationToken ct)
    {
        var api = EnsureClients();
        foreach (var (planeProjectId, _) in options.ProjectMap)
        {
            ct.ThrowIfCancellationRequested();
            string identifier;
            try
            {
                identifier = await GetProjectIdentifierAsync(api, options, planeProjectId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PlaneApiException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogDebug(ex, "Plane resolve skipped project {Project}", planeProjectId);
                continue;
            }
            if (string.IsNullOrWhiteSpace(identifier))
                continue;
            if (!key.StartsWith(identifier + "-", StringComparison.OrdinalIgnoreCase))
                continue;
            string? uuid;
            lock (_cacheLock)
            {
                _uuidCache.TryGetValue(key, out uuid);
            }
            if (string.IsNullOrEmpty(uuid))
            {
                try
                {
                    uuid = await api.ResolveIssueUuidAsync(options, planeProjectId, identifier, key, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is PlaneApiException or HttpRequestException or TaskCanceledException)
                {
                    return new PlaneTarget(api, planeProjectId, string.Empty, $"Plane lookup for '{key}' failed: {ex.Message}");
                }
                if (!string.IsNullOrEmpty(uuid))
                {
                    lock (_cacheLock)
                    {
                        if (_uuidCache.Count >= PlaneWorkSyncOptions.MaxIdCacheEntries)
                            _uuidCache.Clear();
                        _uuidCache[key] = uuid;
                    }
                }
            }
            if (string.IsNullOrEmpty(uuid))
                return new PlaneTarget(api, planeProjectId, string.Empty, $"Plane issue '{key}' not found");
            return new PlaneTarget(api, planeProjectId, uuid, null);
        }
        return new PlaneTarget(api, string.Empty, string.Empty, $"Plane issue '{key}' matches no configured project");
    }

    private async Task<string> GetProjectIdentifierAsync(
        PlaneRestClient api, PlaneWorkSyncOptions options, string projectId, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_projectIdentifierCache.TryGetValue(projectId, out var cached))
                return cached;
        }
        var identifier = await api.GetProjectIdentifierAsync(options, projectId, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(identifier))
        {
            lock (_cacheLock)
            {
                if (_projectIdentifierCache.Count >= PlaneWorkSyncOptions.MaxIdCacheEntries)
                    _projectIdentifierCache.Clear();
                _projectIdentifierCache[projectId] = identifier;
            }
        }
        return identifier;
    }

    private async Task<string?> ResolveStateIdAsync(
        PlaneRestClient api,
        PlaneWorkSyncOptions options,
        string projectId,
        string status,
        CancellationToken ct)
    {
        var states = await api.ListStatesAsync(options, projectId, ct).ConfigureAwait(false);
        return states.FirstOrDefault(s =>
                string.Equals(s.Name, status, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Group, status, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static string FirstConfiguredProject(PlaneWorkSyncOptions options) =>
        options.ProjectMap.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "plane";


    private static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    /// <summary>
    /// Validates the operator's public webhook URL. Delegates SSRF policy to
    /// <see cref="Validation.ValidateWebhookUrl"/> (hostname blocklist,
    /// IP-literal and DNS-resolved private/reserved rejection) and adds the
    /// Plane-only requirement that delivery use https (no credentials).
    /// </summary>
    internal static void ValidateWebhookUrl(string url)
    {
        Validation.ValidateWebhookUrl(url, "Plane WebhookUrl");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Plane WebhookUrl must use https://.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Plane WebhookUrl must not embed credentials.");
    }

    /// <summary>Resolved mutation target: the client, project, and issue to write to, or a failure detail.</summary>
    internal sealed record PlaneTarget(
        PlaneRestClient Api, string ProjectId, string IssueUuid, string? FailureDetail)
    {
        public TrackerPostResult ToResult(string key) =>
            new(TrackerPostOutcome.Failed, Detail: FailureDetail ?? $"Plane issue '{key}' not found");

        public void Deconstruct(out PlaneRestClient Api, out string ProjectId, out string IssueUuid)
        {
            Api = this.Api;
            ProjectId = this.ProjectId;
            IssueUuid = this.IssueUuid;
        }
    }
}
