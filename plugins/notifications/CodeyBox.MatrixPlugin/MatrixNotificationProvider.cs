using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.MatrixPlugin;

/// <summary>
/// Matrix notification provider: work-item and fleet notifications posted as
/// <c>m.room.message</c> events to explicitly configured rooms over the Matrix
/// Client-Server API (<c>PUT /_matrix/client/v3/rooms/{roomId}/send/…</c>),
/// rendered with severity markers, fields, and real answer links rather than
/// a dumped text blob.
///
/// <para>Matrix is <b>notification-only</b> here
/// (<see cref="SupportsInteractions"/> = false): offered actions never become
/// interactive controls, and the plugin accepts no inbound approvals or
/// answers — the question stays answerable through the
/// <see cref="Notification.AnswerUrl"/> link surfaced in the message. No
/// inbound exposure is needed and no interaction verifier is supplied: the
/// foundation refuses anything POSTed to
/// <c>/webhooks/interactions/matrix</c>.</para>
///
/// <para>Unencrypted rooms only, verified per send and failed closed: before
/// posting, the provider reads the room's <c>m.room.encryption</c> state. A
/// present state event (encrypted) or any unverifiable outcome (auth failure,
/// rate limit, server error, malformed or oversized body — unknown) skips
/// the send with a warning. Encrypted rooms are never downgraded and E2EE is
/// never claimed. The plugin performs no room creation, join, invite, or
/// membership change — every room must already exist with the sender's user
/// joined.</para>
///
/// <para>Threading and idempotency: the first notification for a work item
/// posts top-level and its event ID becomes the thread root; follow-ups post
/// as <c>m.thread</c> replies. Every send reuses one stable transaction ID
/// (<see cref="MatrixTxnIds"/>), so a rate-limit retry re-PUTs the same path
/// and the server returns the original event instead of double-posting. Off
/// unless an operator enables it: <c>Enabled</c> defaults to false and the
/// access token resolves from the environment (never config files). A
/// delivery failure never affects a work item — it is logged and swallowed,
/// per the provider contract.</para>
/// </summary>
[CodeyBoxPlugin(
    id: MatrixNotificationProvider.PluginId,
    displayName: "CodeyBox: Matrix Notifications",
    minHostApiVersion: "1.3")]
public sealed class MatrixNotificationProvider : INotificationProvider, IPluginInitializer, IDisposable
{
    public const string PluginId = "codeybox.matrix";

    /// <summary>Upper bound applied to a configured <c>MaxRetries</c> so a
    /// bad config edit cannot retry unboundedly.</summary>
    internal const int MaxRetriesCap = 5;

    /// <summary>Fallback retry-delay cap in seconds when the configured
    /// <c>MaxRetryDelaySeconds</c> is invalid.</summary>
    internal const int DefaultMaxRetryDelaySeconds = 30;

    private readonly IConfiguration _configuration;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger<MatrixNotificationProvider> _log;
    private readonly TimeProvider _clock;
    private readonly MatrixThreadStore _threads;

    private ILogger _pluginLog = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public string Name => "matrix";

    /// <summary>Matrix messages carry no authenticated interaction here — a
    /// question surfaces through the AnswerUrl link instead of controls, and
    /// inbound answers are not accepted in this task.</summary>
    public bool SupportsInteractions => false;

    public MatrixNotificationProvider(
        IConfiguration configuration,
        ILogger<MatrixNotificationProvider> logger,
        TimeProvider? clock = null)
    {
        _configuration = configuration;
        _http = MatrixHttpClients.Create();
        _ownsHttp = true;
        _log = logger;
        _clock = clock ?? TimeProvider.System;
        _threads = new MatrixThreadStore(clock: _clock);
    }

    internal MatrixNotificationProvider(
        IConfiguration configuration,
        HttpClient httpClient,
        ILogger<MatrixNotificationProvider> logger,
        TimeProvider? clock = null,
        MatrixThreadStore? threads = null)
    {
        _configuration = configuration;
        _http = httpClient;
        _ownsHttp = false;
        _log = logger;
        _clock = clock ?? TimeProvider.System;
        _threads = threads ?? new MatrixThreadStore(clock: _clock);
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
            _pluginLog.LogInformation("Matrix notifications are disabled (codeybox.matrix Enabled=false).");
            return Task.CompletedTask;
        }
        if (!TryResolveHomeserver(opts, out _, out var homeserverFailure))
            _pluginLog.LogWarning("Matrix notifications are enabled but unusable: {Reason}", homeserverFailure);
        else if (!HasConfiguredRoom(opts))
            _pluginLog.LogWarning("Matrix notifications are enabled with no DefaultRoomId or AllowedRoomIds; notifications will be skipped until a room is configured.");
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(opts.AccessTokenEnvVar)))
            _pluginLog.LogWarning("Matrix notifications are enabled but env var '{EnvVar}' is not set; delivery will be skipped until the access token is provided.", opts.AccessTokenEnvVar);
        else
            _pluginLog.LogInformation("Matrix notifications enabled.");
        return Task.CompletedTask;
    }

    public async Task SendAsync(Notification notification, CancellationToken ct)
    {
        var opts = BindOptions();
        if (!opts.Enabled)
            return;

        _threads.Configure(opts.EntryLifetime, opts.MaxEntries);

        var token = Environment.GetEnvironmentVariable(opts.AccessTokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.LogWarning("MatrixNotificationProvider: env var '{EnvVar}' is not set; skipping notification {Condition}",
                opts.AccessTokenEnvVar, notification.ConditionId);
            return;
        }

        var roomId = ResolveRoom(notification, opts);
        if (roomId is null)
        {
            _log.LogWarning("MatrixNotificationProvider: no room for notification {Condition}; set a recipient or DefaultRoomId",
                notification.ConditionId);
            return;
        }
        if (!MatrixRoomIds.IsValid(roomId))
        {
            _log.LogWarning("MatrixNotificationProvider: refusing non-room-ID recipient for notification {Condition}",
                notification.ConditionId);
            return;
        }
        if (!IsRoomAllowed(roomId, opts))
        {
            _log.LogWarning("MatrixNotificationProvider: room is not explicitly configured; skipping notification {Condition}",
                notification.ConditionId);
            return;
        }

        if (!TryResolveHomeserver(opts, out var homeserver, out var homeserverFailure))
        {
            _log.LogWarning("MatrixNotificationProvider: {Reason}; skipping notification {Condition}",
                homeserverFailure, notification.ConditionId);
            return;
        }

        var timeout = NotificationDelivery.PostTimeoutOrDefault(opts.PostTimeoutSeconds, _log);
        var api = new MatrixApiClient(_http);

        MatrixApiClient.SecurityResult security;
        try
        {
            security = await api.GetRoomSecurityAsync(token, homeserver, roomId, timeout, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MatrixNotificationProvider: room security check failed for notification {Condition}",
                notification.ConditionId);
            return;
        }

        if (security.State == RoomSecurityState.Encrypted)
        {
            _log.LogWarning("MatrixNotificationProvider: room is end-to-end encrypted; refusing to post unencrypted notification {Condition} (encrypted rooms are never downgraded)",
                notification.ConditionId);
            return;
        }
        if (security.State == RoomSecurityState.Unknown)
        {
            _log.LogWarning("MatrixNotificationProvider: room security mode unverifiable ({Error}); failing closed for notification {Condition}",
                security.Error, notification.ConditionId);
            return;
        }

        var boundWorkItemId = NotificationBinding.WorkItemIdFor(notification);
        string? threadRoot = null;
        if (opts.ThreadByWorkItem && boundWorkItemId is not null)
            threadRoot = _threads.ThreadRootFor(roomId, boundWorkItemId);

        var (content, workItemId) = MatrixMessageBuilder.BuildMessage(notification, opts, threadRoot);

        var txnId = MatrixTxnIds.ForNotification(notification);
        var maxRetries = Math.Clamp(opts.MaxRetries, 0, MaxRetriesCap);
        var maxDelay = opts.MaxRetryDelaySeconds >= 1
            ? TimeSpan.FromSeconds(opts.MaxRetryDelaySeconds)
            : TimeSpan.FromSeconds(DefaultMaxRetryDelaySeconds);

        MatrixApiClient.SendResult? result = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Same transaction ID on every attempt: the server dedups on
                // (token, txnId), so a retry can never double-post.
                result = await api.SendMessageAsync(token, homeserver, roomId, txnId, content, timeout, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "MatrixNotificationProvider: delivery failed for condition {Condition}",
                    notification.ConditionId);
                return;
            }

            if (!result.RateLimited || attempt >= maxRetries)
                break;

            var delay = result.RetryAfterMs is { } ms ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromSeconds(1);
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;
            if (delay > maxDelay)
                delay = maxDelay;
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        if (result is null || !result.Ok)
        {
            _log.LogWarning("MatrixNotificationProvider: send failed ({Error}) for condition {Condition}",
                result?.Error, notification.ConditionId);
            return;
        }

        if (workItemId is not null && result.EventId is not null)
        {
            if (opts.ThreadByWorkItem && _threads.ThreadRootFor(roomId, workItemId) is null)
                _threads.RememberThreadRoot(roomId, workItemId, result.EventId);
            if (!string.IsNullOrWhiteSpace(notification.CorrelationToken))
                _threads.RememberEvent(notification.CorrelationToken, roomId, result.EventId);
        }

        _log.LogInformation("MatrixNotificationProvider: posted notification {Condition} ({Severity}) to room",
            notification.ConditionId, notification.Severity);
    }

    internal MatrixPluginOptions BindOptions()
    {
        var opts = new MatrixPluginOptions();
        _configuration.GetSection($"CodeyBox:Plugins:{PluginId}").Bind(opts);
        return opts;
    }

    private static string? ResolveRoom(Notification notification, MatrixPluginOptions opts)
    {
        // One room per call; the first non-empty recipient wins.
        if (notification.Recipients is { Count: > 0 })
        {
            foreach (var recipient in notification.Recipients)
            {
                if (!string.IsNullOrWhiteSpace(recipient))
                    return recipient.Trim();
            }
        }
        return string.IsNullOrWhiteSpace(opts.DefaultRoomId) ? null : opts.DefaultRoomId.Trim();
    }

    /// <summary>The resolved room must be explicitly configured: an exact
    /// member of <c>AllowedRoomIds</c>, or equal to <c>DefaultRoomId</c> when
    /// no allowlist is set. Exact-match only — never substring.</summary>
    internal static bool IsRoomAllowed(string roomId, MatrixPluginOptions opts)
    {
        var allowed = opts.AllowedRoomIds
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim());
        if (allowed.Any())
            return allowed.Contains(roomId, StringComparer.Ordinal);
        return !string.IsNullOrWhiteSpace(opts.DefaultRoomId)
            && string.Equals(roomId, opts.DefaultRoomId.Trim(), StringComparison.Ordinal);
    }

    internal static bool HasConfiguredRoom(MatrixPluginOptions opts) =>
        !string.IsNullOrWhiteSpace(opts.DefaultRoomId)
        || opts.AllowedRoomIds.Any(r => !string.IsNullOrWhiteSpace(r));

    /// <summary>Resolve the homeserver base from configured
    /// <see cref="MatrixPluginOptions.HomeserverUrl"/>. The URL is
    /// operator-supplied config, not untrusted input, but the sink still
    /// carries its own guard: only absolute http(s) URIs resolve, and plain
    /// http resolves only when the operator opted into cleartext-token
    /// exposure via <see cref="MatrixPluginOptions.AllowPlainHttp"/>.</summary>
    internal static bool TryResolveHomeserver(MatrixPluginOptions opts, out Uri homeserver, out string failure)
    {
        homeserver = null!;
        failure = string.Empty;
        if (string.IsNullOrWhiteSpace(opts.HomeserverUrl))
        {
            failure = "HomeserverUrl is not configured";
            return false;
        }
        var trimmed = opts.HomeserverUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            failure = "HomeserverUrl is not a clean absolute http/https base URL (no query, fragment, or user-info)";
            return false;
        }
        if (baseUri.Scheme == Uri.UriSchemeHttp && !opts.AllowPlainHttp)
        {
            failure = "HomeserverUrl is plain http; set AllowPlainHttp=true only if cleartext access-token delivery is acceptable";
            return false;
        }
        homeserver = baseUri;
        return true;
    }
}
