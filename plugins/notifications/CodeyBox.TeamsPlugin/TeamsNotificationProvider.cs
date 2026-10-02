using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.TeamsPlugin;

/// <summary>
/// Teams notification provider: work-item and fleet notifications rendered
/// as native Adaptive Cards (severity styling, fields as facts, offered
/// actions as submit controls with a custom-answer follow-up), posted
/// through the Bot Framework Connector, and decisions reflected back by
/// refreshing the originating card.
///
/// <para>One project, one plugin, against the bidirectional foundation:
/// outbound activities via the Connector; inbound submits arrive at the
/// host's verified <c>/webhooks/interactions/teams</c> endpoint (Bot
/// Framework signed bearer token — the host refuses anything unverified)
/// and resolve through the existing question store. No second answer path
/// exists here.</para>
///
/// <para>Off unless an operator enables it: <c>Enabled</c> defaults to false
/// and the bot credentials resolve from the environment (never config
/// files). A delivery failure never affects a work item — it is logged and
/// swallowed, per the provider contract.</para>
/// </summary>
[CodeyBoxPlugin(
    id: TeamsNotificationProvider.PluginId,
    displayName: "CodeyBox: Teams Notifications",
    minHostApiVersion: "1.3")]
public sealed class TeamsNotificationProvider : INotificationProvider, IPluginInitializer
{
    public const string PluginId = "codeybox.teams";

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<TeamsNotificationProvider> _log;
    private readonly TeamsMessageStore _messages;
    private readonly TeamsBotTokenCache _tokens;
    private readonly TimeProvider _clock;

    private ILogger _pluginLog = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public string Name => "teams";

    /// <summary>Teams carries authenticated interactions: offered actions
    /// render as native submit controls resolving through the verified
    /// inbound endpoint, and landed decisions refresh the original card.</summary>
    public bool SupportsInteractions => true;

    public TeamsNotificationProvider(
        IConfiguration configuration,
        IHttpClientFactory httpClients,
        ILogger<TeamsNotificationProvider> logger,
        TimeProvider? clock = null)
    {
        _configuration = configuration;
        _httpClients = httpClients;
        _log = logger;
        _clock = clock ?? TimeProvider.System;
        _messages = new TeamsMessageStore(clock: _clock);
        _tokens = new TeamsBotTokenCache(httpClients.CreateClient(), _clock);
    }

    internal TeamsNotificationProvider(
        IConfiguration configuration,
        HttpClient httpClient,
        ILogger<TeamsNotificationProvider> logger,
        TimeProvider? clock = null,
        TeamsMessageStore? messages = null,
        TeamsBotTokenCache? tokens = null,
        IHttpClientFactory? httpClients = null)
    {
        _configuration = configuration;
        _httpClients = httpClients ?? new SingleClientFactory(httpClient);
        _log = logger;
        _clock = clock ?? TimeProvider.System;
        _messages = messages ?? new TeamsMessageStore(clock: _clock);
        _tokens = tokens ?? new TeamsBotTokenCache(httpClient, _clock);
    }

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        _pluginLog = context.Logger;
        var opts = BindOptions();
        if (!opts.Enabled)
        {
            _pluginLog.LogInformation("Teams notifications are disabled (codeybox.teams Enabled=false).");
            return Task.CompletedTask;
        }
        if (!TeamsApiClient.IsUsableServiceUrl(opts.ServiceUrl))
            _pluginLog.LogWarning("Teams notifications are enabled but ServiceUrl is not an absolute https URL; delivery will be skipped until it is configured.");
        if (string.IsNullOrWhiteSpace(opts.ConversationId))
            _pluginLog.LogWarning("Teams notifications are enabled with no ConversationId; notifications without an explicit recipient conversation will be skipped.");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(opts.AppIdEnvVar)))
            _pluginLog.LogWarning("Teams notifications are enabled but env var '{EnvVar}' is not set; delivery will be skipped until the bot App ID is provided.", opts.AppIdEnvVar);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(opts.AppPasswordEnvVar)))
            _pluginLog.LogWarning("Teams notifications are enabled but env var '{EnvVar}' is not set; delivery will be skipped until the bot App password is provided.", opts.AppPasswordEnvVar);
        else
            _pluginLog.LogInformation("Teams notifications enabled.");
        return Task.CompletedTask;
    }

    public async Task SendAsync(Notification notification, CancellationToken ct)
    {
        var opts = BindOptions();
        if (!opts.Enabled)
            return;

        _messages.Configure(opts.EntryLifetime, opts.MaxEntries);

        if (!TeamsApiClient.IsUsableServiceUrl(opts.ServiceUrl))
        {
            _log.LogWarning("TeamsNotificationProvider: ServiceUrl is not an absolute https URL; skipping notification {Condition}",
                notification.ConditionId);
            return;
        }

        var appId = Environment.GetEnvironmentVariable(opts.AppIdEnvVar);
        var appPassword = Environment.GetEnvironmentVariable(opts.AppPasswordEnvVar);
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appPassword))
        {
            _log.LogWarning("TeamsNotificationProvider: bot credentials are not set (env vars '{AppIdVar}' / '{PasswordVar}'); skipping notification {Condition}",
                opts.AppIdEnvVar, opts.AppPasswordEnvVar, notification.ConditionId);
            return;
        }

        var conversationId = ResolveConversation(notification, opts);
        if (conversationId is null)
        {
            _log.LogWarning("TeamsNotificationProvider: no conversation for notification {Condition}; set a recipient or ConversationId",
                notification.ConditionId);
            return;
        }

        var (card, _) = TeamsAdaptiveCard.BuildCard(notification, opts);
        var activity = new Dictionary<string, object?>
        {
            ["type"] = "message",
            ["text"] = $"[{notification.Severity}] {notification.Title}",
            ["attachments"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["contentType"] = TeamsAdaptiveCard.AdaptiveCardContentType,
                    ["content"] = card,
                },
            },
        };

        var timeout = NotificationDelivery.PostTimeoutOrDefault(opts.PostTimeoutSeconds, _log);

        try
        {
            var client = _httpClients.CreateClient();
            var botToken = await _tokens.GetTokenAsync(appId, appPassword, timeout, ct).ConfigureAwait(false);
            var api = new TeamsApiClient(client);
            var result = await api.PostActivityAsync(opts.ServiceUrl, conversationId, botToken, activity, timeout, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                _log.LogWarning("TeamsNotificationProvider: activity post failed ({Error}) for condition {Condition}",
                    result.Error, notification.ConditionId);
                return;
            }
            if (!string.IsNullOrWhiteSpace(notification.CorrelationToken) && !string.IsNullOrWhiteSpace(result.ActivityId))
                _messages.RememberMessage(notification.CorrelationToken, conversationId, result.ActivityId!);
        }
        catch (OperationCanceledException)
        {
            // Any cancellation reaching this layer is foreign: the client's
            // own timeout already surfaces as a result, so rethrow
            // unconditionally and let shutdown proceed.
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "TeamsNotificationProvider: delivery failed for condition {Condition}",
                notification.ConditionId);
            return;
        }

        _log.LogInformation("TeamsNotificationProvider: posted notification {Condition} ({Severity}) to conversation",
            notification.ConditionId, notification.Severity);
    }

    /// <summary>Reflect a landed decision back into the channel by refreshing
    /// the card that carried the question. Best-effort: failures are logged
    /// and swallowed so they can never affect the work item.</summary>
    public async Task UpdateDecisionAsync(Notification notification, string decisionSummary, CancellationToken ct)
    {
        var opts = BindOptions();
        if (!opts.Enabled)
            return;
        if (string.IsNullOrWhiteSpace(notification.CorrelationToken))
            return;
        if (!TeamsApiClient.IsUsableServiceUrl(opts.ServiceUrl))
            return;

        _messages.Configure(opts.EntryLifetime, opts.MaxEntries);

        var identity = _messages.MessageFor(notification.CorrelationToken);
        if (identity is null)
            return;

        var appId = Environment.GetEnvironmentVariable(opts.AppIdEnvVar);
        var appPassword = Environment.GetEnvironmentVariable(opts.AppPasswordEnvVar);
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appPassword))
        {
            _log.LogWarning("TeamsNotificationProvider: bot credentials are not set; skipping decision update for {Condition}",
                notification.ConditionId);
            return;
        }

        var card = TeamsAdaptiveCard.BuildDecidedCard(notification.Title, decisionSummary, notification.ConditionId);
        var activity = new Dictionary<string, object?>
        {
            ["type"] = "message",
            ["text"] = decisionSummary,
            ["attachments"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["contentType"] = TeamsAdaptiveCard.AdaptiveCardContentType,
                    ["content"] = card,
                },
            },
        };
        var timeout = NotificationDelivery.PostTimeoutOrDefault(opts.PostTimeoutSeconds, _log);

        try
        {
            var client = _httpClients.CreateClient();
            var botToken = await _tokens.GetTokenAsync(appId, appPassword, timeout, ct).ConfigureAwait(false);
            var api = new TeamsApiClient(client);
            var result = await api.UpdateActivityAsync(opts.ServiceUrl, identity.ConversationId, identity.ActivityId, botToken, activity, timeout, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                _log.LogWarning("TeamsNotificationProvider: activity update failed ({Error}) for condition {Condition}",
                    result.Error, notification.ConditionId);
            }
        }
        catch (OperationCanceledException)
        {
            // Any cancellation reaching this layer is foreign: the client's
            // own timeout already surfaces as a result, so rethrow
            // unconditionally and let shutdown proceed.
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "TeamsNotificationProvider: decision update failed for condition {Condition}",
                notification.ConditionId);
        }
    }

    internal TeamsPluginOptions BindOptions()
    {
        var opts = new TeamsPluginOptions();
        _configuration.GetSection($"CodeyBox:Plugins:{PluginId}").Bind(opts);
        return opts;
    }

    private static string? ResolveConversation(Notification notification, TeamsPluginOptions opts)
    {
        // The Connector posts to one conversation per call; the first
        // non-empty recipient wins.
        if (notification.Recipients is { Count: > 0 })
        {
            foreach (var recipient in notification.Recipients)
            {
                if (!string.IsNullOrWhiteSpace(recipient))
                    return recipient.Trim();
            }
        }
        return string.IsNullOrWhiteSpace(opts.ConversationId) ? null : opts.ConversationId.Trim();
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
}
