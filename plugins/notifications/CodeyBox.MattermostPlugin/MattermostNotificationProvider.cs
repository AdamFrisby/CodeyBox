using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.MattermostPlugin;

/// <summary>
/// Mattermost notification provider: work-item and fleet notifications posted
/// to a channel over the REST API v4 (<c>POST /api/v4/posts</c>, bearer
/// notification token), rendered as markdown — severity heading, fields as a
/// bullet list, and the answer/Agnes routes as real links rather than a dumped
/// text blob — with follow-ups threaded per work item (<c>root_id</c>).
///
/// <para>Mattermost cannot carry an authenticated interaction here: this
/// provider declares <see cref="SupportsInteractions"/> = false and surfaces
/// <see cref="Notification.AnswerUrl"/> so a question is never unanswerable —
/// the operator answers in CodeyBox (or Agnes), never through a synthesized
/// inbound path. Correspondingly the plugin supplies no interaction verifier:
/// the foundation refuses anything POSTed to
/// <c>/webhooks/interactions/mattermost</c>. No inbound exposure is
/// needed, and the notification credential is isolated from any future inbound
/// authorization (a separate credential consumed by separate code).</para>
///
/// <para>Credential split honoured: the configured token is outbound-only
/// (needs just post creation on the target channel) — no account/channel
/// creation or membership changes are ever attempted. The token rides in a
/// request header, so the plugin owns its HTTP client
/// (<see cref="MattermostHttpClients"/>) rather than sharing the host factory:
/// it never follows a redirect, which would re-send the credential to a
/// server-chosen host. Ambiguous post responses (2xx without a usable post
/// ID) are tombstoned per correlation token rather than reposted, so a
/// redelivery can never duplicate-spam the channel. Rate limits (HTTP 429)
/// are honoured with a bounded wait-and-retry, then the notification is
/// dropped with a warning. Off unless an operator enables it:
/// <c>Enabled</c> defaults to false. A delivery failure never affects a
/// work item — it is logged and swallowed, per the provider
/// contract.</para>
/// </summary>
[CodeyBoxPlugin(
    id: MattermostNotificationProvider.PluginId,
    displayName: "CodeyBox: Mattermost Notifications",
    minHostApiVersion: "1.3")]
public sealed class MattermostNotificationProvider : INotificationProvider, IPluginInitializer, IDisposable
{
    public const string PluginId = "codeybox.mattermost";

    private readonly IConfiguration _configuration;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger<MattermostNotificationProvider> _log;
    private readonly TimeProvider _clock;
    private readonly MattermostPostStore _posts;

    private ILogger _pluginLog = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public string Name => "mattermost";

    /// <summary>Mattermost carries no authenticated interaction here — a
    /// question surfaces through the AnswerUrl link instead of buttons.</summary>
    public bool SupportsInteractions => false;

    public MattermostNotificationProvider(
        IConfiguration configuration,
        ILogger<MattermostNotificationProvider> logger,
        TimeProvider? clock = null)
    {
        _configuration = configuration;
        _http = MattermostHttpClients.Create();
        _ownsHttp = true;
        _log = logger;
        _clock = clock ?? TimeProvider.System;
        _posts = new MattermostPostStore(clock: _clock);
    }

    internal MattermostNotificationProvider(
        IConfiguration configuration,
        HttpClient httpClient,
        ILogger<MattermostNotificationProvider> logger,
        TimeProvider? clock = null,
        MattermostPostStore? posts = null)
    {
        _configuration = configuration;
        _http = httpClient;
        _log = logger;
        _clock = clock ?? TimeProvider.System;
        _posts = posts ?? new MattermostPostStore(clock: _clock);
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        _pluginLog = context.Logger;
        var opts = BindOptions();
        if (!opts.Enabled)
        {
            _pluginLog.LogInformation("Mattermost notifications are disabled (codeybox.mattermost Enabled=false).");
            return Task.CompletedTask;
        }
        if (!TryResolveServer(opts, out _, out var serverFailure))
            _pluginLog.LogWarning("Mattermost notifications are enabled but unusable: {Reason}", serverFailure);
        if (string.IsNullOrWhiteSpace(opts.DefaultChannelId))
            _pluginLog.LogWarning("Mattermost notifications are enabled with no DefaultChannelId; notifications without an explicit recipient channel will be skipped.");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(opts.TokenEnvVar)))
            _pluginLog.LogWarning("Mattermost notifications are enabled but env var '{EnvVar}' is not set; delivery will be skipped until the notification token is provided.", opts.TokenEnvVar);
        else
            _pluginLog.LogInformation("Mattermost notifications enabled.");
        return Task.CompletedTask;
    }

    public async Task SendAsync(Notification notification, CancellationToken ct)
    {
        var opts = BindOptions();
        if (!opts.Enabled)
            return;

        _posts.Configure(opts.EntryLifetime, opts.MaxEntries);

        if (!string.IsNullOrWhiteSpace(notification.CorrelationToken))
        {
            if (_posts.PostedFor(notification.CorrelationToken) is not null)
            {
                _log.LogInformation(
                    "MattermostNotificationProvider: notification {Condition} already posted for this correlation token; skipping duplicate",
                    notification.ConditionId);
                return;
            }
            if (_posts.IsAmbiguous(notification.CorrelationToken))
            {
                _log.LogWarning(
                    "MattermostNotificationProvider: earlier post for {Condition} answered without a usable post ID (it may already exist); suppressing repost rather than risking a duplicate",
                    notification.ConditionId);
                return;
            }
        }

        if (!TryResolveServer(opts, out var serverUrl, out var serverFailure))
        {
            _log.LogWarning("MattermostNotificationProvider: {Reason}; skipping notification {Condition}",
                serverFailure, notification.ConditionId);
            return;
        }

        var token = Environment.GetEnvironmentVariable(opts.TokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.LogWarning("MattermostNotificationProvider: env var '{EnvVar}' is not set; skipping notification {Condition}",
                opts.TokenEnvVar, notification.ConditionId);
            return;
        }

        var channel = ResolveChannel(notification, opts);
        if (!MattermostApiClient.IsUsableChannelId(channel))
        {
            _log.LogWarning("MattermostNotificationProvider: no usable channel for notification {Condition}; set a recipient or DefaultChannelId",
                notification.ConditionId);
            return;
        }

        var (payload, workItemId) = MattermostMessageBuilder.BuildMessage(notification, opts);

        string? rootId = null;
        if (opts.ThreadByWorkItem && workItemId is not null)
            rootId = _posts.ThreadRootFor(channel!, workItemId);

        var timeout = NotificationDelivery.PostTimeoutOrDefault(opts.PostTimeoutSeconds, _log);
        var maxWait = opts.MaxRateLimitWaitSeconds >= 1
            ? TimeSpan.FromSeconds(opts.MaxRateLimitWaitSeconds)
            : TimeSpan.FromSeconds(30);

        MattermostApiClient.PostResult result;
        try
        {
            var api = new MattermostApiClient(_http);
            result = await api.PostMessageAsync(
                token, serverUrl!, channel!, payload, rootId,
                timeout, opts.RateLimitMaxRetries, maxWait, ct).ConfigureAwait(false);
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
            _log.LogError(ex, "MattermostNotificationProvider: delivery failed for condition {Condition}",
                notification.ConditionId);
            return;
        }

        if (!result.Ok)
        {
            if (result.Ambiguous && !string.IsNullOrWhiteSpace(notification.CorrelationToken))
                _posts.RememberAmbiguous(notification.CorrelationToken, channel!);
            _log.LogWarning("MattermostNotificationProvider: POST /api/v4/posts failed ({Error}) for condition {Condition}",
                result.Error, notification.ConditionId);
            return;
        }

        if (workItemId is not null && result.PostId is not null)
        {
            if (opts.ThreadByWorkItem && rootId is null)
                _posts.RememberThreadRoot(channel!, workItemId, result.PostId);
            if (!string.IsNullOrWhiteSpace(notification.CorrelationToken))
                _posts.RememberPosted(notification.CorrelationToken, channel!, result.PostId);
        }

        _log.LogInformation("MattermostNotificationProvider: posted notification {Condition} ({Severity}) as post {PostId}",
            notification.ConditionId, notification.Severity, result.PostId);
    }

    internal MattermostPluginOptions BindOptions()
    {
        var opts = new MattermostPluginOptions();
        _configuration.GetSection($"CodeyBox:Plugins:{PluginId}").Bind(opts);
        return opts;
    }

    /// <summary>Resolve the <c>POST /api/v4/posts</c> base from configured
    /// <see cref="MattermostPluginOptions.ServerUrl"/>. The URL is
    /// operator-supplied config, not untrusted input, but the sink still
    /// carries its own guard: only clean absolute http(s) origins resolve,
    /// and plain http resolves only when the operator opted into cleartext
    /// bearer-token exposure via
    /// <see cref="MattermostPluginOptions.AllowPlainHttp"/>.</summary>
    internal static bool TryResolveServer(MattermostPluginOptions opts, out string? serverUrl, out string failure)
    {
        serverUrl = null;
        failure = string.Empty;
        if (!MattermostApiClient.IsUsableBaseUrl(opts.ServerUrl))
        {
            failure = "ServerUrl is not a clean absolute http/https base URL (no query, fragment, or user-info)";
            return false;
        }
        var trimmed = opts.ServerUrl.TrimEnd('/');
        if (trimmed.StartsWith(Uri.UriSchemeHttp + ":", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !opts.AllowPlainHttp)
        {
            failure = "ServerUrl is plain http; set AllowPlainHttp=true only if cleartext bearer-token delivery is acceptable";
            return false;
        }
        serverUrl = trimmed;
        return true;
    }

    private static string? ResolveChannel(Notification notification, MattermostPluginOptions opts)
    {
        // One channel per post; the first non-empty recipient wins.
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
}
