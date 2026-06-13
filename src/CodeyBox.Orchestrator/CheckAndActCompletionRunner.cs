using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

public interface ICheckAndActCompletionRunner
{
    /// <summary>
    /// Runs one no-tools completion for a check. Returns null only when no
    /// account-safe completion provider is configured, allowing the caller to
    /// fall back to the agentic sandbox path.
    /// </summary>
    Task<CheckAndActCompletionResult?> TryCompleteAsync(
        CheckAndActCompletionRequest request,
        CancellationToken ct = default);
}

public sealed record CheckAndActCompletionRequest(
    WorkItemId WorkItemId,
    string Phase,
    int? Iteration,
    CheckAndActCompletionPromptBlocks Blocks,
    CheckAndActCompletionCredentials Credentials,
    string? ModelId = null);

public sealed record CheckAndActCompletionCredentials(
    AgentCredential? Gemini = null,
    AgentCredential? Codex = null,
    AgentCredential? Claude = null);

public sealed record CheckAndActCompletionResult(
    string Provider,
    AgentKind AgentKind,
    string? ModelId,
    string Output,
    CheckAndActCompletionUsage Usage);

public sealed record CheckAndActCompletionUsage(
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    bool CacheHit);

public sealed class CheckAndActCompletionOptions
{
    public bool Enabled { get; set; } = true;
    public string HttpClientName { get; set; } = "check-completion";
    public List<string> ProviderOrder { get; set; } =
    [
        CheckAndActCompletionProviders.GeminiOAuth,
        CheckAndActCompletionProviders.GeminiApiKey,
        CheckAndActCompletionProviders.OpenAiApiKey,
        CheckAndActCompletionProviders.AnthropicApiKey,
    ];

    public string GeminiModel { get; set; } = "gemini-2.5-pro";
    public string OpenAiModel { get; set; } = "gpt-4o-mini";
    public string AnthropicModel { get; set; } = "claude-3-5-haiku-latest";
    public int MaxOutputTokens { get; set; } = 1024;
    public int CacheTtlSeconds { get; set; } = 300;
    public int MaxResponseChars { get; set; } = 512 * 1024;

    public string? GeminiApiKey { get; set; }
    public string? OpenAiApiKey { get; set; }
    public string? AnthropicApiKey { get; set; }

    public List<string> GeminiApiKeyEnvVars { get; set; } =
    [
        "GEMINI_API_KEY",
        "CODEYBOX_GEMINI_API_KEY",
    ];
    public List<string> OpenAiApiKeyEnvVars { get; set; } =
    [
        "OPENAI_API_KEY",
        "CODEYBOX_CODEX_API_KEY",
    ];
    public List<string> AnthropicApiKeyEnvVars { get; set; } =
    [
        "ANTHROPIC_API_KEY",
    ];
}

public static class CheckAndActCompletionProviders
{
    public const string GeminiOAuth = "gemini-oauth";
    public const string GeminiApiKey = "gemini-api-key";
    public const string OpenAiApiKey = "openai-api-key";
    public const string AnthropicApiKey = "anthropic-api-key";
}

public sealed class DefaultCheckAndActCompletionRunner : ICheckAndActCompletionRunner
{
    internal const string GeminiOAuthEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:generateContent";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CheckAndActCompletionOptions _options;
    private readonly ILogger<DefaultCheckAndActCompletionRunner> _log;
    private readonly Dictionary<string, DateTimeOffset> _prefixCache = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();

    public DefaultCheckAndActCompletionRunner(
        IHttpClientFactory httpClientFactory,
        CheckAndActCompletionOptions options,
        ILogger<DefaultCheckAndActCompletionRunner> log)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _log = log;
    }

    public async Task<CheckAndActCompletionResult?> TryCompleteAsync(
        CheckAndActCompletionRequest request,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return null;

        foreach (var rawProvider in _options.ProviderOrder)
        {
            var provider = NormaliseProvider(rawProvider);
            switch (provider)
            {
                case CheckAndActCompletionProviders.GeminiOAuth:
                    if (!TryGetGeminiOAuthAccessToken(request.Credentials.Gemini, out var oauthToken))
                        continue;
                    return await SendGeminiAsync(
                        request,
                        provider,
                        AgentKind.Gemini,
                        _options.GeminiModel,
                        oauthToken,
                        isOAuth: true,
                        ct);

                case CheckAndActCompletionProviders.GeminiApiKey:
                    if (!TryGetApiKey(
                            request.Credentials.Gemini,
                            "GEMINI_API_KEY",
                            _options.GeminiApiKey,
                            _options.GeminiApiKeyEnvVars,
                            out var geminiApiKey))
                    {
                        continue;
                    }
                    return await SendGeminiAsync(
                        request,
                        provider,
                        AgentKind.Gemini,
                        _options.GeminiModel,
                        geminiApiKey,
                        isOAuth: false,
                        ct);

                case CheckAndActCompletionProviders.OpenAiApiKey:
                    if (!TryGetApiKey(
                            request.Credentials.Codex,
                            "OPENAI_API_KEY",
                            _options.OpenAiApiKey,
                            _options.OpenAiApiKeyEnvVars,
                            out var openAiKey))
                    {
                        continue;
                    }
                    return await SendOpenAiAsync(request, openAiKey, ct);

                case CheckAndActCompletionProviders.AnthropicApiKey:
                    if (!TryGetApiKey(
                            request.Credentials.Claude,
                            "ANTHROPIC_API_KEY",
                            _options.AnthropicApiKey,
                            _options.AnthropicApiKeyEnvVars,
                            out var anthropicKey))
                    {
                        continue;
                    }
                    return await SendAnthropicAsync(request, anthropicKey, ct);

                default:
                    _log.LogWarning("Ignoring unknown check-and-act completion provider '{Provider}'", rawProvider);
                    break;
            }
        }

        return null;
    }

    private async Task<CheckAndActCompletionResult> SendGeminiAsync(
        CheckAndActCompletionRequest request,
        string provider,
        AgentKind agentKind,
        string model,
        string credential,
        bool isOAuth,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(_options.HttpClientName);
        var prompt = request.Blocks.Render();
        object body = isOAuth
            ? new
            {
                model = $"models/{model}",
                request = new
                {
                    contents = new[] { UserContent(prompt) },
                    generationConfig = new { maxOutputTokens = _options.MaxOutputTokens, temperature = 0 },
                },
            }
            : new
            {
                contents = new[] { UserContent(prompt) },
                generationConfig = new { maxOutputTokens = _options.MaxOutputTokens, temperature = 0 },
            };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            isOAuth
                ? GeminiOAuthEndpoint
                : $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = JsonContent(body),
        };
        if (isOAuth)
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        else
            httpRequest.Headers.Add("x-goog-api-key", credential);

        using var response = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        var responseText = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"check-and-act completion provider {provider} failed: HTTP {(int)response.StatusCode}: {responseText}");

        var output = ExtractGeminiText(responseText);
        var usage = ExtractGeminiUsage(responseText);
        return BuildResult(request, provider, agentKind, model, output, usage);
    }

    private async Task<CheckAndActCompletionResult> SendOpenAiAsync(
        CheckAndActCompletionRequest request,
        string apiKey,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(_options.HttpClientName);
        var body = new
        {
            model = _options.OpenAiModel,
            messages = new object[]
            {
                new { role = "system", content = request.Blocks.SystemBlock },
                new { role = "user", content = request.Blocks.ReviewBlock },
                new { role = "user", content = request.Blocks.QuestionBlock },
            },
            temperature = 0,
            max_tokens = _options.MaxOutputTokens,
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = JsonContent(body),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        var responseText = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"check-and-act completion provider openai-api-key failed: HTTP {(int)response.StatusCode}: {responseText}");

        var output = ExtractOpenAiText(responseText);
        var usage = ExtractOpenAiUsage(responseText);
        return BuildResult(request, CheckAndActCompletionProviders.OpenAiApiKey, AgentKind.Codex, _options.OpenAiModel, output, usage);
    }

    private async Task<CheckAndActCompletionResult> SendAnthropicAsync(
        CheckAndActCompletionRequest request,
        string apiKey,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(_options.HttpClientName);
        var body = new
        {
            model = _options.AnthropicModel,
            max_tokens = _options.MaxOutputTokens,
            temperature = 0,
            system = new object[]
            {
                new
                {
                    type = "text",
                    text = request.Blocks.SystemBlock,
                    cache_control = new { type = "ephemeral" },
                },
            },
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "text",
                            text = request.Blocks.ReviewBlock,
                            cache_control = new { type = "ephemeral" },
                        },
                        new { type = "text", text = request.Blocks.QuestionBlock },
                    },
                },
            },
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent(body),
        };
        httpRequest.Headers.Add("x-api-key", apiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        var responseText = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"check-and-act completion provider anthropic-api-key failed: HTTP {(int)response.StatusCode}: {responseText}");

        var output = ExtractAnthropicText(responseText);
        var usage = ExtractAnthropicUsage(responseText);
        return BuildResult(request, CheckAndActCompletionProviders.AnthropicApiKey, AgentKind.Claude, _options.AnthropicModel, output, usage);
    }

    private CheckAndActCompletionResult BuildResult(
        CheckAndActCompletionRequest request,
        string provider,
        AgentKind agentKind,
        string? model,
        string output,
        RawUsage? rawUsage)
    {
        var now = DateTimeOffset.UtcNow;
        var prefixKey = ComputePrefixCacheKey(request.Blocks.CacheablePrefix);
        var prefixTokens = EstimateTokens(request.Blocks.CacheablePrefix);
        var totalPromptTokens = rawUsage?.PromptTokens ?? EstimateTokens(request.Blocks.Render());
        var rawCached = rawUsage?.CachedPromptTokens ?? 0;
        var cacheHit = rawCached > 0 || WasPrefixRecentlySeen(prefixKey, now);
        RememberPrefix(prefixKey, now);

        var cached = rawCached;
        if (cached <= 0 && cacheHit)
            cached = Math.Min(prefixTokens, totalPromptTokens);
        var fresh = Math.Max(0, totalPromptTokens - cached);
        var outputTokens = rawUsage?.OutputTokens ?? EstimateTokens(output);

        return new CheckAndActCompletionResult(
            provider,
            agentKind,
            model,
            output,
            new CheckAndActCompletionUsage(
                InputTokens: fresh,
                CachedInputTokens: cached,
                OutputTokens: Math.Max(0, outputTokens),
                CacheHit: cacheHit));
    }

    private bool WasPrefixRecentlySeen(string prefixKey, DateTimeOffset now)
    {
        lock (_cacheLock)
        {
            if (_prefixCache.TryGetValue(prefixKey, out var expiresAt) && expiresAt > now)
                return true;
            _prefixCache.Remove(prefixKey);
            return false;
        }
    }

    private void RememberPrefix(string prefixKey, DateTimeOffset now)
    {
        lock (_cacheLock)
        {
            _prefixCache[prefixKey] = now.AddSeconds(Math.Max(1, _options.CacheTtlSeconds));
        }
    }

    private static string ComputePrefixCacheKey(string prefix)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(prefix));
        return Convert.ToHexString(hash);
    }

    private static string NormaliseProvider(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim().ToLowerInvariant();

    private static bool TryGetGeminiOAuthAccessToken(AgentCredential? credential, out string accessToken)
    {
        accessToken = "";
        if (credential is null)
            return false;
        if (!credential.EnvironmentVariables.TryGetValue(GeminiConstants.OAuthCredsEnvVar, out var bundle)
            || string.IsNullOrWhiteSpace(bundle))
        {
            return false;
        }

        accessToken = CredentialFileTokenExtractor.ExtractGeminiAccessToken(bundle) ?? "";
        return accessToken.Length > 0;
    }

    private static bool TryGetApiKey(
        AgentCredential? credential,
        string sandboxEnvVar,
        string? configuredValue,
        IReadOnlyList<string> envVarNames,
        out string apiKey)
    {
        apiKey = "";
        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            apiKey = configuredValue.Trim();
            return true;
        }
        if (credential is not null
            && credential.EnvironmentVariables.TryGetValue(sandboxEnvVar, out var fromCredential)
            && !string.IsNullOrWhiteSpace(fromCredential))
        {
            apiKey = fromCredential.Trim();
            return true;
        }
        foreach (var envVar in envVarNames)
        {
            if (string.IsNullOrWhiteSpace(envVar))
                continue;
            var value = Environment.GetEnvironmentVariable(envVar.Trim());
            if (!string.IsNullOrWhiteSpace(value))
            {
                apiKey = value.Trim();
                return true;
            }
        }
        return false;
    }

    private StringContent JsonContent(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static object UserContent(string text) => new
    {
        role = "user",
        parts = new[] { new { text } },
    };

    private async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[_options.MaxResponseChars + 1];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            totalRead += read;
        }
        if (totalRead > _options.MaxResponseChars)
            throw new InvalidOperationException("check-and-act completion response exceeded the configured size cap");
        return new string(buffer, 0, totalRead);
    }

    private static string ExtractGeminiText(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        var root = UnwrapGeminiResponse(doc.RootElement);
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            return "";

        var sb = new StringBuilder();
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)
                || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    sb.Append(text.GetString());
            }
        }
        return sb.ToString();
    }

    private static RawUsage? ExtractGeminiUsage(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        var root = UnwrapGeminiResponse(doc.RootElement);
        if (!root.TryGetProperty("usageMetadata", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        var prompt = TryGetInt(usage, "promptTokenCount");
        var cached = TryGetInt(usage, "cachedContentTokenCount")
            ?? TryGetInt(usage, "cachedInputTokenCount")
            ?? 0;
        var output = TryGetInt(usage, "candidatesTokenCount") ?? 0;
        return new RawUsage(prompt ?? 0, cached, output);
    }

    private static JsonElement UnwrapGeminiResponse(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("response", out var wrapped)
            && wrapped.ValueKind == JsonValueKind.Object
                ? wrapped
                : root;

    private static string ExtractOpenAiText(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return "";
        var first = choices.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
            return "";
        if (first.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }
        return "";
    }

    private static RawUsage? ExtractOpenAiUsage(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        var prompt = TryGetInt(usage, "prompt_tokens") ?? 0;
        var output = TryGetInt(usage, "completion_tokens") ?? 0;
        var cached = 0;
        if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
            cached = TryGetInt(details, "cached_tokens") ?? 0;
        return new RawUsage(prompt, cached, output);
    }

    private static string ExtractAnthropicText(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                sb.Append(text.GetString());
        }
        return sb.ToString();
    }

    private static RawUsage? ExtractAnthropicUsage(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        var input = TryGetInt(usage, "input_tokens") ?? 0;
        var cacheRead = TryGetInt(usage, "cache_read_input_tokens") ?? 0;
        var output = TryGetInt(usage, "output_tokens") ?? 0;
        return new RawUsage(input + cacheRead, cacheRead, output);
    }

    private static int? TryGetInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            return Math.Max(0, intValue);
        return null;
    }

    private static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }

    private sealed record RawUsage(int PromptTokens, int CachedPromptTokens, int OutputTokens);
}
