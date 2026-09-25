using System.Runtime.CompilerServices;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.LinearWorkSyncPlugin;

/// <summary>
/// CodeyBox work-source / work-tracker plugin for Linear. One project, one
/// plugin: this single instance implements <see cref="IWorkSource"/> (inbound:
/// assignee/label/status-signalled issues become work items) and
/// <see cref="IWorkTracker"/> (outbound: progress, questions, commit/PR links,
/// completion) against the Linear GraphQL API.
/// <para>Off unless an operator enables it: the plugin loads only when
/// allowlisted AND named in <c>Plugins:Enabled</c>, and it polls/posts nothing
/// until <c>Enabled=true</c> in its own section.</para>
/// <para>Credentials come from the host credential chain (environment
/// variables populated by the operator's vault agent or container secrets);
/// configuration holds only the variable <em>names</em>. OAuth access tokens
/// are refreshed as part of the integration (<see cref="LinearTokenProvider"/>).</para>
/// </summary>
[CodeyBoxPlugin(
    id: LinearWorkSyncOptions.PluginId,
    displayName: "CodeyBox: Linear Work Sync",
    minHostApiVersion: "1.0")]
public sealed class LinearWorkSyncPlugin
    : IWorkSource, IWorkTracker, IPluginInitializer, IDisposable
{
    private readonly IHttpClientFactory? _httpFactory;
    private readonly TimeProvider _clock;
    private readonly Func<string, string?> _env;
    private readonly IConfigurationSection? _testConfig;

    private IPluginHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private HttpClient? _http;
    private LinearTokenProvider? _tokens;
    private LinearGraphQlClient? _api;
    private readonly bool _ownsHttpClient;
    private readonly object _clientLock = new();
    private bool _disposed;

    private readonly Dictionary<string, string> _issueIdCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    /// <summary>Production constructor (DI provides the HTTP factory).</summary>
    public LinearWorkSyncPlugin(IHttpClientFactory httpFactory, TimeProvider? clock = null)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _clock = clock ?? TimeProvider.System;
        _env = Environment.GetEnvironmentVariable;
        _ownsHttpClient = true;
    }

    /// <summary>Test constructor: explicit client, config, clock, and environment.</summary>
    internal LinearWorkSyncPlugin(
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
    public string Namespace => LinearWorkSyncOptions.ProviderNamespace;

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
            _logger.LogInformation("Linear work sync is disabled (Enabled=false); polling and posting are no-ops");
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
        var api = EnsureClients();
        var cap = Math.Max(1, options.MaxItemsPerPoll);
        var count = 0;
        await foreach (var page in api.ListIssuesPagedAsync(options, options.PollPageSize, ct).ConfigureAwait(false))
        {
            foreach (var issue in page)
            {
                ct.ThrowIfCancellationRequested();
                if (count >= cap)
                    yield break;
                var candidate = ToCandidate(issue, options);
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

    /// <inheritdoc />
    public ExternalWorkItem? ParseVerifiedWebhookBody(string verifiedBody)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifiedBody);
        var options = CurrentOptions();
        var parsed = LinearWebhook.Parse(verifiedBody, options.TeamProjectMap);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Identifier))
            return null;

        if (parsed.Kind == LinearWebhookEventKind.Comment)
        {
            return new ExternalWorkItem
            {
                Namespace = Namespace,
                ExternalId = parsed.Identifier,
                ProjectId = new ProjectId(parsed.ProjectId ?? FirstConfiguredProject(options)),
                Title = string.IsNullOrWhiteSpace(parsed.Title) ? parsed.Identifier : parsed.Title,
                Body = WorkSyncText.Truncate(parsed.Body, options.MaxIngestedBodyChars),
                PresentSignals = [],
                LastActorLogin = parsed.ActorLogin,
                HasSignal = false,
            };
        }

        if (string.IsNullOrWhiteSpace(parsed.ProjectId))
            return null;
        var present = ToWorkSignals(parsed.PresentSignals);
        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = parsed.Identifier,
            ProjectId = new ProjectId(parsed.ProjectId),
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
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "linear work sync is disabled");
        var check = this.CheckTracked(update.Namespace, update.ExternalId);
        if (check is not null)
            return check;

        var mapping = WorkStateMapping.ParseOrEmpty(options.StateMapping, _logger, "Linear");
        if (!mapping.TryMap(update.State, out var status) || string.IsNullOrEmpty(status))
            return TrackerPostResult.UnmappedFor(update.State);

        var api = EnsureClients();
        var issueId = await ResolveIssueIdAsync(api, options, update.ExternalId, ct).ConfigureAwait(false);
        if (issueId is null)
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Linear issue '{update.ExternalId}' not found");

        var stateId = await ResolveStateIdAsync(api, options, issueId, status, ct).ConfigureAwait(false);
        if (stateId is null)
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Linear workflow state '{status}' not found on issue '{update.ExternalId}'");

        await api.UpdateIssueStateAsync(options, issueId, stateId, ct).ConfigureAwait(false);
        var commentId = await api.CreateCommentAsync(
            options, issueId, WorkSyncText.ClipComment(update.Body, update.WorkItemId), ct).ConfigureAwait(false);
        return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostQuestionAsync(
        TrackerQuestionPost post, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "linear work sync is disabled");
        var check = this.CheckTracked(post.Namespace, post.ExternalId);
        if (check is not null)
            return check;

        var api = EnsureClients();
        var issueId = await ResolveIssueIdAsync(api, options, post.ExternalId, ct).ConfigureAwait(false);
        if (issueId is null)
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Linear issue '{post.ExternalId}' not found");

        var body = WorkSyncText.ClipComment($"{post.Body}\n\n{LinearWebhook.QuestionTag(post.QuestionId)}", post.WorkItemId);
        var commentId = await api.CreateCommentAsync(options, issueId, body, ct).ConfigureAwait(false);
        return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostOutcomeAsync(
        TrackerOutcomeReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "linear work sync is disabled");
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
        var issueId = await ResolveIssueIdAsync(api, options, report.ExternalId, ct).ConfigureAwait(false);
        if (issueId is null)
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Linear issue '{report.ExternalId}' not found");

        if (!string.IsNullOrEmpty(status))
        {
            var stateId = await ResolveStateIdAsync(api, options, issueId, status, ct).ConfigureAwait(false);
            if (stateId is null)
                return new TrackerPostResult(TrackerPostOutcome.Failed,
                    Detail: $"Linear workflow state '{status}' not found on issue '{report.ExternalId}'");
            await api.UpdateIssueStateAsync(options, issueId, stateId, ct).ConfigureAwait(false);
        }

        var commentId = await api.CreateCommentAsync(
            options, issueId, WorkSyncText.ClipComment(report.Body, report.WorkItemId), ct).ConfigureAwait(false);
        return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
    }

    /// <summary>
    /// Idempotent webhook registration: keeps the enabled registration for
    /// <see cref="LinearWorkSyncOptions.WebhookUrl"/>, recreating it when
    /// absent or disabled. Never assumes a prior registration persists.
    /// </summary>
    public async Task EnsureWebhookAsync(LinearWorkSyncOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ManageWebhooks || string.IsNullOrWhiteSpace(options.WebhookUrl))
            return;
        ValidateWebhookUrl(options.WebhookUrl);
        var secret = _env(options.WebhookSecretEnvVar);
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogError(
                "Linear webhook management is on but env var '{Var}' is empty; skipping registration",
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
                _logger.LogInformation("Linear webhook already registered at {Url}", options.WebhookUrl);
                return;
            }
            if (match is not null)
                await api.DeleteWebhookAsync(options, match.Id, ct).ConfigureAwait(false);
            var id = await api.CreateWebhookAsync(options, options.WebhookUrl, secret, ct).ConfigureAwait(false);
            _logger.LogInformation("Linear webhook registered at {Url} (id {Id})", options.WebhookUrl, id);
        }
        catch (Exception ex) when (ex is LinearApiException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Linear webhook registration failed; continuing with polling");
        }
    }

    /// <summary>Removes the registration for <paramref name="webhookUrl"/>. Unknown URLs report false.</summary>
    public async Task<bool> RemoveWebhookAsync(
        LinearWorkSyncOptions options, string webhookUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookUrl);
        var api = EnsureClients();
        var existing = await api.ListWebhooksAsync(options, ct).ConfigureAwait(false);
        var match = existing.FirstOrDefault(w =>
            string.Equals(NormalizeUrl(w.Url), NormalizeUrl(webhookUrl), StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;
        return await api.DeleteWebhookAsync(options, match.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies a Linear webhook delivery over the raw bytes. Thin wrapper over
    /// <see cref="LinearWebhook.VerifySignature"/> reading the secret from the
    /// configured env var, for hosting endpoints.
    /// </summary>
    public bool VerifyWebhookDelivery(byte[] rawBody, string? signatureHeader)
    {
        var options = CurrentOptions();
        return LinearWebhook.VerifySignature(rawBody, signatureHeader, _env(options.WebhookSecretEnvVar) ?? string.Empty);
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

    internal LinearWorkSyncOptions CurrentOptions()
    {
        var section = _host?.ScopedConfig ?? _testConfig;
        return section is null
            ? new LinearWorkSyncOptions()
            : LinearWorkSyncOptions.FromConfiguration(section);
    }

    private LinearGraphQlClient EnsureClients()
    {
        if (_api is not null)
            return _api;
        lock (_clientLock)
        {
            if (_api is not null)
                return _api;
            _http ??= _httpFactory!.CreateClient("linear-worksync");
            _http.Timeout = TimeSpan.FromSeconds(CurrentOptions().TimeoutSeconds);
            _tokens = new LinearTokenProvider(_http, _env, _clock);
            _api = new LinearGraphQlClient(_http, _tokens);
            return _api;
        }
    }

    private ExternalWorkItem? ToCandidate(LinearIssue issue, LinearWorkSyncOptions options)
    {
        if (string.IsNullOrWhiteSpace(issue.Identifier))
            return null;
        if (!options.TeamProjectMap.TryGetValue(issue.TeamKey, out var projectId)
            || string.IsNullOrWhiteSpace(projectId))
            return null;

        var present = new List<WorkSignal>();
        foreach (var label in issue.LabelNames)
        {
            if (!string.IsNullOrWhiteSpace(label))
                present.Add(new WorkSignal(WorkSignalKind.Label, label));
        }
        foreach (var assignee in new[] { issue.AssigneeId, issue.AssigneeEmail, issue.AssigneeName })
        {
            if (!string.IsNullOrWhiteSpace(assignee))
                present.Add(new WorkSignal(WorkSignalKind.Assignee, assignee));
        }
        if (!string.IsNullOrWhiteSpace(issue.StateName))
            present.Add(new WorkSignal(WorkSignalKind.Status, issue.StateName));

        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = issue.Identifier,
            ProjectId = new ProjectId(projectId),
            Title = issue.Title,
            Body = WorkSyncText.Truncate(issue.Description, options.MaxIngestedBodyChars),
            PresentSignals = present,
            LastActorLogin = null,
            HasSignal = options.RequiredSignal.IsPresentIn(present),
        };
    }

    private static IReadOnlyList<WorkSignal> ToWorkSignals(
        IReadOnlyList<LinearWebhook.LinearSignals.SignalDatum> data)
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

    private async Task<string?> ResolveIssueIdAsync(
        LinearGraphQlClient api, LinearWorkSyncOptions options, string identifier, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_issueIdCache.TryGetValue(identifier, out var cached))
                return cached;
        }
        var resolved = await api.ResolveIssueIdAsync(options, identifier, ct).ConfigureAwait(false);
        if (resolved is null)
            return null;
        lock (_cacheLock)
        {
            if (_issueIdCache.Count >= LinearWorkSyncOptions.MaxIdCacheEntries)
                _issueIdCache.Clear();
            _issueIdCache[identifier] = resolved;
        }
        return resolved;
    }

    private async Task<string?> ResolveStateIdAsync(
        LinearGraphQlClient api,
        LinearWorkSyncOptions options,
        string issueId,
        string status,
        CancellationToken ct)
    {
        var states = await api.ListWorkflowStatesAsync(options, issueId, ct).ConfigureAwait(false);
        return states.FirstOrDefault(s =>
            string.Equals(s.Name, status, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static string FirstConfiguredProject(LinearWorkSyncOptions options) =>
        options.TeamProjectMap.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "linear";


    private static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    /// <summary>
    /// Validates the operator's public webhook URL. Delegates SSRF policy to
    /// <see cref="Validation.ValidateWebhookUrl"/> (hostname blocklist,
    /// IP-literal and DNS-resolved private/reserved rejection) and adds the
    /// Linear-only requirement that delivery use https (no credentials).
    /// </summary>
    internal static void ValidateWebhookUrl(string url)
    {
        Validation.ValidateWebhookUrl(url, "Linear WebhookUrl");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Linear WebhookUrl must use https://.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Linear WebhookUrl must not embed credentials.");
    }
}
