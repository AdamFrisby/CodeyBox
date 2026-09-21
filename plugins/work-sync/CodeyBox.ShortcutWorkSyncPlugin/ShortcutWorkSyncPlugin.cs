using System.Globalization;
using System.Runtime.CompilerServices;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.ShortcutWorkSyncPlugin;

/// <summary>
/// CodeyBox work-source / work-tracker plugin for Shortcut. One project, one
/// plugin: this single instance implements <see cref="IWorkSource"/> (inbound:
/// label/assignee/status-signalled stories — and, when enabled, epics — become
/// work items) and <see cref="IWorkTracker"/> (outbound: progress, questions,
/// commit/PR links, completion) against the Shortcut REST API v3.
/// <para>Off unless an operator enables it: the plugin loads only when
/// allowlisted AND named in <c>Plugins:Enabled</c>, and it polls/posts nothing
/// until <c>Enabled=true</c> in its own section.</para>
/// <para>Credentials come from the host credential chain (environment
/// variables populated by the operator's vault agent or container secrets);
/// configuration holds only the variable <em>names</em>. Static API tokens
/// travel as <c>Shortcut-Token</c>; OAuth access tokens are refreshed as part
/// of the integration (<see cref="ShortcutTokenProvider"/>).</para>
/// <para>Epic policy (explicit, not emergent): signalled epics ingest as ONE
/// work item each (<c>sc-epic-{id}</c>) only when <c>IngestEpics=true</c>
/// (default false); they never fan out into per-story items. Tracker state
/// moves apply to stories; epics receive comments only.</para>
/// </summary>
[CodeyBoxPlugin(
    id: ShortcutWorkSyncOptions.PluginId,
    displayName: "CodeyBox: Shortcut Work Sync",
    minHostApiVersion: "1.0")]
public sealed class ShortcutWorkSyncPlugin
    : IWorkSource, IWorkTracker, IPluginInitializer, IDisposable
{
    private readonly IHttpClientFactory? _httpFactory;
    private readonly TimeProvider _clock;
    private readonly Func<string, string?> _env;
    private readonly IConfigurationSection? _testConfig;

    private IPluginHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private HttpClient? _http;
    private ShortcutTokenProvider? _tokens;
    private ShortcutRestClient? _api;
    private readonly bool _ownsHttpClient;
    private readonly object _clientLock = new();
    private bool _disposed;

    /// <summary>Maximum comment body posted upstream (Shortcut text limit guard).</summary>
    internal const int MaxCommentChars = 32 * 1024;

    private readonly object _memberLock = new();
    private IReadOnlyList<ShortcutMember> _memberCache = [];
    private DateTimeOffset _memberCacheAt = DateTimeOffset.MinValue;

    /// <summary>Production constructor (DI provides the HTTP factory).</summary>
    public ShortcutWorkSyncPlugin(IHttpClientFactory httpFactory, TimeProvider? clock = null)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _clock = clock ?? TimeProvider.System;
        _env = Environment.GetEnvironmentVariable;
        _ownsHttpClient = true;
    }

    /// <summary>Test constructor: explicit client, config, clock, and environment.</summary>
    internal ShortcutWorkSyncPlugin(
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
    public string Namespace => ShortcutWorkSyncOptions.ProviderNamespace;

    /// <inheritdoc />
    public WorkSourceCapabilities Capabilities { get; } = new(SupportsWebhooks: true, SupportsPolling: true);

    /// <inheritdoc />
    public WorkTrackerCapabilities TrackerCapabilities => new(CanPostComments: true, CanSetStatus: true);

    WorkTrackerCapabilities IWorkTracker.Capabilities => TrackerCapabilities;

    /// <inheritdoc />
    public WorkSignal RequiredSignal => CurrentOptions().RequiredSignal;

    /// <summary>Ingestion key for a story id. Stable, readable, non-UUID.</summary>
    public static string StoryKey(long storyId) =>
        "sc-" + storyId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Ingestion key for an epic id. Stable, readable, non-UUID.</summary>
    public static string EpicKey(long epicId) =>
        "sc-epic-" + epicId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses an ingestion key back into its entity. Returns false for foreign
    /// keys so tracker posts fail honestly instead of addressing the wrong entity.
    /// </summary>
    internal static bool TryParseExternalId(string externalId, out bool isEpic, out long id)
    {
        isEpic = false;
        id = 0;
        if (string.IsNullOrWhiteSpace(externalId))
            return false;
        var rest = externalId.Trim();
        if (rest.StartsWith("sc-epic-", StringComparison.OrdinalIgnoreCase))
        {
            isEpic = true;
            rest = rest["sc-epic-".Length..];
        }
        else if (rest.StartsWith("sc-", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest["sc-".Length..];
        }
        else
        {
            return false;
        }
        return long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    /// <summary>Builds webhook-parser signal data from entity fields.</summary>
    internal static IReadOnlyList<ShortcutWebhookSignal> SignalsOf(
        IReadOnlyList<string> labels, IReadOnlyList<string> ownerRefs, string stateName)
    {
        var signals = new List<ShortcutWebhookSignal>(labels.Count + ownerRefs.Count + 1);
        foreach (var label in labels)
        {
            if (!string.IsNullOrWhiteSpace(label))
                signals.Add(new ShortcutWebhookSignal("Label", label));
        }
        foreach (var owner in ownerRefs)
        {
            if (!string.IsNullOrWhiteSpace(owner))
                signals.Add(new ShortcutWebhookSignal("Assignee", owner));
        }
        if (!string.IsNullOrWhiteSpace(stateName))
            signals.Add(new ShortcutWebhookSignal("Status", stateName));
        return signals;
    }

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
            _logger.LogInformation("Shortcut work sync is disabled (Enabled=false); polling and posting are no-ops");
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
            _logger.LogWarning("Shortcut poll skipped: no projects are mapped (ProjectMap is empty)");
            yield break;
        }
        var api = EnsureClients();
        var cap = Math.Max(1, options.MaxItemsPerPoll);
        var count = 0;
        var members = await GetMembersAsync(api, options, ct).ConfigureAwait(false);

        IAsyncEnumerable<IReadOnlyList<ShortcutStory>> pages;
        try
        {
            pages = api.SearchStoriesPagedAsync(options, ct);
        }
        catch (Exception ex) when (ex is ShortcutApiException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Shortcut poll skipped: story search failed");
            yield break;
        }
        await foreach (var page in pages.ConfigureAwait(false))
        {
            foreach (var story in page)
            {
                ct.ThrowIfCancellationRequested();
                if (count >= cap)
                    yield break;
                var candidate = ToCandidate(story, members, options);
                if (candidate is null)
                    continue;
                if (!string.Equals(candidate.Namespace, Namespace, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"poll yielded namespace '{candidate.Namespace}' from source '{Namespace}'");
                count++;
                yield return candidate;
            }
        }

        if (options.IngestEpics && count < cap)
        {
            IReadOnlyList<ShortcutEpic> epics;
            try
            {
                epics = await api.ListEpicsAsync(options, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ShortcutApiException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Shortcut epic poll skipped: epic list failed");
                yield break;
            }
            foreach (var epic in epics)
            {
                ct.ThrowIfCancellationRequested();
                if (count >= cap)
                    yield break;
                var candidate = ToEpicCandidate(epic, members, options);
                if (candidate is null)
                    continue;
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
        var parsed = ShortcutWebhook.Parse(verifiedBody, options.ProjectMap);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.ExternalId))
            return null;

        if (parsed.Kind == ShortcutWebhookEventKind.Comment)
        {
            return new ExternalWorkItem
            {
                Namespace = Namespace,
                ExternalId = parsed.ExternalId,
                ProjectId = new ProjectId(parsed.ProjectId ?? FirstConfiguredProject(options)),
                Title = string.IsNullOrWhiteSpace(parsed.Title) ? parsed.ExternalId : parsed.Title,
                Body = Truncate(parsed.Body, options.MaxIngestedBodyChars),
                PresentSignals = [],
                LastActorLogin = parsed.ActorLogin,
                HasSignal = false,
            };
        }

        if (parsed.Kind == ShortcutWebhookEventKind.Epic && !options.IngestEpics)
            return null;

        if (string.IsNullOrWhiteSpace(parsed.ProjectId))
            return null;
        var present = ToWorkSignals(parsed.PresentSignals);
        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = parsed.ExternalId,
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
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "shortcut work sync is disabled");
        var check = CheckTracked(update.Namespace, update.ExternalId);
        if (check is not null)
            return check;

        var mapping = CurrentMapping(options);
        if (!mapping.TryMap(update.State, out var status) || string.IsNullOrEmpty(status))
            return Unmapped(update.State);

        if (!TryParseExternalId(update.ExternalId, out var isEpic, out var id))
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Shortcut item '{update.ExternalId}' not found");

        try
        {
            var api = EnsureClients();
            if (!isEpic)
            {
                var stateId = await ResolveStateIdAsync(api, options, status, ct).ConfigureAwait(false);
                if (stateId is null)
                    return new TrackerPostResult(TrackerPostOutcome.Failed,
                        Detail: $"Shortcut workflow state '{status}' not found for story '{update.ExternalId}'");
                await api.UpdateStoryStateAsync(options, id, stateId.Value, ct).ConfigureAwait(false);
                var commentId = await api.CreateStoryCommentAsync(
                    options, id, ClipComment(update.Body), ct).ConfigureAwait(false);
                return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
            }

            _logger.LogInformation(
                "Shortcut epic '{ExternalId}' has no workflow state; posting progress as a comment only",
                update.ExternalId);
            var epicCommentId = await api.CreateEpicCommentAsync(
                options, id, ClipComment(update.Body), ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: epicCommentId);
        }
        catch (Exception ex) when (ex is ShortcutApiException or HttpRequestException
            or TaskCanceledException or InvalidOperationException)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Shortcut progress post for '{update.ExternalId}' failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostQuestionAsync(
        TrackerQuestionPost post, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "shortcut work sync is disabled");
        var check = CheckTracked(post.Namespace, post.ExternalId);
        if (check is not null)
            return check;

        if (!TryParseExternalId(post.ExternalId, out var isEpic, out var id))
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Shortcut item '{post.ExternalId}' not found");

        try
        {
            var api = EnsureClients();
            var body = ClipComment($"{post.Body}\n\n{ShortcutWebhook.QuestionTag(post.QuestionId)}");
            var commentId = isEpic
                ? await api.CreateEpicCommentAsync(options, id, body, ct).ConfigureAwait(false)
                : await api.CreateStoryCommentAsync(options, id, body, ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
        }
        catch (Exception ex) when (ex is ShortcutApiException or HttpRequestException
            or TaskCanceledException or InvalidOperationException)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Shortcut question post for '{post.ExternalId}' failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<TrackerPostResult> PostOutcomeAsync(
        TrackerOutcomeReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var options = CurrentOptions();
        if (!options.Enabled)
            return new TrackerPostResult(TrackerPostOutcome.Failed, Detail: "shortcut work sync is disabled");
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

        if (!TryParseExternalId(report.ExternalId, out var isEpic, out var id))
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Shortcut item '{report.ExternalId}' not found");

        try
        {
            var api = EnsureClients();
            if (!isEpic && !string.IsNullOrEmpty(status))
            {
                var stateId = await ResolveStateIdAsync(api, options, status, ct).ConfigureAwait(false);
                if (stateId is null)
                    return new TrackerPostResult(TrackerPostOutcome.Failed,
                        Detail: $"Shortcut workflow state '{status}' not found for story '{report.ExternalId}'");
                await api.UpdateStoryStateAsync(options, id, stateId.Value, ct).ConfigureAwait(false);
            }
            else if (isEpic && !string.IsNullOrEmpty(status))
            {
                _logger.LogInformation(
                    "Shortcut epic '{ExternalId}' has no workflow state; posting outcome as a comment only",
                    report.ExternalId);
            }

            var commentId = isEpic
                ? await api.CreateEpicCommentAsync(options, id, ClipComment(report.Body), ct).ConfigureAwait(false)
                : await api.CreateStoryCommentAsync(options, id, ClipComment(report.Body), ct).ConfigureAwait(false);
            return new TrackerPostResult(TrackerPostOutcome.Posted, RemoteId: commentId);
        }
        catch (Exception ex) when (ex is ShortcutApiException or HttpRequestException
            or TaskCanceledException or InvalidOperationException)
        {
            return new TrackerPostResult(TrackerPostOutcome.Failed,
                Detail: $"Shortcut outcome post for '{report.ExternalId}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates the UI-managed webhook expectation: Shortcut exposes no
    /// webhook-management API, so this method validates the configured delivery
    /// URL and logs what the operator must register in the Shortcut UI. It never
    /// assumes a prior registration persists — polling remains the source of
    /// truth and webhooks only accelerate it.
    /// </summary>
    public Task EnsureWebhookAsync(ShortcutWorkSyncOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ManageWebhooks || string.IsNullOrWhiteSpace(options.WebhookUrl))
            return Task.CompletedTask;
        ValidateWebhookUrl(options.WebhookUrl);
        _logger.LogInformation(
            "Shortcut webhooks are UI-managed (Settings → API → Outgoing Webhooks): " +
            "register delivery URL {Url} for story, epic, and comment events; polling remains the source of truth",
            options.WebhookUrl);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes the registration for <paramref name="webhookUrl"/>. Always
    /// reports false: Shortcut exposes no webhook-management API, so
    /// registrations are removed in the Shortcut UI.
    /// </summary>
    public Task<bool> RemoveWebhookAsync(
        ShortcutWorkSyncOptions options, string webhookUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookUrl);
        _logger.LogInformation(
            "Shortcut webhook registrations are UI-managed; remove {Url} in Settings → API → Outgoing Webhooks",
            webhookUrl);
        return Task.FromResult(false);
    }

    /// <summary>
    /// Verifies a webhook delivery over the raw bytes against the HMAC-SHA256
    /// hex signature added by a signing proxy under the configured header, for
    /// hosting endpoints. Returns false when no signing secret is configured.
    /// </summary>
    public bool VerifyWebhookDelivery(byte[] rawBody, string? signatureHeader)
    {
        var options = CurrentOptions();
        return ShortcutWebhook.VerifySignature(
            rawBody, signatureHeader, _env(options.WebhookSecretEnvVar) ?? string.Empty);
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

    internal ShortcutWorkSyncOptions CurrentOptions()
    {
        var section = _host?.ScopedConfig ?? _testConfig;
        return section is null
            ? new ShortcutWorkSyncOptions()
            : ShortcutWorkSyncOptions.FromConfiguration(section);
    }

    internal WorkStateMapping CurrentMapping(ShortcutWorkSyncOptions options)
    {
        try
        {
            return WorkStateMapping.Parse(options.StateMapping);
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Shortcut state mapping is invalid; reporting every state as unmapped");
            return WorkStateMapping.Empty;
        }
    }

    private ShortcutRestClient EnsureClients()
    {
        if (_api is not null)
            return _api;
        lock (_clientLock)
        {
            if (_api is not null)
                return _api;
            _http ??= _httpFactory!.CreateClient("shortcut-worksync");
            _http.Timeout = TimeSpan.FromSeconds(CurrentOptions().TimeoutSeconds);
            _tokens = new ShortcutTokenProvider(_http, _env, _clock);
            _api = new ShortcutRestClient(_http, _tokens);
            return _api;
        }
    }

    private async Task<IReadOnlyList<ShortcutMember>> GetMembersAsync(
        ShortcutRestClient api, ShortcutWorkSyncOptions options, CancellationToken ct)
    {
        lock (_memberLock)
        {
            if (_memberCache.Count > 0
                && _clock.GetUtcNow() - _memberCacheAt < TimeSpan.FromMinutes(Math.Max(1, options.MemberCacheMinutes)))
                return _memberCache;
        }
        IReadOnlyList<ShortcutMember> members = [];
        try
        {
            members = await api.ListMembersAsync(options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ShortcutApiException or HttpRequestException
            or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Shortcut member lookup failed; assignee signals will match member ids only");
            return [];
        }
        lock (_memberLock)
        {
            _memberCache = members.Count > ShortcutWorkSyncOptions.MaxMemberCacheEntries
                ? members.Take(ShortcutWorkSyncOptions.MaxMemberCacheEntries).ToList()
                : members;
            _memberCacheAt = _clock.GetUtcNow();
        }
        return members;
    }

    private ExternalWorkItem? ToCandidate(
        ShortcutStory story, IReadOnlyList<ShortcutMember> members, ShortcutWorkSyncOptions options)
    {
        if (story.Id == 0)
            return null;
        var projectKey = story.ProjectId?.ToString(CultureInfo.InvariantCulture);
        if (projectKey is null
            || !options.ProjectMap.TryGetValue(projectKey, out var projectId)
            || string.IsNullOrWhiteSpace(projectId))
            return null;

        var present = PresentSignalsOf(
            story.LabelNames, story.OwnerIds, members, story.WorkflowStateName);
        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = StoryKey(story.Id),
            ProjectId = new ProjectId(projectId),
            Title = story.Name,
            Body = Truncate(story.Description, options.MaxIngestedBodyChars),
            PresentSignals = present,
            LastActorLogin = null,
            HasSignal = SignalPresent(options.RequiredSignal, present),
        };
    }

    private ExternalWorkItem? ToEpicCandidate(
        ShortcutEpic epic, IReadOnlyList<ShortcutMember> members, ShortcutWorkSyncOptions options)
    {
        if (epic.Id == 0)
            return null;
        var projectId = FirstConfiguredProject(options);
        if (options.ProjectMap.Count == 0 || string.IsNullOrWhiteSpace(projectId))
            return null;

        var present = PresentSignalsOf(epic.LabelNames, epic.OwnerIds, members, string.Empty);
        return new ExternalWorkItem
        {
            Namespace = Namespace,
            ExternalId = EpicKey(epic.Id),
            ProjectId = new ProjectId(projectId),
            Title = epic.Name,
            Body = Truncate(epic.Description, options.MaxIngestedBodyChars),
            PresentSignals = present,
            LastActorLogin = null,
            HasSignal = SignalPresent(options.RequiredSignal, present),
        };
    }

    private static IReadOnlyList<WorkSignal> PresentSignalsOf(
        IReadOnlyList<string> labels,
        IReadOnlyList<string> ownerIds,
        IReadOnlyList<ShortcutMember> members,
        string stateName)
    {
        var present = new List<WorkSignal>(labels.Count + (ownerIds.Count * 2) + 1);
        foreach (var label in labels)
        {
            if (!string.IsNullOrWhiteSpace(label))
                present.Add(new WorkSignal(WorkSignalKind.Label, label));
        }
        var byId = members.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var ownerId in ownerIds)
        {
            if (string.IsNullOrWhiteSpace(ownerId))
                continue;
            present.Add(new WorkSignal(WorkSignalKind.Assignee, ownerId));
            if (byId.TryGetValue(ownerId, out var member))
            {
                if (!string.IsNullOrWhiteSpace(member.Email))
                    present.Add(new WorkSignal(WorkSignalKind.Assignee, member.Email));
                if (!string.IsNullOrWhiteSpace(member.MentionName))
                    present.Add(new WorkSignal(WorkSignalKind.Assignee, member.MentionName));
                if (!string.IsNullOrWhiteSpace(member.Name))
                    present.Add(new WorkSignal(WorkSignalKind.Assignee, member.Name));
            }
        }
        if (!string.IsNullOrWhiteSpace(stateName))
            present.Add(new WorkSignal(WorkSignalKind.Status, stateName));
        return present;
    }

    private static IReadOnlyList<WorkSignal> ToWorkSignals(
        IReadOnlyList<ShortcutWebhookSignal> data)
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

    private async Task<long?> ResolveStateIdAsync(
        ShortcutRestClient api,
        ShortcutWorkSyncOptions options,
        string status,
        CancellationToken ct)
    {
        var states = await api.ListWorkflowStatesAsync(options, ct).ConfigureAwait(false);
        return states.FirstOrDefault(s =>
            string.Equals(s.Name, status, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static string FirstConfiguredProject(ShortcutWorkSyncOptions options) =>
        options.ProjectMap.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "shortcut";

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    private static string ClipComment(string body)
    {
        if (string.IsNullOrEmpty(body))
            throw new ArgumentException("tracker body must not be empty", nameof(body));
        return body.Length <= MaxCommentChars ? body : body[..MaxCommentChars];
    }

    /// <summary>
    /// Validates the operator's public webhook URL. Delegates SSRF policy to
    /// <see cref="Validation.ValidateWebhookUrl"/> (hostname blocklist,
    /// IP-literal and DNS-resolved private/reserved rejection) and adds the
    /// requirement that delivery use https (no credentials).
    /// </summary>
    internal static void ValidateWebhookUrl(string url)
    {
        Validation.ValidateWebhookUrl(url, "Shortcut WebhookUrl");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Shortcut WebhookUrl must use https://.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Shortcut WebhookUrl must not embed credentials.");
    }
}
