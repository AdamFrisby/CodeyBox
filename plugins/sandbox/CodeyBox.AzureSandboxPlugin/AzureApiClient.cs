using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeyBox.AzureSandboxPlugin;

/// <summary>
/// Size, timeout, and safety bounds for <see cref="AzureApiClient"/>.
/// Defaults are the single source of truth — <see cref="AzureSandboxOptions"/>
/// mirrors them so operators tune hot-reloadable knobs, not literals.
/// </summary>
public sealed record AzureClientLimits
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

    /// <summary>Default cap on cloud-init userData bytes.</summary>
    public const int DefaultMaxUserDataBytes = 64 * 1024;

    /// <summary>Per-request HTTP timeout. Does not bound status polls — those carry their own timeout.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(DefaultHttpTimeoutSeconds);

    /// <summary>Base delay between status polls; doubles every attempt up to <see cref="MaxPollInterval"/>.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(DefaultPollIntervalMilliseconds);

    /// <summary>Ceiling for the exponential poll backoff and for honoured Retry-After values.</summary>
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromMilliseconds(DefaultMaxPollIntervalMilliseconds);

    /// <summary>Upper bound on one decoded API response body, in bytes. Enforced while reading.</summary>
    public int MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;

    /// <summary>Upper bound on items collected from one list operation, across pages.</summary>
    public int MaxListItems { get; init; } = DefaultMaxListItems;

    /// <summary>Maximum list pages walked. Hitting the cap fails loudly.</summary>
    public int MaxListPages { get; init; } = DefaultMaxListPages;

    /// <summary>
    /// Test hook: allow plain-http endpoints, but only for loopback hosts.
    /// Remote http URLs are refused even with this set.
    /// </summary>
    public bool AllowUnsafeHttp { get; init; }
}

/// <summary>Classified ARM failure kinds so the provider can defer (not fail) work items.</summary>
public enum AzureFailureKind
{
    Unauthorized,
    Forbidden,
    Throttled,
    QuotaExhausted,
    Conflict,
    NotFound,
    Unreachable,
    ServerError,
    InvalidResponse,
    Unexpected,
}

/// <summary>
/// Typed ARM failure. The bearer token never reaches the message: only the
/// operation name and a bounded, redacted slice of the error body are kept.
/// </summary>
public sealed class AzureApiException : Exception
{
    public AzureApiException(AzureFailureKind kind, string operation, string message, TimeSpan? retryAfter = null)
        : base($"Azure {operation} failed ({kind}): {message}")
    {
        Kind = kind;
        Operation = operation;
        RetryAfter = retryAfter;
    }

    public AzureFailureKind Kind { get; }

    public string Operation { get; }

    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Host-only ARM credentials. The token is read from the process environment
/// at call time and never logged, persisted, or sent anywhere except the
/// configured management origin as a bearer token.
/// </summary>
public sealed record AzureCredentials(Uri ManagementBaseUri, string SubscriptionId, string Token)
{
    /// <summary>Resolves credentials from options plus the host environment. Throws when unusable.</summary>
    public static AzureCredentials Resolve(AzureSandboxOptions options, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        if (string.IsNullOrWhiteSpace(options.SubscriptionId))
            throw new InvalidOperationException("Azure SubscriptionId must be set.");
        if (string.IsNullOrWhiteSpace(options.ResourceGroupName))
            throw new InvalidOperationException("Azure ResourceGroupName must be set.");
        if (!Uri.TryCreate(options.ManagementUrl, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException($"Azure ManagementUrl '{options.ManagementUrl}' is not an absolute URI.");
        var allowHttp = options.AllowUnsafeHttp && IsLoopbackHost(baseUri.Host);
        if (!string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowHttp || !string.Equals(baseUri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Azure ManagementUrl must use https.");
        }
        var envVar = string.IsNullOrWhiteSpace(options.TokenEnvVar) ? "AZURE_ACCESS_TOKEN" : options.TokenEnvVar;
        var token = environment(envVar);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"Azure bearer token is not available: environment variable '{envVar}' is empty. " +
                "Export a token (e.g. via `az account get-access-token`) without storing it in config files.");
        return new AzureCredentials(baseUri, AzureResourceIds.ValidateSegment(options.SubscriptionId, "SubscriptionId"), token);
    }

    internal static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(host, "::1", StringComparison.Ordinal)
        || string.Equals(host, "[::1]", StringComparison.Ordinal);
}

/// <summary>Deterministic owned ARM resource ids. One source of truth for every id the provider builds.</summary>
public static class AzureResourceIds
{
    public static string SubscriptionPrefix(AzureCredentials credentials) =>
        $"{credentials.ManagementBaseUri.AbsoluteUri.TrimEnd('/')}/subscriptions/{credentials.SubscriptionId}";

    public static string ResourceGroupPrefix(AzureCredentials credentials, string resourceGroup) =>
        $"{SubscriptionPrefix(credentials)}/resourceGroups/{ValidateSegment(resourceGroup, nameof(resourceGroup))}";

    public static string VmId(AzureCredentials credentials, string resourceGroup, string vmName) =>
        $"{ResourceGroupPrefix(credentials, resourceGroup)}/providers/Microsoft.Compute/virtualMachines/{ValidateSegment(vmName, nameof(vmName))}";

    public static string NicId(AzureCredentials credentials, string resourceGroup, string nicName) =>
        $"{ResourceGroupPrefix(credentials, resourceGroup)}/providers/Microsoft.Network/networkInterfaces/{ValidateSegment(nicName, nameof(nicName))}";

    public static string NsgId(AzureCredentials credentials, string resourceGroup, string nsgName) =>
        $"{ResourceGroupPrefix(credentials, resourceGroup)}/providers/Microsoft.Network/networkSecurityGroups/{ValidateSegment(nsgName, nameof(nsgName))}";

    public static string PublicIpId(AzureCredentials credentials, string resourceGroup, string pipName) =>
        $"{ResourceGroupPrefix(credentials, resourceGroup)}/providers/Microsoft.Network/publicIPAddresses/{ValidateSegment(pipName, nameof(pipName))}";

    public static string DiskId(AzureCredentials credentials, string resourceGroup, string diskName) =>
        $"{ResourceGroupPrefix(credentials, resourceGroup)}/providers/Microsoft.Compute/disks/{ValidateSegment(diskName, nameof(diskName))}";

    public static string SubnetId(
        AzureCredentials credentials, string resourceGroup, string vnetName, string subnetName) =>
        $"{ResourceGroupPrefix(credentials, resourceGroup)}/providers/Microsoft.Network/virtualNetworks/{ValidateSegment(vnetName, nameof(vnetName))}/subnets/{ValidateSegment(subnetName, nameof(subnetName))}";

    public static string VmsListUrl(AzureCredentials credentials, string resourceGroup, string apiVersion) =>
        $"{ResourceGroupPrefix(credentials, resourceGroup)}/providers/Microsoft.Compute/virtualMachines?api-version={Uri.EscapeDataString(apiVersion)}";

    internal static string ValidateSegment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Azure resource name '{name}' must not be blank.", name);
        var trimmed = value.Trim();
        if (trimmed.Any(ch => ch == '/' || ch == '?' || ch == '#' || char.IsControl(ch)))
            throw new ArgumentException($"Azure resource name '{name}' contains a forbidden character.", name);
        return trimmed;
    }
}

/// <summary>Parsed ARM virtual machine (only the fields the provider acts on).</summary>
public sealed record AzureVm(
    string Id,
    string Name,
    string? Location,
    string? ProvisioningState,
    string? VmSize,
    string? ImageVersion,
    IReadOnlyDictionary<string, string> Tags,
    string? NicId,
    string? OsDiskName);

/// <summary>Parsed ARM network interface.</summary>
public sealed record AzureNic(
    string Id,
    string Name,
    string? ProvisioningState,
    string? PrivateIpAddress,
    string? PublicIpId,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>Parsed ARM network security group.</summary>
public sealed record AzureNsg(
    string Id,
    string Name,
    string? ProvisioningState,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>Parsed ARM public IP address.</summary>
public sealed record AzurePublicIp(
    string Id,
    string Name,
    string? ProvisioningState,
    string? IpAddress,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>Parsed ARM managed disk.</summary>
public sealed record AzureDisk(
    string Id,
    string Name,
    string? ProvisioningState,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>
/// One ARM resource from a type listing: identity plus the attachment markers
/// the orphan sweep needs. <see cref="AttachedTo"/> is the first non-empty
/// attachment reference found (<c>virtualMachine.id</c>, <c>ipConfiguration.id</c>,
/// <c>managedBy</c>); <see cref="DiskState"/> carries <c>diskState</c> for disks.
/// </summary>
public sealed record AzureListedResource(
    string Id,
    string Name,
    IReadOnlyDictionary<string, string> Tags,
    string? AttachedTo,
    string? DiskState);

/// <summary>
/// Small typed ARM client: bearer-token auth, bounded responses, long-running
/// operation polling (Azure-AsyncOperation / Location with bounded
/// Retry-After), scope-validated poll/nextLink URLs, and final
/// provisioning-state verification. No provider logic lives here.
///
/// <para>Thread-safe: no mutable state; every method may run concurrently.
/// Cancellation is propagated to every request; nothing is fire-and-forget.
/// The bearer token never reaches an exception message.</para>
/// </summary>
public sealed class AzureApiClient
{
    /// <summary>Pinned Microsoft.Compute API version for VM and disk resources.</summary>
    public const string ComputeApiVersion = "2024-11-01";

    /// <summary>Pinned Microsoft.Network API version for NIC/NSG/public-IP resources.</summary>
    public const string NetworkApiVersion = "2024-05-01";

    internal const int MaxErrorBodyChars = 2048;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly AzureClientLimits _limits;

    public AzureApiClient(HttpClient http, TimeProvider? clock = null, AzureClientLimits? limits = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clock = clock ?? TimeProvider.System;
        _limits = limits ?? new AzureClientLimits();
    }

    // ------------------------------------------------------------------
    // Virtual machines
    // ------------------------------------------------------------------

    public async Task<AzureVm?> GetVmAsync(
        AzureCredentials credentials, string vmId, string apiVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        RequireInScopeResourceId(credentials, vmId, "get virtual machine");
        var (status, body) = await SendAsync(
            credentials, HttpMethod.Get, AppendApiVersion(vmId, apiVersion), content: null, "get virtual machine", ct,
            throwOnNotFound: false).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound)
            return null;
        return ParseVm(body);
    }

    public async Task<AzureVm> PutVmAsync(
        AzureCredentials credentials,
        string vmId,
        string apiVersion,
        string jsonBody,
        TimeSpan waitTimeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonBody);
        RequireInScopeResourceId(credentials, vmId, "create virtual machine");
        var url = AppendApiVersion(vmId, apiVersion);
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        var response = await SendRawAsync(credentials, request, "create virtual machine", ct).ConfigureAwait(false);
        return await CompleteLongRunningAsync(
            credentials, response, "create virtual machine", StripQuery(url), apiVersion, ParseVm, waitTimeout, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AzureVm>> ListVmsAsync(
        AzureCredentials credentials, string resourceGroup, string apiVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var url = AzureResourceIds.VmsListUrl(credentials, resourceGroup, apiVersion);
        var result = new List<AzureVm>();
        var pages = 0;
        while (url is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (++pages > _limits.MaxListPages)
                throw new AzureApiException(
                    AzureFailureKind.InvalidResponse, "list virtual machines",
                    $"paged past the {nameof(_limits.MaxListPages)} cap ({_limits.MaxListPages}); failing loudly instead of silently truncating inventory.");
            var (status, body) = await SendAsync(
                credentials, HttpMethod.Get, url, content: null, "list virtual machines", ct,
                throwOnNotFound: true).ConfigureAwait(false);
            _ = status;
            using var doc = ParseDocument(body, "list virtual machines");
            if (doc.RootElement.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (result.Count >= _limits.MaxListItems)
                        throw new AzureApiException(
                            AzureFailureKind.InvalidResponse, "list virtual machines",
                            $"collected past the {nameof(_limits.MaxListItems)} cap ({_limits.MaxListItems}); failing loudly instead of silently truncating inventory.");
                    result.Add(ParseVmElement(item));
                }
            }
            url = null;
            if (doc.RootElement.TryGetProperty("nextLink", out var next)
                && next.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(next.GetString()))
            {
                url = RequireInScopeUrl(credentials, next.GetString()!, "list virtual machines");
            }
        }
        return result;
    }

    /// <summary>
    /// Lists one ARM resource type in the resource group with pagination.
    /// Used by the orphan sweep; the caller filters by ownership and name.
    /// </summary>
    public async Task<IReadOnlyList<AzureListedResource>> ListResourcesAsync(
        AzureCredentials credentials,
        string resourceGroup,
        string providerNamespace,
        string resourceType,
        string apiVersion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        foreach (var (value, name) in new[] { (providerNamespace, nameof(providerNamespace)), (resourceType, nameof(resourceType)) })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(ch => ch == '/' || ch == '?' || char.IsControl(ch)))
                throw new ArgumentException($"ARM path segment '{name}' is blank or contains a forbidden character.", name);
        }
        var url = $"{AzureResourceIds.ResourceGroupPrefix(credentials, resourceGroup)}/providers/{providerNamespace.Trim()}/{resourceType.Trim()}?api-version={Uri.EscapeDataString(apiVersion)}";
        var result = new List<AzureListedResource>();
        var pages = 0;
        while (url is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (++pages > _limits.MaxListPages)
                throw new AzureApiException(
                    AzureFailureKind.InvalidResponse, "list " + resourceType,
                    $"paged past the {nameof(_limits.MaxListPages)} cap ({_limits.MaxListPages}); failing loudly instead of silently truncating inventory.");
            var (status, body) = await SendAsync(
                credentials, HttpMethod.Get, url, content: null, "list " + resourceType, ct,
                throwOnNotFound: true).ConfigureAwait(false);
            _ = status;
            using var doc = ParseDocument(body, "list " + resourceType);
            if (doc.RootElement.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (result.Count >= _limits.MaxListItems)
                        throw new AzureApiException(
                            AzureFailureKind.InvalidResponse, "list " + resourceType,
                            $"collected past the {nameof(_limits.MaxListItems)} cap ({_limits.MaxListItems}); failing loudly instead of silently truncating inventory.");
                    result.Add(ParseListedResource(item));
                }
            }
            url = null;
            if (doc.RootElement.TryGetProperty("nextLink", out var next)
                && next.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(next.GetString()))
            {
                url = RequireInScopeUrl(credentials, next.GetString()!, "list " + resourceType);
            }
        }
        return result;
    }

    internal static AzureListedResource ParseListedResource(JsonElement item)
    {
        var id = RequiredString(item, "id");
        var name = OptionalString(item, "name") ?? LastSegment(id);
        string? attached = null;
        string? diskState = null;
        if (item.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            attached = FirstReferenceId(props, "virtualMachine")
                ?? FirstReferenceId(props, "ipConfiguration")
                ?? OptionalString(props, "managedBy");
            diskState = OptionalString(props, "diskState");
        }
        return new AzureListedResource(id, name, ReadTags(item), attached, diskState);
    }

    private static string? FirstReferenceId(JsonElement props, string name)
    {
        if (props.TryGetProperty(name, out var reference) && reference.ValueKind == JsonValueKind.Object)
            return OptionalString(reference, "id");
        return null;
    }

    // ------------------------------------------------------------------
    // Network + disks (generic GET/PUT by id; parsed into typed records)
    // ------------------------------------------------------------------

    public async Task<AzureNic?> GetNicAsync(
        AzureCredentials credentials, string nicId, string apiVersion, CancellationToken ct)
    {
        RequireInScopeResourceId(credentials, nicId, "get network interface");
        var (status, body) = await SendAsync(
            credentials, HttpMethod.Get, AppendApiVersion(nicId, apiVersion), content: null, "get network interface", ct,
            throwOnNotFound: false).ConfigureAwait(false);
        return status == HttpStatusCode.NotFound ? null : ParseNic(body);
    }

    public async Task<AzureNic> PutNicAsync(
        AzureCredentials credentials,
        string nicId,
        string apiVersion,
        string jsonBody,
        TimeSpan waitTimeout,
        CancellationToken ct)
    {
        RequireInScopeResourceId(credentials, nicId, "create network interface");
        var url = AppendApiVersion(nicId, apiVersion);
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        var response = await SendRawAsync(credentials, request, "create network interface", ct).ConfigureAwait(false);
        return await CompleteLongRunningAsync(
            credentials, response, "create network interface", StripQuery(url), apiVersion, ParseNic, waitTimeout, ct).ConfigureAwait(false);
    }

    public async Task<AzureNsg?> GetNsgAsync(
        AzureCredentials credentials, string nsgId, string apiVersion, CancellationToken ct)
    {
        RequireInScopeResourceId(credentials, nsgId, "get network security group");
        var (status, body) = await SendAsync(
            credentials, HttpMethod.Get, AppendApiVersion(nsgId, apiVersion), content: null, "get network security group", ct,
            throwOnNotFound: false).ConfigureAwait(false);
        return status == HttpStatusCode.NotFound ? null : ParseNsg(body);
    }

    public async Task<AzureNsg> PutNsgAsync(
        AzureCredentials credentials,
        string nsgId,
        string apiVersion,
        string jsonBody,
        TimeSpan waitTimeout,
        CancellationToken ct)
    {
        RequireInScopeResourceId(credentials, nsgId, "create network security group");
        var url = AppendApiVersion(nsgId, apiVersion);
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        var response = await SendRawAsync(credentials, request, "create network security group", ct).ConfigureAwait(false);
        return await CompleteLongRunningAsync(
            credentials, response, "create network security group", StripQuery(url), apiVersion, ParseNsg, waitTimeout, ct).ConfigureAwait(false);
    }

    public async Task<AzurePublicIp?> GetPublicIpAsync(
        AzureCredentials credentials, string pipId, string apiVersion, CancellationToken ct)
    {
        RequireInScopeResourceId(credentials, pipId, "get public IP address");
        var (status, body) = await SendAsync(
            credentials, HttpMethod.Get, AppendApiVersion(pipId, apiVersion), content: null, "get public IP address", ct,
            throwOnNotFound: false).ConfigureAwait(false);
        return status == HttpStatusCode.NotFound ? null : ParsePublicIp(body);
    }

    public async Task<AzurePublicIp> PutPublicIpAsync(
        AzureCredentials credentials,
        string pipId,
        string apiVersion,
        string jsonBody,
        TimeSpan waitTimeout,
        CancellationToken ct)
    {
        RequireInScopeResourceId(credentials, pipId, "create public IP address");
        var url = AppendApiVersion(pipId, apiVersion);
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        var response = await SendRawAsync(credentials, request, "create public IP address", ct).ConfigureAwait(false);
        return await CompleteLongRunningAsync(
            credentials, response, "create public IP address", StripQuery(url), apiVersion, ParsePublicIp, waitTimeout, ct).ConfigureAwait(false);
    }

    public async Task<AzureDisk?> GetDiskAsync(
        AzureCredentials credentials, string diskId, string apiVersion, CancellationToken ct)
    {
        RequireInScopeResourceId(credentials, diskId, "get managed disk");
        var (status, body) = await SendAsync(
            credentials, HttpMethod.Get, AppendApiVersion(diskId, apiVersion), content: null, "get managed disk", ct,
            throwOnNotFound: false).ConfigureAwait(false);
        return status == HttpStatusCode.NotFound ? null : ParseDisk(body);
    }

    /// <summary>
    /// Deletes any ARM resource by id. Returns true when the resource is gone
    /// (deleted or already absent — 404 is success). A 202-accepted deletion
    /// is polled to a terminal state and re-verified with GET: an accepted
    /// request alone never counts as deletion.
    /// </summary>
    public async Task<bool> DeleteByIdAsync(
        AzureCredentials credentials,
        string resourceId,
        string apiVersion,
        string operation,
        TimeSpan waitTimeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        RequireInScopeResourceId(credentials, resourceId, operation);
        var url = AppendApiVersion(resourceId, apiVersion);
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        HttpResponseMessage response = await SendRawAsync(credentials, request, operation, ct).ConfigureAwait(false);
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return true;
            ThrowIfError(response, operation, await ReadBodyAsync(response, operation, ct).ConfigureAwait(false));
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                await PollOperationAsync(
                    credentials, HeaderUrl(response, "Azure-AsyncOperation"),
                    HeaderUrl(response, "Location"), StripQuery(url), apiVersion,
                    operation, waitTimeout, ct).ConfigureAwait(false);
            }
        }
        // Re-verify: the resource must actually be gone before we report success.
        var (status, _) = await SendAsync(
            credentials, HttpMethod.Get, url, content: null, operation + " (verify delete)", ct,
            throwOnNotFound: false).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound)
            return true;
        throw new AzureApiException(
            AzureFailureKind.Unexpected, operation,
            "deletion was accepted but the resource is still present; identity retained for reconciliation.");
    }

    // ------------------------------------------------------------------
    // Long-running operations
    // ------------------------------------------------------------------

    private async Task<T> CompleteLongRunningAsync<T>(
        AzureCredentials credentials,
        HttpResponseMessage response,
        string operation,
        string resourceUrl,
        string apiVersion,
        Func<string, T> parse,
        TimeSpan waitTimeout,
        CancellationToken ct) where T : class
    {
        using (response)
        {
            var body = await ReadBodyAsync(response, operation, ct).ConfigureAwait(false);
            ThrowIfError(response, operation, body);
            var asyncUrl = HeaderUrl(response, "Azure-AsyncOperation");
            var locationUrl = HeaderUrl(response, "Location");
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
                && asyncUrl is null && locationUrl is null)
            {
                return RequireSucceeded(parse(body), operation);
            }
            await PollOperationAsync(credentials, asyncUrl, locationUrl, resourceUrl, apiVersion, operation, waitTimeout, ct).ConfigureAwait(false);
        }
        // Final state comes from the resource itself, never from the poll payload alone.
        var (_, finalBody) = await SendAsync(
            credentials, HttpMethod.Get, AppendApiVersion(resourceUrl, apiVersion), content: null, operation + " (verify provisioning)", ct,
            throwOnNotFound: true).ConfigureAwait(false);
        return RequireSucceeded(parse(finalBody), operation);
    }

    private async Task PollOperationAsync(
        AzureCredentials credentials,
        string? asyncOperationUrl,
        string? locationUrl,
        string resourceUrl,
        string apiVersion,
        string operation,
        TimeSpan waitTimeout,
        CancellationToken ct)
    {
        if (asyncOperationUrl is not null)
            asyncOperationUrl = RequireInScopeUrl(credentials, asyncOperationUrl, operation);
        if (locationUrl is not null)
            locationUrl = RequireInScopeUrl(credentials, locationUrl, operation);
        if (asyncOperationUrl is null && locationUrl is null)
        {
            // No poll URL: fall back to polling the resource itself via provisioningState.
            await PollResourceStateAsync(credentials, resourceUrl, apiVersion, operation, waitTimeout, ct).ConfigureAwait(false);
            return;
        }
        var deadline = _clock.GetUtcNow() + waitTimeout;
        var attempt = 0;
        var pollUrl = asyncOperationUrl ?? locationUrl!;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_clock.GetUtcNow() >= deadline)
                throw new AzureApiException(
                    AzureFailureKind.Unexpected, operation,
                    "long-running operation did not reach a terminal state before the deadline; outcome unknown — reconcile by resource identity.");
            TimeSpan? serverDelay = null;
            string status;
            using (var request = new HttpRequestMessage(HttpMethod.Get, pollUrl))
            {
                var pollResponse = await SendRawAsync(credentials, request, operation + " (poll)", ct).ConfigureAwait(false);
                using (pollResponse)
                {
                    serverDelay = ParseRetryAfter(pollResponse);
                    var pollBody = await ReadBodyAsync(pollResponse, operation + " (poll)", ct).ConfigureAwait(false);
                    if (asyncOperationUrl is not null)
                    {
                        status = ParseAsyncOperationStatus(pollBody, operation);
                    }
                    else
                    {
                        if (pollResponse.StatusCode != HttpStatusCode.Accepted)
                        {
                            ThrowIfError(pollResponse, operation + " (poll)", pollBody);
                            return;
                        }
                        status = "InProgress";
                        var next = HeaderUrl(pollResponse, "Location") ?? HeaderUrl(pollResponse, "Azure-AsyncOperation");
                        if (next is not null)
                            pollUrl = RequireInScopeUrl(credentials, next, operation);
                    }
                }
            }
            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                return;
            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new AzureApiException(
                    AzureFailureKind.Unexpected, operation,
                    $"long-running operation ended in '{status}'; reconciling by resource identity.");
            }
            await Task.Delay(ClampDelay(serverDelay, attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    private async Task PollResourceStateAsync(
        AzureCredentials credentials, string resourceUrl, string apiVersion, string operation, TimeSpan waitTimeout, CancellationToken ct)
    {
        var pollUrl = AppendApiVersion(resourceUrl, apiVersion);
        var deadline = _clock.GetUtcNow() + waitTimeout;
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (_, body) = await SendAsync(
                credentials, HttpMethod.Get, pollUrl, content: null, operation + " (poll state)", ct,
                throwOnNotFound: false).ConfigureAwait(false);
            var state = TryReadProvisioningState(body);
            if (string.Equals(state, "Succeeded", StringComparison.OrdinalIgnoreCase))
                return;
            if (string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "Canceled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new AzureApiException(
                    AzureFailureKind.Unexpected, operation,
                    $"resource provisioning state is '{state}'; reconciling by resource identity.");
            }
            if (_clock.GetUtcNow() >= deadline)
                throw new AzureApiException(
                    AzureFailureKind.Unexpected, operation,
                    "resource did not reach Succeeded before the deadline; outcome unknown — reconcile by resource identity.");
            await Task.Delay(NextPollDelay(attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // HTTP plumbing
    // ------------------------------------------------------------------

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        AzureCredentials credentials,
        HttpMethod method,
        string url,
        HttpContent? content,
        string operation,
        CancellationToken ct,
        bool throwOnNotFound)
    {
        using var request = new HttpRequestMessage(method, url);
        if (content is not null)
            request.Content = content;
        var response = await SendRawAsync(credentials, request, operation, ct).ConfigureAwait(false);
        using (response)
        {
            var body = await ReadBodyAsync(response, operation, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound && !throwOnNotFound)
                return (response.StatusCode, body);
            ThrowIfError(response, operation, body);
            return (response.StatusCode, body);
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        AzureCredentials credentials, HttpRequestMessage request, string operation, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_limits.HttpTimeout);
        try
        {
            return await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AzureApiException(AzureFailureKind.Unreachable, operation, "request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new AzureApiException(AzureFailureKind.Unreachable, operation, $"request failed: {TrimError(ex.Message)}");
        }
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var content = response.Content;
        if (content is null)
            return string.Empty;
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var cap = (long)_limits.MaxResponseBytes + 1;
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > cap)
                throw new AzureApiException(
                    AzureFailureKind.InvalidResponse, operation,
                    $"response exceeded the {nameof(_limits.MaxResponseBytes)} cap ({_limits.MaxResponseBytes} bytes).");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private void ThrowIfError(HttpResponseMessage response, string operation, string body)
    {
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300)
            return;
        var detail = TrimError(ExtractErrorDetail(body));
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new AzureApiException(AzureFailureKind.Unauthorized, operation, detail);
            case HttpStatusCode.Forbidden:
                throw new AzureApiException(AzureFailureKind.Forbidden, operation, detail);
            case HttpStatusCode.NotFound:
                throw new AzureApiException(AzureFailureKind.NotFound, operation, detail);
            case (HttpStatusCode)429:
                throw new AzureApiException(
                    AzureFailureKind.Throttled, operation, detail, ClampDelay(ParseRetryAfter(response), 0));
            case HttpStatusCode.Conflict:
                throw new AzureApiException(ToConflictKind(body), operation, detail);
            default:
                if (status >= 500)
                    throw new AzureApiException(AzureFailureKind.ServerError, operation, detail);
                if (status == 400 && LooksLikeQuotaError(body))
                    throw new AzureApiException(AzureFailureKind.QuotaExhausted, operation, detail);
                throw new AzureApiException(AzureFailureKind.Unexpected, operation, $"HTTP {status}: {detail}");
        }
    }

    private static AzureFailureKind ToConflictKind(string body) =>
        LooksLikeQuotaError(body) ? AzureFailureKind.QuotaExhausted : AzureFailureKind.Conflict;

    private static bool LooksLikeQuotaError(string body) =>
        body.Contains("QuotaExceeded", StringComparison.OrdinalIgnoreCase)
        || body.Contains("OperationNotAllowed", StringComparison.OrdinalIgnoreCase)
        || body.Contains("SkuNotAvailable", StringComparison.OrdinalIgnoreCase)
        || body.Contains("quota", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------
    // Parsing
    // ------------------------------------------------------------------

    internal static AzureVm ParseVm(string body) => ParseVmElement(ParseDocument(body, "parse virtual machine").RootElement);

    internal static AzureVm ParseVmElement(JsonElement element)
    {
        var id = RequiredString(element, "id");
        var name = element.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString() ?? LastSegment(id)
            : LastSegment(id);
        var tags = ReadTags(element);
        string? location = OptionalString(element, "location");
        string? provisioningState = null;
        string? vmSize = null;
        string? imageVersion = null;
        string? nicId = null;
        string? osDiskName = null;
        if (element.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            provisioningState = OptionalString(props, "provisioningState");
            if (props.TryGetProperty("hardwareProfile", out var hw) && hw.ValueKind == JsonValueKind.Object)
                vmSize = OptionalString(hw, "vmSize");
            if (props.TryGetProperty("storageProfile", out var storage) && storage.ValueKind == JsonValueKind.Object)
            {
                if (storage.TryGetProperty("imageReference", out var image) && image.ValueKind == JsonValueKind.Object)
                    imageVersion = OptionalString(image, "version") ?? OptionalString(image, "exactVersion");
                if (storage.TryGetProperty("osDisk", out var osDisk) && osDisk.ValueKind == JsonValueKind.Object)
                {
                    if (osDisk.TryGetProperty("managedDisk", out var managed) && managed.ValueKind == JsonValueKind.Object)
                    {
                        var diskId = OptionalString(managed, "id");
                        osDiskName = diskId is null ? OptionalString(osDisk, "name") : LastSegment(diskId);
                    }
                    else
                    {
                        osDiskName = OptionalString(osDisk, "name");
                    }
                }
            }
            if (props.TryGetProperty("networkProfile", out var net) && net.ValueKind == JsonValueKind.Object
                && net.TryGetProperty("networkInterfaces", out var nics) && nics.ValueKind == JsonValueKind.Array)
            {
                foreach (var nic in nics.EnumerateArray())
                {
                    nicId = OptionalString(nic, "id");
                    if (nicId is not null)
                        break;
                }
            }
        }
        return new AzureVm(id, name, location, provisioningState, vmSize, imageVersion, tags, nicId, osDiskName);
    }

    internal static AzureNic ParseNic(string body)
    {
        var root = ParseDocument(body, "parse network interface").RootElement;
        var id = RequiredString(root, "id");
        var name = OptionalString(root, "name") ?? LastSegment(id);
        string? state = null;
        string? privateIp = null;
        string? pipId = null;
        if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            state = OptionalString(props, "provisioningState");
            if (props.TryGetProperty("ipConfigurations", out var configs) && configs.ValueKind == JsonValueKind.Array)
            {
                foreach (var config in configs.EnumerateArray())
                {
                    if (config.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!config.TryGetProperty("properties", out var cp) || cp.ValueKind != JsonValueKind.Object)
                        continue;
                    privateIp ??= OptionalString(cp, "privateIPAddress");
                    if (cp.TryGetProperty("publicIPAddress", out var pip) && pip.ValueKind == JsonValueKind.Object)
                        pipId ??= OptionalString(pip, "id");
                }
            }
        }
        return new AzureNic(id, name, state, privateIp, pipId, ReadTags(root));
    }

    internal static AzureNsg ParseNsg(string body)
    {
        var root = ParseDocument(body, "parse network security group").RootElement;
        var id = RequiredString(root, "id");
        var name = OptionalString(root, "name") ?? LastSegment(id);
        string? state = null;
        if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            state = OptionalString(props, "provisioningState");
        return new AzureNsg(id, name, state, ReadTags(root));
    }

    internal static AzurePublicIp ParsePublicIp(string body)
    {
        var root = ParseDocument(body, "parse public IP address").RootElement;
        var id = RequiredString(root, "id");
        var name = OptionalString(root, "name") ?? LastSegment(id);
        string? state = null;
        string? address = null;
        if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            state = OptionalString(props, "provisioningState");
            address = OptionalString(props, "ipAddress");
        }
        return new AzurePublicIp(id, name, state, address, ReadTags(root));
    }

    internal static AzureDisk ParseDisk(string body)
    {
        var root = ParseDocument(body, "parse managed disk").RootElement;
        var id = RequiredString(root, "id");
        var name = OptionalString(root, "name") ?? LastSegment(id);
        string? state = null;
        if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            state = OptionalString(props, "provisioningState");
        return new AzureDisk(id, name, state, ReadTags(root));
    }

    internal static IReadOnlyDictionary<string, string> ReadTags(JsonElement element)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in tagsEl.EnumerateObject())
            {
                if (tags.Count >= 256)
                    break;
                tags[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? (property.Value.GetString() ?? string.Empty)
                    : property.Value.ToString();
            }
        }
        return tags;
    }

    private static T RequireSucceeded<T>(T resource, string operation) where T : class
    {
        var state = resource switch
        {
            AzureVm vm => vm.ProvisioningState,
            AzureNic nic => nic.ProvisioningState,
            AzureNsg nsg => nsg.ProvisioningState,
            AzurePublicIp pip => pip.ProvisioningState,
            AzureDisk disk => disk.ProvisioningState,
            _ => null,
        };
        if (!string.Equals(state, "Succeeded", StringComparison.OrdinalIgnoreCase))
        {
            throw new AzureApiException(
                AzureFailureKind.Unexpected, operation,
                $"resource provisioning state is '{state ?? "(missing)"}', not Succeeded; reconciling by resource identity.");
        }
        return resource;
    }

    private static string ParseAsyncOperationStatus(string body, string operation)
    {
        using var doc = ParseDocument(body, operation);
        if (doc.RootElement.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(status.GetString()))
        {
            return status.GetString()!;
        }
        throw new AzureApiException(
            AzureFailureKind.InvalidResponse, operation,
            "async-operation poll body carries no status; outcome unknown — reconcile by resource identity.");
    }

    private static string? TryReadProvisioningState(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("properties", out var props)
                && props.ValueKind == JsonValueKind.Object
                && props.TryGetProperty("provisioningState", out var state)
                && state.ValueKind == JsonValueKind.String)
            {
                return state.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    internal static JsonDocument ParseDocument(string body, string operation)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new AzureApiException(
                AzureFailureKind.InvalidResponse, operation,
                $"response is not valid JSON: {TrimError(ex.Message)}");
        }
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }
        throw new AzureApiException(
            AzureFailureKind.InvalidResponse, "parse resource",
            $"response is missing required string property '{name}'.");
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string LastSegment(string id)
    {
        var trimmed = id.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    private static string ExtractErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "empty error body";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = OptionalString(error, "code");
                var message = OptionalString(error, "message");
                var combined = string.Join(" ", new[] { code, message }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (!string.IsNullOrWhiteSpace(combined))
                    return combined;
            }
        }
        catch (JsonException)
        {
        }
        return body;
    }

    internal static string TrimError(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "empty error body";
        var trimmed = detail.Trim();
        return trimmed.Length <= MaxErrorBodyChars ? trimmed : trimmed[..MaxErrorBodyChars];
    }

    // ------------------------------------------------------------------
    // URL helpers
    // ------------------------------------------------------------------

    internal static string AppendApiVersion(string resourceId, string apiVersion) =>
        resourceId.Contains('?')
            ? resourceId + "&api-version=" + Uri.EscapeDataString(apiVersion)
            : resourceId + "?api-version=" + Uri.EscapeDataString(apiVersion);

    internal static string StripQuery(string url)
    {
        var index = url.IndexOf('?');
        return index < 0 ? url : url[..index];
    }

    /// <summary>
    /// Refuses to follow a server-returned URL that leaves the configured
    /// management origin or subscription scope (poll and nextLink URLs are
    /// untrusted remote input aimed at a sink that carries the bearer token).
    /// </summary>
    internal string RequireInScopeUrl(AzureCredentials credentials, string url, string operation)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(url))
            throw new AzureApiException(AzureFailureKind.InvalidResponse, operation, "server returned an empty poll URL; refusing to follow it.");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var candidate))
            throw new AzureApiException(AzureFailureKind.InvalidResponse, operation, "server returned a relative poll URL; refusing to follow it.");
        AssertInScope(credentials, candidate, operation, "poll URL");
        return url;
    }

    /// <summary>
    /// Refuses to send the bearer token to a cloud-returned resource id that
    /// leaves the configured management origin or subscription scope.
    /// ARM <c>id</c> fields (VM NIC references, NIC public-IP references,
    /// listed-resource ids) are dependency runtime output and therefore
    /// untrusted: every id reaches a bearer-token sink only through this
    /// guard, which runs inside the sink methods themselves so future callers
    /// inherit the check.
    /// </summary>
    internal string RequireInScopeResourceId(AzureCredentials credentials, string resourceId, string operation)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(resourceId))
            throw new AzureApiException(AzureFailureKind.InvalidResponse, operation, "server returned an empty resource id; refusing to follow it.");
        var stripped = StripQuery(resourceId.Trim());
        if (!Uri.TryCreate(stripped, UriKind.Absolute, out var candidate))
            throw new AzureApiException(AzureFailureKind.InvalidResponse, operation, "server returned a relative resource id; refusing to follow it.");
        AssertInScope(credentials, candidate, operation, "resource id");
        return resourceId;
    }

    private static void AssertInScope(AzureCredentials credentials, Uri candidate, string operation, string label)
    {
        var baseUri = credentials.ManagementBaseUri;
        if (!string.Equals(candidate.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != baseUri.Port)
        {
            throw new AzureApiException(
                AzureFailureKind.InvalidResponse, operation,
                $"server returned a {label} outside the configured management origin; refusing to follow it.");
        }
        var scopePrefix = $"/subscriptions/{credentials.SubscriptionId}/";
        if (!candidate.AbsolutePath.StartsWith(scopePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new AzureApiException(
                AzureFailureKind.InvalidResponse, operation,
                $"server returned a {label} outside the configured subscription scope; refusing to follow it.");
        }
    }

    private static string? HeaderUrl(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
        }
        return null;
    }

    private TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - _clock.GetUtcNow();
            if (delay > TimeSpan.Zero)
                return delay;
        }
        return null;
    }

    private TimeSpan ClampDelay(TimeSpan? serverDelay, int attempt)
    {
        if (serverDelay is { } retryAfter && retryAfter > TimeSpan.Zero)
            return retryAfter > _limits.MaxPollInterval ? _limits.MaxPollInterval : retryAfter;
        return NextPollDelay(attempt);
    }

    private TimeSpan NextPollDelay(int attempt)
    {
        var baseMs = Math.Max(1.0, _limits.PollInterval.TotalMilliseconds);
        var doubled = baseMs * (1L << Math.Min(attempt, 10));
        var capped = Math.Min(doubled, Math.Max(1.0, _limits.MaxPollInterval.TotalMilliseconds));
        return TimeSpan.FromMilliseconds(capped);
    }
}
