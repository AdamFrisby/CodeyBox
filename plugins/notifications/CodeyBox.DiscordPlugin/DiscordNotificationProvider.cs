using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.DiscordPlugin;

/// <summary>
/// Discord notification provider: work-item and fleet notifications rendered
/// as a native embed with action-row buttons, follow-ups threaded per work
/// item, and decisions reflected back by editing the originating message.
///
/// <para>One project, one plugin, against the bidirectional foundation:
/// outbound posts via the Discord Bot REST API; inbound button presses
/// arrive at the host's verified <c>/webhooks/interactions/discord</c>
/// endpoint (Ed25519 over <c>timestamp + raw_body</c> with a replay window —
/// the host refuses anything unverified, and answers the PING registration
/// challenge before anything else) and resolve through the existing
/// question store. No second answer path exists here.</para>
///
/// <para>Off unless an operator enables it: <c>Enabled</c> defaults to false
/// and the bot token resolves from the environment (never config files).
/// A delivery failure never affects a work item — it is logged and
/// swallowed, per the provider contract.</para>
/// </summary>
[CodeyBoxPlugin(
    id: DiscordNotificationProvider.PluginId,
    displayName: "CodeyBox: Discord Notifications",
    minHostApiVersion: "1.3")]
public sealed class DiscordNotificationProvider : INotificationProvider, IPluginInitializer
{
    public const string PluginId = "codeybox.discord";

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<DiscordNotificationProvider> _log;
    private readonly TimeProvider _clock;
    private readonly DiscordThreadStore _threads;

    private ILogger _pluginLog = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public string Name => "discord";

    /// <summary>Discord carries authenticated interactions: offered actions
    /// render as native buttons resolving through the verified inbound
    /// endpoint, and landed decisions edit the original message.</summary>
    public bool SupportsInteractions => true;

    public DiscordNotificationProvider(
        IConfiguration configuration,
        IHttpClientFactory httpClients,
        ILogger<DiscordNotificationProvider> logger,
        TimeProvider? clock = null)
    {
        _configuration = configuration;
        _httpClients = httpClients;
        _log = logger;
        _clock = clock ?? TimeProvider.System;
        _threads = new DiscordThreadStore(clock: _clock);
    }

    internal DiscordNotificationProvider(
        IConfiguration configuration,
        HttpClient httpClient,
        ILogger<DiscordNotificationProvider> logger,
        TimeProvider? clock = null,
        DiscordThreadStore? threads = null,
        IHttpClientFactory? httpClients = null)
    {
        _configuration = configuration;
        _httpClients = httpClients ?? new SingleClientFactory(httpClient);
        _log = logger;
        _clock = clock ?? TimeProvider.System;
        _threads = threads ?? new DiscordThreadStore(clock: _clock);
    }

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        _pluginLog = context.Logger;
        var opts = BindOptions();
        if (!opts.Enabled)
        {
            _pluginLog.LogInformation("Discord notifications are disabled (codeybox.discord Enabled=false).");
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(opts.DefaultChannelId))
            _pluginLog.LogWarning("Discord notifications are enabled with no DefaultChannelId; notifications without an explicit recipient channel will be skipped.");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(opts.BotTokenEnvVar)))
            _pluginLog.LogWarning("Discord notifications are enabled but env var '{EnvVar}' is not set; delivery will be skipped until the bot token is provided.", opts.BotTokenEnvVar);
        else
            _pluginLog.LogInformation("Discord notifications enabled.");
        return Task.CompletedTask;
    }

    public async Task SendAsync(Notification notification, CancellationToken ct)
    {
        var opts = BindOptions();
        if (!opts.Enabled)
            return;

        _threads.Configure(opts.EntryLifetime, opts.MaxEntries);

        var token = Environment.GetEnvironmentVariable(opts.BotTokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.LogWarning("DiscordNotificationProvider: env var '{EnvVar}' is not set; skipping notification {Condition}",
                opts.BotTokenEnvVar, notification.ConditionId);
            return;
        }

        var channel = ResolveChannel(notification, opts);
        if (channel is null)
        {
            _log.LogWarning("DiscordNotificationProvider: no channel for notification {Condition}; set a recipient or DefaultChannelId",
                notification.ConditionId);
            return;
        }

        var (payload, workItemId) = DiscordMessageBuilder.BuildMessage(notification, opts);

        string? threadId = null;
        if (opts.ThreadByWorkItem && workItemId is not null)
            threadId = _threads.ThreadRootFor(channel, workItemId);

        var timeout = NotificationDelivery.PostTimeoutOrDefault(opts.PostTimeoutSeconds, _log);

        DiscordApiClient.MessageResult posted;
        try
        {
            var client = _httpClients.CreateClient();
            var api = new DiscordApiClient(client);
            posted = await api.CreateMessageAsync(token, threadId ?? channel, payload, timeout, ct);
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
            _log.LogError(ex, "DiscordNotificationProvider: delivery failed for condition {Condition}",
                notification.ConditionId);
            return;
        }

        if (!posted.Ok)
        {
            _log.LogWarning("DiscordNotificationProvider: message create failed ({Error}) for condition {Condition}",
                posted.Error, notification.ConditionId);
            return;
        }

        var postedChannel = posted.ChannelId ?? threadId ?? channel;
        var postedMessage = posted.MessageId;
        if (workItemId is not null && postedMessage is not null)
        {
            if (opts.ThreadByWorkItem && threadId is null)
                await TryStartThreadAsync(token, channel, postedMessage, notification.Title, workItemId, opts, timeout, ct);
            if (!string.IsNullOrWhiteSpace(notification.CorrelationToken))
                _threads.RememberMessage(notification.CorrelationToken, postedChannel, postedMessage);
        }

        _log.LogInformation("DiscordNotificationProvider: posted notification {Condition} ({Severity}) to channel",
            notification.ConditionId, notification.Severity);
    }

    /// <summary>Reflect a landed decision back into the channel by editing
    /// the message that carried the buttons (which also removes them, so a
    /// decided question cannot be pressed again). Best-effort: failures are
    /// logged and swallowed so they can never affect the work item.</summary>
    public async Task UpdateDecisionAsync(Notification notification, string decisionSummary, CancellationToken ct)
    {
        var opts = BindOptions();
        if (!opts.Enabled)
            return;
        if (string.IsNullOrWhiteSpace(notification.CorrelationToken))
            return;

        _threads.Configure(opts.EntryLifetime, opts.MaxEntries);

        var identity = _threads.MessageFor(notification.CorrelationToken);
        if (identity is null)
            return;

        var token = Environment.GetEnvironmentVariable(opts.BotTokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.LogWarning("DiscordNotificationProvider: env var '{EnvVar}' is not set; skipping decision update for {Condition}",
                opts.BotTokenEnvVar, notification.ConditionId);
            return;
        }

        var payload = DiscordMessageBuilder.BuildDecidedPayload(notification.Title, decisionSummary, notification.ConditionId);
        var timeout = NotificationDelivery.PostTimeoutOrDefault(opts.PostTimeoutSeconds, _log);

        try
        {
            var client = _httpClients.CreateClient();
            var api = new DiscordApiClient(client);
            var result = await api.EditMessageAsync(token, identity.Value.ChannelId, identity.Value.MessageId, payload, timeout, ct);
            if (!result.Ok)
            {
                _log.LogWarning("DiscordNotificationProvider: message edit failed ({Error}) for condition {Condition}",
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
            _log.LogError(ex, "DiscordNotificationProvider: decision update failed for condition {Condition}",
                notification.ConditionId);
        }
    }

    private async Task TryStartThreadAsync(
        string token,
        string channel,
        string messageId,
        string title,
        string workItemId,
        DiscordPluginOptions opts,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            var client = _httpClients.CreateClient();
            var api = new DiscordApiClient(client);
            var thread = await api.CreateThreadFromMessageAsync(
                token, channel, messageId, DiscordMessageBuilder.ThreadNameFor(title), timeout, ct);
            if (thread.Ok && thread.ThreadId is not null)
                _threads.RememberThreadRoot(channel, workItemId, thread.ThreadId);
            else
                _log.LogWarning("DiscordNotificationProvider: thread start failed ({Error}); follow-ups will post top-level",
                    thread.Error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DiscordNotificationProvider: thread start failed; follow-ups will post top-level");
        }
    }

    internal DiscordPluginOptions BindOptions()
    {
        var opts = new DiscordPluginOptions();
        _configuration.GetSection($"CodeyBox:Plugins:{PluginId}").Bind(opts);
        return opts;
    }

    private static string? ResolveChannel(Notification notification, DiscordPluginOptions opts)
    {
        // Discord posts to one channel per call; the first non-empty
        // recipient wins.
        if (notification.Recipients is { Count: > 0 })
        {
            foreach (var recipient in notification.Recipients)
            {
                if (!string.IsNullOrWhiteSpace(recipient))
                    return recipient.Trim();
            }
        }
        return string.IsNullOrWhiteSpace(opts.DefaultChannelId) ? null : opts.DefaultChannelId.Trim();
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
}
