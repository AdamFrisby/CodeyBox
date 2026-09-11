using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    /// <summary>Per-call timeout in seconds for every provider. Default 60.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum prompt size in chars; larger prompts fail without an HTTP call. Default 200000.</summary>
    public int MaxPromptChars { get; set; } = 200_000;

    /// <summary>Configured default for the Gemini OAuth endpoint (was a literal). Default is the legacy endpoint.</summary>
    public string GeminiOAuthEndpointUrl { get; set; } = DefaultCheckAndActCompletionRunner.GeminiOAuthEndpoint;

    /// <summary>Base URL for Gemini API-key calls; the model path is appended. Default is the legacy host.</summary>
    public string GeminiApiKeyBaseUrl { get; set; } = "https://generativelanguage.googleapis.com";

    /// <summary>Configured default for the OpenAI endpoint (was a literal).</summary>
    public string OpenAiEndpointUrl { get; set; } = "https://api.openai.com/v1/chat/completions";

    /// <summary>Configured default for the Anthropic endpoint (was a literal).</summary>
    public string AnthropicEndpointUrl { get; set; } = "https://api.anthropic.com/v1/messages";

    /// <summary>Anthropic API version header value. Default "2023-06-01".</summary>
    public string AnthropicVersion { get; set; } = "2023-06-01";

    /// <summary>Operator-defined extra destinations. Entries are matched by
    /// <see cref="CustomCompletionProviderConfig.Name"/> against <see cref="ProviderOrder"/>
    /// after the built-in providers, so a new destination is config-only.</summary>
    public List<CustomCompletionProviderConfig> CustomProviders { get; set; } = [];

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

/// <summary>
/// An operator-defined completion destination matched by name against
/// <see cref="CheckAndActCompletionOptions.ProviderOrder"/>. Only OpenAI-compatible
/// wire shapes are expected here, but any <see cref="CompletionWireApi"/> may be set.
/// </summary>
public sealed class CustomCompletionProviderConfig
{
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public CompletionWireApi WireApi { get; set; } = CompletionWireApi.OpenAiChatCompletions;
    public string? ApiKey { get; set; }
    public List<string> ApiKeyEnvVars { get; set; } = [];
    public Dictionary<string, string> ExtraHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool UseBearerAuth { get; set; } = true;
    public string AgentKind { get; set; } = "codex";
    public int MaxOutputTokens { get; set; }
}

public sealed class DefaultCheckAndActCompletionRunner : ICheckAndActCompletionRunner
{
    internal const string GeminiOAuthEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:generateContent";

    private readonly Func<CheckAndActCompletionOptions> _options;
    private readonly ICompletionClient _completionClient;
    private readonly ILogger<DefaultCheckAndActCompletionRunner> _log;
    private readonly Dictionary<string, DateTimeOffset> _prefixCache = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();

    public DefaultCheckAndActCompletionRunner(
        IHttpClientFactory httpClientFactory,
        CheckAndActCompletionOptions options,
        ILogger<DefaultCheckAndActCompletionRunner> log)
        : this(httpClientFactory, () => options, log, completionClient: null)
    {
    }

    public DefaultCheckAndActCompletionRunner(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<CheckAndActCompletionOptions> options,
        ILogger<DefaultCheckAndActCompletionRunner> log,
        ICompletionClient? completionClient = null)
        : this(httpClientFactory, () => options.CurrentValue, log, completionClient)
    {
    }

    public DefaultCheckAndActCompletionRunner(
        IHttpClientFactory httpClientFactory,
        Func<CheckAndActCompletionOptions> options,
        ILogger<DefaultCheckAndActCompletionRunner> log,
        ICompletionClient? completionClient = null)
    {
        _options = options;
        _log = log;
        _completionClient = completionClient
            ?? new CompletionClient(httpClientFactory, () => ToClientOptions(_options()), NullLogger<CompletionClient>.Instance);
    }

    private static CompletionClientOptions ToClientOptions(CheckAndActCompletionOptions options) => new()
    {
        HttpClientName = options.HttpClientName,
        RequestTimeoutSeconds = options.RequestTimeoutSeconds,
        MaxResponseBytes = options.MaxResponseChars,
        MaxPromptChars = options.MaxPromptChars,
    };

    public async Task<CheckAndActCompletionResult?> TryCompleteAsync(
        CheckAndActCompletionRequest request,
        CancellationToken ct = default)
    {
        var options = _options();
        if (!options.Enabled)
            return null;

        foreach (var rawProvider in options.ProviderOrder)
        {
            var provider = NormaliseProvider(rawProvider);
            switch (provider)
            {
                case CheckAndActCompletionProviders.GeminiOAuth:
                    if (!TryGetGeminiOAuthAccessToken(request.Credentials.Gemini, out var oauthToken))
                        continue;
                    {
                        var result = await SendGeminiAsync(
                            request,
                            provider,
                            AgentKind.Gemini,
                            options.GeminiModel,
                            oauthToken,
                            isOAuth: true,
                            ct);
                        if (result is not null)
                            return result;
                        continue;
                    }

                case CheckAndActCompletionProviders.GeminiApiKey:
                    if (!TryGetApiKey(
                            request.Credentials.Gemini,
                            "GEMINI_API_KEY",
                            options.GeminiApiKey,
                            options.GeminiApiKeyEnvVars,
                            out var geminiApiKey))
                    {
                        continue;
                    }
                    {
                        var result = await SendGeminiAsync(
                            request,
                            provider,
                            AgentKind.Gemini,
                            options.GeminiModel,
                            geminiApiKey,
                            isOAuth: false,
                            ct);
                        if (result is not null)
                            return result;
                        continue;
                    }

                case CheckAndActCompletionProviders.OpenAiApiKey:
                    if (!TryGetApiKey(
                            request.Credentials.Codex,
                            "OPENAI_API_KEY",
                            options.OpenAiApiKey,
                            options.OpenAiApiKeyEnvVars,
                            out var openAiKey))
                    {
                        continue;
                    }
                    {
                        var result = await SendOpenAiAsync(request, openAiKey, ct);
                        if (result is not null)
                            return result;
                        continue;
                    }

                case CheckAndActCompletionProviders.AnthropicApiKey:
                    if (!TryGetApiKey(
                            request.Credentials.Claude,
                            "ANTHROPIC_API_KEY",
                            options.AnthropicApiKey,
                            options.AnthropicApiKeyEnvVars,
                            out var anthropicKey))
                    {
                        continue;
                    }
                    {
                        var result = await SendAnthropicAsync(request, anthropicKey, ct);
                        if (result is not null)
                            return result;
                        continue;
                    }

                default:
                    {
                        var custom = FindCustomProvider(options, provider);
                        if (custom is null)
                        {
                            _log.LogWarning("Ignoring unknown check-and-act completion provider '{Provider}'", rawProvider);
                            break;
                        }
                        var result = await SendCustomAsync(request, provider, custom, ct);
                        if (result is not null)
                            return result;
                        continue;
                    }
            }
        }

        return null;
    }

    private async Task<CheckAndActCompletionResult?> SendGeminiAsync(
        CheckAndActCompletionRequest request,
        string provider,
        AgentKind agentKind,
        string model,
        string credential,
        bool isOAuth,
        CancellationToken ct)
    {
        var options = _options();
        var endpoint = isOAuth
            ? options.GeminiOAuthEndpointUrl
            : $"{options.GeminiApiKeyBaseUrl.TrimEnd('/')}/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";
        var completion = new CompletionRequest
        {
            Endpoint = endpoint,
            Model = model,
            Messages = [new CompletionMessage("user", request.Blocks.Render())],
            WireApi = CompletionWireApi.GeminiGenerateContent,
            BearerToken = isOAuth ? credential : null,
            ExtraHeaders = isOAuth
                ? null
                : new Dictionary<string, string> { ["x-goog-api-key"] = credential },
            MaxOutputTokens = options.MaxOutputTokens,
            GeminiOAuthEnvelope = isOAuth,
        };
        var result = await _completionClient.CompleteAsync(completion, ct).ConfigureAwait(false);
        return MapCompletion(request, provider, agentKind, model, result);
    }

    private async Task<CheckAndActCompletionResult?> SendOpenAiAsync(
        CheckAndActCompletionRequest request,
        string apiKey,
        CancellationToken ct)
    {
        var options = _options();
        var completion = new CompletionRequest
        {
            Endpoint = options.OpenAiEndpointUrl,
            Model = options.OpenAiModel,
            Messages = OpenAiMessages(request),
            WireApi = CompletionWireApi.OpenAiChatCompletions,
            BearerToken = apiKey,
            MaxOutputTokens = options.MaxOutputTokens,
        };
        var result = await _completionClient.CompleteAsync(completion, ct).ConfigureAwait(false);
        return MapCompletion(request, CheckAndActCompletionProviders.OpenAiApiKey, AgentKind.Codex, options.OpenAiModel, result);
    }

    private async Task<CheckAndActCompletionResult?> SendAnthropicAsync(
        CheckAndActCompletionRequest request,
        string apiKey,
        CancellationToken ct)
    {
        var options = _options();
        var completion = new CompletionRequest
        {
            Endpoint = options.AnthropicEndpointUrl,
            Model = options.AnthropicModel,
            Messages =
            [
                new CompletionMessage("system", request.Blocks.SystemBlock),
                new CompletionMessage("user", $"{request.Blocks.ReviewBlock}\n\n{request.Blocks.QuestionBlock}"),
            ],
            WireApi = CompletionWireApi.AnthropicMessages,
            ExtraHeaders = new Dictionary<string, string>
            {
                ["x-api-key"] = apiKey,
                ["anthropic-version"] = options.AnthropicVersion,
            },
            MaxOutputTokens = options.MaxOutputTokens,
        };
        var result = await _completionClient.CompleteAsync(completion, ct).ConfigureAwait(false);
        return MapCompletion(request, CheckAndActCompletionProviders.AnthropicApiKey, AgentKind.Claude, options.AnthropicModel, result);
    }

    private async Task<CheckAndActCompletionResult?> SendCustomAsync(
        CheckAndActCompletionRequest request,
        string provider,
        CustomCompletionProviderConfig custom,
        CancellationToken ct)
    {
        var options = _options();
        if (!TryGetApiKey(null, "", custom.ApiKey, custom.ApiKeyEnvVars, out var apiKey))
        {
            if (custom.UseBearerAuth)
                return null;
            apiKey = "";
        }
        List<CompletionMessage> messages = custom.WireApi switch
        {
            CompletionWireApi.AnthropicMessages =>
            [
                new CompletionMessage("system", request.Blocks.SystemBlock),
                new CompletionMessage("user", $"{request.Blocks.ReviewBlock}\n\n{request.Blocks.QuestionBlock}"),
            ],
            CompletionWireApi.GeminiGenerateContent =>
                [new CompletionMessage("user", request.Blocks.Render())],
            _ => OpenAiMessages(request),
        };
        var completion = new CompletionRequest
        {
            Endpoint = custom.Endpoint,
            Model = string.IsNullOrWhiteSpace(custom.Model) ? request.ModelId ?? "" : custom.Model,
            Messages = messages,
            WireApi = custom.WireApi,
            BearerToken = custom.UseBearerAuth && !string.IsNullOrEmpty(apiKey) ? apiKey : null,
            ExtraHeaders = custom.ExtraHeaders.Count == 0 && string.IsNullOrEmpty(apiKey)
                ? null
                : MergeApiKey(custom.ExtraHeaders, custom.UseBearerAuth ? null : apiKey),
            MaxOutputTokens = custom.MaxOutputTokens > 0 ? custom.MaxOutputTokens : options.MaxOutputTokens,
        };
        var result = await _completionClient.CompleteAsync(completion, ct).ConfigureAwait(false);
        return MapCompletion(request, provider, new AgentKind(custom.AgentKind), completion.Model, result);
    }

    private static List<CompletionMessage> OpenAiMessages(CheckAndActCompletionRequest request) =>
    [
        new CompletionMessage("system", request.Blocks.SystemBlock),
        new CompletionMessage("user", request.Blocks.ReviewBlock),
        new CompletionMessage("user", request.Blocks.QuestionBlock),
    ];

    private static Dictionary<string, string>? MergeApiKey(
        Dictionary<string, string> extra, string? apiKeyHeaderValue)
    {
        if (string.IsNullOrEmpty(apiKeyHeaderValue))
            return extra.Count == 0 ? null : new Dictionary<string, string>(extra);
        var merged = new Dictionary<string, string>(extra, StringComparer.OrdinalIgnoreCase);
        merged["x-api-key"] = apiKeyHeaderValue;
        return merged;
    }

    private static CustomCompletionProviderConfig? FindCustomProvider(CheckAndActCompletionOptions options, string provider)
    {
        foreach (var custom in options.CustomProviders)
        {
            if (string.IsNullOrWhiteSpace(custom.Name) || string.IsNullOrWhiteSpace(custom.Endpoint))
                continue;
            if (string.Equals(NormaliseProvider(custom.Name), provider, StringComparison.Ordinal))
                return custom;
        }
        return null;
    }

    /// <summary>
    /// Maps a client result onto a pipeline result. Any non-success becomes null
    /// (logged without secrets) so the caller falls back to the next provider or
    /// the agentic path — failures never escape as exceptions into a pipeline phase.
    /// </summary>
    private CheckAndActCompletionResult? MapCompletion(
        CheckAndActCompletionRequest request,
        string provider,
        AgentKind agentKind,
        string? model,
        CompletionResult result)
    {
        if (result.IsSuccess)
            return BuildResult(request, provider, agentKind, model, result.Text, result.Usage);
        _log.LogWarning(
            "Check-and-act completion provider {Provider} returned {Status}: {Detail}",
            provider, result.Status, result.ErrorDetail ?? "");
        return null;
    }

    private CheckAndActCompletionResult BuildResult(
        CheckAndActCompletionRequest request,
        string provider,
        AgentKind agentKind,
        string? model,
        string output,
        CompletionUsage? rawUsage)
    {
        var now = DateTimeOffset.UtcNow;
        var prefixKey = ComputePrefixCacheKey(request.Blocks.CacheablePrefix);
        var prefixTokens = EstimateTokens(request.Blocks.CacheablePrefix);
        var totalPromptTokens = rawUsage?.InputTokens ?? EstimateTokens(request.Blocks.Render());
        var rawCached = rawUsage?.CachedInputTokens ?? 0;
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
            _prefixCache[prefixKey] = now.AddSeconds(Math.Max(1, _options().CacheTtlSeconds));
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

    private static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }
}
