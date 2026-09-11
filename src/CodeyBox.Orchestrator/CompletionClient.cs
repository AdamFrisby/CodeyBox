using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Configuration-driven implementation of <see cref="ICompletionClient"/>.
/// The destination (endpoint, credentials, model, headers, wire shape) comes
/// entirely from <see cref="CompletionRequest"/>; this type contains no vendor
/// endpoints. Every call is bounded (prompt size, timeout, response size) and
/// every operational failure is returned as <see cref="CompletionResult"/>.
/// Authorization material, header values, and body bytes are never logged.
/// </summary>
public sealed class CompletionClient : ICompletionClient
{
    private const int MaxMessages = 64;
    private const int MaxModelChars = 128;
    private const int MaxExtraHeaders = 16;
    private const int MaxHeaderNameChars = 64;
    private const int MaxHeaderValueChars = 4096;
    private const int DefaultTimeoutSeconds = 60;
    private const int DefaultMaxResponseBytes = 512 * 1024;
    private const int DefaultMaxPromptChars = 200_000;
    private const int StreamChunkBytes = 8192;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<CompletionClientOptions> _options;
    private readonly ILogger<CompletionClient> _log;

    public CompletionClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<CompletionClientOptions> options,
        ILogger<CompletionClient> log)
        : this(httpClientFactory, () => options.CurrentValue, log)
    {
    }

    public CompletionClient(
        IHttpClientFactory httpClientFactory,
        Func<CompletionClientOptions> options,
        ILogger<CompletionClient> log)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _log = log;
    }

    public async Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var opts = _options();
        var timeoutSeconds = opts.RequestTimeoutSeconds > 0 ? Math.Min(opts.RequestTimeoutSeconds, 600) : DefaultTimeoutSeconds;
        var maxResponseBytes = opts.MaxResponseBytes > 0 ? opts.MaxResponseBytes : DefaultMaxResponseBytes;
        var maxPromptChars = opts.MaxPromptChars > 0 ? opts.MaxPromptChars : DefaultMaxPromptChars;

        var validation = Validate(request, maxPromptChars);
        if (validation is not null)
            return validation;

        var endpoint = new Uri(request.Endpoint);
        var promptChars = CountPromptChars(request);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        string body;
        try
        {
            body = BuildBody(request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(CompletionStatus.InvalidRequest, $"could not encode {request.WireApi} request: {ex.GetType().Name}");
        }

        HttpResponseMessage response;
        try
        {
            var client = _httpClientFactory.CreateClient(ClientName(opts));
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            AttachAuth(httpRequest, request);

            response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogOutcome(request, endpoint, CompletionStatus.TimedOut, promptChars, 0);
            return Fail(CompletionStatus.TimedOut, $"request exceeded {timeoutSeconds}s timeout");
        }
        catch (HttpRequestException)
        {
            LogOutcome(request, endpoint, CompletionStatus.TransportFailed, promptChars, 0);
            return Fail(CompletionStatus.TransportFailed, "transport failure");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return MapErrorStatus(request, endpoint, response, promptChars);

            CappedRead read;
            try
            {
                read = await ReadCappedAsync(response.Content, maxResponseBytes, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LogOutcome(request, endpoint, CompletionStatus.TimedOut, promptChars, 0);
                return Fail(CompletionStatus.TimedOut, $"request exceeded {timeoutSeconds}s timeout");
            }
            catch (HttpRequestException)
            {
                LogOutcome(request, endpoint, CompletionStatus.TransportFailed, promptChars, 0);
                return Fail(CompletionStatus.TransportFailed, "transport failure");
            }
            if (!read.WithinCap)
            {
                LogOutcome(request, endpoint, CompletionStatus.ResponseTooLarge, promptChars, read.ByteCount);
                return Fail(CompletionStatus.ResponseTooLarge, $"response exceeded {maxResponseBytes} byte cap");
            }

            var parsed = ParseSuccess(request, read.Text);
            LogOutcome(request, endpoint, parsed.Status, promptChars, read.ByteCount, parsed.Usage);
            return parsed;
        }
    }

    private CompletionResult? Validate(CompletionRequest request, int maxPromptChars)
    {
        if (string.IsNullOrWhiteSpace(request.Endpoint)
            || !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Fail(CompletionStatus.InvalidRequest, "endpoint must be an absolute http(s) URL");
        }
        if (string.IsNullOrWhiteSpace(request.Model) || request.Model.Length > MaxModelChars)
            return Fail(CompletionStatus.InvalidRequest, "model must be 1-128 chars");
        if (request.Messages is null || request.Messages.Count == 0 || request.Messages.Count > MaxMessages)
            return Fail(CompletionStatus.InvalidRequest, $"messages must contain 1-{MaxMessages} entries");
        foreach (var message in request.Messages)
        {
            if (message is null || message.Role is null || message.Content is null)
                return Fail(CompletionStatus.InvalidRequest, "messages must have non-null role and content");
        }
        if (request.MaxOutputTokens < 1)
            return Fail(CompletionStatus.InvalidRequest, "maxOutputTokens must be >= 1");
        if (request.ExtraHeaders is not null)
        {
            if (request.ExtraHeaders.Count > MaxExtraHeaders)
                return Fail(CompletionStatus.InvalidRequest, $"at most {MaxExtraHeaders} extra headers");
            foreach (var pair in request.ExtraHeaders)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)
                    || pair.Key.Length > MaxHeaderNameChars
                    || pair.Key.Contains(':')
                    || ContainsControlChars(pair.Key))
                {
                    return Fail(CompletionStatus.InvalidRequest, "extra header name is invalid");
                }
                if (string.Equals(pair.Key, "authorization", StringComparison.OrdinalIgnoreCase))
                    return Fail(CompletionStatus.InvalidRequest, "use bearerToken instead of an authorization header");
                if (pair.Value is null || pair.Value.Length > MaxHeaderValueChars || ContainsControlChars(pair.Value))
                    return Fail(CompletionStatus.InvalidRequest, "extra header value is invalid");
            }
        }
        if (CountPromptChars(request) > maxPromptChars)
            return Fail(CompletionStatus.PromptTooLarge, $"prompt exceeded {maxPromptChars} char cap; no request was sent");
        return null;
    }

    private static int CountPromptChars(CompletionRequest request)
    {
        var total = 0;
        foreach (var message in request.Messages)
            total += (message?.Role?.Length ?? 0) + (message?.Content?.Length ?? 0);
        return total;
    }

    private static bool ContainsControlChars(string value)
    {
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' or '\0')
                return true;
        }
        return false;
    }

    private static string ClientName(CompletionClientOptions opts)
        => string.IsNullOrWhiteSpace(opts.HttpClientName) ? "completion" : opts.HttpClientName;

    private static void AttachAuth(HttpRequestMessage httpRequest, CompletionRequest request)
    {
        if (!string.IsNullOrEmpty(request.BearerToken))
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.BearerToken);
        if (request.ExtraHeaders is not null)
        {
            foreach (var pair in request.ExtraHeaders)
                httpRequest.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
    }

    private static string BuildBody(CompletionRequest request) => request.WireApi switch
    {
        CompletionWireApi.OpenAiChatCompletions => JsonSerializer.Serialize(new
        {
            model = request.Model,
            messages = request.Messages.Select(static m => new { role = m.Role, content = m.Content }).ToArray(),
            temperature = request.Temperature,
            max_tokens = request.MaxOutputTokens,
        }, JsonOpts),
        CompletionWireApi.OpenAiResponses => JsonSerializer.Serialize(new
        {
            model = request.Model,
            input = request.Messages.Select(static m => new { role = m.Role, content = m.Content }).ToArray(),
            temperature = request.Temperature,
            max_output_tokens = request.MaxOutputTokens,
        }, JsonOpts),
        CompletionWireApi.AnthropicMessages => BuildAnthropicBody(request),
        CompletionWireApi.GeminiGenerateContent => BuildGeminiBody(request),
        _ => throw new InvalidOperationException($"unknown wire api {request.WireApi}"),
    };

    private static string BuildAnthropicBody(CompletionRequest request)
    {
        var system = string.Join("\n\n", request.Messages
            .Where(static m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(static m => m.Content));
        var messages = request.Messages
            .Where(static m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(static m => new { role = m.Role, content = m.Content })
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            model = request.Model,
            max_tokens = request.MaxOutputTokens,
            temperature = request.Temperature,
            system = string.IsNullOrEmpty(system) ? null : system,
            messages,
        }, JsonOpts);
    }

    private static string BuildGeminiBody(CompletionRequest request)
    {
        var prompt = string.Join("\n\n", request.Messages.Select(static m => m.Content));
        var contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } };
        object inner = new
        {
            contents,
            generationConfig = new { maxOutputTokens = request.MaxOutputTokens, temperature = request.Temperature },
        };
        object body = request.GeminiOAuthEnvelope
            ? new { model = $"models/{request.Model}", request = inner }
            : inner;
        return JsonSerializer.Serialize(body, JsonOpts);
    }

    private CompletionResult MapErrorStatus(
        CompletionRequest request, Uri endpoint, HttpResponseMessage response, int promptChars)
    {
        var code = (int)response.StatusCode;
        var status = response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => CompletionStatus.AuthenticationFailed,
            System.Net.HttpStatusCode.Forbidden => CompletionStatus.AuthenticationFailed,
            System.Net.HttpStatusCode.TooManyRequests => CompletionStatus.RateLimited,
            >= System.Net.HttpStatusCode.InternalServerError => CompletionStatus.ServerError,
            _ => CompletionStatus.InvalidRequest,
        };
        LogOutcome(request, endpoint, status, promptChars, 0);
        return Fail(status, $"HTTP {code}");
    }

    private static CompletionResult ParseSuccess(CompletionRequest request, string text)
    {
        try
        {
            return request.WireApi switch
            {
                CompletionWireApi.OpenAiChatCompletions => ParseChatCompletions(text),
                CompletionWireApi.OpenAiResponses => ParseResponses(text),
                CompletionWireApi.AnthropicMessages => ParseAnthropic(text),
                CompletionWireApi.GeminiGenerateContent => ParseGemini(text),
                _ => Fail(CompletionStatus.InvalidRequest, "unknown wire api"),
            };
        }
        catch (JsonException)
        {
            return Fail(CompletionStatus.EmptyCompletion, "unexpected response shape");
        }
    }

    private static CompletionResult ParseChatCompletions(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return Fail(CompletionStatus.EmptyCompletion, "unexpected response shape");
        var first = choices.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
            return Fail(CompletionStatus.EmptyCompletion, "empty completion");
        if (first.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            if (message.TryGetProperty("refusal", out var refusal)
                && refusal.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(refusal.GetString()))
            {
                return new CompletionResult { Status = CompletionStatus.Refused, Usage = ReadChatUsage(doc.RootElement) };
            }
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var output = content.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(output))
                    return new CompletionResult { Status = CompletionStatus.EmptyCompletion, Usage = ReadChatUsage(doc.RootElement) };
                return new CompletionResult { Status = CompletionStatus.Succeeded, Text = output, Usage = ReadChatUsage(doc.RootElement) };
            }
        }
        return Fail(CompletionStatus.EmptyCompletion, "empty completion");
    }

    private static CompletionUsage ReadChatUsage(JsonElement root)
    {
        var prompt = 0;
        var output = 0;
        var cached = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            prompt = TryGetInt(usage, "prompt_tokens") ?? 0;
            output = TryGetInt(usage, "completion_tokens") ?? 0;
            if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                cached = TryGetInt(details, "cached_tokens") ?? 0;
        }
        return new CompletionUsage(prompt, cached, output);
    }

    private static CompletionResult ParseResponses(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object
                        && block.TryGetProperty("type", out var type)
                        && type.GetString() == "output_text"
                        && block.TryGetProperty("text", out var slice)
                        && slice.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(slice.GetString());
                    }
                }
            }
        }
        else if (doc.RootElement.TryGetProperty("output_text", out var direct) && direct.ValueKind == JsonValueKind.String)
        {
            sb.Append(direct.GetString());
        }
        var usage = ReadResponsesUsage(doc.RootElement);
        if (sb.Length == 0)
            return new CompletionResult { Status = CompletionStatus.EmptyCompletion, Usage = usage };
        return new CompletionResult { Status = CompletionStatus.Succeeded, Text = sb.ToString(), Usage = usage };
    }

    private static CompletionUsage ReadResponsesUsage(JsonElement root)
    {
        var prompt = 0;
        var output = 0;
        var cached = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            prompt = TryGetInt(usage, "input_tokens") ?? 0;
            output = TryGetInt(usage, "output_tokens") ?? 0;
            if (usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                cached = TryGetInt(details, "cached_tokens") ?? 0;
        }
        return new CompletionUsage(prompt, cached, output);
    }

    private static CompletionResult ParseAnthropic(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("text", out var slice)
                    && slice.ValueKind == JsonValueKind.String)
                {
                    sb.Append(slice.GetString());
                }
            }
        }
        var usage = ReadAnthropicUsage(doc.RootElement);
        if (sb.Length == 0)
            return new CompletionResult { Status = CompletionStatus.EmptyCompletion, Usage = usage };
        return new CompletionResult { Status = CompletionStatus.Succeeded, Text = sb.ToString(), Usage = usage };
    }

    private static CompletionUsage ReadAnthropicUsage(JsonElement root)
    {
        var input = 0;
        var cached = 0;
        var output = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = TryGetInt(usage, "input_tokens") ?? 0;
            cached = TryGetInt(usage, "cache_read_input_tokens") ?? 0;
            output = TryGetInt(usage, "output_tokens") ?? 0;
        }
        return new CompletionUsage(input + cached, cached, output);
    }

    private static CompletionResult ParseGemini(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("response", out var wrapped)
            && wrapped.ValueKind == JsonValueKind.Object)
        {
            root = wrapped;
        }
        var sb = new StringBuilder();
        if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object
                    || !candidate.TryGetProperty("content", out var content)
                    || !content.TryGetProperty("parts", out var parts)
                    || parts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var slice)
                        && slice.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(slice.GetString());
                    }
                }
            }
        }
        var usage = ReadGeminiUsage(root);
        if (sb.Length == 0)
            return new CompletionResult { Status = CompletionStatus.EmptyCompletion, Usage = usage };
        return new CompletionResult { Status = CompletionStatus.Succeeded, Text = sb.ToString(), Usage = usage };
    }

    private static CompletionUsage ReadGeminiUsage(JsonElement root)
    {
        var prompt = 0;
        var cached = 0;
        var output = 0;
        if (root.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            prompt = TryGetInt(usage, "promptTokenCount") ?? 0;
            cached = TryGetInt(usage, "cachedContentTokenCount")
                ?? TryGetInt(usage, "cachedInputTokenCount")
                ?? 0;
            output = TryGetInt(usage, "candidatesTokenCount") ?? 0;
        }
        return new CompletionUsage(prompt, cached, output);
    }

    private static int? TryGetInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            return Math.Max(0, intValue);
        return null;
    }

    private sealed record CappedRead(bool WithinCap, string Text, long ByteCount);

    private static async Task<CappedRead> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is long declared && declared > maxBytes)
            return new CappedRead(false, "", declared);
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[StreamChunkBytes];
        using var collected = new MemoryStream();
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            if (collected.Length + read > maxBytes)
                return new CappedRead(false, "", collected.Length + read);
            collected.Write(buffer, 0, read);
        }
        var bytes = collected.ToArray();
        return new CappedRead(true, Encoding.UTF8.GetString(bytes), bytes.Length);
    }

    private static CompletionResult Fail(CompletionStatus status, string detail)
        => new() { Status = status, ErrorDetail = detail };

    private void LogOutcome(
        CompletionRequest request, Uri endpoint, CompletionStatus status, int promptChars, long responseBytes, CompletionUsage? usage = null)
    {
        _log.LogInformation(
            "Completion call wire={Wire} host={Host} model={Model} status={Status} promptChars={PromptChars} responseBytes={ResponseBytes} inputTokens={Input} outputTokens={Output}",
            request.WireApi,
            endpoint.Host,
            request.Model,
            status,
            promptChars,
            responseBytes,
            usage?.InputTokens ?? 0,
            usage?.OutputTokens ?? 0);
    }
}
