using System.Runtime.CompilerServices;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.JiraWorkSyncPlugin;

/// <summary>
/// CodeyBox work-source / work-tracker plugin for Jira. One project, one
/// plugin: this single instance implements <see cref="IWorkSource"/> (inbound:
/// assignee/label/status-signalled issues become work items) and
/// <see cref="IWorkTracker"/> (outbound: progress, questions, commit/PR links,
/// completion) against the Jira Cloud REST API v3 (with a legacy search
/// fallback for Server/Data Center).
/// <para>Off unless an operator enables it: the plugin loads only when
/// allowlisted AND named in <c>Plugins:Enabled</c>, and it polls/posts nothing
/// until <c>Enabled=true</c> in its own section.</para>
/// <para>Credentials come from the host credential chain (environment
/// variables populated by the operator's vault agent or container secrets);
/// configuration holds only the variable <em>names</em>. API tokens travel as
/// Basic auth; OAuth 3LO access tokens are refreshed as part of the
/// integration (<see cref="JiraTokenProvider"/>).</para>
/// <para>Jira workflows are per-project and operator-defined, so there is no
/// fixed status set: the target status is an explicit operator declaration,
/// and a transition is only executed when it is reachable from the issue's
/// current state. A rejected or unreachable transition is a reported
/// <c>Failed</c> outcome, never a guessed write.</para>
/// </summary>
[CodeyBoxPlugin(
    id: JiraWorkSyncOptions.PluginId,
    displayName: "CodeyBox: Jira Work Sync",
    minHostApiVersion: "1.0")]
public sealed class JiraWorkSyncPlugin
    : IWorkSource, IWorkTracker, IPluginInitializer, IDisposable
{
    private readonly IHttpClientFactory? _httpFactory;
    private readonly TimeProvider _clock;
    private readonly Func<string, string?> _env;
    private readonly IConfigurationSection? _testConfig;

    private IPluginHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private HttpClient? _http;
    private JiraTokenProvider? _tokens;
    private JiraRestClient? _api;
    private readonly bool _ownsHttpClient;
    private readonly object _clientLock = new();
    private bool _disposed;

    /// <summary>Maximum comment body posted upstream (Jira text limit guard).</summary>
    internal const int MaxCommentChars = 32 * 1024;

    private readonly Dictionary<string, string> _keyCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    /// <summary>Production constructor (DI provides the HTTP factory).</summary>
    public JiraWorkSyncPlugin(IHttpClientFactory httpFactory, TimeProvider? clock = null)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _clock = clock ?? TimeProvider.System;
        _env = Environment.GetEnvironmentVariable;
        _ownsHttpClient = true;
    }

    /// <summary>Test constructor: explicit client, config, clock, and environment.</summary>
    internal JiraWorkSyncPlugin(
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
    public string Namespace => JiraWorkSyncOptions.ProviderNamespace;

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
            _logger.LogInformation("Jira work sync is disabled (Enabled=false); polling and posting are no-ops");
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
            _logger.LogWarning("Jira poll skipped: no projects are mapped (ProjectMap is empty)");
            yield break;
        }
        var api = EnsureClients();
        var cap = Math.Max(1, options.MaxItemsPerPoll);
        var count = 0;
        foreach (var (jiraProjectKey, codeyBoxProject) in options.ProjectMap)
        {
            ct.ThrowIfCancellationRequested();
            if (count >= cap)
                yield break;
            if (string.IsNullOrWhiteSpace(jiraProjectKey) || string.IsNullOrWhiteSpace(codeyBoxProject))
                continue;
            IAsyncEnumerable<IReadOnlyList<JiraIssue>> pages;
            try
            {
                pages = api.SearchIssuesPagedAsync(options, jiraProjectKey, ct);
            }
            catch (Exception ex) when (ex is JiraApiException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Jira poll skipped project {Project}: search failed", jiraProjectKey);
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
        var parsed = JiraWebhook.Parse(verifiedBody, options.ProjectMap);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.IssueKey))
            return null;

        if (parsed.Kind == JiraWebhookEventKind.Comment)
        {
            // Comments never ingest directly: only a question-id reply prefix can
            // answer a surfaced question, and content alone never triggers work.
            // A missing project still yields a candidate so the host can attribute
            // loop-guard and reply handling; the ingestion gate skips it.
            return new ExternalWorkItem
            {
                Namespace = Namespace,
                ExternalId = parsed.IssueKey,
                ProjectId = new ProjectId(
                    parsed.ProjectId ?? FirstConfiguredProject(options)),
                Title = parsed.IssueKey,
                Body = Truncate(parsed.Body, options.MaxIngestedBodyChars),
                PresentSignals = [],
                LastActorLogin = parsed.ActorLogin,
                HasSignal = false,
            };
        }

        if (parsed.Kind != JiraWebhookEventKind.Issue
            || string.IsNullOrWhiteSpace(parsed.ProjectId))
            return null;
        var present = ToWorkSignals(parsed.PresentSignals);
        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = parsed.IssueKey,
            ProjectId = new ProjectId(parsed.ProjectId),
            Title = parsed.Title,
            Body = Truncate(parsed.Body, options.MaxIngestedBodyChars),
            PresentSignals = present,
            LastActorLogin = parsed.ActorLogin,
            HasSignal = SignalPresent(options.RequiredSignal, present),
        };
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostProgressAsync(
        TrackerProgressUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "jira work sync is disabled");
        var check = CheckTracked(update.Namespace, update.ExternalId);
        if (check is not null)
            return check;

        var mapping = CurrentMapping(options);
        if (!mapping.TryMap(update.State, out var status) || string.IsNullOrEmpty(status))
            return Unmapped(update.State);

        var api = EnsureClients();
        var transition = await ResolveTransitionAsync(api, options, update.ExternalId, status, ct).ConfigureAwait(false);
        if (transition.FailureDetail is not null || string.IsNullOrEmpty(transition.TransitionId))
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: transition.FailureDetail);

        try
        {
            await api.ExecuteTransitionAsync(options, update.ExternalId, transition.TransitionId, ct).ConfigureAwait(false);
        }
        catch (JiraApiException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Jira rejected the transition to '{status}' for '{update.ExternalId}': {ex.Message}");
        }

        try
        {
            var commentId = await api.CreateCommentAsync(
                options, update.ExternalId, ClipComment(update.Body), ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
        }
        catch (JiraApiException ex)
        {
            // The transition above already landed: report honestly instead of
            // pretending the whole post failed silently.
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Jira transition to '{status}' applied but the comment for '{update.ExternalId}' failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostQuestionAsync(
        TrackerQuestionPost post, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "jira work sync is disabled");
        var check = CheckTracked(post.Namespace, post.ExternalId);
        if (check is not null)
            return check;

        var api = EnsureClients();
        try
        {
            var body = ClipComment($"{post.Body}\n\n{JiraWebhook.QuestionTag(post.QuestionId)}");
            var commentId = await api.CreateCommentAsync(options, post.ExternalId, body, ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
        }
        catch (JiraApiException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Jira question post for '{post.ExternalId}' failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostOutcomeAsync(
        TrackerOutcomeReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "jira work sync is disabled");
        var check = CheckTracked(report.Namespace, report.ExternalId);
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
            var transition = await ResolveTransitionAsync(api, options, report.ExternalId, status, ct).ConfigureAwait(false);
            if (transition.FailureDetail is not null || string.IsNullOrEmpty(transition.TransitionId))
                return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: transition.FailureDetail);
            try
            {
                await api.ExecuteTransitionAsync(options, report.ExternalId, transition.TransitionId, ct).ConfigureAwait(false);
            }
            catch (JiraApiException ex)
            {
                return new TrackerPostResult(TrackerPostOutcome.Failed,
                    Detail: $"Jira rejected the transition to '{status}' for '{report.ExternalId}': {ex.Message}");
            }
        }

        try
        {
            var commentId = await api.CreateCommentAsync(
                options, report.ExternalId, ClipComment(report.Body), ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
        }
        catch (JiraApiException ex)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Jira outcome post for '{report.ExternalId}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Idempotent webhook registration with renewal: keeps the enabled
    /// registration for <see cref="JiraWorkSyncOptions.WebhookUrl"/>,
    /// recreating it when absent and refreshing the 30-day expiry when
    /// present. Never assumes a prior registration persists.
    /// </summary>
    public async Task EnsureWebhookAsync(JiraWorkSyncOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ManageWebhooks || string.IsNullOrWhiteSpace(options.WebhookUrl))
            return;
        ValidateWebhookUrl(options.WebhookUrl);
        var secret = _env(options.WebhookSecretEnvVar);
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogError(
                "Jira webhook management is on but env var '{Var}' is empty; skipping registration",
                options.WebhookSecretEnvVar);
            return;
        }
        var api = EnsureClients();
        try
        {
            var existing = await api.ListWebhooksAsync(options, ct).ConfigureAwait(false);
            var match = existing.FirstOrDefault(w =>
                string.Equals(NormalizeUrl(w.Url), NormalizeUrl(options.WebhookUrl), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                var renewed = await api.RefreshWebhooksAsync(options, [match.Id], ct).ConfigureAwait(false);
                if (renewed.Count > 0)
                {
                    _logger.LogInformation("Jira webhook at {Url} renewed (id {Id})", RedactWebhookUrlForLogging(options.WebhookUrl), match.Id);
                    return;
                }
                await api.DeleteWebhooksAsync(options, [match.Id], ct).ConfigureAwait(false);
            }
            var id = await api.CreateWebhookAsync(options, options.WebhookUrl, ct).ConfigureAwait(false);
            _logger.LogInformation("Jira webhook registered at {Url} (id {Id})", RedactWebhookUrlForLogging(options.WebhookUrl), id);
        }
        catch (Exception ex) when (ex is JiraApiException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Jira webhook registration failed; continuing with polling");
        }
    }

    /// <summary>Removes the registration for <paramref name="webhookUrl"/>. Unknown URLs report false.</summary>
    public async Task<bool> RemoveWebhookAsync(
        JiraWorkSyncOptions options, string webhookUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookUrl);
        var api = EnsureClients();
        var existing = await api.ListWebhooksAsync(options, ct).ConfigureAwait(false);
        var match = existing.FirstOrDefault(w =>
            string.Equals(NormalizeUrl(w.Url), NormalizeUrl(webhookUrl), StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;
        return await api.DeleteWebhooksAsync(options, [match.Id], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies a Jira webhook delivery. Jira Cloud webhooks are unsigned, so
    /// this compares the token in the request query string against the shared
    /// secret from the configured env var. The hosting endpoint MUST call
    /// this over the raw query BEFORE calling
    /// <c>ParseVerifiedWebhookBody</c>.
    /// </summary>
    /// <param name="requestQuery">The raw request query string (with or without a leading <c>?</c>).</param>
    public bool VerifyWebhookDelivery(string? requestQuery)
    {
        var options = CurrentOptions();
        return JiraWebhook.VerifyDelivery(requestQuery, _env(options.WebhookSecretEnvVar) ?? string.Empty);
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

    internal JiraWorkSyncOptions CurrentOptions()
    {
        var section = _host?.ScopedConfig ?? _testConfig;
        return section is null
            ? new JiraWorkSyncOptions()
            : JiraWorkSyncOptions.FromConfiguration(section);
    }

    internal WorkStateMapping CurrentMapping(JiraWorkSyncOptions options)
    {
        try
        {
            return WorkStateMapping.Parse(options.StateMapping);
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Jira state mapping is invalid; reporting every state as unmapped");
            return WorkStateMapping.Empty;
        }
    }

    private JiraRestClient EnsureClients()
    {
        if (_api is not null)
            return _api;
        lock (_clientLock)
        {
            if (_api is not null)
                return _api;
            _http ??= _httpFactory!.CreateClient("jira-worksync");
            _http.Timeout = TimeSpan.FromSeconds(CurrentOptions().TimeoutSeconds);
            _tokens = new JiraTokenProvider(_http, _env, _clock);
            _api = new JiraRestClient(_http, _tokens);
            return _api;
        }
    }

    private ExternalWorkItem? ToCandidate(JiraIssue issue, string codeyBoxProject, JiraWorkSyncOptions options)
    {
        if (string.IsNullOrWhiteSpace(issue.Key))
            return null;
        RememberKey(issue.Key);

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
        if (!string.IsNullOrWhiteSpace(issue.StatusName))
            present.Add(new WorkSignal(WorkSignalKind.Status, issue.StatusName));

        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = issue.Key,
            ProjectId = new ProjectId(codeyBoxProject),
            Title = issue.Title,
            Body = Truncate(issue.Description, options.MaxIngestedBodyChars),
            PresentSignals = present,
            LastActorLogin = issue.LastActorLogin,
            HasSignal = SignalPresent(options.RequiredSignal, present),
        };
    }

    private void RememberKey(string key)
    {
        lock (_cacheLock)
        {
            if (_keyCache.Count >= JiraWorkSyncOptions.MaxIdCacheEntries)
                _keyCache.Clear();
            _keyCache[key] = key;
        }
    }

    private static IReadOnlyList<WorkSignal> ToWorkSignals(
        IReadOnlyList<JiraWebhook.JiraSignals.SignalDatum> data)
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

    internal static bool SignalPresent(WorkSignal required, IReadOnlyList<WorkSignal> present) =>
        !string.IsNullOrWhiteSpace(required.Value)
        && present.Any(s => s.Kind == required.Kind
            && string.Equals(s.Value, required.Value, StringComparison.OrdinalIgnoreCase));

    private TrackerPostResult? CheckTracked(string @namespace, string externalId)
    {
        if (!string.Equals(@namespace, Namespace, StringComparison.OrdinalIgnoreCase))
            return new TrackerPostResult(TrackerPostOutcome.NotTracked,
                Detail: $"no external id under '{Namespace}'");
        if (string.IsNullOrWhiteSpace(externalId))
            return new TrackerPostResult(TrackerPostOutcome.NotTracked,
                Detail: $"no external id under '{Namespace}'");
        return null;
    }

    private static TrackerPostResult Unmapped(WorkItemState state)
    {
        var unmapped = new UnmappedWorkItemState(state);
        return new TrackerPostResult(TrackerPostOutcome.UnmappedState, Detail: unmapped.Describe());
    }

    private sealed record TransitionResolution(string? TransitionId, string? FailureDetail);

    /// <summary>
    /// Resolves a declared status name to a transition that is reachable from
    /// the issue's current state. Matching is exact (ordinal-ignore-case) on
    /// the transition name or its target status — never substring, never a
    /// free-form status write. An unreachable target is a reported failure.
    /// </summary>
    private async Task<TransitionResolution> ResolveTransitionAsync(
        JiraRestClient api,
        JiraWorkSyncOptions options,
        string issueKey,
        string status,
        CancellationToken ct)
    {
        IReadOnlyList<JiraTransition> transitions;
        try
        {
            transitions = await api.ListTransitionsAsync(options, issueKey, ct).ConfigureAwait(false);
        }
        catch (JiraApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new TransitionResolution(null, $"Jira issue '{issueKey}' not found");
        }
        var match = transitions.FirstOrDefault(t =>
            string.Equals(t.Name, status, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.ToName, status, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return new TransitionResolution(null,
                $"Jira issue '{issueKey}' has no transition to '{status}' from its current state; " +
                "the target is not reachable so nothing was written");
        }
        return new TransitionResolution(match.Id, null);
    }

    private static string FirstConfiguredProject(JiraWorkSyncOptions options) =>
        options.ProjectMap.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "jira";

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    private static string ClipComment(string body)
    {
        if (string.IsNullOrEmpty(body))
            throw new ArgumentException("tracker body must not be empty", nameof(body));
        return body.Length <= MaxCommentChars ? body : body[..MaxCommentChars];
    }

    private static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    /// <summary>
    /// Strips the query and fragment from a webhook URL before logging, so the
    /// shared-secret delivery token in the query string never reaches the log sink.
    /// </summary>
    internal static string RedactWebhookUrlForLogging(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "(empty)";
        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return uri.GetLeftPart(UriPartial.Path);
        var cut = trimmed.IndexOfAny(['?', '#']);
        return cut < 0 ? trimmed : trimmed[..cut];
    }

    /// <summary>
    /// Validates the operator's public webhook URL. Delegates SSRF policy to
    /// <see cref="Validation.ValidateWebhookUrl"/> (hostname blocklist,
    /// IP-literal and DNS-resolved private/reserved rejection) and adds the
    /// Jira-only requirements that delivery use https without embedded
    /// credentials and carry the shared-secret token this integration
    /// authenticates with.
    /// </summary>
    internal static void ValidateWebhookUrl(string url)
    {
        Validation.ValidateWebhookUrl(url, "Jira WebhookUrl");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Jira WebhookUrl must use https://.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Jira WebhookUrl must not embed credentials.");
        if (string.IsNullOrEmpty(uri.Query)
            || !uri.Query.Contains(
                JiraWebhook.TokenQueryParameter + "=", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Jira WebhookUrl must carry the shared-secret delivery token " +
                "(e.g. https://host/webhooks/jira?token=<unguessable>); " +
                "Jira Cloud webhooks are unsigned and the token is the delivery authentication.");
    }
}
