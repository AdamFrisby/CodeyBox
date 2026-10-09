using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CodeyBox.Core;
using CodeyBox.Ec2SandboxPlugin;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Shared fake AWS EC2 Query API plus SSH/key/DNS seams for the ec2 provider
/// tests. The fake mirrors the EC2 Query request/response shapes (SigV4
/// verification, form-encoded actions, XML bodies and errors, NextToken
/// paging, ownership tags) without any network or account.
/// </summary>
internal sealed class Ec2Harness : IDisposable
{
    public const string OwnerId = "test-host";
    public const string OtherOwnerId = "other-host";
    public const string AccessKey = "AKIATESTKEY123456789";
    public const string SecretKey = "test-secret-key-for-sigv4-verification-000";
    public const string Region = "us-east-1";
    public const string AmiId = "ami-0123456789abcdef0";
    public const string InstanceType = "t3.medium";
    public const string SubnetId = "subnet-0123456789abcdef0";
    public const string VpcId = "vpc-0123456789abcdef0";

    public FakeEc2Cloud Cloud { get; }
    public FakeEc2Dns Dns { get; } = new();
    public FakeEc2TransportFactory TransportFactory { get; } = new();
    public FakeEc2Transport Transport => TransportFactory.Created.Last();
    public Ec2SandboxOptions Options { get; private set; }
    public Ec2SandboxProvider Provider { get; private set; }
    private readonly HttpClient _http;

    public Ec2Harness(
        Func<Ec2SandboxOptions, Ec2SandboxOptions>? configure = null,
        FakeEc2Cloud? sharedCloud = null)
    {
        Cloud = sharedCloud ?? new();
        _http = new HttpClient(Cloud, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new Ec2SandboxOptions
        {
            Enabled = true,
            ServiceUrl = "http://localhost/",
            AllowUnsafeHttp = true,
            OwnerId = OwnerId,
            Region = Region,
            AmiId = AmiId,
            InstanceType = InstanceType,
            SubnetId = SubnetId,
            VpcId = VpcId,
            OrchestratorSshCidrs = ["203.0.113.0/24"],
            DnsServerIps = [],
            NtpServerIps = [],
            PollIntervalMilliseconds = 200,
            MaxPollIntervalMilliseconds = 200,
            ReadyTimeoutSeconds = 30,
            SshReadyTimeoutSeconds = 30,
            SshUser = "tester",
        };
        Options = configure is null ? options : configure(options);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AWS_ACCESS_KEY_ID"] = AccessKey,
            ["AWS_SECRET_ACCESS_KEY"] = SecretKey,
        };
        Provider = new Ec2SandboxProvider(
            () => Options,
            _http,
            new FakeEc2KeyGenerator(),
            Dns,
            TransportFactory,
            name => env.TryGetValue(name, out var value) ? value : null,
            TimeProvider.System,
            NullLogger.Instance);
    }

    public string SshTempDir(string instanceName)
    {
        var suffix = instanceName[Ec2SandboxProviderTests.InstancePrefix.Length..];
        return Path.Combine(Path.GetTempPath(), "codeybox-ec2-" + suffix);
    }

    public void Dispose()
    {
        Provider.Dispose();
        _http.Dispose();
    }
}

internal sealed class FakeEc2KeyGenerator : IEc2KeyGenerator
{
    public static string ClientPublicKey { get; } = "ssh-ed25519 " + new string('A', 64);

    public static string HostPublicKey { get; } = "ssh-ed25519 " + new string('B', 64);

    public Task<Ec2ClientKeyMaterial> GenerateClientKeyAsync(
        string keygenBinary, string directory, string comment, CancellationToken ct)
    {
        _ = keygenBinary; _ = comment; _ = ct;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "id-ed25519-test");
        File.WriteAllText(path, "fake-private");
        return Task.FromResult(new Ec2ClientKeyMaterial(path, ClientPublicKey));
    }

    public Task<Ec2HostKeyMaterial> GenerateHostKeyAsync(
        string keygenBinary, string directory, string comment, CancellationToken ct)
    {
        _ = keygenBinary; _ = directory; _ = comment; _ = ct;
        return Task.FromResult(new Ec2HostKeyMaterial(
            "-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END OPENSSH PRIVATE KEY-----\n",
            HostPublicKey));
    }
}

internal sealed class FakeEc2Dns : IEc2DnsResolver
{
    public Dictionary<string, System.Net.IPAddress[]> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<System.Net.IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        _ = ct;
        if (Hosts.TryGetValue(host, out var ips))
            return Task.FromResult(ips);
        throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
    }
}

internal sealed class FakeEc2TransportFactory : IEc2TransportFactory
{
    public List<FakeEc2Transport> Created { get; } = [];
    public string LastTarget { get; private set; } = string.Empty;
    public Action<FakeEc2Transport>? ConfigureTransport { get; set; }

    public IRemoteHostTransport Create(Ec2SshTransportSpec spec)
    {
        LastTarget = spec.SshTarget;
        var transport = new FakeEc2Transport();
        ConfigureTransport?.Invoke(transport);
        Created.Add(transport);
        return transport;
    }
}

internal sealed class FakeEc2Transport : IRemoteHostTransport
{
    public string DiagnosticId => "fake";
    public List<IReadOnlyList<string>> Calls { get; } = [];
    public List<(string HostPath, string RemotePath)> StageInCalls { get; } = [];
    public List<(string RemotePath, string HostPath)> StageOutCalls { get; } = [];
    public int? LastMaxStdoutBytes { get; private set; }
    public int? LastMaxStderrBytes { get; private set; }
    public Action<string, string>? OnStageIn { get; set; }
    public Action<string, string>? OnStageOut { get; set; }
    public bool ThrowTransportLossOnRun { get; set; }
    public Func<IReadOnlyList<string>, string?, ProcessRunResult> OnRun { get; set; } =
        (_, _) => new ProcessRunResult(0, "ok", string.Empty);

    public string LastArgv() => string.Join(" ", Calls.Last());

    public Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null,
        bool killOnOutputLimit = true)
    {
        ct.ThrowIfCancellationRequested();
        _ = stdoutChunkCallback; _ = stderrChunkCallback;
        _ = killOnOutputLimit;
        Calls.Add(argv.ToArray());
        LastMaxStdoutBytes = maxStdoutBytes;
        LastMaxStderrBytes = maxStderrBytes;
        if (ThrowTransportLossOnRun)
            throw new RemoteSshTransportException("simulated transport loss");
        return Task.FromResult(OnRun(argv, stdin));
    }

    public Task StageInAsync(string hostPath, string remotePath, CancellationToken ct)
    {
        _ = ct;
        OnStageIn?.Invoke(hostPath, remotePath);
        StageInCalls.Add((hostPath, remotePath));
        return Task.CompletedTask;
    }

    public Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct)
    {
        _ = ct;
        OnStageOut?.Invoke(remotePath, hostPath);
        StageOutCalls.Add((remotePath, hostPath));
        return Task.CompletedTask;
    }
}

internal sealed class FakeInstance
{
    public string InstanceId { get; set; } = string.Empty;
    public string State { get; set; } = "pending";
    public string ImageId { get; set; } = string.Empty;
    public string InstanceType { get; set; } = string.Empty;
    public string SubnetId { get; set; } = string.Empty;
    public string VpcId { get; set; } = string.Empty;
    public string KeyName { get; set; } = string.Empty;
    public string PublicIp { get; set; } = "192.0.2.10";
    public List<string> GroupIds { get; } = [];
    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);
    public int Polls;
}

internal sealed class FakeSecurityGroup
{
    public string GroupId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string VpcId { get; set; } = string.Empty;
    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);
    public List<string> IngressRules { get; } = [];
    public List<string> EgressRules { get; } = [];
}

internal sealed class FakeAddress
{
    public string AllocationId { get; set; } = string.Empty;
    public string? AssociationId { get; set; }
    public string PublicIp { get; set; } = string.Empty;
    public string? InstanceId { get; set; }
    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);
}

internal sealed class FakeVolume
{
    public string VolumeId { get; set; } = string.Empty;
    public string State { get; set; } = "in-use";
    public string? InstanceId { get; set; }
    public bool DeleteOnTermination { get; set; } = true;
    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);
}

internal sealed class FakeEc2Cloud : HttpMessageHandler
{
    public ConcurrentDictionary<string, FakeInstance> Instances { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, Dictionary<string, string>> KeyPairs { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, FakeSecurityGroup> SecurityGroups { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, FakeAddress> Addresses { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, FakeVolume> Volumes { get; } = new(StringComparer.Ordinal);
    public List<Dictionary<string, string>> RunBodies { get; } = [];
    public List<string> Events { get; } = [];
    public string? ForceInstanceState;
    public int RunningAfterPolls = 1;
    public int BlindDescribeCalls;
    public bool FailInstanceDelete;
    public (HttpStatusCode Status, string Code, string Message, bool StoreInstance)? FailNextRun;
    public (HttpStatusCode Status, string Code, string Message)? FailNextImportKey;
    public (HttpStatusCode Status, string Code, string Message)? FailNextCreateSecurityGroup;
    /// <summary>Overrides the public IP stamped on the next launched instance (hostile-response tests).</summary>
    public string? NextPublicIpOverride;
    /// <summary>Returns one malformed (non-XML) success body for the next request.</summary>
    public bool MalformedNextResponse;
    /// <summary>Returns one truncated (over-cap) success body for the next request.</summary>
    public bool OversizedNextResponse;
    /// <summary>When set, DescribeImages reports this state for the known AMI.</summary>
    public string ImageState = "available";
    private long _seq;
    private readonly object _lock = new();

    public string NextInstanceId()
    {
        lock (_lock)
            return "i-" + (++_seq + 0x10000000).ToString("x16", CultureInfo.InvariantCulture);
    }

    public string NextGroupId()
    {
        lock (_lock)
            return "sg-" + (++_seq + 0x20000000).ToString("x16", CultureInfo.InvariantCulture).TrimStart('0').PadLeft(8, '0');
    }

    public string NextAllocationId()
    {
        lock (_lock)
            return "eipalloc-" + (++_seq + 0x30000000).ToString("x16", CultureInfo.InvariantCulture).TrimStart('0').PadLeft(8, '0');
    }

    public string NextAssociationId()
    {
        lock (_lock)
            return "eipassoc-" + (++_seq + 0x40000000).ToString("x16", CultureInfo.InvariantCulture).TrimStart('0').PadLeft(8, '0');
    }

    public string NextVolumeId()
    {
        lock (_lock)
            return "vol-" + (++_seq + 0x50000000).ToString("x16", CultureInfo.InvariantCulture).TrimStart('0').PadLeft(8, '0');
    }

    public void Log(string entry)
    {
        lock (_lock)
            Events.Add(entry);
    }

    /// <summary>
    /// Seeds an instance as if left behind by a crashed run: owned tags plus
    /// optionally a full dangling set (key pair, security group, address,
    /// volume) under the same request tag. Returns the instance Name tag.
    /// </summary>
    public string SeedOrphan(string owner, Ec2SandboxOptions options, bool withResources = true)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var name = options.InstanceNamePrefix + suffix;
        var request = Guid.NewGuid().ToString();
        var instanceId = NextInstanceId();
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["codeybox-owned"] = "true",
            ["codeybox-owner"] = owner,
            ["codeybox-work-item"] = "none",
            ["codeybox-request"] = request,
            ["codeybox-created"] = "1700000000",
            ["Name"] = name,
        };
        var instance = new FakeInstance
        {
            InstanceId = instanceId,
            State = "running",
            ImageId = options.AmiId,
            InstanceType = options.InstanceType,
            SubnetId = options.SubnetId,
            VpcId = options.VpcId,
            KeyName = options.KeyNamePrefix + suffix,
            Tags = tags,
        };
        Instances[instanceId] = instance;
        if (withResources)
        {
            KeyPairs[options.KeyNamePrefix + suffix] = new Dictionary<string, string>(tags, StringComparer.Ordinal);
            var group = new FakeSecurityGroup
            {
                GroupId = NextGroupId(),
                GroupName = options.SecurityGroupNamePrefix + suffix,
                VpcId = options.VpcId,
                Tags = new Dictionary<string, string>(tags, StringComparer.Ordinal),
            };
            SecurityGroups[group.GroupId] = group;
            instance.GroupIds.Add(group.GroupId);
            var allocationId = NextAllocationId();
            Addresses[allocationId] = new FakeAddress
            {
                AllocationId = allocationId,
                PublicIp = "203.0.113.77",
                Tags = new Dictionary<string, string>(tags, StringComparer.Ordinal),
            };
            var volumeId = NextVolumeId();
            Volumes[volumeId] = new FakeVolume
            {
                VolumeId = volumeId,
                State = "in-use",
                InstanceId = instanceId,
                DeleteOnTermination = false,
                Tags = new Dictionary<string, string>(tags, StringComparer.Ordinal),
            };
        }
        return name;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var form = ParseForm(body);
        if (!form.TryGetValue("Action", out var action) || string.IsNullOrEmpty(action))
            return Error(HttpStatusCode.BadRequest, "MissingAction", "no Action parameter");
        var authFailure = VerifySignature(request, body);
        if (authFailure is not null)
            return authFailure;

        return action switch
        {
            "RunInstances" => RunInstances(form),
            "DescribeInstances" => DescribeInstances(form),
            "TerminateInstances" => TerminateInstances(form),
            "DescribeImages" => DescribeImages(form),
            "ImportKeyPair" => ImportKeyPair(form),
            "DescribeKeyPairs" => DescribeKeyPairs(form),
            "DeleteKeyPair" => DeleteKeyPair(form),
            "CreateSecurityGroup" => CreateSecurityGroup(form),
            "DescribeSecurityGroups" => DescribeSecurityGroups(form),
            "AuthorizeSecurityGroupIngress" => AuthorizeSecurityGroup(form, ingress: true),
            "AuthorizeSecurityGroupEgress" => AuthorizeSecurityGroup(form, ingress: false),
            "RevokeSecurityGroupEgress" => RevokeSecurityGroupEgress(form),
            "DeleteSecurityGroup" => DeleteSecurityGroup(form),
            "AllocateAddress" => AllocateAddress(form),
            "AssociateAddress" => AssociateAddress(form),
            "DisassociateAddress" => DisassociateAddress(form),
            "DescribeAddresses" => DescribeAddresses(form),
            "ReleaseAddress" => ReleaseAddress(form),
            "DescribeVolumes" => DescribeVolumes(form),
            "DeleteVolume" => DeleteVolume(form),
            _ => Error(HttpStatusCode.BadRequest, "InvalidAction", "no such action: " + action),
        };
    }

    private HttpResponseMessage? VerifySignature(HttpRequestMessage request, string body)
    {
        var auth = request.Headers.TryGetValues("Authorization", out var authValues)
            ? authValues.FirstOrDefault() : null;
        var amzDate = request.Headers.TryGetValues("x-amz-date", out var dateValues)
            ? dateValues.FirstOrDefault() : null;
        if (string.IsNullOrEmpty(auth) || string.IsNullOrEmpty(amzDate))
            return Error(HttpStatusCode.Forbidden, "AuthFailure", "missing signature");
        const string prefix = "AWS4-HMAC-SHA256 Credential=";
        if (!auth.StartsWith(prefix, StringComparison.Ordinal))
            return Error(HttpStatusCode.Forbidden, "AuthFailure", "bad authorization scheme");
        var credential = auth[prefix.Length..];
        var slash = credential.IndexOf('/');
        if (slash <= 0)
            return Error(HttpStatusCode.Forbidden, "AuthFailure", "bad credential scope");
        var accessKey = credential[..slash];
        if (!string.Equals(accessKey, Ec2Harness.AccessKey, StringComparison.Ordinal))
            return Error(HttpStatusCode.Forbidden, "InvalidClientTokenId", "unknown access key");
        var scopeParts = credential[(slash + 1)..].Split('/');
        if (scopeParts.Length != 4)
            return Error(HttpStatusCode.Forbidden, "AuthFailure", "bad credential scope");
        var region = scopeParts[1];
        if (!DateTimeOffset.TryParseExact(
                amzDate, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            return Error(HttpStatusCode.Forbidden, "AuthFailure", "bad x-amz-date");
        }
        var host = request.RequestUri!.Host;
        var (expectedAuth, _) = Ec2SigV4Signer.Sign(
            Ec2Harness.AccessKey, Ec2Harness.SecretKey, region, host, body, timestamp);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedAuth), Encoding.UTF8.GetBytes(auth)))
        {
            return Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch", "signature mismatch");
        }
        return null;
    }

    private HttpResponseMessage RunInstances(Dictionary<string, string> form)
    {
        lock (_lock)
            RunBodies.Add(new Dictionary<string, string>(form, StringComparer.Ordinal));
        if (FailNextRun is { } failure)
        {
            FailNextRun = null;
            if (failure.StoreInstance)
                StoreRunInstance(form);
            return Error(failure.Status, failure.Code, failure.Message);
        }
        if (form.TryGetValue("MinCount", out var min) && form.TryGetValue("MaxCount", out var max)
            && (min != "1" || max != "1"))
        {
            return Error(HttpStatusCode.BadRequest, "InvalidParameterCombination", "exactly one instance is required");
        }
        if (!form.TryGetValue("ClientToken", out var token) || string.IsNullOrWhiteSpace(token))
            return Error(HttpStatusCode.BadRequest, "MissingParameter", "ClientToken is required");
        var instance = StoreRunInstance(form);
        return Xml("RunInstancesResponse", InstanceXml(instance, includeStateReason: false),
            requestId: "req-" + Guid.NewGuid().ToString("N"));
    }

    private FakeInstance StoreRunInstance(Dictionary<string, string> form)
    {
        var tags = ReadTagSpecifications(form, "instance");
        var name = tags.TryGetValue("Name", out var nameTag) ? nameTag : "codeybox-unnamed";
        var instanceId = NextInstanceId();
        var groupIds = form
            .Where(kv => kv.Key.StartsWith("NetworkInterface.1.Group.", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value)
            .ToList();
        var instance = new FakeInstance
        {
            InstanceId = instanceId,
            ImageId = form.GetValueOrDefault("ImageId", string.Empty),
            InstanceType = form.GetValueOrDefault("InstanceType", string.Empty),
            SubnetId = form.GetValueOrDefault("NetworkInterface.1.SubnetId", string.Empty),
            KeyName = form.GetValueOrDefault("KeyName", string.Empty),
            VpcId = Ec2Harness.VpcId,
            PublicIp = NextPublicIpOverride ?? "192.0.2.10",
            Tags = tags,
        };
        NextPublicIpOverride = null;
        instance.Tags["Name"] = name;
        foreach (var groupId in groupIds)
            instance.GroupIds.Add(groupId);
        Instances[instanceId] = instance;
        var volumeId = NextVolumeId();
        Volumes[volumeId] = new FakeVolume
        {
            VolumeId = volumeId,
            State = "in-use",
            InstanceId = instanceId,
            DeleteOnTermination = form.GetValueOrDefault("BlockDeviceMapping.1.Ebs.DeleteOnTermination", "true") == "true",
            Tags = new Dictionary<string, string>(ReadTagSpecifications(form, "volume"), StringComparer.Ordinal),
        };
        return instance;
    }

    private HttpResponseMessage DescribeInstances(Dictionary<string, string> form)
    {
        if (BlindDescribeCalls > 0)
        {
            BlindDescribeCalls--;
            return Xml("DescribeInstancesResponse", "<instancesSet/>", requestId: "req-blind");
        }
        List<FakeInstance> matches;
        lock (_lock)
        {
            var ids = IndexedValues(form, "InstanceId");
            var filters = ReadFilters(form);
            matches = Instances.Values
                .Where(i => (ids.Count == 0 || ids.Contains(i.InstanceId))
                    && filters.All(f => f.Value.Any(v =>
                        string.Equals(TagValueOf(i, f.Key), v, StringComparison.Ordinal))))
                .OrderBy(i => i.InstanceId, StringComparer.Ordinal)
                .ToList();
            foreach (var instance in matches)
            {
                instance.Polls++;
                if (ForceInstanceState is not null)
                    instance.State = ForceInstanceState;
                else if (instance.State == "pending" && instance.Polls > RunningAfterPolls)
                    instance.State = "running";
            }
        }
        string? nextToken = null;
        if (form.TryGetValue("MaxResults", out var maxRaw)
            && int.TryParse(maxRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max)
            && matches.Count > max)
        {
            matches = matches.Take(max).ToList();
            nextToken = "tok-" + Guid.NewGuid().ToString("N");
        }
        var builder = new StringBuilder("<instancesSet>");
        foreach (var instance in matches)
            builder.Append(InstanceXml(instance, includeStateReason: false));
        builder.Append("</instancesSet>");
        if (nextToken is not null)
            builder.Append("<nextToken>").Append(nextToken).Append("</nextToken>");
        return Xml("DescribeInstancesResponse", builder.ToString(), requestId: "req-describe");
    }

    private static string TagValueOf(FakeInstance instance, string filterName)
    {
        if (filterName.StartsWith("tag:", StringComparison.Ordinal)
            && instance.Tags.TryGetValue(filterName["tag:".Length..], out var value))
        {
            return value;
        }
        return "\0";
    }

    private HttpResponseMessage TerminateInstances(Dictionary<string, string> form)
    {
        if (FailInstanceDelete)
            return Error(HttpStatusCode.InternalServerError, "InternalError", "terminate failed");
        var ids = IndexedValues(form, "InstanceId");
        var builder = new StringBuilder("<instancesSet>");
        lock (_lock)
        {
            foreach (var id in ids)
            {
                if (!Instances.TryGetValue(id, out var instance))
                    return Error(HttpStatusCode.BadRequest, "InvalidInstanceID.NotFound", "no such instance");
                instance.State = "terminated";
                Log($"terminate-instance:{id}");
                foreach (var volume in Volumes.Values.Where(v => v.InstanceId == id))
                {
                    volume.InstanceId = volume.DeleteOnTermination ? volume.InstanceId : null;
                    if (volume.DeleteOnTermination)
                        Volumes.TryRemove(volume.VolumeId, out _);
                    else
                        volume.State = "available";
                }
                builder.Append("<item><instanceId>").Append(Esc(id)).Append("</instanceId>")
                    .Append("<currentState><code>48</code><name>terminated</name></currentState></item>");
            }
        }
        return Xml("TerminateInstancesResponse", builder.ToString(), requestId: "req-term");
    }

    private HttpResponseMessage DescribeImages(Dictionary<string, string> form)
    {
        var ids = IndexedValues(form, "ImageId");
        var builder = new StringBuilder("<imagesSet>");
        foreach (var id in ids)
        {
            if (!string.Equals(id, Ec2Harness.AmiId, StringComparison.Ordinal))
                return Error(HttpStatusCode.BadRequest, "InvalidAMIID.NotFound", "no such image");
            builder.Append("<item><imageId>").Append(Esc(id)).Append("</imageId>")
                .Append("<imageState>").Append(Esc(ImageState)).Append("</imageState>")
                .Append("<architecture>x86_64</architecture>")
                .Append("<rootDeviceName>/dev/sda1</rootDeviceName></item>");
        }
        return Xml("DescribeImagesResponse", builder.ToString(), requestId: "req-img");
    }

    private HttpResponseMessage ImportKeyPair(Dictionary<string, string> form)
    {
        if (FailNextImportKey is { } failure)
        {
            FailNextImportKey = null;
            return Error(failure.Status, failure.Code, failure.Message);
        }
        var name = form.GetValueOrDefault("KeyName", string.Empty);
        if (string.IsNullOrWhiteSpace(name))
            return Error(HttpStatusCode.BadRequest, "MissingParameter", "KeyName is required");
        lock (_lock)
        {
            if (KeyPairs.ContainsKey(name))
                return Error(HttpStatusCode.BadRequest, "InvalidKeyPair.Duplicate", "key pair exists");
            KeyPairs[name] = ReadTagSpecifications(form, "key-pair");
        }
        Log($"import-key:{name}");
        return Xml("ImportKeyPairResponse",
            "<keyName>" + Esc(name) + "</keyName><keyFingerprint>ff:ff</keyFingerprint>",
            requestId: "req-key");
    }

    private HttpResponseMessage DescribeKeyPairs(Dictionary<string, string> form)
    {
        var names = IndexedValues(form, "KeyName");
        var filters = ReadFilters(form);
        var builder = new StringBuilder("<keySet>");
        var found = false;
        lock (_lock)
        {
            foreach (var (name, tags) in KeyPairs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (names.Count > 0 && !names.Contains(name))
                    continue;
                if (!filters.All(f => f.Value.Any(v =>
                    f.Key.StartsWith("tag:", StringComparison.Ordinal)
                    && tags.TryGetValue(f.Key["tag:".Length..], out var tagValue)
                    && string.Equals(tagValue, v, StringComparison.Ordinal))))
                {
                    continue;
                }
                found = true;
                builder.Append("<item><keyName>").Append(Esc(name)).Append("</keyName>")
                    .Append("<keyFingerprint>ff:ff</keyFingerprint>")
                    .Append(TagSetXml(tags)).Append("</item>");
            }
        }
        if (names.Count > 0 && !found)
            return Error(HttpStatusCode.BadRequest, "InvalidKeyPair.NotFound", "no such key pair");
        return Xml("DescribeKeyPairsResponse", builder.ToString(), requestId: "req-keys");
    }

    private HttpResponseMessage DeleteKeyPair(Dictionary<string, string> form)
    {
        var name = form.GetValueOrDefault("KeyName", string.Empty);
        lock (_lock)
            KeyPairs.TryRemove(name, out _);
        Log($"delete-key:{name}");
        return Xml("DeleteKeyPairResponse", "<return>true</return>", requestId: "req-delkey");
    }

    private HttpResponseMessage CreateSecurityGroup(Dictionary<string, string> form)
    {
        if (FailNextCreateSecurityGroup is { } failure)
        {
            FailNextCreateSecurityGroup = null;
            return Error(failure.Status, failure.Code, failure.Message);
        }
        var name = form.GetValueOrDefault("GroupName", string.Empty);
        var vpcId = form.GetValueOrDefault("VpcId", string.Empty);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(vpcId))
            return Error(HttpStatusCode.BadRequest, "MissingParameter", "GroupName and VpcId are required");
        lock (_lock)
        {
            if (SecurityGroups.Values.Any(g =>
                string.Equals(g.GroupName, name, StringComparison.Ordinal)
                && string.Equals(g.VpcId, vpcId, StringComparison.Ordinal)))
            {
                return Error(HttpStatusCode.BadRequest, "InvalidGroup.Duplicate", "group exists");
            }
            var group = new FakeSecurityGroup
            {
                GroupId = NextGroupId(),
                GroupName = name,
                VpcId = vpcId,
                Tags = ReadTagSpecifications(form, "security-group"),
            };
            SecurityGroups[group.GroupId] = group;
            Log($"create-sg:{group.GroupId}");
            return Xml("CreateSecurityGroupResponse",
                "<return>true</return><groupId>" + Esc(group.GroupId) + "</groupId>",
                requestId: "req-sg");
        }
    }

    private HttpResponseMessage DescribeSecurityGroups(Dictionary<string, string> form)
    {
        var ids = IndexedValues(form, "GroupId");
        var filters = ReadFilters(form);
        var builder = new StringBuilder("<securityGroupInfo>");
        var found = false;
        lock (_lock)
        {
            foreach (var group in SecurityGroups.Values.OrderBy(g => g.GroupId, StringComparer.Ordinal))
            {
                if (ids.Count > 0 && !ids.Contains(group.GroupId))
                    continue;
                if (!filters.All(f => FilterMatchesGroup(f, group)))
                    continue;
                found = true;
                builder.Append("<item><groupId>").Append(Esc(group.GroupId)).Append("</groupId>")
                    .Append("<groupName>").Append(Esc(group.GroupName)).Append("</groupName>")
                    .Append("<vpcId>").Append(Esc(group.VpcId)).Append("</vpcId>")
                    .Append(TagSetXml(group.Tags)).Append("</item>");
            }
        }
        if (ids.Count > 0 && !found)
            return Error(HttpStatusCode.BadRequest, "InvalidGroup.NotFound", "no such group");
        return Xml("DescribeSecurityGroupsResponse", builder.ToString(), requestId: "req-sgs");
    }

    private static bool FilterMatchesGroup(KeyValuePair<string, List<string>> filter, FakeSecurityGroup group)
    {
        if (filter.Key == "group-name")
            return filter.Value.Contains(group.GroupName);
        if (filter.Key == "vpc-id")
            return filter.Value.Contains(group.VpcId);
        if (filter.Key.StartsWith("tag:", StringComparison.Ordinal)
            && group.Tags.TryGetValue(filter.Key["tag:".Length..], out var value))
        {
            return filter.Value.Contains(value);
        }
        return false;
    }

    private HttpResponseMessage AuthorizeSecurityGroup(Dictionary<string, string> form, bool ingress)
    {
        var groupId = form.GetValueOrDefault("GroupId", string.Empty);
        lock (_lock)
        {
            if (!SecurityGroups.TryGetValue(groupId, out var group))
                return Error(HttpStatusCode.BadRequest, "InvalidGroup.NotFound", "no such group");
            var rule = string.Join(";", form.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
            var rules = ingress ? group.IngressRules : group.EgressRules;
            if (rules.Contains(rule))
                return Error(HttpStatusCode.BadRequest, "InvalidPermission.Duplicate", "rule exists");
            rules.Add(rule);
            Log($"authorize-{(ingress ? "ingress" : "egress")}:{groupId}");
        }
        return Xml(ingress ? "AuthorizeSecurityGroupIngressResponse" : "AuthorizeSecurityGroupEgressResponse",
            "<return>true</return>", requestId: "req-auth");
    }

    private HttpResponseMessage RevokeSecurityGroupEgress(Dictionary<string, string> form)
    {
        var groupId = form.GetValueOrDefault("GroupId", string.Empty);
        lock (_lock)
        {
            if (!SecurityGroups.TryGetValue(groupId, out _))
                return Error(HttpStatusCode.BadRequest, "InvalidGroup.NotFound", "no such group");
            Log($"revoke-egress:{groupId}");
        }
        return Xml("RevokeSecurityGroupEgressResponse", "<return>true</return>", requestId: "req-revoke");
    }

    private HttpResponseMessage DeleteSecurityGroup(Dictionary<string, string> form)
    {
        var groupId = form.GetValueOrDefault("GroupId", string.Empty);
        lock (_lock)
        {
            if (!SecurityGroups.TryGetValue(groupId, out _))
                return Error(HttpStatusCode.BadRequest, "InvalidGroup.NotFound", "no such group");
            if (Instances.Values.Any(i => i.GroupIds.Contains(groupId)
                && i.State is not ("terminated" or "shutting-down")))
            {
                return Error(HttpStatusCode.BadRequest, "DependencyViolation", "group in use");
            }
            SecurityGroups.TryRemove(groupId, out _);
            Log($"delete-sg:{groupId}");
        }
        return Xml("DeleteSecurityGroupResponse", "<return>true</return>", requestId: "req-delsg");
    }

    private HttpResponseMessage AllocateAddress(Dictionary<string, string> form)
    {
        var allocationId = NextAllocationId();
        var address = new FakeAddress
        {
            AllocationId = allocationId,
            PublicIp = "203.0.113." + (10 + (Addresses.Count % 200)).ToString(CultureInfo.InvariantCulture),
            Tags = ReadTagSpecifications(form, "elastic-ip"),
        };
        Addresses[allocationId] = address;
        Log($"allocate-address:{allocationId}");
        return Xml("AllocateAddressResponse",
            "<publicIp>" + Esc(address.PublicIp) + "</publicIp><allocationId>" + Esc(allocationId)
            + "</allocationId><domain>vpc</domain>",
            requestId: "req-alloc");
    }

    private HttpResponseMessage AssociateAddress(Dictionary<string, string> form)
    {
        var allocationId = form.GetValueOrDefault("AllocationId", string.Empty);
        var instanceId = form.GetValueOrDefault("InstanceId", string.Empty);
        lock (_lock)
        {
            if (!Addresses.TryGetValue(allocationId, out var address))
                return Error(HttpStatusCode.BadRequest, "InvalidAllocationID.NotFound", "no such address");
            if (!Instances.TryGetValue(instanceId, out var instance))
                return Error(HttpStatusCode.BadRequest, "InvalidInstanceID.NotFound", "no such instance");
            address.AssociationId = NextAssociationId();
            address.InstanceId = instanceId;
            instance.PublicIp = address.PublicIp;
            Log($"associate-address:{allocationId}->{instanceId}");
            return Xml("AssociateAddressResponse",
                "<associationId>" + Esc(address.AssociationId) + "</associationId>",
                requestId: "req-assoc");
        }
    }

    private HttpResponseMessage DisassociateAddress(Dictionary<string, string> form)
    {
        var associationId = form.GetValueOrDefault("AssociationId", string.Empty);
        lock (_lock)
        {
            var address = Addresses.Values.FirstOrDefault(a =>
                string.Equals(a.AssociationId, associationId, StringComparison.Ordinal));
            if (address is null)
                return Error(HttpStatusCode.BadRequest, "InvalidAssociationID.NotFound", "no such association");
            address.AssociationId = null;
            address.InstanceId = null;
            Log($"disassociate-address:{associationId}");
        }
        return Xml("DisassociateAddressResponse", "<return>true</return>", requestId: "req-disassoc");
    }

    private HttpResponseMessage DescribeAddresses(Dictionary<string, string> form)
    {
        var ids = IndexedValues(form, "AllocationId");
        var filters = ReadFilters(form);
        var builder = new StringBuilder("<addressesSet>");
        var found = false;
        lock (_lock)
        {
            foreach (var address in Addresses.Values.OrderBy(a => a.AllocationId, StringComparer.Ordinal))
            {
                if (ids.Count > 0 && !ids.Contains(address.AllocationId))
                    continue;
                if (!filters.All(f => f.Key.StartsWith("tag:", StringComparison.Ordinal)
                    && address.Tags.TryGetValue(f.Key["tag:".Length..], out var value)
                    && f.Value.Contains(value)))
                {
                    continue;
                }
                found = true;
                builder.Append("<item><allocationId>").Append(Esc(address.AllocationId)).Append("</allocationId>")
                    .Append("<publicIp>").Append(Esc(address.PublicIp)).Append("</publicIp>");
                if (address.AssociationId is not null)
                    builder.Append("<associationId>").Append(Esc(address.AssociationId)).Append("</associationId>");
                if (address.InstanceId is not null)
                    builder.Append("<instanceId>").Append(Esc(address.InstanceId)).Append("</instanceId>");
                builder.Append(TagSetXml(address.Tags)).Append("</item>");
            }
        }
        if (ids.Count > 0 && !found)
            return Error(HttpStatusCode.BadRequest, "InvalidAllocationID.NotFound", "no such address");
        return Xml("DescribeAddressesResponse", builder.ToString(), requestId: "req-addrs");
    }

    private HttpResponseMessage ReleaseAddress(Dictionary<string, string> form)
    {
        var allocationId = form.GetValueOrDefault("AllocationId", string.Empty);
        lock (_lock)
        {
            if (!Addresses.TryRemove(allocationId, out _))
                return Error(HttpStatusCode.BadRequest, "InvalidAllocationID.NotFound", "no such address");
            Log($"release-address:{allocationId}");
        }
        return Xml("ReleaseAddressResponse", "<return>true</return>", requestId: "req-rel");
    }

    private HttpResponseMessage DescribeVolumes(Dictionary<string, string> form)
    {
        var filters = ReadFilters(form);
        List<FakeVolume> matches;
        lock (_lock)
        {
            matches = Volumes.Values
                .Where(v => filters.All(f => f.Key.StartsWith("tag:", StringComparison.Ordinal)
                    && v.Tags.TryGetValue(f.Key["tag:".Length..], out var value)
                    && f.Value.Contains(value)))
                .OrderBy(v => v.VolumeId, StringComparer.Ordinal)
                .ToList();
        }
        string? nextToken = null;
        if (form.TryGetValue("MaxResults", out var maxRaw)
            && int.TryParse(maxRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max)
            && matches.Count > max)
        {
            matches = matches.Take(max).ToList();
            nextToken = "tok-" + Guid.NewGuid().ToString("N");
        }
        var builder = new StringBuilder("<volumeSet>");
        foreach (var volume in matches)
        {
            builder.Append("<item><volumeId>").Append(Esc(volume.VolumeId)).Append("</volumeId>")
                .Append("<status>").Append(Esc(volume.State)).Append("</status>");
            if (volume.InstanceId is not null)
            {
                builder.Append("<attachmentSet><item><instanceId>").Append(Esc(volume.InstanceId))
                    .Append("</instanceId><deleteOnTermination>")
                    .Append(volume.DeleteOnTermination ? "true" : "false")
                    .Append("</deleteOnTermination></item></attachmentSet>");
            }
            builder.Append(TagSetXml(volume.Tags)).Append("</item>");
        }
        builder.Append("</volumeSet>");
        if (nextToken is not null)
            builder.Append("<nextToken>").Append(nextToken).Append("</nextToken>");
        return Xml("DescribeVolumesResponse", builder.ToString(), requestId: "req-vols");
    }

    private HttpResponseMessage DeleteVolume(Dictionary<string, string> form)
    {
        var volumeId = form.GetValueOrDefault("VolumeId", string.Empty);
        lock (_lock)
        {
            if (!Volumes.TryGetValue(volumeId, out var volume))
                return Error(HttpStatusCode.BadRequest, "InvalidVolume.NotFound", "no such volume");
            if (volume.State == "in-use")
                return Error(HttpStatusCode.BadRequest, "VolumeInUse", "volume attached");
            Volumes.TryRemove(volumeId, out _);
            Log($"delete-volume:{volumeId}");
        }
        return Xml("DeleteVolumeResponse", "<return>true</return>", requestId: "req-delvol");
    }

    private HttpResponseMessage Xml(string root, string inner, string requestId)
    {
        if (MalformedNextResponse)
        {
            MalformedNextResponse = false;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<" + root + "><broken", Encoding.UTF8, "text/xml"),
            };
        }
        if (OversizedNextResponse)
        {
            OversizedNextResponse = false;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<" + root + ">" + new string('x', 9 * 1024 * 1024) + "</" + root + ">",
                    Encoding.UTF8, "text/xml"),
            };
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"<{root} xmlns=\"http://ec2.amazonaws.com/doc/2016-11-15/\">{inner}<requestId>{requestId}</requestId></{root}>",
                Encoding.UTF8, "text/xml"),
        };
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        new(status)
        {
            Content = new StringContent(
                $"<Response><Errors><Error><Code>{Esc(code)}</Code><Message>{Esc(message)}</Message></Error></Errors>" +
                $"<RequestID>req-err</RequestID></Response>",
                Encoding.UTF8, "text/xml"),
        };

    private static string InstanceXml(FakeInstance instance, bool includeStateReason)
    {
        _ = includeStateReason;
        var builder = new StringBuilder("<item>");
        builder.Append("<instanceId>").Append(Esc(instance.InstanceId)).Append("</instanceId>")
            .Append("<imageId>").Append(Esc(instance.ImageId)).Append("</imageId>")
            .Append("<instanceState><code>16</code><name>").Append(Esc(instance.State)).Append("</name></instanceState>")
            .Append("<privateDnsName/>")
            .Append("<dnsName>").Append(Esc(instance.InstanceId)).Append(".example.invalid</dnsName>")
            .Append("<keyName>").Append(Esc(instance.KeyName)).Append("</keyName>")
            .Append("<instanceType>").Append(Esc(instance.InstanceType)).Append("</instanceType>")
            .Append("<ipAddress>").Append(Esc(instance.PublicIp)).Append("</ipAddress>")
            .Append("<privateIpAddress>10.0.0.5</privateIpAddress>")
            .Append("<subnetId>").Append(Esc(instance.SubnetId)).Append("</subnetId>")
            .Append("<vpcId>").Append(Esc(instance.VpcId)).Append("</vpcId>");
        builder.Append("<groupSet>");
        foreach (var groupId in instance.GroupIds)
            builder.Append("<item><groupId>").Append(Esc(groupId)).Append("</groupId></item>");
        builder.Append("</groupSet>");
        builder.Append(TagSetXml(instance.Tags));
        builder.Append("</item>");
        return builder.ToString();
    }

    private static string TagSetXml(Dictionary<string, string> tags)
    {
        var builder = new StringBuilder("<tagSet>");
        foreach (var (key, value) in tags.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            builder.Append("<item><key>").Append(Esc(key)).Append("</key><value>")
                .Append(Esc(value)).Append("</value></item>");
        }
        builder.Append("</tagSet>");
        return builder.ToString();
    }

    private static Dictionary<string, string> ReadTagSpecifications(Dictionary<string, string> form, string resourceType)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var spec = 1; spec <= 16; spec++)
        {
            if (!form.TryGetValue($"TagSpecification.{spec}.ResourceType", out var type))
                continue;
            if (!string.Equals(type, resourceType, StringComparison.Ordinal))
                continue;
            for (var tag = 1; tag <= 128; tag++)
            {
                if (!form.TryGetValue($"TagSpecification.{spec}.Tag.{tag}.Key", out var key))
                    break;
                tags[key] = form.GetValueOrDefault($"TagSpecification.{spec}.Tag.{tag}.Value", string.Empty);
            }
        }
        return tags;
    }

    private static Dictionary<string, List<string>> ReadFilters(Dictionary<string, string> form)
    {
        var filters = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 1; index <= 32; index++)
        {
            if (!form.TryGetValue($"Filter.{index}.Name", out var name))
                break;
            filters[name] = IndexedValues(form, $"Filter.{index}.Value");
        }
        return filters;
    }

    private static List<string> IndexedValues(Dictionary<string, string> form, string prefix)
    {
        var values = new List<string>();
        for (var index = 1; index <= 1024; index++)
        {
            if (!form.TryGetValue($"{prefix}.{index}", out var value))
                break;
            values.Add(value);
        }
        return values;
    }

    internal static Dictionary<string, string> ParseForm(string body)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            var key = equals < 0 ? part : part[..equals];
            var value = equals < 0 ? string.Empty : part[(equals + 1)..];
            form[Uri.UnescapeDataString(key.Replace('+', ' '))] =
                Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return form;
    }

    internal static string Esc(string value) =>
        System.Security.SecurityElement.Escape(value) ?? string.Empty;
}
