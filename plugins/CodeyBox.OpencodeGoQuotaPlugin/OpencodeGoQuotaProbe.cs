using System.Net;
using System.Net.Http.Headers;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.OpencodeGoQuotaPlugin;

/// <summary>
/// Out-of-tree quota meter for the opencode-go plan (<c>GET /usage</c> on the
/// zen go API root). Serves two members backed by the same plan: the opencode
/// agent's opencode-go models, and a copilot member whose configured BYOK
/// provider base URL is the zen go endpoint. Keeping this reader in the plugin
/// that owns the provider keeps provider-specific knowledge out of the
/// provider-neutral orchestrator.
///
/// <para>The <c>/usage</c> path is derived from the configured provider base
/// URL (scoped <c>ProviderBaseUrl</c>), never hardcoded to a host. Timeout and
/// cache TTL are re-read from configuration on every call so operator edits
/// hot-reload without a restart.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: opencode-go Quota",
    minHostApiVersion: "1.2")]
public sealed class OpencodeGoQuotaProbe
    : IAgentQuotaProbe, IAgentQuotaCacheInvalidator, IPluginInitializer
{
    public const string PluginId = "codeybox.opencode-go-quota";

    /// <summary>Default API root; <c>/usage</c> is appended to derive the endpoint.</summary>
    internal const string DefaultProviderBaseUrl = "https://opencode.ai/zen/go/v1";

    private const string HttpClientName = "agent-quota";
    private const int MaxResponseChars = 64 * 1024;
    private const string OpencodeGoModelPrefix = "opencode-go/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly Func<AgentMembership, AgentQuotaCredentials> _credentialsProvider;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private ILogger _logger = NullLogger.Instance;

    // Single-entry response cache keyed by token: the /usage reading is
    // account-wide, not per member, so members sharing a credential share it.
    private (string Token, AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt)? _cache;
    private int _consecutiveFailures;

    public OpencodeGoQuotaProbe(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OpencodeGoQuotaProbe>? logger = null,
        Func<AgentMembership, AgentQuotaCredentials>? credentialsProvider = null,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        if (logger is not null) _logger = logger;
        _credentialsProvider = credentialsProvider ?? DefaultCredentials;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The plan owner. Informational only — member resolution runs
    /// through <see cref="Handles"/>, which additionally serves qualifying
    /// copilot members on the same plan.</summary>
    public AgentKind Kind => AgentKind.Opencode;

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger = context.Logger;
        var options = ReadOptions();
        _logger.LogInformation(
            "opencode-go quota plugin initialised: usageEndpoint={UsageEndpoint}, cacheTtlSeconds={CacheTtlSeconds}, timeoutSeconds={TimeoutSeconds}",
            UsageEndpointFor(options),
            options.CacheTtl.TotalSeconds,
            options.Timeout.TotalSeconds);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Claims the members backed by the opencode-go plan: opencode members
    /// routing opencode-go models (an empty model id means the agent default,
    /// which rides the same subscription), and copilot members only when the
    /// configured BYOK provider base URL is the zen go endpoint. A copilot
    /// member pointed at any other BYOK provider is never claimed, because
    /// <c>/usage</c> exists only on this plan — those members keep resolving
    /// to the per-kind probe path.
    /// </summary>
    public bool Handles(AgentQuotaMemberKey key)
    {
        try
        {
            if (key.Agent == AgentKind.Opencode)
                return IsOpencodeGoModel(key.ModelId);
            if (key.Agent == AgentKind.Copilot)
                return IsZenGoEndpoint(EffectiveCopilotBaseUrl());
            return false;
        }
        catch
        {
            // Handles must be pure and cheap and never throw: a misconfigured
            // value opts this probe out rather than breaking resolution.
            return false;
        }
    }

    public async Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        if (!Handles(AgentQuotaMemberKey.From(member)))
            return AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.Permanent, "member is not served by the opencode-go plan");

        var options = ReadOptions();
        var token = _credentialsProvider(member).AccessToken;
        if (string.IsNullOrEmpty(token))
            return AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.NoCredential, "no opencode-go API key configured");

        var usageEndpoint = UsageEndpointFor(options);
        if (usageEndpoint is null)
            return AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.Permanent, "misconfigured provider base URL");

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_cache is { } entry
                && string.Equals(entry.Token, token, StringComparison.Ordinal)
                && now < entry.ExpiresAt)
                return entry.Snapshot;

            var snapshot = await FetchAsync(token, usageEndpoint, options.Timeout, ct).ConfigureAwait(false);
            if (snapshot.IsKnown)
            {
                _consecutiveFailures = 0;
                _cache = (token, snapshot, _timeProvider.GetUtcNow() + options.CacheTtl);
            }
            else
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= options.SustainedFailureWarningThreshold)
                    _logger.LogWarning(
                        "opencode-go quota probe failing ({Count} consecutive failures): {Notes}",
                        _consecutiveFailures, snapshot.Notes);
                else
                    _logger.LogDebug(
                        "opencode-go quota probe failed ({Count} consecutive): {Notes}",
                        _consecutiveFailures, snapshot.Notes);
            }

            return snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void InvalidateCache()
    {
        _lock.Wait();
        try
        {
            _cache = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void InvalidateCredentialState()
    {
        _lock.Wait();
        try
        {
            _cache = null;
            _consecutiveFailures = 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<AgentQuotaSnapshot> FetchAsync(
        string token, string usageEndpoint, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var linked = linkedCts.Token;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, usageEndpoint);
            // Do NOT log the Authorization header — it carries the plan key.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, linked).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return Unknown(QuotaUnknownReason.Transient, "HTTP 429 (rate-limited)");
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                return Unknown(
                    QuotaUnknownReasons.FromHttpStatus(response.StatusCode), $"HTTP {status}");
            }

            // Do NOT log the response body — it may contain account identifiers.
            var body = await ReadCappedAsync(response.Content, linked).ConfigureAwait(false);
            if (body is null)
                return Unknown(QuotaUnknownReason.Permanent, "response too large");
            return OpencodeGoUsageParser.Parse(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Unknown(QuotaUnknownReason.Transient, "timed out");
        }
        catch (HttpRequestException ex)
        {
            return Unknown(QuotaUnknownReason.Transient, $"transport error: {ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            return Unknown(QuotaUnknownReason.Transient, $"probe error: {ex.GetType().Name}");
        }
    }

    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxResponseChars + 1];
        int totalRead = 0, chunk;
        do
        {
            chunk = await reader.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct)
                .ConfigureAwait(false);
            totalRead += chunk;
        }
        while (chunk > 0 && totalRead < buffer.Length);
        if (totalRead > MaxResponseChars) return null;
        return new string(buffer, 0, totalRead);
    }

    private static AgentQuotaSnapshot Unknown(QuotaUnknownReason reason, string notes) =>
        AgentQuotaSnapshot.UnknownSnapshot(reason, notes);

    private static bool IsOpencodeGoModel(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return true;
        return modelId.StartsWith(OpencodeGoModelPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private string? EffectiveCopilotBaseUrl()
    {
        var options = ReadOptions();
        if (!string.IsNullOrEmpty(options.CopilotProviderBaseUrl)) return options.CopilotProviderBaseUrl;
        return _configuration.GetSection("CodeyBox:Copilot:Provider")["BaseUrl"]?.Trim();
    }

    /// <summary>
    /// Whether <paramref name="baseUrl"/> addresses the zen go plan API:
    /// host <c>opencode.ai</c> with <c>/zen/go</c> as the first two path
    /// segments. Canonicalize-then-check on parsed URI parts — never a raw
    /// substring — so lookalike hosts or paths cannot claim the plan.
    /// </summary>
    internal static bool IsZenGoEndpoint(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Host, "opencode.ai", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            && string.Equals(segments[0], "zen", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[1], "go", StringComparison.OrdinalIgnoreCase);
    }

    private OpencodeGoQuotaPluginOptions ReadOptions() =>
        OpencodeGoQuotaPluginOptions.FromConfiguration(
            _configuration.GetSection($"CodeyBox:Plugins:{PluginId}"));

    private static string? UsageEndpointFor(OpencodeGoQuotaPluginOptions options)
    {
        var baseUrl = options.ProviderBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl)) return null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        return baseUrl + "/usage";
    }

    private AgentQuotaCredentials DefaultCredentials(AgentMembership member)
    {
        _ = member;
        var options = ReadOptions();
        var key = string.IsNullOrEmpty(options.ApiKey)
            ? Environment.GetEnvironmentVariable(OpencodeGoQuotaPluginOptions.ApiKeyEnvironmentVariable)
            : options.ApiKey;
        return new AgentQuotaCredentials(string.IsNullOrWhiteSpace(key) ? null : key.Trim());
    }
}
