using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.HetznerSandboxPlugin;

/// <summary>
/// Size, timeout, and safety bounds for <see cref="HetznerApiClient"/>.
/// Defaults are the single source of truth — <see cref="HetznerSandboxOptions"/>
/// mirrors them so operators tune hot-reloadable knobs, not literals.
/// </summary>
public sealed record HetznerClientLimits
{
    /// <summary>Default per-request HTTP timeout, in seconds.</summary>
    public const int DefaultHttpTimeoutSeconds = 60;

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
    public const int DefaultMaxUserDataBytes = 32 * 1024;

    /// <summary>Per-request HTTP timeout. Does not bound status polls — those carry their own timeout.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(DefaultHttpTimeoutSeconds);

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
/// Small, typed Hetzner Cloud API client (public endpoint
/// <c>https://api.hetzner.cloud/v1</c>, bearer-token auth). Covers exactly the
/// sandbox lifecycle: server types, images, locations, servers (+ power
/// actions), SSH keys, firewalls, and floating IPs. No provider logic lives
/// here.
///
/// <para>Thread-safe: no mutable auth state (the token is static), so every
/// method may run concurrently. Cancellation is propagated to every request;
/// nothing is fire-and-forget. Response bodies are size-bounded while
/// streaming, listings are page-bounded, and the API token never reaches an
/// exception message or log.</para>
///
/// <para>Vendor semantics were implemented against the public reference
/// (<c>https://docs.hetzner.cloud/reference/cloud</c>); where the reference
/// could not be extracted during implementation, the client assumes nothing
/// beyond the stable core (bearer auth, <c>{"error":{"code","message"}}</c>
/// failures, <c>meta.pagination</c> paging) and re-verifies every server-side
/// filter client-side.</para>
/// </summary>
public sealed class HetznerApiClient
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Error bodies are untrusted remote text: bounded before they are folded
    // into exception messages and logs.
    internal const int MaxErrorBodyChars = 2048;

    // Protocol sanity bounds (not operator knobs): a single create call must
    // stay a single call — these reject caller bugs, not cloud limits.
    internal const int MaxLabelEntries = 64;
    internal const int MaxLabelChars = 63;
    internal const int MaxFirewallRulesPerCreate = 1024;
    internal const int MaxSshKeysPerCreate = 16;

    /// <summary>
    /// Fallback ceiling for status waits when the caller passes no timeout.
    /// Mirrors <c>HetznerSandboxOptions.ReadyTimeoutSeconds</c> default 600:
    /// change both together.
    /// </summary>
    internal static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(600);
    private const int ListPageSize = 50;

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly HetznerClientLimits _limits;

    /// <summary>Creates a client over an injected <see cref="HttpClient"/> (test seam for fakes).</summary>
    public HetznerApiClient(HttpClient http, TimeProvider? clock = null, HetznerClientLimits? limits = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clock = clock ?? TimeProvider.System;
        _limits = limits ?? new HetznerClientLimits();
        if (_limits.MaxResponseBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "MaxResponseBytes must be positive.");
        if (_limits.MaxListItems <= 0 || _limits.MaxListPages <= 0 || _limits.MaxUserDataBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "List and user-data bounds must be positive.");
        if (_limits.HttpTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits), "HttpTimeout must be positive.");
        if (_limits.PollInterval <= TimeSpan.Zero || _limits.MaxPollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits), "Poll intervals must be positive.");
    }

    internal static bool IsCleartextHttpPermitted(Uri uri, bool allowUnsafeHttp)
    {
        if (!allowUnsafeHttp)
            return false;
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.Ordinal)
            || uri.Host.Equals("::1", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Server types / images / locations (placement pins)
    // ------------------------------------------------------------------

    /// <summary>
    /// Looks up a server type by exact (ordinal) name. Returns null when no
    /// type bears the name; throws when several do — an ambiguous match must
    /// never silently pick one. Deprecated types are refused loudly: the
    /// placement pins a supported type, not a rotting one.
    /// </summary>
    public async Task<HetznerServerType?> GetServerTypeByNameAsync(
        HetznerCredentials credentials, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Server type name must not be blank.", nameof(name));
        var elements = await GetPagedAsync(
            credentials, $"server_types?name={Uri.EscapeDataString(name.Trim())}",
            "get server type",
            el => el.TryGetProperty("server_types", out var items) ? items : null,
            ct).ConfigureAwait(false);
        var matches = elements
            .Select(el => el.Deserialize<HetznerServerType>(Json))
            .Where(t => t is not null && string.Equals(t.Name, name.Trim(), StringComparison.Ordinal))
            .Cast<HetznerServerType>()
            .ToList();
        if (matches.Count == 0)
            return null;
        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Hetzner server type name '{name.Trim()}' is ambiguous ({matches.Count} matches); use an exact name");
        }
        if (matches[0].Deprecated)
        {
            throw new InvalidOperationException(
                $"Hetzner server type '{name.Trim()}' is deprecated and refused as a placement pin");
        }
        return matches[0];
    }

    /// <summary>
    /// Resolves an image reference: a numeric id is fetched directly, any
    /// other value resolves by exact (ordinal) name among available,
    /// non-deprecated system images. Returns null when nothing matches;
    /// throws on ambiguity or on a deprecated match.
    /// </summary>
    public async Task<HetznerImage?> GetImageByReferenceAsync(
        HetznerCredentials credentials, string reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new ArgumentException("Image reference must not be blank.", nameof(reference));
        var trimmed = reference.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            var byId = await GetImageAsync(credentials, id, ct).ConfigureAwait(false);
            if (byId is null)
                return null;
            ThrowIfImageUnusable(byId);
            return byId;
        }
        var elements = await GetPagedAsync(
            credentials, $"images?name={Uri.EscapeDataString(trimmed)}&type=system&status=available",
            "get image",
            el => el.TryGetProperty("images", out var items) ? items : null,
            ct).ConfigureAwait(false);
        var matches = elements
            .Select(el => el.Deserialize<HetznerImage>(Json))
            .Where(image => image is not null
                && string.Equals(image.Name, trimmed, StringComparison.Ordinal)
                && string.Equals(image.Status, "available", StringComparison.OrdinalIgnoreCase))
            .Cast<HetznerImage>()
            .ToList();
        if (matches.Count == 0)
            return null;
        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Hetzner image name '{trimmed}' is ambiguous ({matches.Count} matches); use an image id");
        }
        ThrowIfImageUnusable(matches[0]);
        return matches[0];
    }

    /// <summary>Gets one image by id. Returns null when it does not exist (404).</summary>
    public async Task<HetznerImage?> GetImageAsync(
        HetznerCredentials credentials, long imageId, CancellationToken ct)
    {
        RequirePositiveId(imageId, nameof(imageId));
        using var response = await SendAsync(
            credentials, $"images/{imageId.ToString(CultureInfo.InvariantCulture)}",
            HttpMethod.Get, null, "get image", ct, allowNotFound: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        var dto = await ReadJsonAsync<HetznerImageWrapper>(response, "get image", ct).ConfigureAwait(false);
        return dto?.Image
            ?? throw new HetznerApiException(HetznerFailureKind.Unexpected, "get image", "empty response body");
    }

    private static void ThrowIfImageUnusable(HetznerImage image)
    {
        if (image.Deprecated)
        {
            var name = image.Name ?? image.Id.ToString(CultureInfo.InvariantCulture);
            throw new InvalidOperationException(
                $"Hetzner image '{name}' is deprecated and refused as an approved-image pin");
        }
    }

    /// <summary>
    /// Looks up a location by exact (ordinal) name. Returns null when no
    /// location bears the name; throws when several do.
    /// </summary>
    public async Task<HetznerLocation?> GetLocationByNameAsync(
        HetznerCredentials credentials, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Location name must not be blank.", nameof(name));
        var elements = await GetPagedAsync(
            credentials, $"locations?name={Uri.EscapeDataString(name.Trim())}",
            "get location",
            el => el.TryGetProperty("locations", out var items) ? items : null,
            ct).ConfigureAwait(false);
        var matches = elements
            .Select(el => el.Deserialize<HetznerLocation>(Json))
            .Where(l => l is not null && string.Equals(l.Name, name.Trim(), StringComparison.Ordinal))
            .Cast<HetznerLocation>()
            .ToList();
        if (matches.Count == 0)
            return null;
        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Hetzner location name '{name.Trim()}' is ambiguous ({matches.Count} matches)");
        }
        return matches[0];
    }

    // ------------------------------------------------------------------
    // Servers
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a server. There is no vendor idempotency key on this call, so
    /// callers must reconcile ambiguous failures (see
    /// <see cref="HetznerApiException.MayHaveCreated"/>) by stable request
    /// label before resubmitting — never blindly retry.
    /// </summary>
    public async Task<HetznerServer> CreateServerAsync(
        HetznerCredentials credentials, HetznerServerSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        spec.Validate(_limits.MaxUserDataBytes);
        var body = new HetznerCreateServerBody(
            spec.Name, spec.ServerType, spec.ImageId, spec.Location,
            spec.SshKeyNames, spec.NetworkIds, spec.UserData, spec.Labels,
            spec.FirewallIds.Select(id => new HetznerServerFirewallRef(id)).ToList(),
            new HetznerPublicNetSpec(spec.EnablePublicIpv4, spec.EnablePublicIpv6));
        using var response = await SendAsync(
            credentials, "servers", HttpMethod.Post, body, "create server", ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<HetznerCreateServerResponse>(response, "create server", ct).ConfigureAwait(false);
        var server = dto?.Server
            ?? throw new HetznerApiException(HetznerFailureKind.Unexpected, "create server", "empty response body");
        if (server.Id <= 0)
        {
            throw new HetznerApiException(
                HetznerFailureKind.Unexpected, "create server", "response carried no server id");
        }
        return server;
    }

    /// <summary>Gets a server. Returns null when it does not exist (404).</summary>
    public async Task<HetznerServer?> GetServerAsync(
        HetznerCredentials credentials, long serverId, CancellationToken ct)
    {
        RequirePositiveId(serverId, nameof(serverId));
        using var response = await SendAsync(
            credentials, $"servers/{serverId.ToString(CultureInfo.InvariantCulture)}",
            HttpMethod.Get, null, "get server", ct, allowNotFound: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        var dto = await ReadJsonAsync<HetznerServerWrapper>(response, "get server", ct).ConfigureAwait(false);
        return dto?.Server
            ?? throw new HetznerApiException(HetznerFailureKind.Unexpected, "get server", "empty response body");
    }

    /// <summary>
    /// Lists servers filtered server-side by label selector when given and
    /// re-verified client-side against <paramref name="requireLabels"/> — a
    /// hosted filter is a hint, never a proof of ownership. Bounded by the
    /// list limits.
    /// </summary>
    public async Task<IReadOnlyList<HetznerServer>> ListServersAsync(
        HetznerCredentials credentials,
        IReadOnlyDictionary<string, string>? requireLabels,
        CancellationToken ct)
    {
        var query = new StringBuilder("servers?");
        if (requireLabels is { Count: > 0 })
        {
            foreach (var kvp in requireLabels)
                RequireLabel(kvp.Key, kvp.Value);
            query.Append("label_selector=");
            query.Append(Uri.EscapeDataString(string.Join(",",
                requireLabels.Select(kvp => kvp.Key + "==" + kvp.Value))));
            query.Append('&');
        }
        var elements = await GetPagedAsync(
            credentials, query.ToString(), "list servers",
            el => el.TryGetProperty("servers", out var items) ? items : null,
            ct).ConfigureAwait(false);
        var result = new List<HetznerServer>();
        foreach (var el in elements)
        {
            var server = el.Deserialize<HetznerServer>(Json);
            if (server is null)
                continue;
            if (requireLabels is { Count: > 0 } && (server.Labels is null
                || !requireLabels.All(kvp => server.Labels.TryGetValue(kvp.Key, out var v)
                    && string.Equals(v, kvp.Value, StringComparison.Ordinal))))
                continue;
            result.Add(server);
        }
        return result;
    }

    /// <summary>Deletes a server. Returns false when it is already gone (404).</summary>
    public async Task<bool> DeleteServerAsync(
        HetznerCredentials credentials, long serverId, CancellationToken ct)
    {
        RequirePositiveId(serverId, nameof(serverId));
        using var response = await SendAsync(
            credentials, $"servers/{serverId.ToString(CultureInfo.InvariantCulture)}",
            HttpMethod.Delete, null, "delete server", ct, allowNotFound: true).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    /// <summary>
    /// Issues the graceful <c>shutdown</c> server action (ACPI power button).
    /// Best-effort termination fallback before delete: this call only starts
    /// the shutdown — the caller still deletes and confirms deletion.
    /// A 404 (already gone) is success; anything else throws.
    /// </summary>
    public async Task ShutdownServerAsync(
        HetznerCredentials credentials, long serverId, CancellationToken ct)
    {
        RequirePositiveId(serverId, nameof(serverId));
        using var response = await SendAsync(
            credentials, $"servers/{serverId.ToString(CultureInfo.InvariantCulture)}/actions/shutdown",
            HttpMethod.Post, new HetznerEmptyActionBody(), "shutdown server", ct,
            allowNotFound: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        var raw = await ReadBoundedStringAsync(response.Content, _limits.MaxResponseBytes, "shutdown server", ct)
            .ConfigureAwait(false);
        EnsureSuccessFromBody(response, raw, "shutdown server");
    }

    /// <summary>
    /// Issues the hard <c>poweroff</c> server action (power cut). Same
    /// best-effort contract as <see cref="ShutdownServerAsync"/>.
    /// </summary>
    public async Task PoweroffServerAsync(
        HetznerCredentials credentials, long serverId, CancellationToken ct)
    {
        RequirePositiveId(serverId, nameof(serverId));
        using var response = await SendAsync(
            credentials, $"servers/{serverId.ToString(CultureInfo.InvariantCulture)}/actions/poweroff",
            HttpMethod.Post, new HetznerEmptyActionBody(), "poweroff server", ct,
            allowNotFound: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        var raw = await ReadBoundedStringAsync(response.Content, _limits.MaxResponseBytes, "poweroff server", ct)
            .ConfigureAwait(false);
        EnsureSuccessFromBody(response, raw, "poweroff server");
    }

    /// <summary>
    /// Polls a server until it reaches one of <paramref name="desiredStatuses"/>
    /// (exact, case-insensitive). A status in <paramref name="faultStatuses"/>
    /// fails fast; leaving the timeout throws a typed timeout. Backoff is
    /// bounded and cancellation aborts the wait.
    /// </summary>
    public async Task<HetznerServer> WaitForServerStatusAsync(
        HetznerCredentials credentials, long serverId,
        IReadOnlyCollection<string> desiredStatuses, CancellationToken ct,
        TimeSpan? timeout = null, IReadOnlyCollection<string>? faultStatuses = null)
    {
        RequirePositiveId(serverId, nameof(serverId));
        if (desiredStatuses is null || desiredStatuses.Count == 0)
            throw new ArgumentException("At least one desired status is required.", nameof(desiredStatuses));
        faultStatuses ??= ["off", "unknown", "deleting"];
        var deadline = _clock.GetUtcNow() + (timeout ?? DefaultWaitTimeout);
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var server = await GetServerAsync(credentials, serverId, ct).ConfigureAwait(false)
                ?? throw new HetznerApiException(
                    HetznerFailureKind.NotFound, "wait for server status",
                    $"server '{serverId.ToString(CultureInfo.InvariantCulture)}' disappeared while waiting");
            if (server.Status is not null
                && desiredStatuses.Any(s => string.Equals(s, server.Status, StringComparison.OrdinalIgnoreCase)))
            {
                return server;
            }
            if (server.Status is not null
                && faultStatuses.Any(s => string.Equals(s, server.Status, StringComparison.OrdinalIgnoreCase)))
            {
                throw new HetznerApiException(
                    HetznerFailureKind.Unexpected, "wait for server status",
                    $"server '{serverId.ToString(CultureInfo.InvariantCulture)}' entered fault status " +
                    $"'{SanitizeForLog(server.Status)}'");
            }
            var now = _clock.GetUtcNow();
            if (now >= deadline)
            {
                throw new HetznerApiException(
                    HetznerFailureKind.Unexpected, "wait for server status",
                    $"server '{serverId.ToString(CultureInfo.InvariantCulture)}' did not reach " +
                    $"'{SanitizeForLog(string.Join(",", desiredStatuses))}' in time " +
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
        HetznerCredentials credentials, long serverId, CancellationToken ct, TimeSpan? timeout = null)
    {
        RequirePositiveId(serverId, nameof(serverId));
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
                throw new HetznerApiException(
                    HetznerFailureKind.Unexpected, "wait for server deletion",
                    $"server '{serverId.ToString(CultureInfo.InvariantCulture)}' was not deleted in time");
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
    // SSH keys
    // ------------------------------------------------------------------

    /// <summary>Registers an SSH public key. Labels carry the ownership proof.</summary>
    public async Task<HetznerSshKey> CreateSshKeyAsync(
        HetznerCredentials credentials, string name, string publicKey,
        IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 255)
            throw new ArgumentException("SSH key name must be 1-255 characters.", nameof(name));
        if (string.IsNullOrWhiteSpace(publicKey))
            throw new ArgumentException("SSH public key must not be blank.", nameof(publicKey));
        ValidateLabels(labels);
        var body = new HetznerCreateSshKeyBody(name.Trim(), publicKey.Trim(), new Dictionary<string, string>(labels));
        using var response = await SendAsync(
            credentials, "ssh_keys", HttpMethod.Post, body, "create SSH key", ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<HetznerSshKeyWrapper>(response, "create SSH key", ct).ConfigureAwait(false);
        return dto?.SshKey
            ?? throw new HetznerApiException(HetznerFailureKind.Unexpected, "create SSH key", "empty response body");
    }

    /// <summary>Deletes an SSH key by id. Returns false when it is already gone (404).</summary>
    public async Task<bool> DeleteSshKeyAsync(
        HetznerCredentials credentials, long sshKeyId, CancellationToken ct)
    {
        RequirePositiveId(sshKeyId, nameof(sshKeyId));
        using var response = await SendAsync(
            credentials, $"ssh_keys/{sshKeyId.ToString(CultureInfo.InvariantCulture)}",
            HttpMethod.Delete, null, "delete SSH key", ct, allowNotFound: true).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    /// <summary>
    /// Lists SSH keys filtered server-side by label selector and re-verified
    /// client-side. Bounded by the list limits.
    /// </summary>
    public async Task<IReadOnlyList<HetznerSshKey>> ListSshKeysAsync(
        HetznerCredentials credentials,
        IReadOnlyDictionary<string, string>? requireLabels,
        CancellationToken ct)
    {
        var elements = await GetLabeledPagedAsync(
            credentials, "ssh_keys", "ssh_keys", requireLabels, "list SSH keys", ct).ConfigureAwait(false);
        var result = new List<HetznerSshKey>();
        foreach (var el in elements)
        {
            var key = el.Deserialize<HetznerSshKey>(Json);
            if (key is null)
                continue;
            if (requireLabels is { Count: > 0 } && (key.Labels is null
                || !requireLabels.All(kvp => key.Labels.TryGetValue(kvp.Key, out var v)
                    && string.Equals(v, kvp.Value, StringComparison.Ordinal))))
                continue;
            result.Add(key);
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Firewalls
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a firewall with the given rules and no attached resources —
    /// the server attaches it at create time, so protection starts at boot.
    /// </summary>
    public async Task<HetznerFirewall> CreateFirewallAsync(
        HetznerCredentials credentials, string name,
        IReadOnlyList<HetznerFirewallRule> rules,
        IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 255)
            throw new ArgumentException("Firewall name must be 1-255 characters.", nameof(name));
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0 || rules.Count > MaxFirewallRulesPerCreate)
            throw new ArgumentException($"Firewall must carry 1-{MaxFirewallRulesPerCreate} rules.", nameof(rules));
        foreach (var rule in rules)
            rule.Validate();
        ValidateLabels(labels);
        var body = new HetznerCreateFirewallBody(
            name.Trim(), rules.ToList(), new Dictionary<string, string>(labels));
        using var response = await SendAsync(
            credentials, "firewalls", HttpMethod.Post, body, "create firewall", ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<HetznerFirewallWrapper>(response, "create firewall", ct).ConfigureAwait(false);
        return dto?.Firewall
            ?? throw new HetznerApiException(HetznerFailureKind.Unexpected, "create firewall", "empty response body");
    }

    /// <summary>Deletes a firewall by id. Returns false when it is already gone (404).</summary>
    public async Task<bool> DeleteFirewallAsync(
        HetznerCredentials credentials, long firewallId, CancellationToken ct)
    {
        RequirePositiveId(firewallId, nameof(firewallId));
        using var response = await SendAsync(
            credentials, $"firewalls/{firewallId.ToString(CultureInfo.InvariantCulture)}",
            HttpMethod.Delete, null, "delete firewall", ct, allowNotFound: true).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    /// <summary>
    /// Lists firewalls filtered server-side by label selector and re-verified
    /// client-side. Bounded by the list limits.
    /// </summary>
    public async Task<IReadOnlyList<HetznerFirewall>> ListFirewallsAsync(
        HetznerCredentials credentials,
        IReadOnlyDictionary<string, string>? requireLabels,
        CancellationToken ct)
    {
        var elements = await GetLabeledPagedAsync(
            credentials, "firewalls", "firewalls", requireLabels, "list firewalls", ct).ConfigureAwait(false);
        var result = new List<HetznerFirewall>();
        foreach (var el in elements)
        {
            var firewall = el.Deserialize<HetznerFirewall>(Json);
            if (firewall is null)
                continue;
            if (requireLabels is { Count: > 0 } && (firewall.Labels is null
                || !requireLabels.All(kvp => firewall.Labels.TryGetValue(kvp.Key, out var v)
                    && string.Equals(v, kvp.Value, StringComparison.Ordinal))))
                continue;
            result.Add(firewall);
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Floating IPs
    // ------------------------------------------------------------------

    /// <summary>
    /// Allocates a floating IP already assigned to <paramref name="serverId"/>.
    /// </summary>
    public async Task<HetznerFloatingIp> CreateFloatingIpAsync(
        HetznerCredentials credentials, string name, string homeLocation, long serverId,
        IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 255)
            throw new ArgumentException("Floating IP name must be 1-255 characters.", nameof(name));
        if (string.IsNullOrWhiteSpace(homeLocation))
            throw new ArgumentException("Floating IP home location must not be blank.", nameof(homeLocation));
        RequirePositiveId(serverId, nameof(serverId));
        ValidateLabels(labels);
        var body = new HetznerCreateFloatingIpBody(
            "ipv4", homeLocation.Trim(), serverId, name.Trim(), new Dictionary<string, string>(labels));
        using var response = await SendAsync(
            credentials, "floating_ips", HttpMethod.Post, body, "create floating IP", ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<HetznerFloatingIpWrapper>(response, "create floating IP", ct).ConfigureAwait(false);
        return dto?.FloatingIp
            ?? throw new HetznerApiException(HetznerFailureKind.Unexpected, "create floating IP", "empty response body");
    }

    /// <summary>Unassigns a floating IP from its server (best-effort; 404 means already gone).</summary>
    public async Task UnassignFloatingIpAsync(
        HetznerCredentials credentials, long floatingIpId, CancellationToken ct)
    {
        RequirePositiveId(floatingIpId, nameof(floatingIpId));
        using var response = await SendAsync(
            credentials, $"floating_ips/{floatingIpId.ToString(CultureInfo.InvariantCulture)}/actions/unassign",
            HttpMethod.Post, new HetznerEmptyActionBody(), "unassign floating IP", ct,
            allowNotFound: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        var raw = await ReadBoundedStringAsync(response.Content, _limits.MaxResponseBytes, "unassign floating IP", ct)
            .ConfigureAwait(false);
        EnsureSuccessFromBody(response, raw, "unassign floating IP");
    }

    /// <summary>Deletes a floating IP by id. Returns false when it is already gone (404).</summary>
    public async Task<bool> DeleteFloatingIpAsync(
        HetznerCredentials credentials, long floatingIpId, CancellationToken ct)
    {
        RequirePositiveId(floatingIpId, nameof(floatingIpId));
        using var response = await SendAsync(
            credentials, $"floating_ips/{floatingIpId.ToString(CultureInfo.InvariantCulture)}",
            HttpMethod.Delete, null, "delete floating IP", ct, allowNotFound: true).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    /// <summary>
    /// Lists floating IPs filtered server-side by label selector and
    /// re-verified client-side. Bounded by the list limits.
    /// </summary>
    public async Task<IReadOnlyList<HetznerFloatingIp>> ListFloatingIpsAsync(
        HetznerCredentials credentials,
        IReadOnlyDictionary<string, string>? requireLabels,
        CancellationToken ct)
    {
        var elements = await GetLabeledPagedAsync(
            credentials, "floating_ips", "floating_ips", requireLabels, "list floating IPs", ct).ConfigureAwait(false);
        var result = new List<HetznerFloatingIp>();
        foreach (var el in elements)
        {
            var floatingIp = el.Deserialize<HetznerFloatingIp>(Json);
            if (floatingIp is null)
                continue;
            if (requireLabels is { Count: > 0 } && (floatingIp.Labels is null
                || !requireLabels.All(kvp => floatingIp.Labels.TryGetValue(kvp.Key, out var v)
                    && string.Equals(v, kvp.Value, StringComparison.Ordinal))))
                continue;
            result.Add(floatingIp);
        }
        return result;
    }

    private async Task<List<JsonElement>> GetLabeledPagedAsync(
        HetznerCredentials credentials, string resource, string itemsProperty,
        IReadOnlyDictionary<string, string>? requireLabels, string operation, CancellationToken ct)
    {
        var query = new StringBuilder(resource).Append('?');
        if (requireLabels is { Count: > 0 })
        {
            foreach (var kvp in requireLabels)
                RequireLabel(kvp.Key, kvp.Value);
            query.Append("label_selector=");
            query.Append(Uri.EscapeDataString(string.Join(",",
                requireLabels.Select(kvp => kvp.Key + "==" + kvp.Value))));
            query.Append('&');
        }
        return await GetPagedAsync(
            credentials, query.ToString(), operation,
            el => el.TryGetProperty(itemsProperty, out var items) ? items : null,
            ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Transport core
    // ------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendAsync(
        HetznerCredentials credentials, string path, HttpMethod method,
        object? body, string operation, CancellationToken ct, bool allowNotFound = false)
    {
        var uri = JoinUrl(credentials.ApiBaseUrl.AbsoluteUri, path);
        if (uri.Scheme == Uri.UriSchemeHttp
            && !IsCleartextHttpPermitted(uri, credentials.AllowUnsafeHttp))
        {
            throw new HetznerApiException(
                HetznerFailureKind.Unexpected, operation,
                "refusing plain-http API URL for a non-loopback host");
        }
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credentials.ApiToken);
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, body.GetType(), Json), Encoding.UTF8, "application/json");
        }
        HttpResponseMessage response;
        try
        {
            response = await SendUnconditionallyAsync(request, operation, ct).ConfigureAwait(false);
        }
        catch (HetznerApiException)
        {
            throw;
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
            throw new HetznerApiException(
                HetznerFailureKind.Unreachable, operation, "transport error", null, null, null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HetznerApiException(
                HetznerFailureKind.Unreachable, operation,
                $"request exceeded the {_limits.HttpTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s timeout",
                null, null, null, ex);
        }
    }

    private async Task<List<JsonElement>> GetPagedAsync(
        HetznerCredentials credentials, string firstQuery, string operation,
        Func<JsonElement, JsonElement?> pickItems, CancellationToken ct)
    {
        var collected = new List<JsonElement>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var page = 1;
        for (var pages = 0; pages < _limits.MaxListPages; pages++)
        {
            var separator = firstQuery.Contains('?') ? "&" : "?";
            var path = firstQuery + separator
                + "page=" + page.ToString(CultureInfo.InvariantCulture)
                + "&per_page=" + ListPageSize.ToString(CultureInfo.InvariantCulture);
            using var response = await SendAsync(
                    credentials, path, HttpMethod.Get, null, operation, ct).ConfigureAwait(false);
            var raw = await ReadBoundedStringAsync(response.Content, _limits.MaxResponseBytes, operation, ct)
                .ConfigureAwait(false);
            EnsureSuccessFromBody(response, raw, operation);
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(raw);
            }
            catch (JsonException ex)
            {
                throw new HetznerApiException(
                    HetznerFailureKind.Unexpected, operation, "unparseable response body",
                    response.StatusCode, null, null, ex);
            }
            using (doc)
            {
                var items = pickItems(doc.RootElement);
                if (items is not { ValueKind: JsonValueKind.Array })
                    return collected;
                foreach (var el in items.Value.EnumerateArray())
                {
                    var id = el.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
                    if (id is null || !seenIds.Add(id))
                        continue;
                    collected.Add(el.Clone());
                    if (collected.Count > _limits.MaxListItems)
                    {
                        throw new HetznerApiException(
                            HetznerFailureKind.Unexpected, operation,
                            $"listing exceeded the {_limits.MaxListItems.ToString(CultureInfo.InvariantCulture)}-item bound");
                    }
                }
                if (!TryNextPage(doc.RootElement, page, out var next))
                    return collected;
                page = next;
            }
        }
        throw new HetznerApiException(
            HetznerFailureKind.Unexpected, operation,
            $"listing exceeded the {_limits.MaxListPages.ToString(CultureInfo.InvariantCulture)}-page bound; " +
            "refusing to report a truncated inventory");
    }

    private static bool TryNextPage(JsonElement root, int currentPage, out int next)
    {
        next = currentPage + 1;
        if (!root.TryGetProperty("meta", out var meta)
            || !meta.TryGetProperty("pagination", out var pagination))
        {
            // No pagination envelope: a single page. Anything without the
            // envelope is treated as complete — the caller's item bound still
            // applies, so a hostile endless stream cannot OOM the host.
            return false;
        }
        if (pagination.TryGetProperty("next_page", out var nextPage))
        {
            if (nextPage.ValueKind == JsonValueKind.Null)
                return false;
            if (nextPage.ValueKind == JsonValueKind.Number && nextPage.TryGetInt32(out var n) && n > currentPage)
            {
                next = n;
                return true;
            }
            return false;
        }
        return false;
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
            throw new HetznerApiException(
                HetznerFailureKind.Unexpected, operation, "unparseable response body",
                response.StatusCode, null, null, ex);
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
                throw new HetznerApiException(
                    HetznerFailureKind.Unexpected, operation,
                    $"response exceeded the {maxBytes.ToString(CultureInfo.InvariantCulture)}-byte bound");
            }
            return Encoding.UTF8.GetString(bytes);
        }
        catch (HetznerApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        {
            if (ex is TaskCanceledException && ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw new HetznerApiException(
                HetznerFailureKind.Unreachable, operation, "transport error while reading response",
                null, null, null, ex);
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

    private static async Task<string?> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Error detail only: a small fixed cap, streamed, so an error page
        // never becomes an allocation bomb.
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

    private void EnsureSuccessFromBody(HttpResponseMessage response, string rawBody, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;
        var bounded = rawBody.Length > MaxErrorBodyChars ? rawBody[..MaxErrorBodyChars] : rawBody;
        throw BuildServiceError(response, operation, SanitizeForLog(bounded));
    }

    private static HetznerApiException BuildServiceError(
        HttpResponseMessage response, string operation, string? body)
    {
        var (errorCode, message) = ExtractError(body);
        var kind = HetznerApiException.FromStatus(response.StatusCode, errorCode, body);
        // ExtractError JSON-decodes the body, materializing \uXXXX escapes
        // back into live control characters that the pre-decode SanitizeForLog
        // on the raw body cannot see. Sanitize the decoded values after
        // decoding so log-bound exception text never carries forging/escape
        // characters.
        return new HetznerApiException(
            kind, operation, message is null ? body ?? "no response body" : SanitizeForLog(message),
            response.StatusCode,
            errorCode is null ? null : SanitizeForLog(errorCode),
            GetRetryAfter(response));
    }

    /// <summary>
    /// Pulls (code, message) out of the vendor error shape
    /// <c>{"error": {"code": …, "message": …}}</c>. Unknown shapes fall back
    /// to the raw body.
    /// </summary>
    internal static (string? ErrorCode, string? Message) ExtractError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                string? code = null;
                string? message = null;
                if (error.TryGetProperty("code", out var codeEl))
                    code = codeEl.ValueKind == JsonValueKind.String ? codeEl.GetString() : codeEl.ToString();
                if (error.TryGetProperty("message", out var msgEl)
                    && msgEl.ValueKind == JsonValueKind.String)
                {
                    message = msgEl.GetString();
                }
                if (!string.IsNullOrWhiteSpace(message) || !string.IsNullOrWhiteSpace(code))
                    return (code, message);
            }
            return (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    internal static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
            return delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return null;
    }

    private static void RequirePositiveId(long value, string paramName)
    {
        if (value <= 0)
            throw new ArgumentException("Resource id must be positive.", paramName);
    }

    private static void RequireLabel(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxLabelChars || key.Any(char.IsControl))
            throw new ArgumentException($"Label key must be 1-{MaxLabelChars} printable characters.", nameof(key));
        if (value is null || value.Length > MaxLabelChars || value.Any(char.IsControl))
            throw new ArgumentException($"Label value must be at most {MaxLabelChars} printable characters.", nameof(value));
    }

    internal static void ValidateLabels(IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Count > MaxLabelEntries)
            throw new ArgumentException($"Too many labels (at most {MaxLabelEntries}).", nameof(labels));
        foreach (var kvp in labels)
            RequireLabel(kvp.Key, kvp.Value);
    }

    private static Uri JoinUrl(string baseUrl, string path) =>
        new(baseUrl.TrimEnd('/') + "/" + path.TrimStart('/'), UriKind.Absolute);

    /// <summary>
    /// Folds ASCII control characters to spaces so untrusted service text
    /// cannot forge log lines or inject terminal escapes via exceptions.
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

/// <summary>Server type (placement pin): name plus capacity for documentation.</summary>
public sealed class HetznerServerType
{
    /// <summary>Numeric type id.</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>Type name (e.g. <c>cx23</c>).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>vCPU count.</summary>
    [JsonPropertyName("cores")]
    public int Cores { get; set; }

    /// <summary>Memory in GiB.</summary>
    [JsonPropertyName("memory")]
    public double Memory { get; set; }

    /// <summary>Disk in GiB.</summary>
    [JsonPropertyName("disk")]
    public int Disk { get; set; }

    /// <summary>True when the vendor deprecated the type (refused as a pin).</summary>
    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; set; }
}

/// <summary>Image (approved-image pin).</summary>
public sealed class HetznerImage
{
    /// <summary>Numeric image id.</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>Image name (e.g. <c>ubuntu-24.04</c>).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Image kind: <c>system</c>, <c>snapshot</c>, <c>backup</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Lifecycle status (<c>available</c> required).</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>CPU architecture (e.g. <c>x86</c>, <c>arm</c>).</summary>
    [JsonPropertyName("architecture")]
    public string? Architecture { get; set; }

    /// <summary>True when the vendor deprecated the image (refused as a pin).</summary>
    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; set; }
}

/// <summary>Image wrapper for single-get responses.</summary>
public sealed class HetznerImageWrapper
{
    /// <summary>Wrapped image.</summary>
    [JsonPropertyName("image")]
    public HetznerImage? Image { get; set; }
}

/// <summary>Location (placement pin).</summary>
public sealed class HetznerLocation
{
    /// <summary>Numeric location id.</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>Location name (e.g. <c>fsn1</c>).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>Server public-network addresses.</summary>
public sealed class HetznerServerPublicNet
{
    /// <summary>IPv4 slot.</summary>
    [JsonPropertyName("ipv4")]
    public HetznerServerIpv4? Ipv4 { get; set; }

    /// <summary>IPv6 slot.</summary>
    [JsonPropertyName("ipv6")]
    public HetznerServerIpv6? Ipv6 { get; set; }
}

/// <summary>Server public IPv4 address.</summary>
public sealed class HetznerServerIpv4
{
    /// <summary>Dotted address.</summary>
    [JsonPropertyName("ip")]
    public string? Ip { get; set; }
}

/// <summary>Server public IPv6 network.</summary>
public sealed class HetznerServerIpv6
{
    /// <summary>Network address (first address usable for SSH with suffix <c>::1</c>).</summary>
    [JsonPropertyName("ip")]
    public string? Ip { get; set; }
}

/// <summary>Server resource.</summary>
public sealed class HetznerServer
{
    /// <summary>Numeric server id.</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>Server name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Lifecycle status: <c>running</c>, <c>initializing</c>,
    /// <c>starting</c>, <c>stopping</c>, <c>off</c>, <c>deleting</c>,
    /// <c>migrating</c>, <c>rebuilding</c>, <c>unknown</c>.
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Public network addresses.</summary>
    [JsonPropertyName("public_net")]
    public HetznerServerPublicNet? PublicNet { get; set; }

    /// <summary>Ownership labels.</summary>
    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; set; }
}

/// <summary>Server wrapper for single-get responses.</summary>
public sealed class HetznerServerWrapper
{
    /// <summary>Wrapped server.</summary>
    [JsonPropertyName("server")]
    public HetznerServer? Server { get; set; }
}

/// <summary>Server-create response (server plus the queued action).</summary>
public sealed class HetznerCreateServerResponse
{
    /// <summary>Created server.</summary>
    [JsonPropertyName("server")]
    public HetznerServer? Server { get; set; }
}

/// <summary>Validated inputs for one server create.</summary>
public sealed record HetznerServerSpec(
    string Name,
    string ServerType,
    long ImageId,
    string Location,
    IReadOnlyList<string> SshKeyNames,
    IReadOnlyList<long> NetworkIds,
    string UserData,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<long> FirewallIds,
    bool EnablePublicIpv4,
    bool EnablePublicIpv6)
{
    /// <summary>Validates shapes the API would reject, before any request.</summary>
    /// <exception cref="ArgumentException">A field is missing or over bounds.</exception>
    public void Validate(int maxUserDataBytes)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 255)
            throw new ArgumentException("Server name must be 1-255 characters.", nameof(Name));
        if (string.IsNullOrWhiteSpace(ServerType))
            throw new ArgumentException("Server type must not be blank.", nameof(ServerType));
        if (ImageId <= 0)
            throw new ArgumentException("Image id must be positive.", nameof(ImageId));
        if (string.IsNullOrWhiteSpace(Location))
            throw new ArgumentException("Location must not be blank.", nameof(Location));
        if (SshKeyNames is null || SshKeyNames.Count == 0 || SshKeyNames.Count > HetznerApiClient.MaxSshKeysPerCreate)
            throw new ArgumentException(
                $"At least one and at most {HetznerApiClient.MaxSshKeysPerCreate} SSH keys are required.",
                nameof(SshKeyNames));
        ArgumentNullException.ThrowIfNull(NetworkIds);
        ArgumentNullException.ThrowIfNull(UserData);
        ArgumentNullException.ThrowIfNull(Labels);
        ArgumentNullException.ThrowIfNull(FirewallIds);
        HetznerApiClient.ValidateLabels(Labels);
        var userDataBytes = Encoding.UTF8.GetByteCount(UserData);
        if (userDataBytes == 0 || userDataBytes > maxUserDataBytes)
        {
            throw new ArgumentException(
                $"user_data must be 1-{maxUserDataBytes} bytes (was {userDataBytes}).",
                nameof(UserData));
        }
    }
}

internal sealed record HetznerServerFirewallRef(
    [property: JsonPropertyName("firewall")] long Firewall);

internal sealed record HetznerPublicNetSpec(
    [property: JsonPropertyName("enable_ipv4")] bool EnableIpv4,
    [property: JsonPropertyName("enable_ipv6")] bool EnableIpv6);

internal sealed record HetznerCreateServerBody(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("server_type")] string ServerType,
    [property: JsonPropertyName("image")] long Image,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("ssh_keys")] IReadOnlyList<string> SshKeys,
    [property: JsonPropertyName("networks")] IReadOnlyList<long>? Networks,
    [property: JsonPropertyName("user_data")] string UserData,
    [property: JsonPropertyName("labels")] IReadOnlyDictionary<string, string> Labels,
    [property: JsonPropertyName("firewalls")] IReadOnlyList<HetznerServerFirewallRef> Firewalls,
    [property: JsonPropertyName("public_net")] HetznerPublicNetSpec PublicNet);

internal sealed record HetznerEmptyActionBody();

/// <summary>SSH key resource.</summary>
public sealed class HetznerSshKey
{
    /// <summary>Numeric key id.</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>Key name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Ownership labels.</summary>
    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; set; }
}

/// <summary>SSH key wrapper for single-get responses.</summary>
public sealed class HetznerSshKeyWrapper
{
    /// <summary>Wrapped key.</summary>
    [JsonPropertyName("ssh_key")]
    public HetznerSshKey? SshKey { get; set; }
}

internal sealed record HetznerCreateSshKeyBody(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("public_key")] string PublicKey,
    [property: JsonPropertyName("labels")] IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// One Hetzner Cloud firewall rule. Direction is an exact allowlist match
/// (<c>in</c>/<c>out</c>); protocol is an exact allowlist match
/// (<c>tcp</c>/<c>udp</c>/<c>icmp</c>/<c>esp</c>/<c>gre</c>). Ingress rules
/// filter on <see cref="SourceIps"/>, egress rules on
/// <see cref="DestinationIps"/> — the unused side must stay empty so a rule
/// can never silently mean "anywhere".
/// </summary>
public sealed record HetznerFirewallRule(
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("port")] string? Port,
    [property: JsonPropertyName("source_ips")] IReadOnlyList<string> SourceIps,
    [property: JsonPropertyName("destination_ips")] IReadOnlyList<string> DestinationIps,
    [property: JsonPropertyName("description")] string? Description)
{
    private static readonly HashSet<string> KnownDirections =
        new(StringComparer.Ordinal) { "in", "out" };

    private static readonly HashSet<string> KnownProtocols =
        new(StringComparer.Ordinal) { "tcp", "udp", "icmp", "esp", "gre" };

    /// <summary>Validates the rule shape before it is sent.</summary>
    /// <exception cref="ArgumentException">The rule is malformed.</exception>
    public void Validate()
    {
        if (!KnownDirections.Contains(Direction))
            throw new ArgumentException($"Firewall rule direction must be 'in' or 'out': '{Direction}'.", nameof(Direction));
        if (!KnownProtocols.Contains(Protocol))
            throw new ArgumentException(
                $"Firewall rule protocol must be one of tcp/udp/icmp/esp/gre: '{Protocol}'.", nameof(Protocol));
        ArgumentNullException.ThrowIfNull(SourceIps);
        ArgumentNullException.ThrowIfNull(DestinationIps);
        var ingress = string.Equals(Direction, "in", StringComparison.Ordinal);
        if (ingress && SourceIps.Count == 0)
            throw new ArgumentException("Ingress firewall rules need at least one source IP/CIDR.", nameof(SourceIps));
        if (ingress && DestinationIps.Count != 0)
            throw new ArgumentException("Ingress firewall rules must not carry destination IPs.", nameof(DestinationIps));
        if (!ingress && DestinationIps.Count == 0)
            throw new ArgumentException("Egress firewall rules need at least one destination IP/CIDR.", nameof(DestinationIps));
        if (!ingress && SourceIps.Count != 0)
            throw new ArgumentException("Egress firewall rules must not carry source IPs.", nameof(SourceIps));
        var ported = string.Equals(Protocol, "tcp", StringComparison.Ordinal)
            || string.Equals(Protocol, "udp", StringComparison.Ordinal);
        if (ported && string.IsNullOrWhiteSpace(Port))
            throw new ArgumentException($"Firewall rule for {Protocol} must name a port or range.", nameof(Port));
        if (!ported && !string.IsNullOrWhiteSpace(Port))
            throw new ArgumentException($"Firewall rule for {Protocol} must not name a port.", nameof(Port));
        if (!string.IsNullOrWhiteSpace(Port))
            ValidatePort(Port);
        if (Description is not null && Description.Length > 255)
            throw new ArgumentException("Firewall rule description is at most 255 characters.", nameof(Description));
    }

    private static void ValidatePort(string port)
    {
        var trimmed = port.Trim();
        var dash = trimmed.IndexOf('-');
        if (dash < 0)
        {
            ValidatePortNumber(trimmed);
            return;
        }
        ValidatePortNumber(trimmed[..dash]);
        ValidatePortNumber(trimmed[(dash + 1)..]);
    }

    private static void ValidatePortNumber(string text)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port < 1 || port > 65535)
        {
            throw new ArgumentException($"Firewall rule port is not a valid port or range: '{text}'.", nameof(text));
        }
    }
}

/// <summary>Firewall resource.</summary>
public sealed class HetznerFirewall
{
    /// <summary>Numeric firewall id.</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>Firewall name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Ownership labels.</summary>
    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; set; }
}

/// <summary>Firewall wrapper for single-get responses.</summary>
public sealed class HetznerFirewallWrapper
{
    /// <summary>Wrapped firewall.</summary>
    [JsonPropertyName("firewall")]
    public HetznerFirewall? Firewall { get; set; }
}

internal sealed record HetznerCreateFirewallBody(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rules")] IReadOnlyList<HetznerFirewallRule> Rules,
    [property: JsonPropertyName("labels")] IReadOnlyDictionary<string, string> Labels);

/// <summary>Floating IP resource.</summary>
public sealed class HetznerFloatingIp
{
    /// <summary>Numeric floating IP id.</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>Floating IP name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Allocated address.</summary>
    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    /// <summary>Owning server id when assigned, else null.</summary>
    [JsonPropertyName("server")]
    public long? Server { get; set; }

    /// <summary>Ownership labels.</summary>
    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; set; }
}

/// <summary>Floating IP wrapper for single-get responses.</summary>
public sealed class HetznerFloatingIpWrapper
{
    /// <summary>Wrapped floating IP.</summary>
    [JsonPropertyName("floating_ip")]
    public HetznerFloatingIp? FloatingIp { get; set; }
}

internal sealed record HetznerCreateFloatingIpBody(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("home_location")] string HomeLocation,
    [property: JsonPropertyName("server")] long Server,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("labels")] IReadOnlyDictionary<string, string> Labels);
