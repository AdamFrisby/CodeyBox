using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GotifyPlugin;

/// <summary>
/// Gotify notification provider: work-item and fleet notifications pushed
/// to a self-hosted Gotify server over its REST API
/// (<c>POST /message</c>, <c>X-Gotify-Key</c> application token), rendered
/// idiomatically — severity as message priority plus an emoji marker,
/// fields as a markdown list, and the answer/Agnes routes as real links and
/// a click target rather than a dumped text blob.
///
/// <para>Gotify cannot carry an authenticated interaction: its clients
/// render a message and can open a URL on tap, but offer no answer buttons
/// and no signed callback the host could verify. This provider therefore
/// declares <see cref="SupportsInteractions"/> = false and surfaces
/// <see cref="Notification.AnswerUrl"/> so a question is never
/// unanswerable — the operator answers in CodeyBox (or Agnes), never
/// through a synthesized inbound path. Correspondingly the plugin supplies
/// no interaction verifier: the foundation refuses anything POSTed to
/// <c>/webhooks/interactions/gotify</c>. No inbound exposure is
/// needed.</para>
///
/// <para>Credential split honoured: Gotify application tokens send, client
/// tokens read — this provider sends, so it holds only the application
/// token via the environment (never config files). The token rides in a
/// request header, so the plugin owns its HTTP client
/// (<see cref="GotifyHttpClients"/>) rather than sharing the host factory:
/// it never follows a redirect, which would re-send the credential to a
/// server-chosen host. Off unless an operator enables it:
/// <c>Enabled</c> defaults to false. A delivery failure never affects a
/// work item — it is logged and swallowed, per the provider
/// contract.</para>
/// </summary>
[CodeyBoxPlugin(
    id: GotifyNotificationProvider.PluginId,
    displayName: "CodeyBox: Gotify Notifications",
    minHostApiVersion: "1.3")]
public sealed class GotifyNotificationProvider : INotificationProvider, IPluginInitializer, IDisposable
{
    public const string PluginId = "codeybox.gotify";

    private readonly IConfiguration _configuration;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger<GotifyNotificationProvider> _log;

    private ILogger _pluginLog = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public string Name => "gotify";

    /// <summary>Gotify messages carry no authenticated interaction — a
    /// question surfaces through the AnswerUrl link instead of buttons, and
    /// the platform has no message-update API for loop-close.</summary>
    public bool SupportsInteractions => false;

    public GotifyNotificationProvider(
        IConfiguration configuration,
        ILogger<GotifyNotificationProvider> logger)
    {
        _configuration = configuration;
        _http = GotifyHttpClients.Create();
        _ownsHttp = true;
        _log = logger;
    }

    internal GotifyNotificationProvider(
        IConfiguration configuration,
        HttpClient httpClient,
        ILogger<GotifyNotificationProvider> logger)
    {
        _configuration = configuration;
        _http = httpClient;
        _log = logger;
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
            _pluginLog.LogInformation("Gotify notifications are disabled (codeybox.gotify Enabled=false).");
            return Task.CompletedTask;
        }
        if (!TryResolveEndpoint(opts, out _, out var endpointFailure))
            _pluginLog.LogWarning("Gotify notifications are enabled but unusable: {Reason}", endpointFailure);
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(opts.AppTokenEnvVar)))
            _pluginLog.LogWarning("Gotify notifications are enabled but env var '{EnvVar}' is not set; delivery will be skipped until the application token is provided.", opts.AppTokenEnvVar);
        else
            _pluginLog.LogInformation("Gotify notifications enabled.");
        return Task.CompletedTask;
    }

    public async Task SendAsync(Notification notification, CancellationToken ct)
    {
        var opts = BindOptions();
        if (!opts.Enabled)
            return;

        if (!TryResolveEndpoint(opts, out var endpoint, out var endpointFailure))
        {
            _log.LogWarning("GotifyNotificationProvider: {Reason}; skipping notification {Condition}",
                endpointFailure, notification.ConditionId);
            return;
        }

        var token = Environment.GetEnvironmentVariable(opts.AppTokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.LogWarning("GotifyNotificationProvider: env var '{EnvVar}' is not set; skipping notification {Condition}",
                opts.AppTokenEnvVar, notification.ConditionId);
            return;
        }

        var payload = GotifyMessageBuilder.BuildMessage(notification, opts);
        var timeout = NotificationDelivery.PostTimeoutOrDefault(opts.PostTimeoutSeconds, _log);

        GotifyApiClient.PostResult result;
        try
        {
            var api = new GotifyApiClient(_http);
            result = await api.PostMessageAsync(token, endpoint, payload, timeout, ct);
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
            _log.LogError(ex, "GotifyNotificationProvider: delivery failed for condition {Condition}",
                notification.ConditionId);
            return;
        }

        if (!result.Ok)
        {
            _log.LogWarning("GotifyNotificationProvider: POST /message failed ({Error}) for condition {Condition}",
                result.Error, notification.ConditionId);
            return;
        }

        _log.LogInformation("GotifyNotificationProvider: posted notification {Condition} ({Severity}) as message {MessageId}",
            notification.ConditionId, notification.Severity, result.MessageId);
    }

    internal GotifyPluginOptions BindOptions()
    {
        var opts = new GotifyPluginOptions();
        _configuration.GetSection($"CodeyBox:Plugins:{PluginId}").Bind(opts);
        return opts;
    }

    /// <summary>Resolve the <c>POST /message</c> endpoint from configured
    /// <see cref="GotifyPluginOptions.ServerUrl"/>. The URL is
    /// operator-supplied config, not untrusted input, but the sink still
    /// carries its own guard: only absolute http(s) URIs resolve, and plain
    /// http resolves only when the operator opted into cleartext-token
    /// exposure via <see cref="GotifyPluginOptions.AllowPlainHttp"/>
    /// (self-hosted Gotify commonly sits on a private LAN — a private
    /// address is expected here, never blocked).</summary>
    internal static bool TryResolveEndpoint(GotifyPluginOptions opts, out Uri endpoint, out string failure)
    {
        endpoint = null!;
        failure = string.Empty;
        if (string.IsNullOrWhiteSpace(opts.ServerUrl))
        {
            failure = "ServerUrl is not configured";
            return false;
        }
        var trimmed = opts.ServerUrl.TrimEnd('/');
        // Only a clean absolute base is accepted: a query, fragment, or
        // embedded user-info would corrupt the derived /message endpoint
        // (and user-info would smuggle credentials into the URL).
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            failure = "ServerUrl is not a clean absolute http/https base URL (no query, fragment, or user-info)";
            return false;
        }
        var uri = new Uri($"{trimmed}/message");
        if (uri.Scheme == Uri.UriSchemeHttp && !opts.AllowPlainHttp)
        {
            failure = "ServerUrl is plain http; set AllowPlainHttp=true only if cleartext application-token delivery is acceptable";
            return false;
        }
        endpoint = uri;
        return true;
    }
}
