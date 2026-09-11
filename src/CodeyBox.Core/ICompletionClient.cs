namespace CodeyBox.Core;

/// <summary>
/// Wire protocol used to encode a completion request and decode its response.
/// <see cref="OpenAiChatCompletions"/> and <see cref="OpenAiResponses"/> are the
/// two OpenAI-compatible shapes every config-driven destination must support;
/// <see cref="AnthropicMessages"/> and <see cref="GeminiGenerateContent"/> exist
/// so the pre-existing callers (check-and-act, changelog) keep their exact
/// observable request/response behaviour after migrating onto
/// <see cref="ICompletionClient"/>.
/// </summary>
public enum CompletionWireApi
{
    OpenAiChatCompletions = 0,
    OpenAiResponses = 1,
    AnthropicMessages = 2,
    GeminiGenerateContent = 3,
}

/// <summary>A single prompt message. Role is a wire message role such as "system" or "user".</summary>
public sealed record CompletionMessage(string Role, string Content);

/// <summary>Hot-reloadable bounds for <see cref="ICompletionClient"/>.</summary>
public sealed class CompletionClientOptions
{
    public const string SectionName = "CodeyBox:Completion";

    /// <summary>Named <see cref="IHttpClientFactory"/> client to send through. Default "completion".</summary>
    public string HttpClientName { get; set; } = "completion";

    /// <summary>Per-request timeout in seconds. A call that exceeds it returns
    /// <see cref="CompletionStatus.TimedOut"/> instead of throwing. Default 60.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum response body size in bytes. The body is streamed and the
    /// call is aborted once this is exceeded, so an oversized response is rejected
    /// without buffering it entirely; the caller gets
    /// <see cref="CompletionStatus.ResponseTooLarge"/>. Default 524288 (512 KiB).</summary>
    public int MaxResponseBytes { get; set; } = 512 * 1024;

    /// <summary>Maximum total prompt size in characters (sum of all message roles
    /// and contents). When exceeded the client returns
    /// <see cref="CompletionStatus.PromptTooLarge"/> WITHOUT issuing any HTTP
    /// request. Default 200000.</summary>
    public int MaxPromptChars { get; set; } = 200_000;
}

/// <summary>
/// A fully configuration-driven completion request: the destination (endpoint,
/// credentials, model, headers, wire shape) travels with the call, so pointing
/// at a new OpenAI-compatible endpoint is config-only and no vendor hostname
/// lives in client source.
/// </summary>
public sealed record CompletionRequest
{
    /// <summary>Absolute http(s) URL to POST to. Exactly this URL is used; nothing is appended.</summary>
    public required string Endpoint { get; init; }

    /// <summary>Model id sent in the request body. Required, max 128 chars.</summary>
    public required string Model { get; init; }

    /// <summary>Prompt messages. Max 64 entries; total chars bounded by
    /// <see cref="CompletionClientOptions.MaxPromptChars"/>.</summary>
    public required IReadOnlyList<CompletionMessage> Messages { get; init; }

    /// <summary>Which wire shape to encode/decode. Default OpenAiChatCompletions.</summary>
    public CompletionWireApi WireApi { get; init; } = CompletionWireApi.OpenAiChatCompletions;

    /// <summary>Optional bearer token, sent as <c>Authorization: Bearer</c>. Never logged.</summary>
    public string? BearerToken { get; init; }

    /// <summary>Optional extra headers (e.g. x-api-key, anthropic-version). Values are never logged.
    /// Max 16 entries; names max 64 chars, values max 4096 chars. "Authorization" must not
    /// appear here — use <see cref="BearerToken"/> instead.</summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }

    /// <summary>Provider max-output-tokens hint (max_tokens / max_output_tokens /
    /// generationConfig.maxOutputTokens depending on wire shape). Default 1024.</summary>
    public int MaxOutputTokens { get; init; } = 1024;

    /// <summary>Sampling temperature. Default 0 (deterministic).</summary>
    public double Temperature { get; init; } = 0;

    /// <summary>Gemini-only: wrap the body in the OAuth envelope
    /// <c>{model, request: {contents, generationConfig}}</c> instead of the plain
    /// API-key shape <c>{contents, generationConfig}</c>. Ignored by other wire shapes.</summary>
    public bool GeminiOAuthEnvelope { get; init; }
}

/// <summary>Machine-actionable outcome of <see cref="ICompletionClient.CompleteAsync"/>.</summary>
public enum CompletionStatus
{
    Succeeded = 0,
    Refused = 1,
    EmptyCompletion = 2,
    AuthenticationFailed = 3,
    RateLimited = 4,
    ServerError = 5,
    TransportFailed = 6,
    TimedOut = 7,
    PromptTooLarge = 8,
    ResponseTooLarge = 9,
    InvalidRequest = 10,
}

/// <summary>Normalized token usage. Providers that omit a field report 0 for it.</summary>
public sealed record CompletionUsage(int InputTokens, int CachedInputTokens, int OutputTokens);

/// <summary>
/// Returned — never thrown — for every non-cancellation failure. HTTP 401/403 maps to
/// <see cref="CompletionStatus.AuthenticationFailed"/>, 429 to
/// <see cref="CompletionStatus.RateLimited"/>, other 4xx to
/// <see cref="CompletionStatus.InvalidRequest"/>, 5xx to
/// <see cref="CompletionStatus.ServerError"/>, network errors to
/// <see cref="CompletionStatus.TransportFailed"/>, an exceeded per-call timeout to
/// <see cref="CompletionStatus.TimedOut"/>, an explicit provider refusal to
/// <see cref="CompletionStatus.Refused"/>, and a well-formed but textless completion to
/// <see cref="CompletionStatus.EmptyCompletion"/>. <see cref="ErrorDetail"/> carries
/// metadata only (status code, reason phrase): never a header value or body bytes.
/// </summary>
public sealed record CompletionResult
{
    public required CompletionStatus Status { get; init; }

    /// <summary>Completion text on success; empty otherwise.</summary>
    public string Text { get; init; } = "";

    public CompletionUsage? Usage { get; init; }

    /// <summary>Short metadata-only failure description. Never contains secrets or body bytes.</summary>
    public string? ErrorDetail { get; init; }

    public bool IsSuccess => Status == CompletionStatus.Succeeded;
}

/// <summary>
/// Configuration-driven completion client. Follows the <see cref="IChangelogGenerator"/>
/// seam style: callers depend on this abstraction, never on a concrete client.
/// Implementations must bound every call (timeout, response size, prompt size),
/// must never log authorization material, header values, or response bodies,
/// and must return failures as <see cref="CompletionResult"/> rather than throwing
/// (except <see cref="OperationCanceledException"/> when the caller's token is cancelled).
/// </summary>
public interface ICompletionClient
{
    Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default);
}
