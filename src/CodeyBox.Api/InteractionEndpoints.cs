using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

/// <summary>
/// Generic inbound interaction endpoint: one route accepts an interaction
/// from any registered provider, verifies it, resolves it to a work item
/// question, and records the answer through <see cref="QuestionAnswerPipeline"/>
/// (the same source of truth as <c>POST /workitems/{id}/answer</c>).
///
/// Ordering is load-bearing and mirrors the GitHub release receiver:
/// the signature is verified over the raw body BEFORE the body is parsed
/// for meaning. An unverified interaction is rejected and logged, never
/// partially processed.
/// </summary>
internal static class InteractionEndpoints
{
    /// <summary>Upper bound enforced BEFORE buffering the request body.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>HttpClient name for the response_url round-trip POST.
    /// Must match the registration in Program.cs, which disables redirects.</summary>
    private const string InteractionResponseClientName = "interactions-response";

    private static readonly JsonSerializerOptions PayloadJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Regex IdPattern = new(@"^[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled);

    public static void Map(WebApplication app)
    {
        app.MapPost(InteractionContract.RoutePrefix + "/{provider}", HandleInteractionAsync);
        app.MapGet(InteractionContract.RoutePrefix + "/capabilities", GetCapabilitiesAsync);
    }

    private static async Task<IResult> GetCapabilitiesAsync(
        IOptionsMonitor<InteractionsOptions> interactions,
        IEnumerable<INotificationProvider> providers,
        CancellationToken ct)
    {
        await Task.CompletedTask;
        var opts = interactions.CurrentValue;
        return Results.Ok(new
        {
            enabled = opts.Enabled,
            inboundProviders = opts.Providers
                .Where(p => !string.IsNullOrWhiteSpace(p.Provider))
                .Select(p => new
                {
                    provider = p.Provider,
                    scheme = p.Scheme,
                    channelAllowlist = p.AllowedChannels.Count,
                    userAllowlist = p.AllowedUsers.Count,
                })
                .ToArray(),
            renderProviders = providers
                .Select(p => new { provider = p.Name, supportsInteractions = p.SupportsInteractions })
                .ToArray(),
        });
    }

    private static async Task<IResult> HandleInteractionAsync(
        string provider,
        HttpRequest httpRequest,
        IOptionsMonitor<InteractionsOptions> interactions,
        IEnumerable<IInteractionVerifier> verifiers,
        IEnumerable<INotificationProvider> renderProviders,
        IWorkItemStore store,
        IWorkItemQuestionStore? questionStore,
        ITaskQueue queue,
        IWebhookDispatcher webhooks,
        IProjectRepository projects,
        IHumanDeploymentReviewStore? reviews,
        IInteractionDedupStore dedup,
        IHttpClientFactory httpClients,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("CodeyBox.Api.Interactions");
        var opts = interactions.CurrentValue;

        if (!opts.Enabled)
            return Results.NotFound();

        var providerOpts = opts.Providers.FirstOrDefault(p =>
            string.Equals(p.Provider, provider, StringComparison.OrdinalIgnoreCase));
        if (providerOpts is null || string.IsNullOrWhiteSpace(providerOpts.Provider))
            return Results.NotFound();

        var verifier = verifiers.FirstOrDefault(v =>
            string.Equals(v.Provider, providerOpts.Provider, StringComparison.OrdinalIgnoreCase));
        if (verifier is null)
        {
            log.LogError(
                "Interactions: provider '{Provider}' has no registered verifier; refusing",
                providerOpts.Provider);
            return Results.Json(new { error = "provider not configured" }, statusCode: 503);
        }

        // Read the raw body with a hard cap BEFORE any semantic use, so an
        // oversized or hostile payload cannot exhaust memory.
        if (httpRequest.ContentLength > MaxBodyBytes)
            return Results.Json(new { error = "payload too large" }, statusCode: 413);
        byte[] bodyBytes;
        using (var ms = new MemoryStream())
        {
            var buffer = new byte[8192];
            int total = 0, read;
            while ((read = await httpRequest.Body.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxBodyBytes)
                    return Results.Json(new { error = "payload too large" }, statusCode: 413);
                ms.Write(buffer, 0, read);
            }
            bodyBytes = ms.ToArray();
        }

        var headers = httpRequest.Headers.ToDictionary(
            h => h.Key,
            h => h.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

        // Verification precedes parsing for meaning: nothing below this point
        // runs for an unverified payload.
        var verification = await verifier.VerifyAsync(bodyBytes, headers, ct);
        if (!verification.Valid)
        {
            // Log the provider and the (fixed-vocabulary) reason only — never
            // the payload or secrets.
            log.LogWarning(
                "Interactions: rejected unverified interaction for provider '{Provider}': {Reason}",
                providerOpts.Provider, verification.FailureReason);
            return Results.Unauthorized();
        }

        InteractionPayload? payload = null;
        string? validationError;
        try
        {
            payload = JsonSerializer.Deserialize<InteractionPayload>(bodyBytes, PayloadJsonOpts);
            validationError = payload is null
                ? "malformed interaction payload"
                : ValidatePayload(payload);
        }
        catch (JsonException)
        {
            validationError = "malformed interaction payload";
        }

        // Slack posts native `block_actions` form bodies (payload={...}) to
        // the app's Request URL rather than the canonical JSON shape. When
        // the canonical parse fails on a slack-v0 provider, map the native
        // shape before rejecting — verification already passed either way.
        if (validationError is not null && IsSlackScheme(providerOpts.Scheme))
        {
            if (SlackInteractionParser.TryParse(bodyBytes, out var canonical, out _)
                && canonical is not null)
            {
                payload = new InteractionPayload
                {
                    InteractionId = canonical.InteractionId,
                    WorkItemId = canonical.WorkItemId,
                    QuestionId = canonical.QuestionId,
                    Answer = canonical.Answer,
                    User = new InteractionUser { UserId = canonical.UserId, Login = canonical.Login },
                    ChannelId = canonical.ChannelId,
                    ResponseUrl = canonical.ResponseUrl,
                    CorrelationToken = canonical.CorrelationToken,
                };
                validationError = ValidatePayload(payload);
            }
        }
        if (validationError is not null)
            return Results.BadRequest(new { error = validationError });
        if (payload is null)
            return Results.BadRequest(new { error = "malformed interaction payload" });

        // Replay guard: platforms retry, so the same interaction delivered
        // twice answers once. The claim happens before any state change.
        if (!dedup.TryClaim(payload.InteractionId!))
            return Results.Ok(new { status = "duplicate" });

        // Authorisation: connecting the integration is the grant, narrowed
        // optionally by exact-match channel/user allowlists.
        if (providerOpts.AllowedChannels.Count > 0
            && (string.IsNullOrEmpty(payload.ChannelId)
                || !providerOpts.AllowedChannels.Any(c => string.Equals(c, payload.ChannelId, StringComparison.Ordinal))))
        {
            log.LogWarning(
                "Interactions: rejected interaction from unlisted channel for provider '{Provider}'",
                providerOpts.Provider);
            return Results.Json(new { error = "channel is not authorised for this integration" }, statusCode: 403);
        }
        if (providerOpts.AllowedUsers.Count > 0
            && !providerOpts.AllowedUsers.Any(u => string.Equals(u, payload.User!.UserId, StringComparison.Ordinal)))
        {
            log.LogWarning(
                "Interactions: rejected interaction from unlisted user for provider '{Provider}'",
                providerOpts.Provider);
            return Results.Json(new { error = "user is not authorised for this integration" }, statusCode: 403);
        }

        // Stale-button guard: an interaction that carries a correlation token
        // must match the question it claims to answer.
        if (!string.IsNullOrEmpty(payload.CorrelationToken))
        {
            var expected = NotificationInteractionHelper.CorrelationTokenFor(
                payload.WorkItemId!, payload.QuestionId!);
            if (!string.Equals(payload.CorrelationToken, expected, StringComparison.Ordinal))
                return Results.Conflict(new { error = "stale interaction: it does not match the current question" });
        }

        if (questionStore is null)
            return Results.Json(new { error = "question store not configured" }, statusCode: 503);

        WorkItemId workItemId;
        try { workItemId = WorkItemId.Parse(payload.WorkItemId!); }
        catch (FormatException) { return Results.BadRequest(new { error = "workItemId is not a valid work item id" }); }

        var item = await store.GetAsync(workItemId, ct);
        if (item is null)
            return Results.NotFound(new { error = "work item not found" });

        if (item.State != WorkItemState.NeedsOperatorInput)
            return Results.Conflict(new { error = "stale interaction: the work item is no longer awaiting operator input" });

        var question = await questionStore.GetAsync(item.Id.ToString(), payload.QuestionId!, ct);
        if (question is null)
            return Results.NotFound(new { error = $"question '{payload.QuestionId}' not found" });

        // A stale button must fail cleanly and say why — never silently
        // overwrite or resurrect a decided question.
        if (question.State == "answered")
            return Results.Conflict(new { error = "question was already answered", questionState = question.State });
        if (question.State != "open")
            return Results.Conflict(new { error = $"question is no longer open ({question.State})", questionState = question.State });

        var answeredBy = NotificationInteractionHelper.FormatAnsweredBy(
            providerOpts.Provider, payload.User!.UserId!, payload.User.Login);
        var redactedAnswer = RawOutputRedactor.Redact(payload.Answer!);

        await QuestionAnswerPipeline.AnswerAndResumeAsync(
            item, payload.QuestionId!, redactedAnswer,
            answeredBy: answeredBy, decidedBy: answeredBy,
            questionStore, store, queue, webhooks, projects, reviews, ct);

        log.LogInformation(
            "Interactions: provider '{Provider}' answered question '{QuestionId}' on work item '{WorkItemId}'",
            providerOpts.Provider, payload.QuestionId, item.Id);

        // Round-trip the result where the platform allows it: a response_url
        // lets the original message show what was decided and by whom.
        // Best-effort — delivery problems are logged and swallowed so they
        // can never affect the work item.
        var responseTimeout = TimeSpan.FromSeconds(
            opts.ResponseUpdateTimeoutSeconds >= 1 ? opts.ResponseUpdateTimeoutSeconds : 10);
        await TryUpdateOriginalMessageAsync(payload.ResponseUrl, redactedAnswer, answeredBy, httpClients, log, responseTimeout, ct);

        // Provider-owned loop-close runs last so its final state wins: an
        // interactive provider (e.g. Slack via chat.update) replaces the
        // plain-text response_url replacement with its rich decided state.
        // Best-effort likewise — the answer already landed above.
        await TryProviderDecisionUpdateAsync(
            providerOpts.Provider, renderProviders, payload, question.QuestionText,
            redactedAnswer, answeredBy, log, ct);

        return Results.Ok(new { status = "answered", questionState = "answered" });
    }

    private static bool IsSlackScheme(string? scheme) =>
        string.Equals(scheme, "slack-v0", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hand a landed decision to the matching render provider when
    /// it carries interactions itself. Notification-only providers (the
    /// default) need nothing here — the response_url round-trip above is
    /// their loop-close. Never throws: the provider contract is
    /// log-and-swallow by definition.</summary>
    private static async Task TryProviderDecisionUpdateAsync(
        string providerName,
        IEnumerable<INotificationProvider> renderProviders,
        InteractionPayload payload,
        string? questionText,
        string answer,
        string answeredBy,
        ILogger log,
        CancellationToken ct)
    {
        INotificationProvider? target = null;
        foreach (var candidate in renderProviders)
        {
            if (string.Equals(candidate.Name, providerName, StringComparison.OrdinalIgnoreCase))
            {
                target = candidate;
                break;
            }
        }
        if (target is null || !target.SupportsInteractions)
            return;
        try
        {
            var notification = new Notification
            {
                ConditionId = "operator_question",
                Title = string.IsNullOrWhiteSpace(questionText) ? $"Input needed: {payload.QuestionId}" : questionText,
                Severity = NotificationSeverity.Information,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationToken = payload.CorrelationToken,
            };
            await target.UpdateDecisionAsync(notification, $"Decided: {answer} — by {answeredBy}", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Interactions: provider '{Provider}' decision update failed; decision already recorded",
                providerName);
        }
    }

    private static string? ValidatePayload(InteractionPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.InteractionId) || payload.InteractionId.Length > 128)
            return "interactionId is required (max 128 chars)";
        if (string.IsNullOrWhiteSpace(payload.WorkItemId) || payload.WorkItemId.Length > 64)
            return "workItemId is required (max 64 chars)";
        if (string.IsNullOrWhiteSpace(payload.QuestionId) || !IdPattern.IsMatch(payload.QuestionId))
            return "questionId must be 1-64 alphanumeric/hyphen/underscore characters";
        if (string.IsNullOrWhiteSpace(payload.Answer))
            return "answer is required";
        if (payload.Answer.Length > InteractionContract.MaxAnswerChars)
            return $"answer must be <= {InteractionContract.MaxAnswerChars} chars";
        if (payload.User is null || string.IsNullOrWhiteSpace(payload.User.UserId) || payload.User.UserId.Length > 256)
            return "user.userId is required (max 256 chars)";
        if (payload.User.Login is not null && payload.User.Login.Length > 256)
            return "user.login must be <= 256 chars";
        if (payload.ChannelId is not null && payload.ChannelId.Length > 256)
            return "channelId must be <= 256 chars";
        if (payload.CorrelationToken is not null && payload.CorrelationToken.Length > 256)
            return "correlationToken must be <= 256 chars";
        if (payload.ResponseUrl is not null)
        {
            if (payload.ResponseUrl.Length > 2048
                || !Uri.TryCreate(payload.ResponseUrl, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return "responseUrl must be an absolute https URL (max 2048 chars)";
            // SSRF guard: responseUrl is platform-supplied input. Reject
            // loopback/private/metadata hosts and DNS-rebinding hostnames.
            // Signature verification proves who sent the body, not that the
            // URL is safe to POST to.
            try
            {
                Validation.ValidateWebhookUrl(payload.ResponseUrl, "responseUrl");
            }
            catch (ArgumentException)
            {
                return "responseUrl must not point to a private or internal host";
            }
        }
        return null;
    }

    private static async Task TryUpdateOriginalMessageAsync(
        string? responseUrl,
        string answer,
        string answeredBy,
        IHttpClientFactory httpClients,
        ILogger log,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(responseUrl))
            return;
        // Defense in depth: the sink carries its own guard so a future
        // caller passing an unvalidated URL is still safe.
        try
        {
            Validation.ValidateWebhookUrl(responseUrl, "responseUrl");
        }
        catch (ArgumentException ex)
        {
            log.LogWarning(ex, "Interactions: refusing unsafe response_url; decision already recorded");
            return;
        }
        try
        {
            // Redirects stay disabled (see client registration): a 3xx to a
            // private address would otherwise bypass the blocklist above.
            var client = httpClients.CreateClient(InteractionResponseClientName);
            var body = JsonSerializer.Serialize(
                new { text = $"Decided: {answer} — by {answeredBy}" },
                PayloadJsonOpts);
            using var request = new HttpRequestMessage(HttpMethod.Post, responseUrl);
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                log.LogWarning(
                    "Interactions: response_url update returned {Status}; decision already recorded",
                    (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Timeout: best-effort round-trip only; the answer already landed.
            log.LogWarning(ex, "Interactions: response_url update timed out; decision already recorded");
        }
        catch (Exception ex)
        {
            // Delivery problems are logged and swallowed: the answer landed.
            log.LogWarning(ex, "Interactions: response_url update failed; decision already recorded");
        }
    }
}

/// <summary>Wire shape of one inbound interaction. Field sizes are bounded
/// in <see cref="InteractionEndpoints"/> before use.</summary>
internal sealed class InteractionPayload
{
    [JsonPropertyName("interactionId")] public string? InteractionId { get; set; }
    [JsonPropertyName("workItemId")] public string? WorkItemId { get; set; }
    [JsonPropertyName("questionId")] public string? QuestionId { get; set; }
    [JsonPropertyName("answer")] public string? Answer { get; set; }
    [JsonPropertyName("user")] public InteractionUser? User { get; set; }
    [JsonPropertyName("channelId")] public string? ChannelId { get; set; }
    [JsonPropertyName("responseUrl")] public string? ResponseUrl { get; set; }
    [JsonPropertyName("correlationToken")] public string? CorrelationToken { get; set; }
}

internal sealed class InteractionUser
{
    [JsonPropertyName("userId")] public string? UserId { get; set; }
    [JsonPropertyName("login")] public string? Login { get; set; }
}
