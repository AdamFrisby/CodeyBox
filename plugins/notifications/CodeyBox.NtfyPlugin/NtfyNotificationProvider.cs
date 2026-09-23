using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.NtfyPlugin;

/// <summary>
/// ntfy notification provider: work-item and fleet notifications published to
/// a topic as JSON (priority, tag, fields, action buttons), and landed
/// decisions reflected back by republishing over the same
/// <c>sequence_id</c> so the question notification becomes the decision.
///
/// <para>One project, one plugin, against the bidirectional foundation:
/// outbound posts via ntfy's publish endpoint; inbound button presses are
/// ntfy <c>http</c> actions the client invokes itself against the host's
/// verified <c>/webhooks/interactions/ntfy</c> endpoint. ntfy signs nothing —
/// each rendered button carries a body MAC minted at publish time
/// (<c>X-CodeyBox-Signature: sha256=…</c> over the exact body bytes, keyed by
/// the shared interaction secret), and the host refuses anything unverified.
/// The answer resolves through the existing question store. No second answer
/// path exists here.</para>
///
/// <para>Off unless an operator enables it: <c>Enabled</c> defaults to false
/// and both secrets resolve from the environment (never config files).
/// A delivery failure never affects a work item — it is logged and
/// swallowed, per the provider contract.</para>
/// </summary>
[CodeyBoxPlugin(
    id: NtfyNotificationProvider.PluginId,
    displayName: "CodeyBox: ntfy Notifications",
    minHostApiVersion: "1.3")]
public sealed class NtfyNotificationProvider : INotificationProvider, IPluginInitializer
{
    public const string PluginId = "codeybox.ntfy";

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<NtfyNotificationProvider> _log;
    private readonly NtfyMessageStore _messages;

    private ILogger _pluginLog = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public string Name => "ntfy";

    /// <summary>ntfy carries authenticated interactions: offered actions
    /// render as native <c>http</c> buttons whose MAC-signed callback resolves
    /// through the verified inbound endpoint, and landed decisions update the
    /// original notification via its sequence id.</summary>
    public bool SupportsInteractions => true;

    public NtfyNotificationProvider(
        IConfiguration configuration,
        IHttpClientFactory httpClients,
        ILogger<NtfyNotificationProvider> logger)
    {
        _configuration = configuration;
        _httpClients = httpClients;
        _log = logger;
        _messages = new NtfyMessageStore();
    }

    internal NtfyNotificationProvider(
        IConfiguration configuration,
        HttpClient httpClient,
        ILogger<NtfyNotificationProvider> logger,
        NtfyMessageStore? messages = null,
        IHttpClientFactory? httpClients = null)
    {
        _configuration = configuration;
        _httpClients = httpClients ?? new SingleClientFactory(httpClient);
        _log = logger;
        _messages = messages ?? new NtfyMessageStore();
    }

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        _pluginLog = context.Logger;
        var opts = BindOptions();
        if (!opts.Enabled)
        {
            _pluginLog.LogInformation("ntfy notifications are disabled (codeybox.ntfy Enabled=false).");
            return Task.CompletedTask;
        }
        if (!Uri.TryCreate(opts.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
            _pluginLog.LogWarning("ntfy notifications are enabled but BaseUrl '{BaseUrl}' is not an absolute http(s) origin; delivery will fail.", opts.BaseUrl);
        if (string.IsNullOrWhiteSpace(opts.DefaultTopic))
            _pluginLog.LogWarning("ntfy notifications are enabled with no DefaultTopic; notifications without an explicit recipient topic will be skipped.");
        if (opts.ActionsMode == NtfyActionsMode.Buttons)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(opts.InteractionSecretEnvVar)))
                _pluginLog.LogWarning("ntfy action buttons are enabled but env var '{EnvVar}' is not set; actions will render as answer links until the interaction secret is provided.", opts.InteractionSecretEnvVar);
            if (NtfyMessageBuilder.CallbackUrl(ResolvePublicBaseUrl(opts)) is null)
                _pluginLog.LogWarning("ntfy action buttons are enabled but no usable public base URL is configured (PublicBaseUrl or CodeyBox:PublicBaseUrl); actions will render as answer links.");
        }
        _pluginLog.LogInformation("ntfy notifications enabled.");
        return Task.CompletedTask;
    }

    public async Task SendAsync(Notification notification, CancellationToken ct)
    {
        var opts = BindOptions();
        if (!opts.Enabled)
            return;

        _messages.Configure(opts.EntryLifetime, opts.MaxEntries);

        var topic = ResolveTopic(notification, opts);
        if (topic is null)
        {
            _log.LogWarning("NtfyNotificationProvider: no topic for notification {Condition}; set a recipient or DefaultTopic",
                notification.ConditionId);
            return;
        }
        if (!IsUsableBaseUrl(opts.BaseUrl))
        {
            _log.LogWarning("NtfyNotificationProvider: BaseUrl '{BaseUrl}' is not an absolute http(s) origin; skipping notification {Condition}",
                opts.BaseUrl, notification.ConditionId);
            return;
        }

        var publicBaseUrl = ResolvePublicBaseUrl(opts);
        var secret = Environment.GetEnvironmentVariable(opts.InteractionSecretEnvVar);
        if (opts.ActionsMode == NtfyActionsMode.Buttons
            && notification.Actions is { Count: > 0 }
            && !NtfyMessageBuilder.CanRenderButtons(opts, NtfyMessageBuilder.CallbackUrl(publicBaseUrl), secret))
        {
            _log.LogWarning(
                "NtfyNotificationProvider: action buttons requested for {Condition} but the interaction secret or public base URL is missing; rendering answer links instead",
                notification.ConditionId);
        }

        var (payload, _) = NtfyMessageBuilder.BuildPublishPayload(
            notification, opts, topic, publicBaseUrl, secret);

        var timeout = opts.PostTimeoutSeconds >= 1
            ? TimeSpan.FromSeconds(opts.PostTimeoutSeconds)
            : TimeSpan.FromSeconds(15);

        NtfyApiClient.PostResult result;
        try
        {
            var api = new NtfyApiClient(_httpClients.CreateClient(), opts.BaseUrl);
            result = await api.PublishAsync(
                Environment.GetEnvironmentVariable(opts.TokenEnvVar), payload, timeout, ct);
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
            _log.LogError(ex, "NtfyNotificationProvider: delivery failed for condition {Condition}",
                notification.ConditionId);
            return;
        }

        if (!result.Ok)
        {
            _log.LogWarning("NtfyNotificationProvider: publish failed ({Error}) for condition {Condition}",
                result.Error, notification.ConditionId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(notification.CorrelationToken))
            _messages.Remember(notification.CorrelationToken, topic);

        _log.LogInformation("NtfyNotificationProvider: published notification {Condition} ({Severity}) to topic",
            notification.ConditionId, notification.Severity);
    }

    /// <summary>Reflect a landed decision back into the topic by republishing
    /// over the question's <c>sequence_id</c> — ntfy clients replace the
    /// original notification with what was decided and by whom. Best-effort:
    /// failures are logged and swallowed so they can never affect the work
    /// item.</summary>
    public async Task UpdateDecisionAsync(Notification notification, string decisionSummary, CancellationToken ct)
    {
        var opts = BindOptions();
        if (!opts.Enabled)
            return;
        if (string.IsNullOrWhiteSpace(notification.CorrelationToken))
            return;

        _messages.Configure(opts.EntryLifetime, opts.MaxEntries);

        var topic = _messages.TopicFor(notification.CorrelationToken)
            ?? (string.IsNullOrWhiteSpace(opts.DefaultTopic) ? null : opts.DefaultTopic.Trim());
        if (topic is null)
        {
            _log.LogWarning("NtfyNotificationProvider: no topic for decision update on {Condition}; skipping",
                notification.ConditionId);
            return;
        }
        if (!IsUsableBaseUrl(opts.BaseUrl))
            return;

        var payload = NtfyMessageBuilder.BuildDecidedPayload(
            topic, notification.Title, decisionSummary, notification.CorrelationToken, opts);
        var timeout = opts.PostTimeoutSeconds >= 1
            ? TimeSpan.FromSeconds(opts.PostTimeoutSeconds)
            : TimeSpan.FromSeconds(15);

        try
        {
            var api = new NtfyApiClient(_httpClients.CreateClient(), opts.BaseUrl);
            var result = await api.PublishAsync(
                Environment.GetEnvironmentVariable(opts.TokenEnvVar), payload, timeout, ct);
            if (!result.Ok)
            {
                _log.LogWarning("NtfyNotificationProvider: decision update failed ({Error}) for condition {Condition}",
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
            _log.LogError(ex, "NtfyNotificationProvider: decision update failed for condition {Condition}",
                notification.ConditionId);
        }
    }

    internal NtfyPluginOptions BindOptions()
    {
        var opts = new NtfyPluginOptions();
        _configuration.GetSection($"CodeyBox:Plugins:{PluginId}").Bind(opts);
        return opts;
    }

    private string? ResolvePublicBaseUrl(NtfyPluginOptions opts) =>
        !string.IsNullOrWhiteSpace(opts.PublicBaseUrl)
            ? opts.PublicBaseUrl.Trim()
            : _configuration["CodeyBox:PublicBaseUrl"];

    private static bool IsUsableBaseUrl(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string? ResolveTopic(Notification notification, NtfyPluginOptions opts)
    {
        // ntfy publishes to one topic per call; the first non-empty
        // recipient wins.
        if (notification.Recipients is { Count: > 0 })
        {
            foreach (var recipient in notification.Recipients)
            {
                if (!string.IsNullOrWhiteSpace(recipient))
                    return recipient.Trim();
            }
        }
        return string.IsNullOrWhiteSpace(opts.DefaultTopic) ? null : opts.DefaultTopic.Trim();
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
}
