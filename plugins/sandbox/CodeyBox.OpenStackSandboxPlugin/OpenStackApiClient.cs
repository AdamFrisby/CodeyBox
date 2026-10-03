using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.OpenStackSandboxPlugin;

/// <summary>
/// Size, timeout, and safety bounds for <see cref="OpenStackApiClient"/>.
/// Defaults are the single source of truth — <see cref="OpenStackSandboxOptions"/>
/// mirrors them so operators tune hot-reloadable knobs, not literals.
/// </summary>
public sealed record OpenStackClientLimits
{
    /// <summary>Default per-request HTTP timeout, in seconds.</summary>
    public const int DefaultHttpTimeoutSeconds = 60;

    /// <summary>Default token refresh skew, in seconds.</summary>
    public const int DefaultTokenRefreshSkewSeconds = 60;

    /// <summary>Default poll base interval, in milliseconds.</summary>
    public const int DefaultPollIntervalMilliseconds = 2000;

    /// <summary>Default poll backoff ceiling, in milliseconds.</summary>
    public const int DefaultMaxPollIntervalMilliseconds = 15000;

    /// <summary>Default cap on one decoded response body, in bytes.</summary>
    public const int DefaultMaxResponseBytes = 8 * 1024 * 1024;

    /// <summary>Default cap on items collected from one list operation.</summary>
    public const int DefaultMaxListItems = 5000;

    /// <summary>Default cap on list pages walked.</summary>
    public const int DefaultMaxListPages = 100;

    /// <summary>Default cap on cloud-config user_data bytes.</summary>
    public const int DefaultMaxUserDataBytes = 64 * 1024;

    /// <summary>Per-request HTTP timeout. Does not bound status polls — those carry their own timeout.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(DefaultHttpTimeoutSeconds);

    /// <summary>How far before expiry a cached token is refreshed.</summary>
    public TimeSpan TokenRefreshSkew { get; init; } = TimeSpan.FromSeconds(DefaultTokenRefreshSkewSeconds);

    /// <summary>Base delay between status polls; doubles every attempt up to <see cref="MaxPollInterval"/>.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(DefaultPollIntervalMilliseconds);

    /// <summary>Ceiling for the exponential poll backoff.</summary>
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromMilliseconds(DefaultMaxPollIntervalMilliseconds);

    /// <summary>Upper bound on one decoded API response body, in bytes. Enforced while reading.</summary>
    public int MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;

    /// <summary>Upper bound on items collected from one list operation, across pages.</summary>
    public int MaxListItems { get; init; } = DefaultMaxListItems;

    /// <summary>Maximum list pages walked. Hitting the cap fails loudly.</summary>
    public int MaxListPages { get; init; } = DefaultMaxListPages;

    /// <summary>Upper bound on cloud-config user_data bytes sent on server create.</summary>
    public int MaxUserDataBytes { get; init; } = DefaultMaxUserDataBytes;

    /// <summary>
    /// Test hook: allow plain-http endpoints, but only for loopback hosts.
    /// Remote http URLs are refused even with this set.
    /// </summary>
    public bool AllowUnsafeHttp { get; init; }
}

/// <summary>
/// Small, typed OpenStack REST client: Keystone v3 application-credential auth
/// with a skewed token cache, service-catalog endpoint resolution, and typed
/// Nova / Neutron / Glance operations. No provider logic lives here.
///
/// <para>Thread-safe: the token cache is lock-guarded and every method may run
/// concurrently. Cancellation is propagated to every request; nothing is
/// fire-and-forget. Response bodies are size-bounded while streaming, and the
/// credential secret never reaches an exception message or log.</para>
/// </summary>
public sealed class OpenStackApiClient
{
    /// <summary>OpenStack service type for Nova (compute).</summary>
    public const string ComputeServiceType = "compute";

    /// <summary>OpenStack service type for Neutron (network).</summary>
    public const string NetworkServiceType = "network";

    /// <summary>OpenStack service type for Glance (image).</summary>
    public const string ImageServiceType = "image";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Error bodies are untrusted remote text: bounded before they are folded
    // into exception messages and logs.
    internal const int MaxErrorBodyChars = 2048;

    // Protocol sanity bounds (not operator knobs): a single create call must
    // stay a single call — these reject caller bugs, not cloud limits.
    internal const int MaxServerNetworks = 16;
    internal const int MaxMetadataEntries = 128;
    internal const int MaxMetadataChars = 255;
    internal const int MaxServerTags = 64;
    internal const int MaxServerTagChars = 64;

    /// <summary>
    /// Fallback ceiling for status waits when the caller passes no timeout.
    /// Mirrors <c>OpenStackSandboxOptions.ReadyTimeoutSeconds</c> default 600:
    /// change both together.
    /// </summary>
    internal static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(600);
    private const int ListPageSize = 200;

    private static readonly string[] RequestIdHeaders =
        ["x-openstack-request-id", "x-compute-request-id"];

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly OpenStackClientLimits _limits;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private CachedToken? _cachedToken;

    /// <summary>Creates a client over an injected <see cref="HttpClient"/> (test seam for fakes).</summary>
    public OpenStackApiClient(HttpClient http, TimeProvider? clock = null, OpenStackClientLimits? limits = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clock = clock ?? TimeProvider.System;
        _limits = limits ?? new OpenStackClientLimits();
        if (_limits.MaxResponseBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "MaxResponseBytes must be positive.");
        if (_limits.MaxListItems <= 0 || _limits.MaxListPages <= 0 || _limits.MaxUserDataBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "List and user-data bounds must be positive.");
        if (_limits.HttpTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits), "HttpTimeout must be positive.");
        if (_limits.TokenRefreshSkew < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits), "TokenRefreshSkew must be non-negative.");
        if (_limits.PollInterval <= TimeSpan.Zero || _limits.MaxPollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits), "Poll intervals must be positive.");
    }

    // ------------------------------------------------------------------
    // Auth + catalog
    // ------------------------------------------------------------------

    /// <summary>
    /// Authenticates against Keystone v3 with application credentials and
    /// returns the token plus its service catalog. Results are cached and the
    /// token is refreshed <see cref="OpenStackClientLimits.TokenRefreshSkew"/>
    /// before expiry. Exposed for tests; service calls authenticate lazily.
    /// </summary>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> AuthenticateAsync(
        OpenStackCredentials credentials, CancellationToken ct)
    {
        var cached = await GetTokenAsync(credentials, forceRefresh: false, ct).ConfigureAwait(false);
        return (cached.Token, cached.ExpiresAt);
    }

    /// <summary>
    /// Resolves a service endpoint URL from a Keystone catalog by exact service
    /// type, region, and interface. A non-empty region must match exactly;
    /// an empty region takes the first endpoint with the matching interface.
    /// </summary>
    /// <exception cref="OpenStackApiException">No match, or the match is a refused URL.</exception>
    public Uri ResolveServiceEndpoint(
        KeystoneCatalog catalog, string serviceType, string region, string iface)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(serviceType))
            throw new OpenStackApiException(OpenStackFailureKind.Unexpected, "resolve endpoint", "service type is blank");
        if (!OpenStackCredentialChain.KnownInterfaces.Contains(iface))
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "resolve endpoint", $"unknown catalog interface '{iface}'");

        var service = catalog.Services.FirstOrDefault(s =>
            string.Equals(s.Type, serviceType, StringComparison.OrdinalIgnoreCase));
        if (service is null)
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "resolve endpoint",
                $"service catalog has no service of type '{serviceType}'");
        }
        var candidates = service.Endpoints
            .Where(e => string.Equals(e.Interface, iface, StringComparison.Ordinal))
            .ToList();
        if (!string.IsNullOrEmpty(region))
        {
            candidates = candidates
                .Where(e => string.Equals(e.Region, region, StringComparison.Ordinal))
                .ToList();
        }
        var match = candidates.FirstOrDefault();
        if (match is null)
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "resolve endpoint",
                $"service catalog has no '{serviceType}' endpoint for interface '{iface}'" +
                (string.IsNullOrEmpty(region) ? string.Empty : $" in region '{region}'"));
        }
        return RequireServiceHttps(match.Url, $"catalog endpoint for '{serviceType}'");
    }

    private Uri RequireServiceHttps(string? raw, string what)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "resolve endpoint",
                $"{what} is not an absolute http(s) URL: '{SanitizeForLog(raw?.Trim() ?? string.Empty)}'");
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !IsCleartextHttpPermitted(uri, _limits.AllowUnsafeHttp))
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "resolve endpoint",
                $"{what} uses cleartext http to a non-loopback host — refusing to send the " +
                "credential: '" + uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped) + "'. " +
                "AllowUnsafeHttp permits http only for loopback test URLs, never for remote hosts.");
        }
        return uri;
    }

    /// <summary>
    /// Cleartext http is permitted only for loopback URLs under the dev-only
    /// <c>AllowUnsafeHttp</c> opt-in: the credential rides every request, so
    /// one operator edit must never send it cleartext to a remote host.
    /// </summary>
    internal static bool IsCleartextHttpPermitted(Uri uri, bool allowUnsafeHttp) =>
        allowUnsafeHttp
        && uri.Scheme == Uri.UriSchemeHttp
        && uri.IsLoopback;

    private sealed record CachedToken(
        string Token, DateTimeOffset ExpiresAt, KeystoneCatalog Catalog, string AuthUrl, string CredentialId);

    private async Task<CachedToken> GetTokenAsync(
        OpenStackCredentials credentials, bool forceRefresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var snapshot = _cachedToken;
        if (!forceRefresh && snapshot is not null
            && string.Equals(snapshot.AuthUrl, credentials.AuthUrl.AbsoluteUri, StringComparison.Ordinal)
            && string.Equals(snapshot.CredentialId, credentials.ApplicationCredentialId, StringComparison.Ordinal)
            && _clock.GetUtcNow() + _limits.TokenRefreshSkew < snapshot.ExpiresAt)
        {
            return snapshot;
        }

        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = _cachedToken;
            if (!forceRefresh && snapshot is not null
                && string.Equals(snapshot.AuthUrl, credentials.AuthUrl.AbsoluteUri, StringComparison.Ordinal)
                && string.Equals(snapshot.CredentialId, credentials.ApplicationCredentialId, StringComparison.Ordinal)
                && _clock.GetUtcNow() + _limits.TokenRefreshSkew < snapshot.ExpiresAt)
            {
                return snapshot;
            }
            var fresh = await RequestTokenAsync(credentials, ct).ConfigureAwait(false);
            _cachedToken = fresh;
            return fresh;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<CachedToken> RequestTokenAsync(OpenStackCredentials credentials, CancellationToken ct)
    {
        // Guard at the secret POST sink, not just at Resolve: any future
        // caller constructing credentials directly must never send the
        // application-credential secret cleartext to a remote http host.
        if (credentials.AuthUrl.Scheme == Uri.UriSchemeHttp
            && !IsCleartextHttpPermitted(credentials.AuthUrl, _limits.AllowUnsafeHttp))
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "authenticate",
                "auth URL uses cleartext http to a non-loopback host — refusing to send the credential. " +
                "AllowUnsafeHttp permits http only for loopback test URLs, never for remote hosts.");
        }
        var body = new KeystoneAuthRequest(
            new KeystoneAuthIdentity(
                ["application_credential"],
                new KeystoneApplicationCredential(
                    credentials.ApplicationCredentialId, credentials.ApplicationCredentialSecret)));
        var baseUrl = credentials.AuthUrl.AbsoluteUri.TrimEnd('/');
        var uri = baseUrl.EndsWith("/v3", StringComparison.OrdinalIgnoreCase)
            ? new Uri(baseUrl + "/auth/tokens", UriKind.Absolute)
            : JoinUrl(baseUrl, "v3/auth/tokens");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };
        using var response = await SendUnconditionallyAsync(request, "authenticate", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var detail = await ReadErrorBodyAsync(response, ct).ConfigureAwait(false);
            throw new OpenStackApiException(
                OpenStackFailureKind.Unauthorized, "authenticate",
                detail ?? "application credential rejected",
                response.StatusCode, "Unauthorized", GetRequestId(response));
        }
        await EnsureSuccessAsync(response, "authenticate", ct).ConfigureAwait(false);

        if (!response.Headers.TryGetValues("X-Subject-Token", out var tokenValues))
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "authenticate",
                "token response carried no X-Subject-Token header",
                response.StatusCode, null, GetRequestId(response));
        }
        var token = tokenValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "authenticate",
                "token response carried an empty X-Subject-Token header",
                response.StatusCode, null, GetRequestId(response));
        }
        var payload = await ReadJsonAsync<KeystoneTokenResponse>(response, "authenticate", ct).ConfigureAwait(false)
            ?? throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "authenticate", "empty token response body",
                response.StatusCode, null, GetRequestId(response));
        if (payload.Token is null
            || !DateTimeOffset.TryParse(
                payload.Token.ExpiresAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces, out var expiresAt))
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "authenticate",
                "token response carried no parseable token.expires_at",
                response.StatusCode, null, GetRequestId(response));
        }
        return new CachedToken(
            token.Trim(), expiresAt, payload.Token.Catalog ?? new KeystoneCatalog(),
            credentials.AuthUrl.AbsoluteUri, credentials.ApplicationCredentialId);
    }

    // ------------------------------------------------------------------
    // Nova (compute)
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a Nova server. Returns the server record (id, name, initial status).
    /// A 401 re-authenticates once and retries; 409/413/429 surface as typed errors.
    /// </summary>
    public async Task<OpenStackServer> CreateServerAsync(
        OpenStackCredentials credentials, OpenStackServerSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        spec.Validate(_limits.MaxUserDataBytes);
        var networks = spec.NetworkIds.Select(id => new OpenStackServerNetwork(id)).ToList();
        var groups = spec.SecurityGroupNames?.Select(name => new OpenStackServerSecurityGroup(name.Trim())).ToList();
        var body = new OpenStackCreateServerWrapper(new OpenStackCreateServerBody(
            spec.Name, spec.FlavorRef, spec.ImageRef, networks,
            string.IsNullOrWhiteSpace(spec.KeyName) ? null : spec.KeyName,
            spec.UserDataBase64(), spec.Metadata, spec.Tags, groups));
        using var response = await SendServiceAsync(
            credentials, ComputeServiceType, "servers", HttpMethod.Post, body, "create server", ct)
            .ConfigureAwait(false);
        var dto = await ReadJsonAsync<OpenStackServerWrapper>(response, "create server", ct).ConfigureAwait(false);
        return dto?.Server
            ?? throw new OpenStackApiException(OpenStackFailureKind.Unexpected, "create server", "empty response body");
    }

    /// <summary>Gets a Nova server. Returns null when it does not exist (404).</summary>
    public async Task<OpenStackServer?> GetServerAsync(
        OpenStackCredentials credentials, string serverId, CancellationToken ct)
    {
        RequireId(serverId, nameof(serverId));
        using var response = await SendServiceAsync(
            credentials, ComputeServiceType, $"servers/{Uri.EscapeDataString(serverId)}",
            HttpMethod.Get, null, "get server", ct, allowNotFound: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        var dto = await ReadJsonAsync<OpenStackServerWrapper>(response, "get server", ct).ConfigureAwait(false);
        return dto?.Server
            ?? throw new OpenStackApiException(OpenStackFailureKind.Unexpected, "get server", "empty response body");
    }

    /// <summary>
    /// Lists Nova servers (detail), filtered server-side by tags when given and
    /// re-verified client-side by tags and metadata — a hosted filter is a
    /// hint, never a proof of ownership. Bounded by the list limits.
    /// </summary>
    public async Task<IReadOnlyList<OpenStackServer>> ListServersAsync(
        OpenStackCredentials credentials,
        IReadOnlyCollection<string>? tags,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct)
    {
        var query = new StringBuilder("servers/detail?");
        if (tags is { Count: > 0 })
        {
            foreach (var tag in tags)
                RequireTag(tag);
            query.Append("tags=");
            query.Append(string.Join(",", tags.Select(Uri.EscapeDataString)));
            query.Append('&');
        }
        query.Append("limit=").Append(ListPageSize.ToString(CultureInfo.InvariantCulture));
        var servers = await GetMarkerPagedAsync(
            credentials, ComputeServiceType, query.ToString(), "list servers",
            el => el.TryGetProperty("servers", out var items) ? items : null,
            static el => el.TryGetProperty("id", out var id) ? id.GetString() : null,
            ct).ConfigureAwait(false);
        var result = new List<OpenStackServer>();
        foreach (var el in servers)
        {
            var server = el.Deserialize<OpenStackServer>(Json);
            if (server?.Id is null)
                continue;
            if (tags is { Count: > 0 } && (server.Tags is null || !tags.All(t => server.Tags.Contains(t))))
                continue;
            if (metadata is { Count: > 0 } && (server.Metadata is null
                || !metadata.All(kvp => server.Metadata.TryGetValue(kvp.Key, out var v)
                    && string.Equals(v, kvp.Value, StringComparison.Ordinal))))
                continue;
            result.Add(server);
        }
        return result;
    }

    /// <summary>Deletes a Nova server. Returns false when it is already gone (404).</summary>
    public async Task<bool> DeleteServerAsync(
        OpenStackCredentials credentials, string serverId, CancellationToken ct)
    {
        RequireId(serverId, nameof(serverId));
        using var response = await SendServiceAsync(
            credentials, ComputeServiceType, $"servers/{Uri.EscapeDataString(serverId)}",
            HttpMethod.Delete, null, "delete server", ct, allowNotFound: true).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    /// <summary>
    /// Looks up a Nova flavor by exact (ordinal) name. Returns null when no
    /// flavor bears the name; throws when several do — an ambiguous match must
    /// never silently pick one.
    /// </summary>
    public async Task<OpenStackFlavor?> GetFlavorByNameAsync(
        OpenStackCredentials credentials, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Flavor name must not be blank.", nameof(name));
        var query = "flavors/detail?limit=" + ListPageSize.ToString(CultureInfo.InvariantCulture);
        var elements = await GetMarkerPagedAsync(
            credentials, ComputeServiceType, query, "get flavor",
            el => el.TryGetProperty("flavors", out var items) ? items : null,
            static el => el.TryGetProperty("id", out var id) ? id.GetString() : null,
            ct).ConfigureAwait(false);
        var matches = new List<OpenStackFlavor>();
        foreach (var el in elements)
        {
            var flavor = el.Deserialize<OpenStackFlavor>(Json);
            if (flavor?.Id is not null && string.Equals(flavor.Name, name, StringComparison.Ordinal))
                matches.Add(flavor);
        }
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "get flavor",
                $"flavor name '{name}' is ambiguous ({matches.Count.ToString(CultureInfo.InvariantCulture)} matches)"),
        };
    }

    /// <summary>
    /// Creates a Nova keypair. With no public key the service generates one and
    /// returns the private key — the caller owns that secret from here on.
    /// </summary>
    public async Task<OpenStackKeypair> CreateKeypairAsync(
        OpenStackCredentials credentials, string name, string? publicKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 255 || name.Contains('/'))
            throw new ArgumentException("Keypair name must be 1-255 characters without '/'.", nameof(name));
        var body = new OpenStackCreateKeypairWrapper(
            new OpenStackCreateKeypairBody(name.Trim(), string.IsNullOrWhiteSpace(publicKey) ? null : publicKey.Trim()));
        using var response = await SendServiceAsync(
            credentials, ComputeServiceType, "os-keypairs", HttpMethod.Post, body, "create keypair", ct)
            .ConfigureAwait(false);
        var dto = await ReadJsonAsync<OpenStackKeypairWrapper>(response, "create keypair", ct).ConfigureAwait(false);
        return dto?.Keypair
            ?? throw new OpenStackApiException(OpenStackFailureKind.Unexpected, "create keypair", "empty response body");
    }

    /// <summary>Deletes a Nova keypair. Returns false when it is already gone (404).</summary>
    public async Task<bool> DeleteKeypairAsync(
        OpenStackCredentials credentials, string name, CancellationToken ct)
    {
        RequireId(name, nameof(name));
        using var response = await SendServiceAsync(
            credentials, ComputeServiceType, $"os-keypairs/{Uri.EscapeDataString(name)}",
            HttpMethod.Delete, null, "delete keypair", ct, allowNotFound: true).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    /// <summary>
    /// Lists Nova keypairs whose names start with <paramref name="namePrefix"/>
    /// (ordinal). Keypairs carry no tags or metadata, so the provider-owned
    /// naming prefix is the ownership proof — the prefix must be unique to this
    /// provider. A null/empty prefix lists every keypair (bounded); callers
    /// sweeping orphans always pass their prefix.
    /// </summary>
    public async Task<IReadOnlyList<OpenStackKeypair>> ListKeypairsAsync(
        OpenStackCredentials credentials, string? namePrefix, CancellationToken ct)
    {
        using var response = await SendServiceAsync(
            credentials, ComputeServiceType, "os-keypairs", HttpMethod.Get, null, "list keypairs", ct)
            .ConfigureAwait(false);
        var raw = await ReadBoundedStringAsync(response.Content, _limits.MaxResponseBytes, "list keypairs", ct)
            .ConfigureAwait(false);
        EnsureSuccessFromBody(response, raw, "list keypairs");
        var result = new List<OpenStackKeypair>();
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("keypairs", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "list keypairs", "response has no keypairs array",
                response.StatusCode, null, GetRequestId(response));
        }
        foreach (var el in items.EnumerateArray())
        {
            var node = el;
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("keypair", out var nested))
                node = nested;
            var keypair = node.Deserialize<OpenStackKeypair>(Json);
            if (keypair?.Name is null)
                continue;
            if (!string.IsNullOrEmpty(namePrefix)
                && !keypair.Name.StartsWith(namePrefix, StringComparison.Ordinal))
                continue;
            result.Add(keypair);
            if (result.Count > _limits.MaxListItems)
            {
                throw new OpenStackApiException(
                    OpenStackFailureKind.Unexpected, "list keypairs",
                    $"keypair listing exceeded the {_limits.MaxListItems.ToString(CultureInfo.InvariantCulture)}-item bound");
            }
        }
        return result;
    }

    /// <summary>Reads Nova absolute limits plus usage, for capacity reporting.</summary>
    public async Task<OpenStackLimits> GetLimitsAsync(OpenStackCredentials credentials, CancellationToken ct)
    {
        using var response = await SendServiceAsync(
            credentials, ComputeServiceType, "limits", HttpMethod.Get, null, "get limits", ct)
            .ConfigureAwait(false);
        var dto = await ReadJsonAsync<OpenStackLimitsWrapper>(response, "get limits", ct).ConfigureAwait(false);
        return dto?.Limits?.Absolute
            ?? throw new OpenStackApiException(OpenStackFailureKind.Unexpected, "get limits", "empty response body");
    }

    /// <summary>
    /// Snapshots a server into a Glance image via the Nova
    /// <c>createImage</c> server action. Returns the image id, read from the
    /// response body or the <c>Location</c> header — whichever the cloud sends.
    /// </summary>
    public async Task<string> SnapshotServerAsync(
        OpenStackCredentials credentials, string serverId, string imageName,
        IReadOnlyDictionary<string, string>? metadata, CancellationToken ct)
    {
        RequireId(serverId, nameof(serverId));
        if (string.IsNullOrWhiteSpace(imageName))
            throw new ArgumentException("Image name must not be blank.", nameof(imageName));
        var body = new OpenStackServerActionWrapper(
            new OpenStackCreateImageAction(imageName.Trim(), metadata ?? new Dictionary<string, string>()));
        using var response = await SendServiceAsync(
            credentials, ComputeServiceType, $"servers/{Uri.EscapeDataString(serverId)}/action",
            HttpMethod.Post, body, "snapshot server", ct).ConfigureAwait(false);
        var raw = await ReadBoundedStringAsync(response.Content, _limits.MaxResponseBytes, "snapshot server", ct)
            .ConfigureAwait(false);
        EnsureSuccessFromBody(response, raw, "snapshot server");
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("image_id", out var imageId)
                && imageId.GetString() is { Length: > 0 } id)
            {
                return id;
            }
        }
        catch (JsonException)
        {
        }
        var location = response.Headers.Location?.ToString();
        var lastSegment = location?.TrimEnd('/').Split('/').LastOrDefault();
        if (!string.IsNullOrEmpty(lastSegment))
            return lastSegment;
        throw new OpenStackApiException(
            OpenStackFailureKind.Unexpected, "snapshot server",
            "createImage accepted but returned no image id", response.StatusCode, null, GetRequestId(response));
    }

    /// <summary>
    /// Polls a server until it reaches one of <paramref name="desiredStatuses"/>
    /// (exact, case-insensitive). A status in <paramref name="faultStatuses"/>
    /// (default <c>ERROR</c>) fails fast with the server's fault text; leaving
    /// the timeout throws a typed timeout. Backoff is bounded and cancellation
    /// aborts the wait.
    /// </summary>
    public async Task<OpenStackServer> WaitForServerStatusAsync(
        OpenStackCredentials credentials, string serverId,
        IReadOnlyCollection<string> desiredStatuses, CancellationToken ct,
        TimeSpan? timeout = null, IReadOnlyCollection<string>? faultStatuses = null)
    {
        RequireId(serverId, nameof(serverId));
        if (desiredStatuses is null || desiredStatuses.Count == 0)
            throw new ArgumentException("At least one desired status is required.", nameof(desiredStatuses));
        faultStatuses ??= ["ERROR"];
        var deadline = _clock.GetUtcNow() + (timeout ?? DefaultWaitTimeout);
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var server = await GetServerAsync(credentials, serverId, ct).ConfigureAwait(false)
                ?? throw new OpenStackApiException(
                    OpenStackFailureKind.NotFound, "wait for server status",
                    $"server '{SanitizeForLog(serverId)}' disappeared while waiting");
            if (server.Status is not null
                && desiredStatuses.Any(s => string.Equals(s, server.Status, StringComparison.OrdinalIgnoreCase)))
            {
                return server;
            }
            if (server.Status is not null
                && faultStatuses.Any(s => string.Equals(s, server.Status, StringComparison.OrdinalIgnoreCase)))
            {
                throw new OpenStackApiException(
                    OpenStackFailureKind.Unexpected, "wait for server status",
                    $"server '{SanitizeForLog(serverId)}' entered fault status '{SanitizeForLog(server.Status)}'" +
                    (string.IsNullOrEmpty(server.FaultMessage) ? string.Empty : $": {SanitizeForLog(server.FaultMessage)}"));
            }
            var now = _clock.GetUtcNow();
            if (now >= deadline)
            {
                throw new OpenStackApiException(
                    OpenStackFailureKind.Unexpected, "wait for server status",
                    $"server '{SanitizeForLog(serverId)}' did not reach '{SanitizeForLog(string.Join(",", desiredStatuses))}' in time " +
                    $"(last status '{SanitizeForLog(server.Status ?? "unknown")}')");
            }
            var delay = NextPollDelay(attempt++);
            var remaining = deadline - now;
            if (delay > remaining)
                delay = remaining;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Polls until the server reads 404. Leaving the timeout throws a typed timeout.</summary>
    public async Task WaitForServerDeletedAsync(
        OpenStackCredentials credentials, string serverId, CancellationToken ct, TimeSpan? timeout = null)
    {
        RequireId(serverId, nameof(serverId));
        var deadline = _clock.GetUtcNow() + (timeout ?? DefaultWaitTimeout);
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await GetServerAsync(credentials, serverId, ct).ConfigureAwait(false) is null)
                return;
            var now = _clock.GetUtcNow();
            if (now >= deadline)
            {
                throw new OpenStackApiException(
                    OpenStackFailureKind.Unexpected, "wait for server deletion",
                    $"server '{SanitizeForLog(serverId)}' was not deleted in time");
            }
            var delay = NextPollDelay(attempt++);
            var remaining = deadline - now;
            if (delay > remaining)
                delay = remaining;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
        }
    }

    private TimeSpan NextPollDelay(int attempt)
    {
        var doubled = _limits.PollInterval.TotalMilliseconds * (1L << Math.Min(attempt, 20));
        var capped = Math.Min(doubled, _limits.MaxPollInterval.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(capped, 1));
    }

    // ------------------------------------------------------------------
    // Neutron (network)
    // ------------------------------------------------------------------

    /// <summary>Creates a Neutron security group.</summary>
    public async Task<OpenStackSecurityGroup> CreateSecurityGroupAsync(
        OpenStackCredentials credentials, string name, string? description, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 255)
            throw new ArgumentException("Security group name must be 1-255 characters.", nameof(name));
        var body = new OpenStackCreateSecurityGroupWrapper(
            new OpenStackCreateSecurityGroupBody(name.Trim(), description?.Trim()));
        using var response = await SendServiceAsync(
            credentials, NetworkServiceType, "v2.0/security-groups", HttpMethod.Post, body,
            "create security group", ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<OpenStackSecurityGroupWrapper>(response, "create security group", ct)
            .ConfigureAwait(false);
        return dto?.SecurityGroup
            ?? throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "create security group", "empty response body");
    }

    /// <summary>
    /// Deletes a Neutron security group. Returns false when already gone (404);
    /// a group still attached to a port surfaces as a typed 409 conflict.
    /// </summary>
    public async Task<bool> DeleteSecurityGroupAsync(
        OpenStackCredentials credentials, string securityGroupId, CancellationToken ct)
    {
        RequireId(securityGroupId, nameof(securityGroupId));
        using var response = await SendServiceAsync(
            credentials, NetworkServiceType, $"v2.0/security-groups/{Uri.EscapeDataString(securityGroupId)}",
            HttpMethod.Delete, null, "delete security group", ct, allowNotFound: true).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    /// <summary>
    /// Lists Neutron security groups whose names start with
    /// <paramref name="namePrefix"/> (ordinal, client-side — a hosted filter is
    /// a hint, never a proof of ownership). Bounded by the list limits.
    /// </summary>
    public async Task<IReadOnlyList<OpenStackSecurityGroup>> ListSecurityGroupsAsync(
        OpenStackCredentials credentials, string? namePrefix, CancellationToken ct)
    {
        var query = "v2.0/security-groups?limit=" + ListPageSize.ToString(CultureInfo.InvariantCulture);
        var elements = await GetMarkerPagedAsync(
            credentials, NetworkServiceType, query, "list security groups",
            el => el.TryGetProperty("security_groups", out var items) ? items : null,
            static el => el.TryGetProperty("id", out var id) ? id.GetString() : null,
            ct).ConfigureAwait(false);
        var result = new List<OpenStackSecurityGroup>();
        foreach (var el in elements)
        {
            var group = el.Deserialize<OpenStackSecurityGroup>(Json);
            if (group?.Id is null)
                continue;
            if (!string.IsNullOrEmpty(namePrefix)
                && (group.Name is null || !group.Name.StartsWith(namePrefix, StringComparison.Ordinal)))
                continue;
            result.Add(group);
        }
        return result;
    }

    /// <summary>Creates a Neutron security-group rule. Direction/ethertype are exact-match validated.</summary>
    public async Task<OpenStackSecurityGroupRule> CreateSecurityGroupRuleAsync(
        OpenStackCredentials credentials, OpenStackSecurityGroupRuleSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        spec.Validate();
        var body = new OpenStackCreateSecurityGroupRuleWrapper(new OpenStackCreateSecurityGroupRuleBody(
            spec.SecurityGroupId, spec.Direction, spec.Ethertype, spec.Protocol,
            spec.PortRangeMin, spec.PortRangeMax, spec.RemoteIpPrefix));
        using var response = await SendServiceAsync(
            credentials, NetworkServiceType, "v2.0/security-group-rules", HttpMethod.Post, body,
            "create security group rule", ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<OpenStackSecurityGroupRuleWrapper>(response, "create security group rule", ct)
            .ConfigureAwait(false);
        return dto?.Rule
            ?? throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "create security group rule", "empty response body");
    }

    /// <summary>
    /// Allocates a floating IP on the given external network. The optional
    /// <paramref name="description"/> stamps ownership
    /// (<c>codeybox owner=… server=…</c>) so orphan sweeps only release
    /// provider-owned addresses.
    /// </summary>
    public async Task<OpenStackFloatingIp> CreateFloatingIpAsync(
        OpenStackCredentials credentials, string floatingNetworkId, CancellationToken ct,
        string? description = null)
    {
        RequireId(floatingNetworkId, nameof(floatingNetworkId));
        var body = new OpenStackCreateFloatingIpWrapper(
            new OpenStackCreateFloatingIpBody(floatingNetworkId, description?.Trim()));
        using var response = await SendServiceAsync(
            credentials, NetworkServiceType, "v2.0/floatingips", HttpMethod.Post, body,
            "create floating IP", ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<OpenStackFloatingIpWrapper>(response, "create floating IP", ct)
            .ConfigureAwait(false);
        return dto?.FloatingIp
            ?? throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "create floating IP", "empty response body");
    }

    /// <summary>Associates a floating IP with a port (Nova server port).</summary>
    public async Task AssociateFloatingIpAsync(
        OpenStackCredentials credentials, string floatingIpId, string portId, CancellationToken ct)
    {
        RequireId(floatingIpId, nameof(floatingIpId));
        RequireId(portId, nameof(portId));
        var body = new OpenStackUpdateFloatingIpWrapper(new OpenStackUpdateFloatingIpBody(portId));
        using var response = await SendServiceAsync(
            credentials, NetworkServiceType, $"v2.0/floatingips/{Uri.EscapeDataString(floatingIpId)}",
            HttpMethod.Put, body, "associate floating IP", ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a floating IP, releasing it. Returns false when already gone (404).</summary>
    public async Task<bool> DeleteFloatingIpAsync(
        OpenStackCredentials credentials, string floatingIpId, CancellationToken ct)
    {
        RequireId(floatingIpId, nameof(floatingIpId));
        using var response = await SendServiceAsync(
            credentials, NetworkServiceType, $"v2.0/floatingips/{Uri.EscapeDataString(floatingIpId)}",
            HttpMethod.Delete, null, "delete floating IP", ct, allowNotFound: true).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    /// <summary>
    /// Lists Neutron floating IPs whose description starts with
    /// <paramref name="descriptionPrefix"/> (ordinal, client-side). The
    /// provider stamps every floating IP it allocates with a
    /// <c>codeybox owner=… server=…</c> description, so orphan sweeps only
    /// touch provider-owned addresses. Bounded by the list limits.
    /// </summary>
    public async Task<IReadOnlyList<OpenStackFloatingIp>> ListFloatingIpsAsync(
        OpenStackCredentials credentials, string? descriptionPrefix, CancellationToken ct)
    {
        var query = "v2.0/floatingips?limit=" + ListPageSize.ToString(CultureInfo.InvariantCulture);
        var elements = await GetMarkerPagedAsync(
            credentials, NetworkServiceType, query, "list floating IPs",
            el => el.TryGetProperty("floatingips", out var items) ? items : null,
            static el => el.TryGetProperty("id", out var id) ? id.GetString() : null,
            ct).ConfigureAwait(false);
        var result = new List<OpenStackFloatingIp>();
        foreach (var el in elements)
        {
            var floatingIp = el.Deserialize<OpenStackFloatingIp>(Json);
            if (floatingIp?.Id is null)
                continue;
            if (!string.IsNullOrEmpty(descriptionPrefix)
                && (floatingIp.Description is null
                    || !floatingIp.Description.StartsWith(descriptionPrefix, StringComparison.Ordinal)))
                continue;
            result.Add(floatingIp);
        }
        return result;
    }

    /// <summary>Lists Neutron ports attached to a device (e.g. a Nova server id).</summary>
    public async Task<IReadOnlyList<OpenStackPort>> ListPortsByDeviceAsync(
        OpenStackCredentials credentials, string deviceId, CancellationToken ct)
    {
        RequireId(deviceId, nameof(deviceId));
        var query = "v2.0/ports?device_id=" + Uri.EscapeDataString(deviceId)
            + "&limit=" + ListPageSize.ToString(CultureInfo.InvariantCulture);
        var elements = await GetMarkerPagedAsync(
            credentials, NetworkServiceType, query, "list ports",
            el => el.TryGetProperty("ports", out var items) ? items : null,
            static el => el.TryGetProperty("id", out var id) ? id.GetString() : null,
            ct).ConfigureAwait(false);
        var result = new List<OpenStackPort>();
        foreach (var el in elements)
        {
            var port = el.Deserialize<OpenStackPort>(Json);
            if (port?.Id is null)
                continue;
            if (!string.Equals(port.DeviceId, deviceId, StringComparison.Ordinal))
                continue;
            result.Add(port);
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Glance (image)
    // ------------------------------------------------------------------

    /// <summary>Gets a Glance image. Returns null when it does not exist (404).</summary>
    public async Task<OpenStackImage?> GetImageAsync(
        OpenStackCredentials credentials, string imageId, CancellationToken ct)
    {
        RequireId(imageId, nameof(imageId));
        using var response = await SendServiceAsync(
            credentials, ImageServiceType, $"v2/images/{Uri.EscapeDataString(imageId)}",
            HttpMethod.Get, null, "get image", ct, allowNotFound: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        return await ReadJsonAsync<OpenStackImage>(response, "get image", ct).ConfigureAwait(false)
            ?? throw new OpenStackApiException(OpenStackFailureKind.Unexpected, "get image", "empty response body");
    }

    /// <summary>
    /// Lists Glance images, filtered server-side by name/tag when given and
    /// re-verified client-side (exact name, tag membership). Follows
    /// <c>next</c> links only when they stay on the same https host.
    /// </summary>
    public async Task<IReadOnlyList<OpenStackImage>> ListImagesAsync(
        OpenStackCredentials credentials, string? name, string? tag, CancellationToken ct)
    {
        var query = new StringBuilder("v2/images?limit=");
        query.Append(ListPageSize.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(name))
            query.Append("&name=").Append(Uri.EscapeDataString(name.Trim()));
        if (!string.IsNullOrWhiteSpace(tag))
            query.Append("&tag=").Append(Uri.EscapeDataString(tag.Trim()));
        var result = new List<OpenStackImage>();
        var nextPath = query.ToString();
        for (var page = 0; page < _limits.MaxListPages; page++)
        {
            using var response = await SendServiceAsync(
                    credentials, ImageServiceType, nextPath, HttpMethod.Get, null, "list images", ct)
                .ConfigureAwait(false);
            var raw = await ReadBoundedStringAsync(response.Content, _limits.MaxResponseBytes, "list images", ct)
                .ConfigureAwait(false);
            EnsureSuccessFromBody(response, raw, "list images");
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("images", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                throw new OpenStackApiException(
                    OpenStackFailureKind.Unexpected, "list images", "response has no images array",
                    response.StatusCode, null, GetRequestId(response));
            }
            foreach (var el in items.EnumerateArray())
            {
                var image = el.Deserialize<OpenStackImage>(Json);
                if (image?.Id is null)
                    continue;
                if (!string.IsNullOrWhiteSpace(name)
                    && !string.Equals(image.Name, name.Trim(), StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrWhiteSpace(tag)
                    && (image.Tags is null || !image.Tags.Contains(tag.Trim())))
                    continue;
                result.Add(image);
                if (result.Count > _limits.MaxListItems)
                {
                    throw new OpenStackApiException(
                        OpenStackFailureKind.Unexpected, "list images",
                        $"image listing exceeded the {_limits.MaxListItems.ToString(CultureInfo.InvariantCulture)}-item bound");
                }
            }
            if (!doc.RootElement.TryGetProperty("next", out var next)
                || next.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(next.GetString()))
            {
                return result;
            }
            nextPath = SanitizeGlanceNext(next.GetString()!, response, "list images");
        }
        throw new OpenStackApiException(
            OpenStackFailureKind.Unexpected, "list images",
            $"image listing exceeded the {_limits.MaxListPages.ToString(CultureInfo.InvariantCulture)}-page bound");
    }

    /// <summary>
    /// A Glance <c>next</c> link is untrusted service output aimed at an
    /// outbound-request sink: follow it only when it is an https URL on the
    /// same host as the service endpoint (http only for loopback tests).
    /// </summary>
    private string SanitizeGlanceNext(string next, HttpResponseMessage response, string operation)
    {
        if (!Uri.TryCreate(next.Trim(), UriKind.Absolute, out var nextUri)
            || !Uri.TryCreate(response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Authority) ?? string.Empty,
                UriKind.Absolute, out var baseUri)
            || !string.Equals(nextUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || nextUri.Port != baseUri.Port)
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, operation,
                "image listing returned a next link pointing off-host; refusing to follow it");
        }
        if (nextUri.Scheme == Uri.UriSchemeHttp && !IsCleartextHttpPermitted(nextUri, _limits.AllowUnsafeHttp))
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, operation,
                "image listing returned a cleartext http next link to a non-loopback host; refusing to follow it");
        }
        if (nextUri.Scheme != Uri.UriSchemeHttps && nextUri.Scheme != Uri.UriSchemeHttp)
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, operation,
                "image listing returned a next link with an unexpected scheme; refusing to follow it");
        }
        return nextUri.PathAndQuery.TrimStart('/');
    }

    /// <summary>Deletes a Glance image. Returns false when already gone (404).</summary>
    public async Task<bool> DeleteImageAsync(
        OpenStackCredentials credentials, string imageId, CancellationToken ct)
    {
        RequireId(imageId, nameof(imageId));
        using var response = await SendServiceAsync(
            credentials, ImageServiceType, $"v2/images/{Uri.EscapeDataString(imageId)}",
            HttpMethod.Delete, null, "delete image", ct, allowNotFound: true).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    // ------------------------------------------------------------------
    // Transport core
    // ------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendServiceAsync(
        OpenStackCredentials credentials, string serviceType, string path, HttpMethod method,
        object? body, string operation, CancellationToken ct, bool reauthed = false, bool allowNotFound = false)
    {
        var token = await GetTokenAsync(credentials, forceRefresh: false, ct).ConfigureAwait(false);
        var endpoint = ResolveServiceEndpoint(token.Catalog, serviceType, credentials.Region, credentials.Interface);
        var uri = JoinUri(endpoint, path);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Auth-Token", token.Token);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, body.GetType(), Json), Encoding.UTF8, "application/json");
        }
        var response = await SendUnconditionallyAsync(request, operation, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized && !reauthed)
        {
            // The token died between cache check and use (or the service
            // rotated it): re-authenticate exactly once, then retry.
            response.Dispose();
            await GetTokenAsync(credentials, forceRefresh: true, ct).ConfigureAwait(false);
            return await SendServiceAsync(
                credentials, serviceType, path, method, body, operation, ct,
                reauthed: true, allowNotFound: allowNotFound).ConfigureAwait(false);
        }
        if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound)
            return response;
        if (!response.IsSuccessStatusCode)
        {
            using (response)
                await ThrowServiceErrorAsync(response, operation, ct).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendUnconditionallyAsync(
        HttpRequestMessage request, string operation, CancellationToken ct)
    {
        // Per-request timeout that never masks caller cancellation: a linked
        // CTS trips on HttpTimeout while the caller's token still reports why.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_limits.HttpTimeout);
        try
        {
            return await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unreachable, operation, "transport error", null, null, null, null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unreachable, operation,
                $"request exceeded the {_limits.HttpTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s timeout",
                null, null, null, null, ex);
        }
    }

    private async Task<List<JsonElement>> GetMarkerPagedAsync(
        OpenStackCredentials credentials, string serviceType, string firstQuery, string operation,
        Func<JsonElement, JsonElement?> pickItems,
        Func<JsonElement, string?> pickId, CancellationToken ct)
    {
        var collected = new List<JsonElement>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        string? marker = null;
        for (var page = 0; page < _limits.MaxListPages; page++)
        {
            var path = marker is null ? firstQuery : firstQuery + "&marker=" + Uri.EscapeDataString(marker);
            using var response = await SendServiceAsync(
                    credentials, serviceType, path, HttpMethod.Get, null, operation, ct).ConfigureAwait(false);
            var raw = await ReadBoundedStringAsync(response.Content, _limits.MaxResponseBytes, operation, ct)
                .ConfigureAwait(false);
            EnsureSuccessFromBody(response, raw, operation);
            using var doc = JsonDocument.Parse(raw);
            var items = pickItems(doc.RootElement);
            if (items is not { ValueKind: JsonValueKind.Array })
                return collected;
            var newThisPage = 0;
            string? lastId = null;
            foreach (var el in items.Value.EnumerateArray())
            {
                var id = pickId(el);
                if (id is not null)
                    lastId = id;
                if (id is null || !seenIds.Add(id))
                    continue;
                collected.Add(el.Clone());
                newThisPage++;
                if (collected.Count > _limits.MaxListItems)
                {
                    throw new OpenStackApiException(
                        OpenStackFailureKind.Unexpected, operation,
                        $"listing exceeded the {_limits.MaxListItems.ToString(CultureInfo.InvariantCulture)}-item bound");
                }
            }
            // A short page ends the listing; zero new ids means the service
            // ignored the marker — either way there is nothing more to fetch.
            if (newThisPage < ListPageSize || lastId is null)
                return collected;
            marker = lastId;
        }
        throw new OpenStackApiException(
            OpenStackFailureKind.Unexpected, operation,
            $"listing exceeded the {_limits.MaxListPages.ToString(CultureInfo.InvariantCulture)}-page bound; " +
            "refusing to report a truncated inventory");
    }

    private async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct)
        where T : class
    {
        var raw = await ReadBoundedStringAsync(response.Content, _limits.MaxResponseBytes, operation, ct)
            .ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(raw, Json);
        }
        catch (JsonException ex)
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, operation, "unparseable response body",
                response.StatusCode, null, GetRequestId(response), null, ex);
        }
    }

    private async Task<string> ReadBoundedStringAsync(
        HttpContent content, int maxBytes, string operation, CancellationToken ct)
    {
        // Cap BEFORE buffering: stream through a bounded copy so a malicious
        // or broken service cannot OOM the host with one huge body.
        try
        {
            using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var bytes = await ReadTruncatedAsync(stream, maxBytes + 1, ct).ConfigureAwait(false);
            if (bytes.Length > maxBytes)
            {
                throw new OpenStackApiException(
                    OpenStackFailureKind.Unexpected, operation,
                    $"response exceeded the {maxBytes.ToString(CultureInfo.InvariantCulture)}-byte bound");
            }
            return Encoding.UTF8.GetString(bytes);
        }
        catch (OpenStackApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        {
            if (ex is TaskCanceledException && ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw new OpenStackApiException(
                OpenStackFailureKind.Unreachable, operation, "transport error while reading response",
                null, null, null, null, ex);
        }
    }

    /// <summary>
    /// Streams up to <paramref name="maxBytes"/> bytes, discarding the rest.
    /// Single copy of the stream/read-chunk/truncate loop shared by the
    /// bounded response and error-body readers.
    /// </summary>
    private static async Task<byte[]> ReadTruncatedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0;
        int read;
        while (total < maxBytes && (read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maxBytes - total)), ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            total += read;
        }
        return buffer.ToArray();
    }

    private async Task ThrowServiceErrorAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var body = await ReadErrorBodyAsync(response, ct).ConfigureAwait(false);
        throw BuildServiceError(response, operation, body);
    }

    private void EnsureSuccessFromBody(HttpResponseMessage response, string rawBody, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;
        var bounded = rawBody.Length > MaxErrorBodyChars ? rawBody[..MaxErrorBodyChars] : rawBody;
        throw BuildServiceError(response, operation, SanitizeForLog(bounded));
    }

    private static OpenStackApiException BuildServiceError(
        HttpResponseMessage response, string operation, string? body)
    {
        var kind = OpenStackApiException.FromStatus(response.StatusCode, body);
        var (faultCode, message) = ExtractFault(body);
        // ExtractFault JSON-decodes the body, materializing \uXXXX escapes
        // (e.g. \u001b, \u000a) back into live control characters that the
        // pre-decode SanitizeForLog on the raw body cannot see. Sanitize the
        // decoded values (and the request-id header) after decoding so log-
        // bound exception text never carries forging/escape characters.
        return new OpenStackApiException(
            kind, operation, message is null ? body ?? "no response body" : SanitizeForLog(message),
            response.StatusCode,
            faultCode is null ? null : SanitizeForLog(faultCode),
            GetRequestId(response) is { } requestId ? SanitizeForLog(requestId) : null,
            GetRetryAfter(response, body));
    }

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        string? body = null;
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var raw = Encoding.UTF8.GetString(await ReadTruncatedAsync(stream, 8192, ct).ConfigureAwait(false));
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var trimmed = raw.Trim();
                body = SanitizeForLog(trimmed.Length > MaxErrorBodyChars ? trimmed[..MaxErrorBodyChars] : trimmed);
            }
        }
        catch (Exception)
        {
        }
        throw BuildServiceError(response, operation, body);
    }

    private async Task<string?> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Error detail only: a small fixed cap, streamed, so an error page
        // (Nova debug HTML can be large) never becomes an allocation bomb.
        const int maxErrorBytes = 8192;
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var raw = Encoding.UTF8.GetString(await ReadTruncatedAsync(stream, maxErrorBytes, ct).ConfigureAwait(false));
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var trimmed = raw.Trim();
            return SanitizeForLog(trimmed.Length > MaxErrorBodyChars ? trimmed[..MaxErrorBodyChars] : trimmed);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls (fault code, message) out of the known OpenStack error shapes:
    /// Keystone/Glance <c>{"error": …}</c>, Nova <c>{"&lt;kind&gt;": …}</c>,
    /// Neutron <c>{"NeutronError": …}</c>. Unknown shapes fall back to the raw body.
    /// </summary>
    internal static (string? FaultCode, string? Message) ExtractFault(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);
            if (doc.RootElement.TryGetProperty("message", out var topMessage)
                && topMessage.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(topMessage.GetString()))
            {
                return (doc.RootElement.TryGetProperty("code", out var topCode)
                    ? topCode.ToString() : null, topMessage.GetString());
            }
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                    continue;
                string? message = null;
                string? type = null;
                if (prop.Value.TryGetProperty("message", out var msg)
                    && msg.ValueKind == JsonValueKind.String)
                {
                    message = msg.GetString();
                }
                if (prop.Value.TryGetProperty("type", out var typ)
                    && typ.ValueKind == JsonValueKind.String)
                {
                    type = typ.GetString();
                }
                if (!string.IsNullOrWhiteSpace(message))
                    return (type is null ? prop.Name : $"{prop.Name}:{type}", message);
            }
            return (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? GetRequestId(HttpResponseMessage response)
    {
        foreach (var header in RequestIdHeaders)
        {
            if (response.Headers.TryGetValues(header, out var values))
            {
                var id = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(id))
                    return SanitizeForLog(id.Trim());
            }
        }
        return null;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response, string? body)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
            return delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("retryAfter", out var retryAfter)
                        && retryAfter.ValueKind == JsonValueKind.Number
                        && retryAfter.TryGetInt32(out var seconds)
                        && seconds >= 0 && seconds <= 3600)
                    {
                        return TimeSpan.FromSeconds(seconds);
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    private static void RequireId(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Any(char.IsControl))
            throw new ArgumentException("Resource id must be non-blank without '/' or control characters.", paramName);
    }

    private static void RequireTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > MaxServerTagChars || tag.Any(char.IsControl))
            throw new ArgumentException($"Server tag must be 1-{OpenStackApiClient.MaxServerTagChars} printable characters.", nameof(tag));
    }

    private static Uri JoinUrl(string baseUrl, string path) =>
        new(baseUrl.TrimEnd('/') + "/" + path.TrimStart('/'), UriKind.Absolute);

    private static Uri JoinUri(Uri baseUri, string path) =>
        JoinUrl(baseUri.AbsoluteUri, path);

    /// <summary>
    /// Folds ASCII control characters to spaces so untrusted service text
    /// cannot forge log lines or inject terminal escapes via exceptions.
    /// (Local copy of the two-line Daytona pattern: that helper lives in a
    /// plugin assembly this client must not reference.)
    /// </summary>
    internal static string SanitizeForLog(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < 0x20 || chars[i] == 0x7F)
                chars[i] = ' ';
        }
        return new string(chars);
    }
}

/// <summary>Keystone service catalog: the token's map of service types to regional endpoints.</summary>
public sealed class KeystoneCatalog
{
    /// <summary>Catalog services.</summary>
    [JsonPropertyName("catalog")]
    public List<KeystoneService> Services { get; set; } = [];
}

/// <summary>One catalog service (e.g. type <c>compute</c>).</summary>
public sealed class KeystoneService
{
    /// <summary>Service type (<c>compute</c>, <c>network</c>, <c>image</c>, <c>identity</c>).</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Service name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Regional endpoints, one per interface.</summary>
    [JsonPropertyName("endpoints")]
    public List<KeystoneEndpoint> Endpoints { get; set; } = [];
}

/// <summary>One catalog endpoint: an interface URL in a region.</summary>
public sealed class KeystoneEndpoint
{
    /// <summary>Endpoint id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Interface: <c>public</c>, <c>internal</c>, or <c>admin</c>.</summary>
    [JsonPropertyName("interface")]
    public string? Interface { get; set; }

    /// <summary>Region name.</summary>
    [JsonPropertyName("region")]
    public string? Region { get; set; }

    /// <summary>Endpoint URL.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>Nova server record (subset the client consumes).</summary>
public sealed class OpenStackServer
{
    /// <summary>Server id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Server name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Status (<c>BUILD</c>, <c>ACTIVE</c>, <c>ERROR</c>, …).</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Tags.</summary>
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    /// <summary>Metadata.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>Fault message when in <c>ERROR</c> status.</summary>
    [JsonPropertyName("fault")]
    public OpenStackServerFault? Fault { get; set; }

    /// <summary>Flattened fault message, if any.</summary>
    [JsonIgnore]
    public string? FaultMessage => Fault?.Message;
}

/// <summary>Nova server fault block.</summary>
public sealed class OpenStackServerFault
{
    /// <summary>Fault message.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Fault code.</summary>
    [JsonPropertyName("code")]
    public int? Code { get; set; }
}

/// <summary>Parameters for a Nova server create.</summary>
public sealed record OpenStackServerSpec(
    string Name,
    string FlavorRef,
    string? ImageRef,
    IReadOnlyList<string> NetworkIds,
    string? KeyName = null,
    string? UserData = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? SecurityGroupNames = null)
{
    /// <summary>Validates sizes and shapes before anything reaches the wire.</summary>
    /// <exception cref="ArgumentException">A value is missing or over its bound.</exception>
    public void Validate(int maxUserDataBytes)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 255)
            throw new ArgumentException("Server name must be 1-255 characters.", nameof(Name));
        if (string.IsNullOrWhiteSpace(FlavorRef))
            throw new ArgumentException("FlavorRef must not be blank.", nameof(FlavorRef));
        if (NetworkIds is null || NetworkIds.Count == 0 || NetworkIds.Count > OpenStackApiClient.MaxServerNetworks)
            throw new ArgumentException(
                $"At least one and at most {OpenStackApiClient.MaxServerNetworks.ToString(CultureInfo.InvariantCulture)} networks are required.",
                nameof(NetworkIds));
        if (NetworkIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Network ids must not be blank.", nameof(NetworkIds));
        if (UserData is not null && Encoding.UTF8.GetByteCount(UserData) > maxUserDataBytes)
            throw new ArgumentException($"UserData exceeds the {maxUserDataBytes}-byte bound.", nameof(UserData));
        if (Metadata is not null)
        {
            if (Metadata.Count > OpenStackApiClient.MaxMetadataEntries)
                throw new ArgumentException(
                    $"At most {OpenStackApiClient.MaxMetadataEntries.ToString(CultureInfo.InvariantCulture)} metadata entries are allowed.",
                    nameof(Metadata));
            foreach (var kvp in Metadata)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Key.Length > OpenStackApiClient.MaxMetadataChars
                    || kvp.Value is null || kvp.Value.Length > OpenStackApiClient.MaxMetadataChars)
                    throw new ArgumentException(
                        $"Metadata keys/values must be 1-{OpenStackApiClient.MaxMetadataChars.ToString(CultureInfo.InvariantCulture)} characters.",
                        nameof(Metadata));
            }
        }
        if (Tags is not null)
        {
            if (Tags.Count > OpenStackApiClient.MaxServerTags)
                throw new ArgumentException(
                    $"At most {OpenStackApiClient.MaxServerTags.ToString(CultureInfo.InvariantCulture)} tags are allowed.", nameof(Tags));
            foreach (var tag in Tags)
            {
                if (string.IsNullOrWhiteSpace(tag) || tag.Length > OpenStackApiClient.MaxServerTagChars || tag.Any(char.IsControl))
                    throw new ArgumentException(
                        $"Tags must be 1-{OpenStackApiClient.MaxServerTagChars.ToString(CultureInfo.InvariantCulture)} printable characters.",
                        nameof(Tags));
            }
        }
        if (SecurityGroupNames is not null)
        {
            if (SecurityGroupNames.Count == 0 || SecurityGroupNames.Count > OpenStackApiClient.MaxServerNetworks)
                throw new ArgumentException(
                    "At least one and at most "
                    + OpenStackApiClient.MaxServerNetworks.ToString(CultureInfo.InvariantCulture)
                    + " security groups are required when specified.",
                    nameof(SecurityGroupNames));
            foreach (var group in SecurityGroupNames)
            {
                if (string.IsNullOrWhiteSpace(group) || group.Trim().Length > 255 || group.Any(char.IsControl))
                    throw new ArgumentException(
                        "Security group names must be 1-255 printable characters.", nameof(SecurityGroupNames));
            }
        }
    }

    /// <summary>Base64-encoded user_data for the Nova create body (null when no cloud-config).</summary>
    public string? UserDataBase64() =>
        UserData is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(UserData));
}

/// <summary>Nova flavor record.</summary>
public sealed class OpenStackFlavor
{
    /// <summary>Flavor id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Flavor name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>vCPUs.</summary>
    [JsonPropertyName("vcpus")]
    public int? Vcpus { get; set; }

    /// <summary>RAM in MiB.</summary>
    [JsonPropertyName("ram")]
    public int? RamMb { get; set; }

    /// <summary>Disk in GiB.</summary>
    [JsonPropertyName("disk")]
    public int? DiskGb { get; set; }
}

/// <summary>Nova keypair record. <c>PrivateKey</c> is present only when the service generated it.</summary>
public sealed class OpenStackKeypair
{
    /// <summary>Keypair name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Public key material.</summary>
    [JsonPropertyName("public_key")]
    public string? PublicKey { get; set; }

    /// <summary>Generated private key (create-only; the caller owns this secret).</summary>
    [JsonPropertyName("private_key")]
    public string? PrivateKey { get; set; }
}

/// <summary>Nova absolute limits plus usage, for capacity reporting.</summary>
public sealed class OpenStackLimits
{
    /// <summary>Maximum total instances, or null when the cloud reports none.</summary>
    [JsonPropertyName("maxTotalInstances")]
    public int? MaxTotalInstances { get; set; }

    /// <summary>Instances in use.</summary>
    [JsonPropertyName("totalInstancesUsed")]
    public int? TotalInstancesUsed { get; set; }

    /// <summary>Maximum total cores.</summary>
    [JsonPropertyName("maxTotalCores")]
    public int? MaxTotalCores { get; set; }

    /// <summary>Cores in use.</summary>
    [JsonPropertyName("totalCoresUsed")]
    public int? TotalCoresUsed { get; set; }

    /// <summary>Maximum total RAM in MiB.</summary>
    [JsonPropertyName("maxTotalRAMSize")]
    public int? MaxTotalRamMb { get; set; }

    /// <summary>RAM in use, in MiB.</summary>
    [JsonPropertyName("totalRAMUsed")]
    public int? TotalRamUsedMb { get; set; }
}

/// <summary>Neutron security-group record.</summary>
public sealed class OpenStackSecurityGroup
{
    /// <summary>Security group id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Security group name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>Parameters for a Neutron security-group rule create.</summary>
public sealed record OpenStackSecurityGroupRuleSpec(
    string SecurityGroupId,
    string Direction,
    string Ethertype,
    string? Protocol = null,
    int? PortRangeMin = null,
    int? PortRangeMax = null,
    string? RemoteIpPrefix = null)
{
    /// <summary>Validates exact-match enums, port ranges, and the CIDR prefix.</summary>
    /// <exception cref="ArgumentException">A value is missing or malformed.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SecurityGroupId) || SecurityGroupId.Contains('/'))
            throw new ArgumentException("SecurityGroupId must be non-blank without '/'.", nameof(SecurityGroupId));
        if (!string.Equals(Direction, "ingress", StringComparison.Ordinal)
            && !string.Equals(Direction, "egress", StringComparison.Ordinal))
            throw new ArgumentException("Direction must be 'ingress' or 'egress' (exact match).", nameof(Direction));
        if (!string.Equals(Ethertype, "IPv4", StringComparison.Ordinal)
            && !string.Equals(Ethertype, "IPv6", StringComparison.Ordinal))
            throw new ArgumentException("Ethertype must be 'IPv4' or 'IPv6' (exact match).", nameof(Ethertype));
        if (Protocol is not null
            && !string.Equals(Protocol, "tcp", StringComparison.Ordinal)
            && !string.Equals(Protocol, "udp", StringComparison.Ordinal)
            && !string.Equals(Protocol, "icmp", StringComparison.Ordinal))
            throw new ArgumentException("Protocol must be 'tcp', 'udp', or 'icmp' (exact match).", nameof(Protocol));
        if ((PortRangeMin is null) != (PortRangeMax is null))
            throw new ArgumentException("PortRangeMin and PortRangeMax must be set together.", nameof(PortRangeMin));
        if (PortRangeMin is { } lo && PortRangeMax is { } hi
            && (lo < 1 || hi > 65535 || lo > hi))
            throw new ArgumentException("Port range must satisfy 1 <= min <= max <= 65535.", nameof(PortRangeMin));
        if (RemoteIpPrefix is not null)
            ValidateCidr(RemoteIpPrefix);
    }

    private static void ValidateCidr(string cidr)
    {
        var slash = cidr.IndexOf('/');
        if (slash <= 0 || slash == cidr.Length - 1 || cidr.Contains(' ', StringComparison.Ordinal)
            || cidr.IndexOf('/', slash + 1) >= 0)
        {
            throw new ArgumentException($"RemoteIpPrefix '{cidr}' is not a CIDR (address/bits).", nameof(cidr));
        }
        if (!System.Net.IPAddress.TryParse(cidr[..slash], out var address))
            throw new ArgumentException($"RemoteIpPrefix '{cidr}' has an unparseable address.", nameof(cidr));
        if (!int.TryParse(cidr[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits))
            throw new ArgumentException($"RemoteIpPrefix '{cidr}' has an unparseable prefix length.", nameof(cidr));
        var maxBits = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (bits < 0 || bits > maxBits)
            throw new ArgumentException($"RemoteIpPrefix '{cidr}' prefix length is out of range.", nameof(cidr));
    }
}

/// <summary>Neutron security-group rule record.</summary>
public sealed class OpenStackSecurityGroupRule
{
    /// <summary>Rule id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Owning security group id.</summary>
    [JsonPropertyName("security_group_id")]
    public string? SecurityGroupId { get; set; }

    /// <summary>Direction.</summary>
    [JsonPropertyName("direction")]
    public string? Direction { get; set; }
}

/// <summary>Neutron floating-IP record.</summary>
public sealed class OpenStackFloatingIp
{
    /// <summary>Floating IP id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Floating address.</summary>
    [JsonPropertyName("floating_ip_address")]
    public string? Address { get; set; }

    /// <summary>Attached port id, if any.</summary>
    [JsonPropertyName("port_id")]
    public string? PortId { get; set; }

    /// <summary>Operator description (the provider stamps ownership here).</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>Neutron port record (subset).</summary>
public sealed class OpenStackPort
{
    /// <summary>Port id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Attached device id (Nova server id).</summary>
    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    /// <summary>Fixed IPs.</summary>
    [JsonPropertyName("fixed_ips")]
    public List<OpenStackFixedIp>? FixedIps { get; set; }
}

/// <summary>Neutron fixed-IP entry.</summary>
public sealed class OpenStackFixedIp
{
    /// <summary>Subnet id.</summary>
    [JsonPropertyName("subnet_id")]
    public string? SubnetId { get; set; }

    /// <summary>IP address.</summary>
    [JsonPropertyName("ip_address")]
    public string? IpAddress { get; set; }
}

/// <summary>Glance image record (subset).</summary>
public sealed class OpenStackImage
{
    /// <summary>Image id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Image name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Status (<c>active</c>, <c>queued</c>, …).</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Tags.</summary>
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    /// <summary>Visibility.</summary>
    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; }

    /// <summary>Size in bytes, when known.</summary>
    [JsonPropertyName("size")]
    public long? Size { get; set; }
}

// ------------------------------------------------------------------
// Wire shapes (private): request/response envelopes per service.
// ------------------------------------------------------------------

internal sealed record KeystoneApplicationCredential(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("secret")] string Secret);

internal sealed record KeystoneAuthIdentity(
    [property: JsonPropertyName("methods")] string[] Methods,
    [property: JsonPropertyName("application_credential")] KeystoneApplicationCredential ApplicationCredential);

internal sealed record KeystoneAuthBody(
    [property: JsonPropertyName("identity")] KeystoneAuthIdentity Identity);

internal sealed record KeystoneAuthRequest(
    [property: JsonPropertyName("auth")] KeystoneAuthBody Auth)
{
    public KeystoneAuthRequest(KeystoneAuthIdentity identity)
        : this(new KeystoneAuthBody(identity))
    {
    }
}

internal sealed class KeystoneTokenResponse
{
    [JsonPropertyName("token")]
    public KeystoneToken? Token { get; set; }
}

internal sealed class KeystoneToken
{
    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; set; }

    [JsonPropertyName("catalog")]
    public List<KeystoneService>? CatalogServices { get; set; }

    [JsonIgnore]
    internal KeystoneCatalog Catalog => new() { Services = CatalogServices ?? [] };
}

internal sealed record OpenStackServerNetwork(
    [property: JsonPropertyName("uuid")] string Uuid);

internal sealed record OpenStackServerSecurityGroup(
    [property: JsonPropertyName("name")] string Name);

internal sealed record OpenStackCreateServerBody(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("flavorRef")] string FlavorRef,
    [property: JsonPropertyName("imageRef")] string? ImageRef,
    [property: JsonPropertyName("networks")] List<OpenStackServerNetwork> Networks,
    [property: JsonPropertyName("key_name")] string? KeyName,
    [property: JsonPropertyName("user_data")] string? UserData,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("security_groups")] List<OpenStackServerSecurityGroup>? SecurityGroups = null);

internal sealed record OpenStackCreateServerWrapper(
    [property: JsonPropertyName("server")] OpenStackCreateServerBody Server);

internal sealed class OpenStackServerWrapper
{
    [JsonPropertyName("server")]
    public OpenStackServer? Server { get; set; }
}

internal sealed record OpenStackServerActionWrapper(
    [property: JsonPropertyName("createImage")] OpenStackCreateImageAction CreateImage);

internal sealed record OpenStackCreateImageAction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string> Metadata);

internal sealed record OpenStackCreateKeypairBody(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("public_key")] string? PublicKey);

internal sealed record OpenStackCreateKeypairWrapper(
    [property: JsonPropertyName("keypair")] OpenStackCreateKeypairBody Keypair);

internal sealed class OpenStackKeypairWrapper
{
    [JsonPropertyName("keypair")]
    public OpenStackKeypair? Keypair { get; set; }
}

internal sealed class OpenStackLimitsWrapper
{
    [JsonPropertyName("limits")]
    public OpenStackLimitsBody? Limits { get; set; }
}

internal sealed class OpenStackLimitsBody
{
    [JsonPropertyName("absolute")]
    public OpenStackLimits? Absolute { get; set; }
}

internal sealed record OpenStackCreateSecurityGroupBody(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description);

internal sealed record OpenStackCreateSecurityGroupWrapper(
    [property: JsonPropertyName("security_group")] OpenStackCreateSecurityGroupBody SecurityGroup);

internal sealed class OpenStackSecurityGroupWrapper
{
    [JsonPropertyName("security_group")]
    public OpenStackSecurityGroup? SecurityGroup { get; set; }
}

internal sealed record OpenStackCreateSecurityGroupRuleBody(
    [property: JsonPropertyName("security_group_id")] string SecurityGroupId,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("ethertype")] string Ethertype,
    [property: JsonPropertyName("protocol")] string? Protocol,
    [property: JsonPropertyName("port_range_min")] int? PortRangeMin,
    [property: JsonPropertyName("port_range_max")] int? PortRangeMax,
    [property: JsonPropertyName("remote_ip_prefix")] string? RemoteIpPrefix);

internal sealed record OpenStackCreateSecurityGroupRuleWrapper(
    [property: JsonPropertyName("security_group_rule")] OpenStackCreateSecurityGroupRuleBody SecurityGroupRule);

internal sealed class OpenStackSecurityGroupRuleWrapper
{
    [JsonPropertyName("security_group_rule")]
    public OpenStackSecurityGroupRule? Rule { get; set; }
}

internal sealed record OpenStackCreateFloatingIpBody(
    [property: JsonPropertyName("floating_network_id")] string FloatingNetworkId,
    [property: JsonPropertyName("description")] string? Description = null);

internal sealed record OpenStackCreateFloatingIpWrapper(
    [property: JsonPropertyName("floatingip")] OpenStackCreateFloatingIpBody Floatingip);

internal sealed record OpenStackUpdateFloatingIpBody(
    [property: JsonPropertyName("port_id")] string PortId);

internal sealed record OpenStackUpdateFloatingIpWrapper(
    [property: JsonPropertyName("floatingip")] OpenStackUpdateFloatingIpBody Floatingip);

internal sealed class OpenStackFloatingIpWrapper
{
    [JsonPropertyName("floatingip")]
    public OpenStackFloatingIp? FloatingIp { get; set; }
}
