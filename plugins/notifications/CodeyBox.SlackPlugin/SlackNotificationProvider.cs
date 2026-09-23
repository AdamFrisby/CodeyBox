using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.SlackPlugin;

/// <summary>
/// Slack notification provider: work-item and fleet notifications rendered
/// as native Block Kit (severity colour, fields, action buttons), follow-ups
/// threaded per work item, and decisions reflected back into the originating
/// message.
///
/// <para>One project, one plugin, against the bidirectional foundation:
/// outbound posts via the Slack Web API; inbound button presses arrive at the
/// host's verified <c>/webhooks/interactions/slack</c> endpoint (Slack
/// <c>slack-v0</c> HMAC over <c>v0:{timestamp}:{raw_body}</c> with a replay
/// window — the host refuses anything unverified) and resolve through the
/// existing question store. No second answer path exists here.</para>
///
/// <para>Off unless an operator enables it: <c>Enabled</c> defaults to false
/// and the bot token resolves from the environment (never config files).
/// A delivery failure never affects a work item — it is logged and
/// swallowed, per the provider contract.</para>
/// </summary>
[CodeyBoxPlugin(
    id: SlackNotificationProvider.PluginId,
    displayName: "CodeyBox: Slack Notifications",
    minHostApiVersion: "1.3")]
public sealed class SlackNotificationProvider : INotificationProvider, IPluginInitializer
{
    public const string PluginId = "codeybox.slack";

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<SlackNotificationProvider> _log;
    private readonly TimeProvider _clock;
    private readonly SlackThreadStore _threads;

    private ILogger _pluginLog = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public string Name => "slack";

    /// <summary>Slack carries authenticated interactions: offered actions
    /// render as native buttons resolving through the verified inbound
    /// endpoint, and landed decisions update the original message.</summary>
    public bool SupportsInteractions => true;

    public SlackNotificationProvider(
        IConfiguration configuration,
        IHttpClientFactory httpClients,
        ILogger<SlackNotificationProvider> logger,
        TimeProvider? clock = null)
    {
        _configuration = configuration;
        _httpClients = httpClients;
        _log = logger;
        _clock = clock ?? TimeProvider.System;
        _threads = new SlackThreadStore(clock: _clock);
    }

    internal SlackNotificationProvider(
        IConfiguration configuration,
        HttpClient httpClient,
        ILogger<SlackNotificationProvider> logger,
        TimeProvider? clock = null,
        SlackThreadStore? threads = null,
        IHttpClientFactory? httpClients = null)
    {
        _configuration = configuration;
        _httpClients = httpClients ?? new SingleClientFactory(httpClient);
        _log = logger;
        _clock = clock ?? TimeProvider.System;
        _threads = threads ?? new SlackThreadStore(clock: _clock);
    }

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        _pluginLog = context.Logger;
        var opts = BindOptions();
        if (!opts.Enabled)
        {
            _pluginLog.LogInformation("Slack notifications are disabled (codeybox.slack Enabled=false).");
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(opts.DefaultChannel))
            _pluginLog.LogWarning("Slack notifications are enabled with no DefaultChannel; notifications without an explicit recipient channel will be skipped.");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(opts.BotTokenEnvVar)))
            _pluginLog.LogWarning("Slack notifications are enabled but env var '{EnvVar}' is not set; delivery will be skipped until the bot token is provided.", opts.BotTokenEnvVar);
        else
            _pluginLog.LogInformation("Slack notifications enabled.");
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
            _log.LogWarning("SlackNotificationProvider: env var '{EnvVar}' is not set; skipping notification {Condition}",
                opts.BotTokenEnvVar, notification.ConditionId);
            return;
        }

        var channel = ResolveChannel(notification, opts);
        if (channel is null)
        {
            _log.LogWarning("SlackNotificationProvider: no channel for notification {Condition}; set a recipient or DefaultChannel",
                notification.ConditionId);
            return;
        }

        var (payload, workItemId) = SlackBlockKit.BuildMessage(notification, opts);

        string? threadTs = null;
        if (opts.ThreadByWorkItem && workItemId is not null)
            threadTs = _threads.ThreadRootFor(channel, workItemId);

        var timeout = NotificationDelivery.PostTimeoutOrDefault(opts.PostTimeoutSeconds, _log);

        SlackApiClient.PostResult result;
        try
        {
            var client = _httpClients.CreateClient();
            var api = new SlackApiClient(client);
            result = await api.PostMessageAsync(token, channel, payload, threadTs, timeout, ct);
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
            _log.LogError(ex, "SlackNotificationProvider: delivery failed for condition {Condition}",
                notification.ConditionId);
            return;
        }

        if (!result.Ok)
        {
            _log.LogWarning("SlackNotificationProvider: chat.postMessage failed ({Error}) for condition {Condition}",
                result.Error, notification.ConditionId);
            return;
        }

        var postedChannel = result.Channel ?? channel;
        var postedTs = result.Ts;
        if (workItemId is not null && postedTs is not null)
        {
            if (opts.ThreadByWorkItem && threadTs is null)
                _threads.RememberThreadRoot(postedChannel, workItemId, postedTs);
            if (!string.IsNullOrWhiteSpace(notification.CorrelationToken))
                _threads.RememberMessage(notification.CorrelationToken, postedChannel, postedTs);
        }

        _log.LogInformation("SlackNotificationProvider: posted notification {Condition} ({Severity}) to channel",
            notification.ConditionId, notification.Severity);
    }

    /// <summary>Reflect a landed decision back into the channel by updating
    /// the message that carried the buttons. Best-effort: failures are
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
            _log.LogWarning("SlackNotificationProvider: env var '{EnvVar}' is not set; skipping decision update for {Condition}",
                opts.BotTokenEnvVar, notification.ConditionId);
            return;
        }

        var blocks = SlackBlockKit.BuildDecidedBlocks(notification.Title, decisionSummary, notification.ConditionId);
        var fallback = $"Decided: {decisionSummary}";
        var timeout = NotificationDelivery.PostTimeoutOrDefault(opts.PostTimeoutSeconds, _log);

        try
        {
            var client = _httpClients.CreateClient();
            var api = new SlackApiClient(client);
            var result = await api.UpdateMessageAsync(token, identity.Value.Channel, identity.Value.Ts, fallback, blocks, timeout, ct);
            if (!result.Ok)
            {
                _log.LogWarning("SlackNotificationProvider: chat.update failed ({Error}) for condition {Condition}",
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
            _log.LogError(ex, "SlackNotificationProvider: decision update failed for condition {Condition}",
                notification.ConditionId);
        }
    }

    internal SlackPluginOptions BindOptions()
    {
        var opts = new SlackPluginOptions();
        _configuration.GetSection($"CodeyBox:Plugins:{PluginId}").Bind(opts);
        return opts;
    }

    private static string? ResolveChannel(Notification notification, SlackPluginOptions opts)
    {
        // Slack posts to one channel per call; the first non-empty
        // recipient wins.
        if (notification.Recipients is { Count: > 0 })
        {
            foreach (var recipient in notification.Recipients)
            {
                if (!string.IsNullOrWhiteSpace(recipient))
                    return recipient.Trim();
            }
        }
        return string.IsNullOrWhiteSpace(opts.DefaultChannel) ? null : opts.DefaultChannel.Trim();
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
}
