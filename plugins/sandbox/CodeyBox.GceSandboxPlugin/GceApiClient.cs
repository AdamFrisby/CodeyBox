using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeyBox.GceSandboxPlugin;

/// <summary>Bounds for one <see cref="GceApiClient"/>. Every cap is operator-configured.</summary>
public sealed record GceClientLimits
{
    public const int DefaultHttpTimeoutSeconds = 60;
    public const int DefaultPollIntervalMilliseconds = 2000;
    public const int DefaultMaxPollIntervalMilliseconds = 15000;
    public const int DefaultMaxResponseBytes = 8 * 1024 * 1024;
    public const int DefaultMaxListItems = 5000;
    public const int DefaultMaxListPages = 100;
    public const int DefaultMaxStartupScriptBytes = 64 * 1024;

    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(DefaultHttpTimeoutSeconds);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(DefaultPollIntervalMilliseconds);
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromMilliseconds(DefaultMaxPollIntervalMilliseconds);
    public int MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;
    public int MaxListItems { get; init; } = DefaultMaxListItems;
    public int MaxListPages { get; init; } = DefaultMaxListPages;
    public int MaxStartupScriptBytes { get; init; } = DefaultMaxStartupScriptBytes;
    public bool AllowUnsafeHttp { get; init; }
}

/// <summary>Zone operation status as reported by Compute Engine.</summary>
public enum GceOperationStatus
{
    Unknown,
    Pending,
    Running,
    Done,
}

/// <summary>Scoped Compute Engine zone operation.</summary>
public sealed record GceOperation(
    string Name,
    GceOperationStatus Status,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>One attached disk as reported on an instance.</summary>
public sealed record GceAttachedDisk(string? Source, bool AutoDelete, string? DeviceName);

/// <summary>One network interface as reported on an instance.</summary>
public sealed record GceNetworkInterface(string? NetworkIp, string? NatIp);

/// <summary>Instance record (subset the provider consumes).</summary>
public sealed class GceInstance
{
    public string? Name { get; set; }
    public string? Status { get; set; }
    public string? SelfLink { get; set; }
    public string? Zone { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public List<string>? Tags { get; set; }
    public List<GceAttachedDisk>? Disks { get; set; }
    public List<GceNetworkInterface>? NetworkInterfaces { get; set; }
}

/// <summary>Address record (subset the provider consumes).</summary>
public sealed class GceAddress
{
    public string? Name { get; set; }
    public string? Address { get; set; }
    public string? Status { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
}

/// <summary>Firewall rule record (subset the provider consumes).</summary>
public sealed class GceFirewall
{
    public string? Name { get; set; }
    public string? Network { get; set; }
    public List<string>? TargetTags { get; set; }
    public List<string>? SourceRanges { get; set; }
}

/// <summary>Body for <c>instances.insert</c>. Startup-script bytes are capped at the sink.</summary>
public sealed record GceInstanceSpec(
    string Name,
    string MachineType,
    string SourceImage,
    string Network,
    string Subnetwork,
    int DiskSizeGb,
    bool BootDiskAutoDelete,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> MetadataItems,
    IReadOnlyList<string> Tags,
    string SshKeysLine,
    string StartupScript,
    string? NatIp)
{
    public void Validate(int maxStartupScriptBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(MachineType);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(Network);
        ArgumentException.ThrowIfNullOrWhiteSpace(Subnetwork);
        ArgumentException.ThrowIfNullOrWhiteSpace(SshKeysLine);
        ArgumentException.ThrowIfNullOrWhiteSpace(StartupScript);
        if (DiskSizeGb < 10 || DiskSizeGb > 512)
            throw new ArgumentOutOfRangeException(nameof(DiskSizeGb), "Boot disk size must be 10–512 GB.");
        var bytes = Encoding.UTF8.GetByteCount(StartupScript);
        if (bytes > maxStartupScriptBytes)
            throw new ArgumentOutOfRangeException(
                nameof(StartupScript),
                $"Startup script is {bytes} bytes; the cap is {maxStartupScriptBytes}. Refusing to truncate guest bootstrap.");
    }
};

/// <summary>
/// Minimal Compute Engine REST client over an injected <see cref="HttpClient"/> (a fake
/// handler in tests; never a live account outside operator-run tooling). All responses
/// are byte-bounded before buffering, paged listings are page/item-bounded, zone
/// operations are followed until DONE with their error inspected, and failures keep
/// their auth/quota/transient distinctions.
/// </summary>
public sealed class GceApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly TimeProvider _clock;
    private readonly GceClientLimits _limits;

    public GceApiClient(HttpClient http, string baseUrl, TimeProvider? clock = null, GceClientLimits? limits = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _baseUrl = baseUrl.TrimEnd('/');
        _clock = clock ?? TimeProvider.System;
        _limits = limits ?? new GceClientLimits();
    }

    public string BaseUrl => _baseUrl;

    // ------------------------------------------------------------------
    // Instances
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates an instance. <paramref name="requestId"/> must be a stable nonzero UUID:
    /// retries after an unknown outcome reuse the same id so the service dedupes instead
    /// of double-provisioning. Returns the zone operation to follow.
    /// </summary>
    public async Task<GceOperation> InsertInstanceAsync(
        string token,
        string project,
        string zone,
        GceInstanceSpec spec,
        string requestId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);
        ArgumentNullException.ThrowIfNull(spec);
        ValidateRequestId(requestId);
        spec.Validate(_limits.MaxStartupScriptBytes);

        var body = BuildInsertBody(project, zone, spec);
        var doc = await SendAsync(
            token,
            HttpMethod.Post,
            $"projects/{Component(project)}/zones/{Component(zone)}/instances?requestId={Uri.EscapeDataString(requestId)}",
            body,
            "insert instance",
            ct).ConfigureAwait(false);
        using (doc)
        {
            return ParseOperation(doc.RootElement, "insert instance");
        }
    }

    /// <summary>Returns the instance, or null on 404. Any other failure throws.</summary>
    public async Task<GceInstance?> GetInstanceAsync(
        string token, string project, string zone, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            var doc = await SendAsync(
                token,
                HttpMethod.Get,
                $"projects/{Component(project)}/zones/{Component(zone)}/instances/{Component(name)}",
                body: null,
                "get instance",
                ct).ConfigureAwait(false);
            using (doc)
            {
                return ParseInstance(doc.RootElement);
            }
        }
        catch (GceApiException ex) when (ex.Kind == GceFailureKind.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Zone-scoped instance listing with an exact-match label filter. Page- and item-bounded;
    /// hitting either cap fails loudly rather than silently truncating inventory.
    /// </summary>
    public async Task<IReadOnlyList<GceInstance>> ListInstancesAsync(
        string token, string project, string zone, string? filter, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);
        var result = new List<GceInstance>();
        string? pageToken = null;
        for (var page = 0; page < _limits.MaxListPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var query = new StringBuilder($"projects/{Component(project)}/zones/{Component(zone)}/instances?maxResults=500");
            if (!string.IsNullOrWhiteSpace(filter))
                query.Append("&filter=").Append(Uri.EscapeDataString(filter));
            if (pageToken is not null)
                query.Append("&pageToken=").Append(Uri.EscapeDataString(pageToken));
            var doc = await SendAsync(token, HttpMethod.Get, query.ToString(), body: null, "list instances", ct).ConfigureAwait(false);
            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (result.Count >= _limits.MaxListItems)
                        {
                            throw new GceApiException(
                                GceFailureKind.Protocol, "list instances",
                                $"instance inventory exceeded the { _limits.MaxListItems} item cap; refusing to truncate.");
                        }
                        result.Add(ParseInstance(item));
                    }
                }
                pageToken = root.TryGetProperty("nextPageToken", out var next) && next.ValueKind == JsonValueKind.String
                    ? next.GetString()
                    : null;
                if (string.IsNullOrEmpty(pageToken))
                    return result;
            }
        }
        throw new GceApiException(
            GceFailureKind.Protocol, "list instances",
            $"instance listing exceeded the {_limits.MaxListPages} page cap; refusing to truncate.");
    }

    /// <summary>Deletes an instance. Returns the zone operation to follow.</summary>
    public async Task<GceOperation> DeleteInstanceAsync(
        string token, string project, string zone, string name, string requestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateRequestId(requestId);
        try
        {
            var doc = await SendAsync(
                token,
                HttpMethod.Delete,
                $"projects/{Component(project)}/zones/{Component(zone)}/instances/{Component(name)}?requestId={Uri.EscapeDataString(requestId)}",
                body: null,
                "delete instance",
                ct).ConfigureAwait(false);
            using (doc)
            {
                return ParseOperation(doc.RootElement, "delete instance");
            }
        }
        catch (GceApiException ex) when (ex.Kind == GceFailureKind.NotFound)
        {
            return new GceOperation($"delete-{name}", GceOperationStatus.Done, null, null);
        }
    }

    // ------------------------------------------------------------------
    // Zone operations
    // ------------------------------------------------------------------

    public async Task<GceOperation> GetZoneOperationAsync(
        string token, string project, string zone, string operationName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        var doc = await SendAsync(
            token,
            HttpMethod.Get,
            $"projects/{Component(project)}/zones/{Component(zone)}/operations/{Component(operationName)}",
            body: null,
            "get zone operation",
            ct).ConfigureAwait(false);
        using (doc)
        {
            return ParseOperation(doc.RootElement, "get zone operation");
        }
    }

    /// <summary>
    /// Follows a scoped zone operation until DONE with exponential backoff, then inspects
    /// its error: a DONE operation carrying an error throws (quota-shaped errors defer).
    /// A wait timeout throws <see cref="GceFailureKind.Transient"/> — inconclusive, never
    /// success — so the caller reconciles by resource identity instead.
    /// </summary>
    public async Task<GceOperation> WaitForZoneOperationAsync(
        string token,
        string project,
        string zone,
        string operationName,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        var deadline = _clock.GetUtcNow() + timeout;
        var delay = _limits.PollInterval;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var operation = await GetZoneOperationAsync(token, project, zone, operationName, ct).ConfigureAwait(false);
            if (operation.Status == GceOperationStatus.Done)
            {
                if (operation.ErrorCode is not null || operation.ErrorMessage is not null)
                {
                    throw new GceApiException(
                        ClassifyOperationError(operation.ErrorCode, operation.ErrorMessage),
                        "zone operation",
                        $"operation '{operationName}' finished DONE with error {operation.ErrorCode}: {operation.ErrorMessage}",
                        reason: operation.ErrorCode);
                }
                return operation;
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new GceApiException(
                    GceFailureKind.Transient, "zone operation",
                    $"operation '{operationName}' did not reach DONE within {timeout.TotalSeconds:F0}s; outcome unknown, reconcile by resource identity.");
            }
            var wait = delay < timeout ? delay : timeout;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
            var nextTicks = Math.Min(delay.Ticks * 2, _limits.MaxPollInterval.Ticks);
            delay = new TimeSpan(Math.Max(nextTicks, TimeSpan.FromMilliseconds(1).Ticks));
        }
    }

    // ------------------------------------------------------------------
    // Disks
    // ------------------------------------------------------------------

    /// <summary>Disk record (subset the provider consumes for ownership checks).</summary>
    public sealed class GceDisk
    {
        public string? Name { get; set; }
        public string? SelfLink { get; set; }
        public Dictionary<string, string>? Labels { get; set; }
    }

    public async Task<GceDisk?> GetDiskAsync(
        string token, string project, string zone, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            var doc = await SendAsync(
                token, HttpMethod.Get,
                $"projects/{Component(project)}/zones/{Component(zone)}/disks/{Component(name)}",
                body: null, "get disk", ct).ConfigureAwait(false);
            using (doc)
            {
                var root = doc.RootElement;
                return new GceDisk
                {
                    Name = root.TryGetProperty("name", out var diskName) && diskName.ValueKind == JsonValueKind.String
                        ? diskName.GetString() : null,
                    SelfLink = root.TryGetProperty("selfLink", out var link) && link.ValueKind == JsonValueKind.String
                        ? link.GetString() : null,
                    Labels = root.TryGetProperty("labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Object
                        ? ReadStringMap(labelsEl) : null,
                };
            }
        }
        catch (GceApiException ex) when (ex.Kind == GceFailureKind.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes a zonal disk. A 404 means already gone (confirmed deletion, not a claim).
    /// </summary>
    public async Task<GceOperation> DeleteDiskAsync(
        string token, string project, string zone, string name, string requestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateRequestId(requestId);
        try
        {
            var doc = await SendAsync(
                token, HttpMethod.Delete,
                $"projects/{Component(project)}/zones/{Component(zone)}/disks/{Component(name)}?requestId={Uri.EscapeDataString(requestId)}",
                body: null, "delete disk", ct).ConfigureAwait(false);
            using (doc)
            {
                return ParseOperation(doc.RootElement, "delete disk");
            }
        }
        catch (GceApiException ex) when (ex.Kind == GceFailureKind.NotFound)
        {
            return new GceOperation($"delete-{name}", GceOperationStatus.Done, null, null);
        }
    }

    // ------------------------------------------------------------------
    // Addresses
    // ------------------------------------------------------------------

    public async Task<GceOperation> InsertAddressAsync(
        string token, string project, string region, string name, string addressType,
        IReadOnlyDictionary<string, string> labels, string requestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateRequestId(requestId);
        var body = JsonSerializer.Serialize(new
        {
            name,
            addressType,
            labels,
        });
        var doc = await SendAsync(
            token, HttpMethod.Post,
            $"projects/{Component(project)}/regions/{Component(region)}/addresses?requestId={Uri.EscapeDataString(requestId)}",
            body, "insert address", ct).ConfigureAwait(false);
        using (doc)
        {
            return ParseRegionOperation(doc.RootElement, "insert address");
        }
    }

    public async Task<GceAddress?> GetAddressAsync(
        string token, string project, string region, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            var doc = await SendAsync(
                token, HttpMethod.Get,
                $"projects/{Component(project)}/regions/{Component(region)}/addresses/{Component(name)}",
                body: null, "get address", ct).ConfigureAwait(false);
            using (doc)
            {
                var root = doc.RootElement;
                return new GceAddress
                {
                    Name = root.TryGetProperty("name", out var n) ? n.GetString() : null,
                    Address = root.TryGetProperty("address", out var a) ? a.GetString() : null,
                    Status = root.TryGetProperty("status", out var s) ? s.GetString() : null,
                    Labels = root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object
                        ? ReadStringMap(labels)
                        : null,
                };
            }
        }
        catch (GceApiException ex) when (ex.Kind == GceFailureKind.NotFound)
        {
            return null;
        }
    }

    public async Task<GceOperation> DeleteAddressAsync(
        string token, string project, string region, string name, string requestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateRequestId(requestId);
        try
        {
            var doc = await SendAsync(
                token, HttpMethod.Delete,
                $"projects/{Component(project)}/regions/{Component(region)}/addresses/{Component(name)}?requestId={Uri.EscapeDataString(requestId)}",
                body: null, "delete address", ct).ConfigureAwait(false);
            using (doc)
            {
                return ParseRegionOperation(doc.RootElement, "delete address");
            }
        }
        catch (GceApiException ex) when (ex.Kind == GceFailureKind.NotFound)
        {
            return new GceOperation($"delete-{name}", GceOperationStatus.Done, null, null);
        }
    }

    public async Task<GceOperation> WaitForRegionOperationAsync(
        string token, string project, string region, string operationName, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        var deadline = _clock.GetUtcNow() + timeout;
        var delay = _limits.PollInterval;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var doc = await SendAsync(
                token, HttpMethod.Get,
                $"projects/{Component(project)}/regions/{Component(region)}/operations/{Component(operationName)}",
                body: null, "get region operation", ct).ConfigureAwait(false);
            GceOperation operation;
            using (doc)
            {
                operation = ParseRegionOperation(doc.RootElement, "get region operation");
            }
            if (operation.Status == GceOperationStatus.Done)
            {
                if (operation.ErrorCode is not null || operation.ErrorMessage is not null)
                {
                    throw new GceApiException(
                        ClassifyOperationError(operation.ErrorCode, operation.ErrorMessage),
                        "region operation",
                        $"operation '{operationName}' finished DONE with error {operation.ErrorCode}: {operation.ErrorMessage}",
                        reason: operation.ErrorCode);
                }
                return operation;
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new GceApiException(
                    GceFailureKind.Transient, "region operation",
                    $"operation '{operationName}' did not reach DONE within {timeout.TotalSeconds:F0}s; outcome unknown, reconcile by resource identity.");
            }
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
            delay = new TimeSpan(Math.Min(delay.Ticks * 2, _limits.MaxPollInterval.Ticks));
        }
    }

    // ------------------------------------------------------------------
    // Firewalls
    // ------------------------------------------------------------------

    public async Task<GceOperation> InsertFirewallAsync(
        string token,
        string project,
        string name,
        string network,
        IReadOnlyList<string> sourceRanges,
        IReadOnlyList<string> targetTags,
        int sshPort,
        string requestId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(network);
        ValidateRequestId(requestId);
        var body = JsonSerializer.Serialize(new
        {
            name,
            network,
            direction = "INGRESS",
            priority = 1000,
            allowed = new[] { new { IPProtocol = "tcp", ports = new[] { sshPort.ToString(System.Globalization.CultureInfo.InvariantCulture) } } },
            sourceRanges,
            targetTags,
            description = "codeybox gce-sandbox SSH ingress (host-owned, per-sandbox)",
        });
        var doc = await SendAsync(
            token, HttpMethod.Post,
            $"projects/{Component(project)}/global/firewalls?requestId={Uri.EscapeDataString(requestId)}",
            body, "insert firewall", ct).ConfigureAwait(false);
        using (doc)
        {
            return ParseRegionOperation(doc.RootElement, "insert firewall");
        }
    }

    public async Task<GceFirewall?> GetFirewallAsync(
        string token, string project, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            var doc = await SendAsync(
                token, HttpMethod.Get,
                $"projects/{Component(project)}/global/firewalls/{Component(name)}",
                body: null, "get firewall", ct).ConfigureAwait(false);
            using (doc)
            {
                var root = doc.RootElement;
                return new GceFirewall
                {
                    Name = root.TryGetProperty("name", out var n) ? n.GetString() : null,
                    Network = root.TryGetProperty("network", out var net) ? net.GetString() : null,
                    TargetTags = root.TryGetProperty("targetTags", out var tags) && tags.ValueKind == JsonValueKind.Array
                        ? tags.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
                        : null,
                    SourceRanges = root.TryGetProperty("sourceRanges", out var ranges) && ranges.ValueKind == JsonValueKind.Array
                        ? ranges.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
                        : null,
                };
            }
        }
        catch (GceApiException ex) when (ex.Kind == GceFailureKind.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes a firewall rule. A 404 means already gone (confirmed deletion, not a claim).
    /// Ownership must be revalidated by the caller before invoking this: firewall rules
    /// carry no labels, so linkage comes from the instance metadata plus the name prefix.
    /// </summary>
    public async Task<GceOperation> DeleteFirewallAsync(
        string token, string project, string name, string requestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateRequestId(requestId);
        try
        {
            var doc = await SendAsync(
                token, HttpMethod.Delete,
                $"projects/{Component(project)}/global/firewalls/{Component(name)}?requestId={Uri.EscapeDataString(requestId)}",
                body: null, "delete firewall", ct).ConfigureAwait(false);
            using (doc)
            {
                return ParseRegionOperation(doc.RootElement, "delete firewall");
            }
        }
        catch (GceApiException ex) when (ex.Kind == GceFailureKind.NotFound)
        {
            return new GceOperation($"delete-{name}", GceOperationStatus.Done, null, null);
        }
    }

    public async Task<GceOperation> WaitForGlobalOperationAsync(
        string token, string project, string operationName, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        var deadline = _clock.GetUtcNow() + timeout;
        var delay = _limits.PollInterval;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var doc = await SendAsync(
                token, HttpMethod.Get,
                $"projects/{Component(project)}/global/operations/{Component(operationName)}",
                body: null, "get global operation", ct).ConfigureAwait(false);
            GceOperation operation;
            using (doc)
            {
                operation = ParseRegionOperation(doc.RootElement, "get global operation");
            }
            if (operation.Status == GceOperationStatus.Done)
            {
                if (operation.ErrorCode is not null || operation.ErrorMessage is not null)
                {
                    throw new GceApiException(
                        ClassifyOperationError(operation.ErrorCode, operation.ErrorMessage),
                        "global operation",
                        $"operation '{operationName}' finished DONE with error {operation.ErrorCode}: {operation.ErrorMessage}",
                        reason: operation.ErrorCode);
                }
                return operation;
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new GceApiException(
                    GceFailureKind.Transient, "global operation",
                    $"operation '{operationName}' did not reach DONE within {timeout.TotalSeconds:F0}s; outcome unknown, reconcile by resource identity.");
            }
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
            delay = new TimeSpan(Math.Min(delay.Ticks * 2, _limits.MaxPollInterval.Ticks));
        }
    }

    // ------------------------------------------------------------------
    // Bounded fleet listings for the orphan sweep (all page/item-bounded)
    // ------------------------------------------------------------------

    /// <summary>Zone-scoped disk listing. Page- and item-bounded like instances.</summary>
    public async Task<IReadOnlyList<GceDisk>> ListDisksAsync(
        string token, string project, string zone, string? filter, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var result = new List<GceDisk>();
        string? pageToken = null;
        for (var page = 0; page < _limits.MaxListPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var query = new StringBuilder($"projects/{Component(project)}/zones/{Component(zone)}/disks?maxResults=500");
            if (!string.IsNullOrWhiteSpace(filter))
                query.Append("&filter=").Append(Uri.EscapeDataString(filter));
            if (pageToken is not null)
                query.Append("&pageToken=").Append(Uri.EscapeDataString(pageToken));
            var doc = await SendAsync(token, HttpMethod.Get, query.ToString(), body: null, "list disks", ct).ConfigureAwait(false);
            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (result.Count >= _limits.MaxListItems)
                            throw new GceApiException(GceFailureKind.Protocol, "list disks", "disk inventory exceeded the item cap; refusing to truncate.");
                        result.Add(new GceDisk
                        {
                            Name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                            SelfLink = item.TryGetProperty("selfLink", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
                            Labels = item.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object
                                ? ReadStringMap(labels) : null,
                        });
                    }
                }
                pageToken = root.TryGetProperty("nextPageToken", out var next) && next.ValueKind == JsonValueKind.String
                    ? next.GetString() : null;
                if (string.IsNullOrEmpty(pageToken))
                    return result;
            }
        }
        throw new GceApiException(GceFailureKind.Protocol, "list disks", "disk listing exceeded the page cap; refusing to truncate.");
    }

    /// <summary>Region-scoped address listing. Page- and item-bounded.</summary>
    public async Task<IReadOnlyList<GceAddress>> ListAddressesAsync(
        string token, string project, string region, string? filter, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var result = new List<GceAddress>();
        string? pageToken = null;
        for (var page = 0; page < _limits.MaxListPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var query = new StringBuilder($"projects/{Component(project)}/regions/{Component(region)}/addresses?maxResults=500");
            if (!string.IsNullOrWhiteSpace(filter))
                query.Append("&filter=").Append(Uri.EscapeDataString(filter));
            if (pageToken is not null)
                query.Append("&pageToken=").Append(Uri.EscapeDataString(pageToken));
            var doc = await SendAsync(token, HttpMethod.Get, query.ToString(), body: null, "list addresses", ct).ConfigureAwait(false);
            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (result.Count >= _limits.MaxListItems)
                            throw new GceApiException(GceFailureKind.Protocol, "list addresses", "address inventory exceeded the item cap; refusing to truncate.");
                        result.Add(new GceAddress
                        {
                            Name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                            Address = item.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null,
                            Status = item.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
                            Labels = item.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object
                                ? ReadStringMap(labels) : null,
                        });
                    }
                }
                pageToken = root.TryGetProperty("nextPageToken", out var next) && next.ValueKind == JsonValueKind.String
                    ? next.GetString() : null;
                if (string.IsNullOrEmpty(pageToken))
                    return result;
            }
        }
        throw new GceApiException(GceFailureKind.Protocol, "list addresses", "address listing exceeded the page cap; refusing to truncate.");
    }

    /// <summary>Global firewall listing. Page- and item-bounded.</summary>
    public async Task<IReadOnlyList<GceFirewall>> ListFirewallsAsync(
        string token, string project, string? filter, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var result = new List<GceFirewall>();
        string? pageToken = null;
        for (var page = 0; page < _limits.MaxListPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var query = new StringBuilder($"projects/{Component(project)}/global/firewalls?maxResults=500");
            if (!string.IsNullOrWhiteSpace(filter))
                query.Append("&filter=").Append(Uri.EscapeDataString(filter));
            if (pageToken is not null)
                query.Append("&pageToken=").Append(Uri.EscapeDataString(pageToken));
            var doc = await SendAsync(token, HttpMethod.Get, query.ToString(), body: null, "list firewalls", ct).ConfigureAwait(false);
            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (result.Count >= _limits.MaxListItems)
                            throw new GceApiException(GceFailureKind.Protocol, "list firewalls", "firewall inventory exceeded the item cap; refusing to truncate.");
                        result.Add(new GceFirewall
                        {
                            Name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                            Network = item.TryGetProperty("network", out var net) && net.ValueKind == JsonValueKind.String ? net.GetString() : null,
                            TargetTags = item.TryGetProperty("targetTags", out var tags) && tags.ValueKind == JsonValueKind.Array
                                ? tags.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList() : null,
                            SourceRanges = item.TryGetProperty("sourceRanges", out var ranges) && ranges.ValueKind == JsonValueKind.Array
                                ? ranges.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList() : null,
                        });
                    }
                }
                pageToken = root.TryGetProperty("nextPageToken", out var next) && next.ValueKind == JsonValueKind.String
                    ? next.GetString() : null;
                if (string.IsNullOrEmpty(pageToken))
                    return result;
            }
        }
        throw new GceApiException(GceFailureKind.Protocol, "list firewalls", "firewall listing exceeded the page cap; refusing to truncate.");
    }

    // ------------------------------------------------------------------
    // Transport
    // ------------------------------------------------------------------

    private async Task<JsonDocument> SendAsync(
        string token,
        HttpMethod method,
        string relativeUrl,
        string? body,
        string operation,
        CancellationToken ct)
    {
        EnsureBaseUrlAllowed();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_limits.HttpTimeout);
        using var request = new HttpRequestMessage(method, _baseUrl + "/" + relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new GceApiException(
                GceFailureKind.Transient, operation,
                $"request timed out after {_limits.HttpTimeout.TotalSeconds:F0}s; outcome unknown, reconcile by resource identity.",
                inner: ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new GceApiException(
                GceFailureKind.Transient, operation,
                $"transport failure with unknown outcome; reconcile by resource identity before resubmitting: {TrimMessage(ex.Message)}",
                inner: ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw await BuildErrorAsync(operation, response, ct).ConfigureAwait(false);
            var text = await ReadBoundedStringAsync(response.Content, operation, timeoutCts.Token, _limits.MaxResponseBytes).ConfigureAwait(false);
            return ParseJson(text, operation);
        }
    }

    private static async Task<GceApiException> BuildErrorAsync(
        string operation, HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        string text;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            text = await ReadBoundedStringAsync(response.Content, operation, cts.Token, maxBytes: 256 * 1024).ConfigureAwait(false);
        }
        catch
        {
            text = string.Empty;
        }
        var (code, reason, message) = TryParseErrorEnvelope(text);
        var kind = ClassifyStatus(status, reason, message ?? code);
        return new GceApiException(
            kind, operation,
            $"HTTP {status}{(reason is null ? "" : $" reason={reason}")}: {TrimMessage(message ?? code ?? response.ReasonPhrase ?? "request failed")}",
            httpStatus: status,
            reason: reason);
    }

    private static (string? Code, string? Reason, string? Message) TryParseErrorEnvelope(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return (null, null, text.Length <= 500 ? text : text[..500]);
            string? message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;
            string? code = error.TryGetProperty("code", out var c) ? c.ToString() : null;
            string? reason = null;
            if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in errors.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("reason", out var r)
                        && r.ValueKind == JsonValueKind.String)
                    {
                        reason = r.GetString();
                        if (message is null
                            && entry.TryGetProperty("message", out var em)
                            && em.ValueKind == JsonValueKind.String)
                        {
                            message = em.GetString();
                        }
                        break;
                    }
                }
            }
            return (code, reason, message);
        }
        catch (JsonException)
        {
            return (null, null, text.Length <= 500 ? text : text[..500]);
        }
    }

    private static GceFailureKind ClassifyStatus(int status, string? reason, string? message) =>
        status switch
        {
            401 => GceFailureKind.Auth,
            403 when IsQuotaSignal(reason, message) => GceFailureKind.Quota,
            403 => GceFailureKind.Forbidden,
            404 => GceFailureKind.NotFound,
            409 => GceFailureKind.Conflict,
            429 => GceFailureKind.Quota,
            _ when status >= 500 && IsQuotaSignal(reason, message) => GceFailureKind.Quota,
            _ when status >= 500 => GceFailureKind.Transient,
            _ => GceFailureKind.Unexpected,
        };

    private static GceFailureKind ClassifyOperationError(string? code, string? message) =>
        IsQuotaSignal(code, message) ? GceFailureKind.Quota : GceFailureKind.Unexpected;

    private static bool IsQuotaSignal(string? reason, string? message)
    {
        if (reason is not null && (reason.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("rateLimit", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("RateExceeded", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return message is not null && message.Contains("quota", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadBoundedStringAsync(
        HttpContent content, string operation, CancellationToken ct, int maxBytes)
    {
        var cap = maxBytes;
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var remaining = (long)cap + 1;
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, remaining)), ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            remaining -= read;
            if (remaining <= 0)
            {
                throw new GceApiException(
                    GceFailureKind.Protocol, operation,
                    $"response exceeded the {cap} byte cap; refusing to trust a truncated body.");
            }
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonDocument ParseJson(string text, string operation)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new GceApiException(GceFailureKind.Protocol, operation, "empty response body; refusing to infer success.");
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new GceApiException(
                GceFailureKind.Protocol, operation,
                $"malformed response body; refusing to infer success: {TrimMessage(ex.Message)}",
                inner: ex);
        }
    }

    private static GceOperation ParseOperation(JsonElement root, string operation)
    {
        var name = root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new GceApiException(
                GceFailureKind.Protocol, operation,
                "operation response carried no name; the request outcome is unknown, reconcile by resource identity.");
        }
        var (code, message) = ExtractOperationError(root);
        return new GceOperation(name, ParseStatus(root), ErrorCode: code, ErrorMessage: message);
    }

    private static GceOperation ParseRegionOperation(JsonElement root, string operation) =>
        ParseOperation(root, operation);

    private static GceOperationStatus ParseStatus(JsonElement root)
    {
        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
        {
            return status.GetString() switch
            {
                "DONE" => GceOperationStatus.Done,
                "RUNNING" => GceOperationStatus.Running,
                "PENDING" => GceOperationStatus.Pending,
                _ => GceOperationStatus.Unknown,
            };
        }
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var (code, message) = ExtractOperationError(root);
            if (code is not null || message is not null)
                return GceOperationStatus.Done;
        }
        return GceOperationStatus.Unknown;
    }

    private static (string? Code, string? Message) ExtractOperationError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return (null, null);
        string? code = null;
        string? message = null;
        if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in errors.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                code ??= entry.TryGetProperty("code", out var c) ? c.ToString() : null;
                message ??= entry.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() : null;
            }
        }
        message ??= error.TryGetProperty("message", out var top) && top.ValueKind == JsonValueKind.String
            ? top.GetString() : null;
        return (code, message);
    }

    private static GceInstance ParseInstance(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new GceApiException(GceFailureKind.Protocol, "parse instance", "instance body is not a JSON object.");
        var instance = new GceInstance
        {
            Name = root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
            Status = root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString() : null,
            SelfLink = root.TryGetProperty("selfLink", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null,
            Zone = root.TryGetProperty("zone", out var zone) && zone.ValueKind == JsonValueKind.String ? zone.GetString() : null,
        };
        if (root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
            instance.Labels = ReadStringMap(labels);
        if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
            && tags.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            instance.Tags = items.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? string.Empty)
                .ToList();
        }
        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("items", out var metaItems) && metaItems.ValueKind == JsonValueKind.Array)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in metaItems.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                if (entry.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
                    && entry.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    map[key.GetString() ?? string.Empty] = value.GetString() ?? string.Empty;
                }
            }
            instance.Metadata = map;
        }
        if (root.TryGetProperty("networkInterfaces", out var nics) && nics.ValueKind == JsonValueKind.Array)
        {
            instance.NetworkInterfaces = [];
            foreach (var nic in nics.EnumerateArray())
            {
                if (nic.ValueKind != JsonValueKind.Object)
                    continue;
                string? natIp = null;
                if (nic.TryGetProperty("accessConfigs", out var configs) && configs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var config in configs.EnumerateArray())
                    {
                        if (config.ValueKind == JsonValueKind.Object
                            && config.TryGetProperty("natIP", out var nat)
                            && nat.ValueKind == JsonValueKind.String)
                        {
                            natIp = nat.GetString();
                            break;
                        }
                    }
                }
                instance.NetworkInterfaces.Add(new GceNetworkInterface(
                    nic.TryGetProperty("networkIP", out var internal_) && internal_.ValueKind == JsonValueKind.String
                        ? internal_.GetString() : null,
                    natIp));
            }
        }
        if (root.TryGetProperty("disks", out var disks) && disks.ValueKind == JsonValueKind.Array)
        {
            instance.Disks = [];
            foreach (var disk in disks.EnumerateArray())
            {
                if (disk.ValueKind != JsonValueKind.Object)
                    continue;
                instance.Disks.Add(new GceAttachedDisk(
                    disk.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String
                        ? source.GetString() : null,
                    disk.TryGetProperty("autoDelete", out var auto) && auto.ValueKind == JsonValueKind.True,
                    disk.TryGetProperty("deviceName", out var device) && device.ValueKind == JsonValueKind.String
                        ? device.GetString() : null));
            }
        }
        return instance;
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement element)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                map[property.Name] = property.Value.GetString() ?? string.Empty;
        }
        return map;
    }

    private static string BuildInsertBody(string project, string zone, GceInstanceSpec spec)
    {
        var machineType = spec.MachineType.StartsWith("https://", StringComparison.Ordinal)
            || spec.MachineType.StartsWith("projects/", StringComparison.Ordinal)
            ? spec.MachineType
            : $"projects/{project}/zones/{zone}/machineTypes/{spec.MachineType}";
        var accessConfigs = new List<object>();
        if (spec.NatIp is null)
            accessConfigs.Add(new { name = "External NAT", type = "ONE_TO_ONE_NAT" });
        else
            accessConfigs.Add(new { name = "External NAT", type = "ONE_TO_ONE_NAT", natIP = spec.NatIp });
        var metadataItems = new List<object>();
        foreach (var (key, value) in spec.MetadataItems)
            metadataItems.Add(new { key, value });
        metadataItems.Add(new { key = "ssh-keys", value = spec.SshKeysLine });
        metadataItems.Add(new { key = "startup-script", value = spec.StartupScript });
        return JsonSerializer.Serialize(new
        {
            name = spec.Name,
            description = $"codeybox {GceSandboxOptions.ProviderKind} sandbox",
            machineType,
            labels = spec.Labels,
            metadata = new { items = metadataItems },
            tags = new { items = spec.Tags },
            disks = new[]
            {
                new
                {
                    boot = true,
                    autoDelete = spec.BootDiskAutoDelete,
                    initializeParams = new
                    {
                        sourceImage = spec.SourceImage,
                        diskSizeGb = spec.DiskSizeGb.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        labels = spec.Labels,
                    },
                },
            },
            networkInterfaces = new[]
            {
                new
                {
                    network = spec.Network,
                    subnetwork = spec.Subnetwork,
                    accessConfigs,
                },
            },
            serviceAccounts = Array.Empty<object>(),
        });
    }

    private static void ValidateRequestId(string requestId)
    {
        if (!Guid.TryParse(requestId, out var parsed) || parsed == Guid.Empty)
        {
            throw new ArgumentException(
                "requestId must be a stable nonzero UUID so unknown outcomes reconcile instead of double-provisioning.",
                nameof(requestId));
        }
    }

    private static string Component(string value) => Uri.EscapeDataString(value);

    private void EnsureBaseUrlAllowed()
    {
        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException(
                $"GCE ComputeBaseUrl '{_baseUrl}' is not an absolute URL.");
        }
        if (string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return;
        if (_limits.AllowUnsafeHttp
            && string.Equals(baseUri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            && baseUri.Host is "localhost" or "127.0.0.1" or "::1")
        {
            return;
        }
        throw new InvalidOperationException(
            $"Refusing to send the workload access token over '{baseUri.Scheme}://{baseUri.Host}'. " +
            "GCE API traffic must use https (http only for loopback test URLs under AllowUnsafeHttp).");
    }

    private static string TrimMessage(string message)
    {
        var trimmed = message.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300];
    }
}

/// <summary>
/// GCE naming and label rules shared by the client and the provider: one source of truth
/// for the instance-name grammar, label sanitization, and the ownership label keys.
/// </summary>
public static class GceNaming
{
    public const string ManagedLabelKey = "codeybox-managed";
    public const string OwnerLabelKey = "codeybox-owner";
    public const string WorkItemLabelKey = "codeybox-work-item";

    public const string MetadataManagedKey = "codeybox-managed";
    public const string MetadataOwnerKey = "codeybox-owner";
    public const string MetadataWorkItemKey = "codeybox-work-item";
    public const string MetadataCreatedKey = "codeybox-created";
    public const string MetadataFirewallKey = "codeybox-firewall";
    public const string MetadataAddressKey = "codeybox-address";

    /// <summary>
    /// True when <paramref name="name"/> satisfies the GCE instance-name grammar
    /// (<c>[a-z]([-a-z0-9]*[a-z0-9])?</c>, at most 63 characters).
    /// </summary>
    public static bool IsValidInstanceName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 63)
            return false;
        if (name[0] < 'a' || name[0] > 'z')
            return false;
        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if ((c < 'a' || c > 'z') && (c < '0' || c > '9') && c != '-')
                return false;
        }
        return name[^1] != '-';
    }

    /// <summary>
    /// Sanitizes an owner id into the GCE label alphabet (lowercase, <c>[a-z0-9_-]</c>,
    /// at most 63 characters). Both stamping and verification use this, so comparison
    /// stays exact on the sanitized form.
    /// </summary>
    public static string SanitizeLabelValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
                builder.Append(c);
            else
                builder.Append('-');
        }
        var sanitized = builder.ToString().Trim('-', '_');
        if (sanitized.Length > 63)
            sanitized = sanitized[..63].TrimEnd('-', '_');
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }
}
