using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CodeyBox.Ec2SandboxPlugin;

/// <summary>Bounds for one <see cref="Ec2ApiClient"/>. Every cap is operator-configured.</summary>
public sealed record Ec2ClientLimits
{
    public const int DefaultHttpTimeoutSeconds = 60;
    public const int DefaultPollIntervalMilliseconds = 2000;
    public const int DefaultMaxPollIntervalMilliseconds = 15000;
    public const int DefaultMaxResponseBytes = 8 * 1024 * 1024;
    public const int DefaultMaxListItems = 5000;
    public const int DefaultMaxListPages = 100;

    /// <summary>
    /// Default ceiling for raw user-data bytes. Mirrors the EC2 16 KiB
    /// user-data limit: change both together.
    /// </summary>
    public const int DefaultMaxUserDataBytes = 16 * 1024;

    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(DefaultHttpTimeoutSeconds);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(DefaultPollIntervalMilliseconds);
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromMilliseconds(DefaultMaxPollIntervalMilliseconds);
    public int MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;
    public int MaxListItems { get; init; } = DefaultMaxListItems;
    public int MaxListPages { get; init; } = DefaultMaxListPages;
    public int MaxUserDataBytes { get; init; } = DefaultMaxUserDataBytes;
    public bool AllowUnsafeHttp { get; init; }
}

/// <summary>Instance record (subset the provider consumes).</summary>
public sealed class Ec2Instance
{
    public string? InstanceId { get; set; }
    public string? State { get; set; }
    public string? ImageId { get; set; }
    public string? InstanceType { get; set; }
    public string? SubnetId { get; set; }
    public string? VpcId { get; set; }
    public string? KeyName { get; set; }
    public string? PublicIpAddress { get; set; }
    public string? PrivateIpAddress { get; set; }
    public string? PublicDnsName { get; set; }
    public List<string>? SecurityGroupIds { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
}

/// <summary>Image record (subset the provider consumes for the AMI pin check).</summary>
public sealed class Ec2Image
{
    public string? ImageId { get; set; }
    public string? State { get; set; }
    public string? Name { get; set; }
    public string? Architecture { get; set; }
    public string? RootDeviceName { get; set; }
}

/// <summary>Key-pair record (subset the provider consumes).</summary>
public sealed class Ec2KeyPairInfo
{
    public string? KeyName { get; set; }
    public string? KeyFingerprint { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
}

/// <summary>Security-group record (subset the provider consumes).</summary>
public sealed class Ec2SecurityGroup
{
    public string? GroupId { get; set; }
    public string? GroupName { get; set; }
    public string? VpcId { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
}

/// <summary>Elastic-IP record (subset the provider consumes).</summary>
public sealed class Ec2Address
{
    public string? AllocationId { get; set; }
    public string? AssociationId { get; set; }
    public string? PublicIp { get; set; }
    public string? InstanceId { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
}

/// <summary>EBS-volume record (subset the provider consumes).</summary>
public sealed class Ec2Volume
{
    public string? VolumeId { get; set; }
    public string? State { get; set; }
    public string? InstanceId { get; set; }
    public string? Device { get; set; }
    public bool DeleteOnTermination { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
}

/// <summary>Ingress/egress permission the provider authorizes on a per-sandbox security group.</summary>
public sealed record Ec2IpPermission(
    string IpProtocol,
    int FromPort,
    int ToPort,
    IReadOnlyList<string> CidrRanges,
    IReadOnlyList<string> Cidr6Ranges)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(IpProtocol);
        ArgumentNullException.ThrowIfNull(CidrRanges);
        ArgumentNullException.ThrowIfNull(Cidr6Ranges);
        if (CidrRanges.Count + Cidr6Ranges.Count == 0)
            throw new ArgumentException("An IP permission must carry at least one CIDR range.", nameof(CidrRanges));
        foreach (var cidr in CidrRanges.Concat(Cidr6Ranges))
        {
            if (string.IsNullOrWhiteSpace(cidr) || !cidr.Contains('/'))
                throw new ArgumentException($"'{cidr}' is not a CIDR (address/bits).", nameof(CidrRanges));
        }
    }
};

/// <summary>Body for <c>RunInstances</c>. User-data bytes are capped at the sink.</summary>
public sealed record Ec2RunSpec(
    string AmiId,
    string InstanceType,
    string SubnetId,
    IReadOnlyList<string> SecurityGroupIds,
    string KeyName,
    string UserData,
    string InstanceName,
    IReadOnlyDictionary<string, string> Tags,
    int VolumeSizeGb,
    string VolumeType,
    string RootDeviceName,
    bool DeleteOnTermination,
    bool AssociatePublicIp,
    bool EnableInstanceMetadata,
    string? AvailabilityZone)
{
    public void Validate(int maxUserDataBytes)
    {
        if (!Ec2Placement.IsValidAmiId(AmiId))
            throw new ArgumentException($"AMI id '{AmiId}' must be a concrete ami- id pin.", nameof(AmiId));
        if (!Ec2Placement.IsValidInstanceType(InstanceType))
            throw new ArgumentException($"Instance type '{InstanceType}' is malformed.", nameof(InstanceType));
        if (!Ec2Placement.IsValidSubnetId(SubnetId))
            throw new ArgumentException($"Subnet id '{SubnetId}' is malformed.", nameof(SubnetId));
        ArgumentException.ThrowIfNullOrWhiteSpace(KeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(UserData);
        ArgumentException.ThrowIfNullOrWhiteSpace(InstanceName);
        ArgumentNullException.ThrowIfNull(Tags);
        ArgumentNullException.ThrowIfNull(SecurityGroupIds);
        foreach (var groupId in SecurityGroupIds)
        {
            if (!Ec2Placement.IsValidSecurityGroupId(groupId))
                throw new ArgumentException($"Security group id '{groupId}' is malformed.", nameof(SecurityGroupIds));
        }
        if (VolumeSizeGb < 8 || VolumeSizeGb > 512)
            throw new ArgumentOutOfRangeException(nameof(VolumeSizeGb), "Root volume size must be 8–512 GiB.");
        if (Ec2Placement.AllowedVolumeTypes.Contains(VolumeType) != true)
            throw new ArgumentException($"Volume type '{VolumeType}' is not in the allowlist.", nameof(VolumeType));
        ArgumentException.ThrowIfNullOrWhiteSpace(RootDeviceName);
        var bytes = Encoding.UTF8.GetByteCount(UserData);
        if (bytes > maxUserDataBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(UserData),
                $"User-data is {bytes} bytes; the cap is {maxUserDataBytes}. Refusing to truncate guest bootstrap.");
        }
    }
};

/// <summary>
/// Minimal EC2 Query client over an injected <see cref="HttpClient"/> (a fake
/// handler in tests; never a live account outside operator-run tooling).
/// Every request is a SigV4-signed POST carrying
/// <c>Action</c>/<c>Version</c> form fields against the EC2 Query API
/// (<c>https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_RunInstances.html</c>).
/// All responses are byte-bounded before buffering, paged Describe calls are
/// page/item-bounded, instance waits poll until a terminal state, and
/// failures keep their auth/quota/throttle/transient distinctions.
/// </summary>
public sealed class Ec2ApiClient
{
    // Error bodies are untrusted remote text: bounded before they are folded
    // into exception messages and logs.
    internal const int MaxErrorBodyChars = 2048;

    // Protocol sanity bounds (not operator knobs): a single create call must
    // stay a single call — these reject caller bugs, not cloud limits.
    internal const int MaxTagEntries = 64;
    internal const int MaxTagChars = 256;
    internal const int DescribePageSize = 100;

    /// <summary>
    /// Fallback ceiling for status waits when the caller passes no timeout.
    /// Mirrors <c>Ec2SandboxOptions.ReadyTimeoutSeconds</c> default 600:
    /// change both together.
    /// </summary>
    internal static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(600);

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly Ec2ClientLimits _limits;

    /// <summary>Creates a client over an injected <see cref="HttpClient"/> (test seam for fakes).</summary>
    public Ec2ApiClient(HttpClient http, TimeProvider? clock = null, Ec2ClientLimits? limits = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clock = clock ?? TimeProvider.System;
        _limits = limits ?? new Ec2ClientLimits();
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
    // Instances
    // ------------------------------------------------------------------

    /// <summary>
    /// Launches exactly one instance. <paramref name="clientToken"/> must be
    /// stable per provisioning attempt: retries after an unknown outcome reuse
    /// the same token so the service dedupes instead of double-provisioning,
    /// and the token is also stamped as a tag for client-side reconciliation.
    /// Returns the launched instance id.
    /// </summary>
    public async Task<string> RunInstancesAsync(
        Ec2Credentials credentials,
        Ec2RunSpec spec,
        string clientToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientToken);
        spec.Validate(_limits.MaxUserDataBytes);
        ValidateTags(spec.Tags);

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("ImageId", spec.AmiId.Trim()),
            new("InstanceType", spec.InstanceType.Trim()),
            new("MinCount", "1"),
            new("MaxCount", "1"),
            new("ClientToken", clientToken.Trim()),
            new("KeyName", spec.KeyName),
            new("UserData", Convert.ToBase64String(Encoding.UTF8.GetBytes(spec.UserData))),
            new("NetworkInterface.1.DeviceIndex", "0"),
            new("NetworkInterface.1.SubnetId", spec.SubnetId.Trim()),
            new("NetworkInterface.1.AssociatePublicIpAddress", spec.AssociatePublicIp ? "true" : "false"),
            new("NetworkInterface.1.DeleteOnTermination", "true"),
            new("BlockDeviceMapping.1.DeviceName", spec.RootDeviceName.Trim()),
            new("BlockDeviceMapping.1.Ebs.VolumeSize", spec.VolumeSizeGb.ToString(CultureInfo.InvariantCulture)),
            new("BlockDeviceMapping.1.Ebs.VolumeType", spec.VolumeType.Trim()),
            new("BlockDeviceMapping.1.Ebs.DeleteOnTermination", spec.DeleteOnTermination ? "true" : "false"),
            new("MetadataOptions.HttpTokens", "require"),
            new("MetadataOptions.HttpPutResponseHopLimit", "1"),
            new("MetadataOptions.HttpEndpoint", spec.EnableInstanceMetadata ? "enabled" : "disabled"),
        };
        var index = 1;
        foreach (var groupId in spec.SecurityGroupIds)
            parameters.Add(new($"NetworkInterface.1.Group.{index++}", groupId));
        if (!string.IsNullOrWhiteSpace(spec.AvailabilityZone))
            parameters.Add(new("Placement.AvailabilityZone", spec.AvailabilityZone.Trim()));
        AddTagSpecifications(parameters, "instance", spec.Tags, spec.InstanceName);
        AddTagSpecifications(parameters, "volume", spec.Tags, spec.InstanceName);
        AddTagSpecifications(parameters, "network-interface", spec.Tags, spec.InstanceName);

        var root = await SendAsync(credentials, "RunInstances", parameters, "run instances", ct).ConfigureAwait(false);
        var ids = root.Descendants()
            .Where(e => e.Name.LocalName == "instanceId")
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0)
            .ToList();
        if (ids.Count != 1)
        {
            throw new Ec2ApiException(
                Ec2FailureKind.Unexpected, "run instances",
                $"RunInstances returned {ids.Count} instance ids; exactly one was requested — " +
                "the outcome is unknown, reconcile by client-token tag before resubmitting.");
        }
        if (!Ec2Placement.IsValidInstanceId(ids[0]))
        {
            throw new Ec2ApiException(
                Ec2FailureKind.Unexpected, "run instances",
                "RunInstances returned a malformed instance id; refusing to trust it.");
        }
        return ids[0];
    }

    /// <summary>Returns the instance, or null on NotFound. Any other failure throws.</summary>
    public async Task<Ec2Instance?> DescribeInstanceAsync(
        Ec2Credentials credentials, string instanceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        try
        {
            var root = await SendAsync(
                credentials, "DescribeInstances",
                [new("InstanceId.1", instanceId.Trim())],
                "describe instance", ct).ConfigureAwait(false);
            return ParseInstances(root).FirstOrDefault();
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Tag-filtered instance listing with exact-match tag filters. Page- and
    /// item-bounded; hitting either cap fails loudly rather than silently
    /// truncating inventory. Server-side filtering is a candidate selector
    /// only: callers re-verify ownership client-side.
    /// </summary>
    public async Task<IReadOnlyList<Ec2Instance>> DescribeInstancesByTagAsync(
        Ec2Credentials credentials, IReadOnlyDictionary<string, string> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(tags);
        var result = new List<Ec2Instance>();
        string? nextToken = null;
        var filters = BuildTagFilter(tags);
        for (var page = 0; page < _limits.MaxListPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var parameters = new List<KeyValuePair<string, string>>(filters)
            {
                new("MaxResults", DescribePageSize.ToString(CultureInfo.InvariantCulture)),
            };
            if (nextToken is not null)
                parameters.Add(new("NextToken", nextToken));
            var root = await SendAsync(credentials, "DescribeInstances", parameters, "describe instances", ct).ConfigureAwait(false);
            foreach (var instance in ParseInstances(root))
            {
                if (result.Count >= _limits.MaxListItems)
                {
                    throw new Ec2ApiException(
                        Ec2FailureKind.Unexpected, "describe instances",
                        $"instance inventory exceeded the {_limits.MaxListItems} item cap; refusing to truncate.");
                }
                result.Add(instance);
            }
            nextToken = FirstValue(root, "nextToken");
            if (string.IsNullOrEmpty(nextToken))
                return result;
        }
        throw new Ec2ApiException(
            Ec2FailureKind.Unexpected, "describe instances",
            $"instance listing exceeded the {_limits.MaxListPages} page cap; refusing to truncate.");
    }

    /// <summary>Terminates an instance. A NotFound instance counts as already gone.</summary>
    public async Task TerminateInstanceAsync(
        Ec2Credentials credentials, string instanceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        try
        {
            await SendAsync(
                credentials, "TerminateInstances",
                [new("InstanceId.1", instanceId.Trim())],
                "terminate instance", ct).ConfigureAwait(false);
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.NotFound)
        {
            // Already gone: idempotent teardown stays silent.
        }
    }

    /// <summary>
    /// Polls one instance until its state is in <paramref name="wanted"/>,
    /// failing closed on any <paramref name="faultStates"/> entry. A wait
    /// timeout throws — inconclusive, never success — so the caller
    /// reconciles by resource identity instead.
    /// </summary>
    public async Task<Ec2Instance> WaitForInstanceStateAsync(
        Ec2Credentials credentials,
        string instanceId,
        IReadOnlyList<string> wanted,
        IReadOnlyList<string> faultStates,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(faultStates);
        var deadline = _clock.GetUtcNow() + timeout;
        var delay = _limits.PollInterval;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var instance = await DescribeInstanceAsync(credentials, instanceId, ct).ConfigureAwait(false);
            if (instance?.State is not null)
            {
                if (wanted.Contains(instance.State, StringComparer.OrdinalIgnoreCase))
                    return instance;
                if (faultStates.Contains(instance.State, StringComparer.OrdinalIgnoreCase))
                {
                    throw new Ec2ApiException(
                        Ec2FailureKind.Unexpected, "wait instance state",
                        $"instance '{instanceId}' entered terminal state '{instance.State}'; refusing to treat it as ready.");
                }
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new Ec2ApiException(
                    Ec2FailureKind.Unreachable, "wait instance state",
                    $"instance '{instanceId}' did not reach {string.Join("/", wanted)} within " +
                    $"{timeout.TotalSeconds:F0}s; outcome unknown, reconcile by resource identity.");
            }
            var wait = delay < timeout ? delay : timeout;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
            delay = NextDelay(delay);
        }
    }

    /// <summary>
    /// Confirms an instance is gone: polls until Describe reports NotFound or
    /// a terminal <c>terminated</c> state. A confirmation timeout keeps the
    /// orphan identity instead of claiming deletion.
    /// </summary>
    public async Task WaitForInstanceTerminatedAsync(
        Ec2Credentials credentials, string instanceId, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var deadline = _clock.GetUtcNow() + timeout;
        var delay = _limits.PollInterval;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var instance = await DescribeInstanceAsync(credentials, instanceId, ct).ConfigureAwait(false);
            if (instance is null
                || string.Equals(instance.State, "terminated", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new Ec2ApiException(
                    Ec2FailureKind.Unreachable, "wait instance terminated",
                    $"instance '{instanceId}' is still '{instance.State}' after the delete confirm window; " +
                    "deletion unconfirmed, orphan identity retained for the reaper.");
            }
            var wait = delay < timeout ? delay : timeout;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
            delay = NextDelay(delay);
        }
    }

    /// <summary>
    /// Verifies the pinned AMI exists and is available. Returns null on
    /// NotFound; throws when the image exists but is not available.
    /// </summary>
    public async Task<Ec2Image?> DescribeImageAsync(
        Ec2Credentials credentials, string amiId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!Ec2Placement.IsValidAmiId(amiId))
            throw new ArgumentException($"AMI id '{amiId}' must be a concrete ami- id pin.", nameof(amiId));
        Ec2Image? image;
        try
        {
            var root = await SendAsync(
                credentials, "DescribeImages",
                [new("ImageId.1", amiId.Trim())],
                "describe image", ct).ConfigureAwait(false);
            image = root.Descendants()
                .Where(e => e.Name.LocalName == "item" && e.Parent?.Name.LocalName == "imagesSet")
                .Select(ParseImage)
                .FirstOrDefault();
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.NotFound)
        {
            return null;
        }
        if (image is null)
            return null;
        if (!string.Equals(image.State, "available", StringComparison.OrdinalIgnoreCase))
        {
            throw new Ec2ApiException(
                Ec2FailureKind.Unexpected, "describe image",
                $"AMI '{amiId}' exists but is '{image.State}'; the approval pins an available image, not a rotting one.");
        }
        return image;
    }

    // ------------------------------------------------------------------
    // Key pairs
    // ------------------------------------------------------------------    /// <summary>
    /// Imports a per-sandbox public key as an EC2 key pair with owned tags.
    /// The private half never leaves the host. A duplicate name owned by this
    /// host's tags is adopted; any other duplicate fails closed.
    /// </summary>
    public async Task ImportKeyPairAsync(
        Ec2Credentials credentials,
        string keyName,
        string publicKeyMaterial,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyMaterial);
        ArgumentNullException.ThrowIfNull(tags);
        ValidateTags(tags);
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("KeyName", keyName.Trim()),
            new("PublicKeyMaterial", Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKeyMaterial.Trim()))),
        };
        AddTagSpecifications(parameters, "key-pair", tags, keyName.Trim());
        try
        {
            await SendAsync(credentials, "ImportKeyPair", parameters, "import key pair", ct).ConfigureAwait(false);
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.Conflict)
        {
            var existing = await DescribeKeyPairAsync(credentials, keyName.Trim(), ct).ConfigureAwait(false);
            if (existing?.KeyName is not null && TagsMatch(tags, existing.Tags))
                return;
            throw new InvalidOperationException(
                $"EC2 key pair '{keyName}' already exists outside this sandbox's ownership; " +
                "refusing to adopt or replace it.");
        }
    }

    /// <summary>Returns the key pair, or null on NotFound.</summary>
    public async Task<Ec2KeyPairInfo?> DescribeKeyPairAsync(
        Ec2Credentials credentials, string keyName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        try
        {
            var root = await SendAsync(
                credentials, "DescribeKeyPairs",
                [new("KeyName.1", keyName.Trim())],
                "describe key pair", ct).ConfigureAwait(false);
            return root.Descendants()
                .Where(e => e.Name.LocalName == "item" && e.Parent?.Name.LocalName == "keySet")
                .Select(ParseKeyPair)
                .FirstOrDefault();
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Tag-filtered key-pair listing. Page- and item-bounded; hitting either
    /// cap fails loudly rather than silently truncating inventory.
    /// </summary>
    public async Task<IReadOnlyList<Ec2KeyPairInfo>> ListKeyPairsByTagAsync(
        Ec2Credentials credentials, IReadOnlyDictionary<string, string> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(tags);
        var result = new List<Ec2KeyPairInfo>();
        var parameters = BuildTagFilter(tags);
        {
            var root = await SendAsync(credentials, "DescribeKeyPairs", parameters, "describe key pairs", ct).ConfigureAwait(false);
            foreach (var key in root.Descendants()
                .Where(e => e.Name.LocalName == "item" && e.Parent?.Name.LocalName == "keySet")
                .Select(ParseKeyPair))
            {
                if (result.Count >= _limits.MaxListItems)
                {
                    throw new Ec2ApiException(
                        Ec2FailureKind.Unexpected, "describe key pairs",
                        $"key-pair inventory exceeded the {_limits.MaxListItems} item cap; refusing to truncate.");
                }
                result.Add(key);
            }
            return result;
        }
    }

    /// <summary>Deletes a key pair. Absent keys count as already gone (the service is idempotent here).</summary>
    public async Task DeleteKeyPairAsync(
        Ec2Credentials credentials, string keyName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        await SendAsync(
            credentials, "DeleteKeyPair",
            [new("KeyName", keyName.Trim())],
            "delete key pair", ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Security groups
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a per-sandbox security group with owned tags. Returns the
    /// group id. A duplicate name re-verified by VPC and tags is adopted;
    /// any other duplicate fails closed.
    /// </summary>
    public async Task<string> CreateSecurityGroupAsync(
        Ec2Credentials credentials,
        string groupName,
        string description,
        string vpcId,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (!Ec2Placement.IsValidVpcId(vpcId))
            throw new ArgumentException($"VPC id '{vpcId}' is malformed.", nameof(vpcId));
        ArgumentNullException.ThrowIfNull(tags);
        ValidateTags(tags);
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("GroupName", groupName.Trim()),
            new("GroupDescription", description.Trim()),
            new("VpcId", vpcId.Trim()),
        };
        AddTagSpecifications(parameters, "security-group", tags, groupName.Trim());
        try
        {
            var root = await SendAsync(credentials, "CreateSecurityGroup", parameters, "create security group", ct).ConfigureAwait(false);
            var groupId = FirstValue(root, "groupId");
            if (!Ec2Placement.IsValidSecurityGroupId(groupId))
            {
                throw new Ec2ApiException(
                    Ec2FailureKind.Unexpected, "create security group",
                    "CreateSecurityGroup returned a malformed group id; refusing to trust it.");
            }
            return groupId!;
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.Conflict)
        {
            var existing = await DescribeSecurityGroupByNameAsync(credentials, groupName.Trim(), vpcId.Trim(), ct).ConfigureAwait(false);
            if (existing?.GroupId is not null
                && string.Equals(existing.VpcId, vpcId.Trim(), StringComparison.Ordinal)
                && TagsMatch(tags, existing.Tags))
            {
                return existing.GroupId;
            }
            throw new InvalidOperationException(
                $"EC2 security group '{groupName}' already exists outside this sandbox's ownership; " +
                "refusing to adopt or replace it.");
        }
    }

    /// <summary>Returns the security group, or null on NotFound.</summary>
    public async Task<Ec2SecurityGroup?> DescribeSecurityGroupAsync(
        Ec2Credentials credentials, string groupId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        try
        {
            var root = await SendAsync(
                credentials, "DescribeSecurityGroups",
                [new("GroupId.1", groupId.Trim())],
                "describe security group", ct).ConfigureAwait(false);
            return ParseSecurityGroups(root).FirstOrDefault();
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.NotFound)
        {
            return null;
        }
    }

    internal async Task<Ec2SecurityGroup?> DescribeSecurityGroupByNameAsync(
        Ec2Credentials credentials, string groupName, string vpcId, CancellationToken ct)
    {
        var root = await SendAsync(
            credentials, "DescribeSecurityGroups",
            [
                new("Filter.1.Name", "group-name"),
                new("Filter.1.Value.1", groupName),
                new("Filter.2.Name", "vpc-id"),
                new("Filter.2.Value.1", vpcId),
            ],
            "describe security group", ct).ConfigureAwait(false);
        return ParseSecurityGroups(root).FirstOrDefault();
    }

    /// <summary>Authorizes ingress permissions. Duplicate rules are absorbed (idempotent).</summary>
    public async Task AuthorizeIngressAsync(
        Ec2Credentials credentials, string groupId, IReadOnlyList<Ec2IpPermission> permissions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Count == 0)
            return;
        foreach (var permission in permissions)
            permission.Validate();
        try
        {
            await SendAsync(
                credentials, "AuthorizeSecurityGroupIngress",
                BuildPermissionParameters(groupId, permissions),
                "authorize security group ingress", ct).ConfigureAwait(false);
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.Conflict)
        {
            // InvalidPermission.Duplicate: the exact rule already exists — absorbed, not retried.
        }
    }

    /// <summary>Authorizes egress permissions. Duplicate rules are absorbed (idempotent).</summary>
    public async Task AuthorizeEgressAsync(
        Ec2Credentials credentials, string groupId, IReadOnlyList<Ec2IpPermission> permissions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Count == 0)
            return;
        foreach (var permission in permissions)
            permission.Validate();
        try
        {
            await SendAsync(
                credentials, "AuthorizeSecurityGroupEgress",
                BuildPermissionParameters(groupId, permissions),
                "authorize security group egress", ct).ConfigureAwait(false);
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.Conflict)
        {
            // InvalidPermission.Duplicate: absorbed, not retried.
        }
    }

    /// <summary>
    /// Revokes the default allow-all egress rule so the group carries only
    /// explicit egress. A missing rule (already revoked) is absorbed.
    /// </summary>
    public async Task RevokeDefaultEgressAsync(
        Ec2Credentials credentials, string groupId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("GroupId", groupId.Trim()),
            new("IpPermissions.1.IpProtocol", "-1"),
            new("IpPermissions.1.IpRanges.1.CidrIp", "0.0.0.0/0"),
        };
        try
        {
            await SendAsync(credentials, "RevokeSecurityGroupEgress", parameters, "revoke default egress", ct).ConfigureAwait(false);
        }
        catch (Ec2ApiException ex) when (ex.Kind is Ec2FailureKind.NotFound or Ec2FailureKind.Conflict)
        {
            // Rule already absent (InvalidPermission.NotFound surfaced as NotFound): nothing to revoke.
        }
    }

    /// <summary>
    /// Tag-filtered security-group listing. Page- and item-bounded; hitting
    /// either cap fails loudly rather than silently truncating inventory.
    /// </summary>
    public async Task<IReadOnlyList<Ec2SecurityGroup>> ListSecurityGroupsByTagAsync(
        Ec2Credentials credentials, IReadOnlyDictionary<string, string> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(tags);
        var result = new List<Ec2SecurityGroup>();
        var parameters = BuildTagFilter(tags);
        {
            var root = await SendAsync(credentials, "DescribeSecurityGroups", parameters, "describe security groups", ct).ConfigureAwait(false);
            foreach (var group in ParseSecurityGroups(root))
            {
                if (result.Count >= _limits.MaxListItems)
                {
                    throw new Ec2ApiException(
                        Ec2FailureKind.Unexpected, "describe security groups",
                        $"security-group inventory exceeded the {_limits.MaxListItems} item cap; refusing to truncate.");
                }
                result.Add(group);
            }
            return result;
        }
    }

    /// <summary>Deletes a security group. A NotFound group counts as already gone.</summary>
    public async Task DeleteSecurityGroupAsync(
        Ec2Credentials credentials, string groupId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        try
        {
            await SendAsync(
                credentials, "DeleteSecurityGroup",
                [new("GroupId", groupId.Trim())],
                "delete security group", ct).ConfigureAwait(false);
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.NotFound)
        {
            // Already gone: idempotent teardown stays silent.
        }
    }

    // ------------------------------------------------------------------
    // Elastic IPs
    // ------------------------------------------------------------------

    /// <summary>Allocates one VPC Elastic IP with owned tags. Returns allocation id and public IP.</summary>
    public async Task<Ec2Address> AllocateAddressAsync(
        Ec2Credentials credentials, IReadOnlyDictionary<string, string> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(tags);
        ValidateTags(tags);
        var parameters = new List<KeyValuePair<string, string>> { new("Domain", "vpc") };
        AddTagSpecifications(parameters, "elastic-ip", tags, NameTagOf(tags));
        var root = await SendAsync(credentials, "AllocateAddress", parameters, "allocate address", ct).ConfigureAwait(false);
        var allocationId = FirstValue(root, "allocationId");
        var publicIp = FirstValue(root, "publicIp");
        if (!Ec2Placement.IsValidAllocationId(allocationId))
        {
            throw new Ec2ApiException(
                Ec2FailureKind.Unexpected, "allocate address",
                "AllocateAddress returned a malformed allocation id; refusing to trust it.");
        }
        if (string.IsNullOrWhiteSpace(publicIp) || !IPAddress.TryParse(publicIp.Trim(), out _))
        {
            throw new Ec2ApiException(
                Ec2FailureKind.Unexpected, "allocate address",
                "AllocateAddress returned no usable public IP; refusing to SSH at an unconfirmed target.");
        }
        return new Ec2Address { AllocationId = allocationId!.Trim(), PublicIp = publicIp!.Trim(), Tags = new Dictionary<string, string>(tags, StringComparer.Ordinal) };
    }

    /// <summary>Associates an Elastic IP with an instance. Returns the association id.</summary>
    public async Task<string> AssociateAddressAsync(
        Ec2Credentials credentials, string allocationId, string instanceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(allocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var root = await SendAsync(
            credentials, "AssociateAddress",
            [new("AllocationId", allocationId.Trim()), new("InstanceId", instanceId.Trim())],
            "associate address", ct).ConfigureAwait(false);
        var associationId = FirstValue(root, "associationId");
        if (!Ec2Placement.IsValidAssociationId(associationId))
        {
            throw new Ec2ApiException(
                Ec2FailureKind.Unexpected, "associate address",
                "AssociateAddress returned a malformed association id; refusing to trust it.");
        }
        return associationId!;
    }

    /// <summary>Disassociates an Elastic IP. An absent association counts as already gone.</summary>
    public async Task DisassociateAddressAsync(
        Ec2Credentials credentials, string associationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(associationId);
        try
        {
            await SendAsync(
                credentials, "DisassociateAddress",
                [new("AssociationId", associationId.Trim())],
                "disassociate address", ct).ConfigureAwait(false);
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.NotFound)
        {
            // Already disassociated: idempotent teardown stays silent.
        }
    }

    /// <summary>
    /// Tag-filtered Elastic-IP listing. Page- and item-bounded; hitting
    /// either cap fails loudly rather than silently truncating inventory.
    /// </summary>
    public async Task<IReadOnlyList<Ec2Address>> ListAddressesByTagAsync(
        Ec2Credentials credentials, IReadOnlyDictionary<string, string> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(tags);
        var result = new List<Ec2Address>();
        var parameters = BuildTagFilter(tags);
        {
            var root = await SendAsync(credentials, "DescribeAddresses", parameters, "describe addresses", ct).ConfigureAwait(false);
            foreach (var address in root.Descendants()
                .Where(e => e.Name.LocalName == "item" && e.Parent?.Name.LocalName == "addressesSet")
                .Select(ParseAddress))
            {
                if (result.Count >= _limits.MaxListItems)
                {
                    throw new Ec2ApiException(
                        Ec2FailureKind.Unexpected, "describe addresses",
                        $"address inventory exceeded the {_limits.MaxListItems} item cap; refusing to truncate.");
                }
                result.Add(address);
            }
            return result;
        }
    }

    /// <summary>Returns the Elastic IP, or null on NotFound.</summary>
    public async Task<Ec2Address?> DescribeAddressAsync(
        Ec2Credentials credentials, string allocationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(allocationId);
        try
        {
            var root = await SendAsync(
                credentials, "DescribeAddresses",
                [new("AllocationId.1", allocationId.Trim())],
                "describe address", ct).ConfigureAwait(false);
            return root.Descendants()
                .Where(e => e.Name.LocalName == "item" && e.Parent?.Name.LocalName == "addressesSet")
                .Select(ParseAddress)
                .FirstOrDefault();
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.NotFound)
        {
            return null;
        }
    }

    /// <summary>Releases an Elastic IP. A NotFound allocation counts as already gone.</summary>
    public async Task ReleaseAddressAsync(
        Ec2Credentials credentials, string allocationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(allocationId);
        try
        {
            await SendAsync(
                credentials, "ReleaseAddress",
                [new("AllocationId", allocationId.Trim())],
                "release address", ct).ConfigureAwait(false);
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.NotFound)
        {
            // Already gone: idempotent teardown stays silent.
        }
    }

    // ------------------------------------------------------------------
    // Volumes
    // ------------------------------------------------------------------

    /// <summary>
    /// Tag-filtered volume listing. Page- and item-bounded; hitting either cap
    /// fails loudly rather than silently truncating inventory.
    /// </summary>
    public async Task<IReadOnlyList<Ec2Volume>> DescribeVolumesByTagAsync(
        Ec2Credentials credentials, IReadOnlyDictionary<string, string> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(tags);
        var result = new List<Ec2Volume>();
        string? nextToken = null;
        var filters = BuildTagFilter(tags);
        for (var page = 0; page < _limits.MaxListPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var parameters = new List<KeyValuePair<string, string>>(filters)
            {
                new("MaxResults", DescribePageSize.ToString(CultureInfo.InvariantCulture)),
            };
            if (nextToken is not null)
                parameters.Add(new("NextToken", nextToken));
            var root = await SendAsync(credentials, "DescribeVolumes", parameters, "describe volumes", ct).ConfigureAwait(false);
            foreach (var volume in root.Descendants()
                .Where(e => e.Name.LocalName == "item" && e.Parent?.Name.LocalName == "volumeSet")
                .Select(ParseVolume))
            {
                if (result.Count >= _limits.MaxListItems)
                {
                    throw new Ec2ApiException(
                        Ec2FailureKind.Unexpected, "describe volumes",
                        $"volume inventory exceeded the {_limits.MaxListItems} item cap; refusing to truncate.");
                }
                result.Add(volume);
            }
            nextToken = FirstValue(root, "nextToken");
            if (string.IsNullOrEmpty(nextToken))
                return result;
        }
        throw new Ec2ApiException(
            Ec2FailureKind.Unexpected, "describe volumes",
            $"volume listing exceeded the {_limits.MaxListPages} page cap; refusing to truncate.");
    }

    /// <summary>Deletes a volume. A NotFound volume counts as already gone.</summary>
    public async Task DeleteVolumeAsync(
        Ec2Credentials credentials, string volumeId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);
        try
        {
            await SendAsync(
                credentials, "DeleteVolume",
                [new("VolumeId", volumeId.Trim())],
                "delete volume", ct).ConfigureAwait(false);
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.NotFound)
        {
            // Already gone: idempotent teardown stays silent.
        }
    }

    // ------------------------------------------------------------------
    // Transport
    // ------------------------------------------------------------------

    private async Task<XElement> SendAsync(
        Ec2Credentials credentials,
        string action,
        IReadOnlyList<KeyValuePair<string, string>> parameters,
        string operation,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        EnsureEndpointAllowed(credentials);
        var body = BuildFormBody(action, parameters);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_limits.HttpTimeout);
        var (authorization, amzDate) = Ec2SigV4Signer.Sign(
            credentials.AccessKeyId, credentials.SecretAccessKey,
            credentials.Region, credentials.ServiceUrl.Host, body, _clock.GetUtcNow());
        using var request = new HttpRequestMessage(HttpMethod.Post, credentials.ServiceUrl);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        if (!string.IsNullOrEmpty(credentials.SessionToken))
            request.Headers.TryAddWithoutValidation("x-amz-security-token", credentials.SessionToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new Ec2ApiException(
                Ec2FailureKind.Unreachable, operation,
                $"request timed out after {_limits.HttpTimeout.TotalSeconds:F0}s; outcome unknown, reconcile by resource identity.",
                innerException: ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new Ec2ApiException(
                Ec2FailureKind.Unreachable, operation,
                $"transport failure with unknown outcome; reconcile by resource identity before resubmitting: {TrimMessage(ex.Message)}",
                innerException: ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw await BuildErrorAsync(operation, response, ct).ConfigureAwait(false);
            var text = await ReadBoundedStringAsync(response.Content, operation, timeoutCts.Token, _limits.MaxResponseBytes).ConfigureAwait(false);
            return ParseXml(text, operation);
        }
    }

    private void EnsureEndpointAllowed(Ec2Credentials credentials)
    {
        if (credentials.ServiceUrl.Scheme == Uri.UriSchemeHttp
            && !IsCleartextHttpPermitted(credentials.ServiceUrl, credentials.AllowUnsafeHttp))
        {
            throw new Ec2ApiException(
                Ec2FailureKind.Unexpected, "endpoint",
                "EC2 endpoint must use https:// (http only for loopback test URLs under AllowUnsafeHttp).");
        }
    }

    internal static string BuildFormBody(string action, IReadOnlyList<KeyValuePair<string, string>> parameters)
    {
        var builder = new StringBuilder();
        builder.Append("Action=").Append(Encode(action));
        builder.Append("&Version=").Append(Encode(Ec2SandboxOptions.ApiVersion));
        foreach (var (key, value) in parameters)
            builder.Append('&').Append(Encode(key)).Append('=').Append(Encode(value ?? string.Empty));
        return builder.ToString();
    }

    internal static string Encode(string value) => Uri.EscapeDataString(value ?? string.Empty);

    private static async Task<Ec2ApiException> BuildErrorAsync(
        string operation, HttpResponseMessage response, CancellationToken ct)
    {
        var status = response.StatusCode;
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
        var (code, message) = ExtractError(text);
        var kind = Ec2ApiException.FromStatus(status, code, text);
        return new Ec2ApiException(
            kind, operation,
            $"HTTP {(int)status}{(code is null ? "" : $" {code}")}: {TrimMessage(message ?? code ?? response.ReasonPhrase ?? "request failed")}",
            statusCode: status,
            errorCode: code);
    }

    internal static (string? Code, string? Message) ExtractError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);
        try
        {
            var root = ParseXml(body, "error");
            var code = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value.Trim();
            var message = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value.Trim();
            if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(message))
                return (null, body.Length <= 500 ? body : body[..500]);
            return (
                string.IsNullOrEmpty(code) ? null : code,
                string.IsNullOrEmpty(message) ? code : message);
        }
        catch (Ec2ApiException)
        {
            return (null, body.Length <= 500 ? body : body[..500]);
        }
    }

    internal static XElement ParseXml(string text, string operation)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new Ec2ApiException(Ec2FailureKind.Unexpected, operation, "empty response body; refusing to infer success.");
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 64L * 1024 * 1024,
            };
            using var reader = XmlReader.Create(new System.IO.StringReader(text), settings);
            return XElement.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new Ec2ApiException(
                Ec2FailureKind.Unexpected, operation,
                $"malformed response body; refusing to infer success: {TrimMessage(ex.Message)}",
                innerException: ex);
        }
    }

    private static async Task<string> ReadBoundedStringAsync(
        HttpContent content, string operation, CancellationToken ct, int maxBytes)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var remaining = (long)maxBytes + 1;
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, remaining)), ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            remaining -= read;
            if (remaining <= 0)
            {
                throw new Ec2ApiException(
                    Ec2FailureKind.Unexpected, operation,
                    $"response exceeded the {maxBytes} byte cap; refusing to trust a truncated body.");
            }
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ------------------------------------------------------------------
    // XML shapes
    // ------------------------------------------------------------------

    internal static List<Ec2Instance> ParseInstances(XElement root)
    {
        var result = new List<Ec2Instance>();
        foreach (var item in root.Descendants().Where(e =>
            e.Name.LocalName == "item" && e.Parent?.Name.LocalName == "instancesSet"))
        {
            result.Add(ParseInstance(item));
        }
        return result;
    }

    internal static Ec2Instance ParseInstance(XElement item)
    {
        if (item.Name.LocalName != "item")
            throw new Ec2ApiException(Ec2FailureKind.Unexpected, "parse instance", "instance body is not an XML item.");
        var groupSet = item.Elements().FirstOrDefault(e => e.Name.LocalName == "groupSet");
        return new Ec2Instance
        {
            InstanceId = Child(item, "instanceId"),
            State = item.Descendants().FirstOrDefault(e => e.Name.LocalName == "name"
                && e.Parent?.Name.LocalName == "instanceState")?.Value.Trim(),
            ImageId = Child(item, "imageId"),
            InstanceType = Child(item, "instanceType"),
            SubnetId = Child(item, "subnetId"),
            VpcId = Child(item, "vpcId"),
            KeyName = Child(item, "keyName"),
            PublicIpAddress = Child(item, "ipAddress") ?? Child(item, "publicIpAddress"),
            PrivateIpAddress = Child(item, "privateIpAddress"),
            PublicDnsName = Child(item, "dnsName"),
            SecurityGroupIds = groupSet?.Elements()
                .Where(e => e.Name.LocalName == "item")
                .Select(e => Child(e, "groupId"))
                .Where(g => !string.IsNullOrEmpty(g))
                .Select(g => g!)
                .ToList(),
            Tags = ReadTags(item),
        };
    }

    internal static Ec2Image ParseImage(XElement item) => new()
    {
        ImageId = Child(item, "imageId"),
        State = Child(item, "imageState") ?? Child(item, "state"),
        Name = Child(item, "imageLocation") ?? Child(item, "name"),
        Architecture = Child(item, "architecture"),
        RootDeviceName = Child(item, "rootDeviceName"),
    };

    internal static Ec2KeyPairInfo ParseKeyPair(XElement item) => new()
    {
        KeyName = Child(item, "keyName"),
        KeyFingerprint = Child(item, "keyFingerprint"),
        Tags = ReadTags(item),
    };

    internal static List<Ec2SecurityGroup> ParseSecurityGroups(XElement root)
    {
        var result = new List<Ec2SecurityGroup>();
        foreach (var item in root.Descendants().Where(e =>
            e.Name.LocalName == "item" && e.Parent?.Name.LocalName == "securityGroupInfo"))
        {
            result.Add(new Ec2SecurityGroup
            {
                GroupId = Child(item, "groupId"),
                GroupName = Child(item, "groupName"),
                VpcId = Child(item, "vpcId"),
                Tags = ReadTags(item),
            });
        }
        return result;
    }

    internal static Ec2Volume ParseVolume(XElement item)
    {
        string? instanceId = null;
        string? device = null;
        var deleteOnTermination = false;
        var attachment = item.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "item" && e.Parent?.Name.LocalName == "attachmentSet");
        if (attachment is not null)
        {
            instanceId = Child(attachment, "instanceId");
            device = Child(attachment, "device");
            deleteOnTermination = string.Equals(Child(attachment, "deleteOnTermination"), "true", StringComparison.OrdinalIgnoreCase);
        }
        return new Ec2Volume
        {
            VolumeId = Child(item, "volumeId"),
            State = Child(item, "status") ?? Child(item, "state"),
            InstanceId = instanceId,
            Device = device,
            DeleteOnTermination = deleteOnTermination,
            Tags = ReadTags(item),
        };
    }

    internal static Ec2Address ParseAddress(XElement item) => new()
    {
        AllocationId = Child(item, "allocationId"),
        AssociationId = Child(item, "associationId"),
        PublicIp = Child(item, "publicIp"),
        InstanceId = Child(item, "instanceId"),
        Tags = ReadTags(item),
    };

    internal static Dictionary<string, string> ReadTags(XElement item)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        var tagSet = item.Elements().FirstOrDefault(e => e.Name.LocalName == "tagSet");
        if (tagSet is null)
            return tags;
        foreach (var entry in tagSet.Elements().Where(e => e.Name.LocalName == "item"))
        {
            var key = Child(entry, "key");
            var value = Child(entry, "value");
            if (!string.IsNullOrEmpty(key) && value is not null && !tags.ContainsKey(key))
                tags[key] = value;
        }
        return tags;
    }

    internal static string? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();

    internal static string? FirstValue(XElement root, string localName) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim() is { } value
        && value.Length > 0 ? value : null;

    internal static bool TagsMatch(
        IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string>? actual)
    {
        if (actual is null)
            return false;
        foreach (var (key, value) in expected)
        {
            if (!actual.TryGetValue(key, out var got) || !string.Equals(got, value, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    internal static void ValidateTags(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count > MaxTagEntries)
            throw new ArgumentException($"Tag set carries {tags.Count} entries; the cap is {MaxTagEntries}.", nameof(tags));
        foreach (var (key, value) in tags)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaxTagChars)
                throw new ArgumentException($"Tag key '{key}' is blank or over {MaxTagChars} chars.", nameof(tags));
            if (value is null || value.Length > MaxTagChars)
                throw new ArgumentException($"Tag value for '{key}' is over {MaxTagChars} chars.", nameof(tags));
        }
    }

    internal static void AddTagSpecifications(
        List<KeyValuePair<string, string>> parameters,
        string resourceType,
        IReadOnlyDictionary<string, string> tags,
        string name)
    {
        var spec = 1;
        while (parameters.Any(p => p.Key.StartsWith(
            $"TagSpecification.{spec}.", StringComparison.Ordinal)))
        {
            spec++;
        }
        parameters.Add(new($"TagSpecification.{spec}.ResourceType", resourceType));
        var tagIndex = 1;
        parameters.Add(new($"TagSpecification.{spec}.Tag.{tagIndex}.Key", "Name"));
        parameters.Add(new($"TagSpecification.{spec}.Tag.{tagIndex}.Value", name));
        tagIndex++;
        foreach (var (key, value) in tags)
        {
            if (string.Equals(key, "Name", StringComparison.Ordinal))
                continue;
            parameters.Add(new($"TagSpecification.{spec}.Tag.{tagIndex}.Key", key));
            parameters.Add(new($"TagSpecification.{spec}.Tag.{tagIndex}.Value", value ?? string.Empty));
            tagIndex++;
        }
    }

    internal static List<KeyValuePair<string, string>> BuildTagFilter(
        IReadOnlyDictionary<string, string> tags)
    {
        var filters = new List<KeyValuePair<string, string>>();
        var filterIndex = 1;
        foreach (var (key, value) in tags)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            filters.Add(new($"Filter.{filterIndex}.Name", $"tag:{key}"));
            filters.Add(new($"Filter.{filterIndex}.Value.1", value));
            filterIndex++;
        }
        return filters;
    }

    internal static List<KeyValuePair<string, string>> BuildPermissionParameters(
        string groupId, IReadOnlyList<Ec2IpPermission> permissions)
    {
        var parameters = new List<KeyValuePair<string, string>> { new("GroupId", groupId.Trim()) };
        var index = 1;
        foreach (var permission in permissions)
        {
            parameters.Add(new($"IpPermissions.{index}.IpProtocol", permission.IpProtocol));
            parameters.Add(new($"IpPermissions.{index}.FromPort", permission.FromPort.ToString(CultureInfo.InvariantCulture)));
            parameters.Add(new($"IpPermissions.{index}.ToPort", permission.ToPort.ToString(CultureInfo.InvariantCulture)));
            var rangeIndex = 1;
            foreach (var cidr in permission.CidrRanges)
                parameters.Add(new($"IpPermissions.{index}.IpRanges.{rangeIndex++}.CidrIp", cidr));
            var range6Index = 1;
            foreach (var cidr in permission.Cidr6Ranges)
                parameters.Add(new($"IpPermissions.{index}.Ipv6Ranges.{range6Index++}.CidrIpv6", cidr));
            index++;
        }
        return parameters;
    }

    internal static string NameTagOf(IReadOnlyDictionary<string, string> tags) =>
        tags.TryGetValue("Name", out var name) && !string.IsNullOrWhiteSpace(name) ? name : "codeybox";

    private TimeSpan NextDelay(TimeSpan current)
    {
        var nextTicks = Math.Min(current.Ticks * 2, _limits.MaxPollInterval.Ticks);
        return new TimeSpan(Math.Max(nextTicks, TimeSpan.FromMilliseconds(1).Ticks));
    }

    private static string TrimMessage(string message)
    {
        var trimmed = message.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }
}
