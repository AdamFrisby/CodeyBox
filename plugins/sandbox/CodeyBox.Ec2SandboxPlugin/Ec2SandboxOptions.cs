using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Ec2SandboxPlugin;

/// <summary>
/// Operator knobs for the AWS EC2 sandbox-provider plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.ec2-sandbox</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read on
/// every operation so edits take effect without a host restart.
///
/// <para>Secrets never appear here: the AWS access key, secret key, and
/// optional session token are read from the host credential chain (process
/// environment) at call time, so rotation propagates without a restart and a
/// config file can never carry secret material. Only the variable
/// <em>names</em> are configured.</para>
///
/// <para>Disabled by default: the provider refuses to operate until
/// <c>Enabled</c> is set and the plugin is allowlisted. The provider kind
/// contributed by this plugin is <c>ec2</c>. No baseline bake/snapshot
/// capability is advertised: acquisitions always boot the configured pinned
/// AMI. Instances launch with no IAM instance profile, as on-demand (never
/// spot), with exactly one instance per <c>RunInstances</c> call.</para>
/// </summary>
public sealed record Ec2SandboxOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.ec2-sandbox";

    /// <summary>Sandbox provider kind this plugin contributes.</summary>
    public const string ProviderKind = "ec2";

    /// <summary>EC2 Query API version sent on every request.</summary>
    public const string ApiVersion = "2016-11-15";

    /// <summary>Master switch. Default false: the provider refuses to operate until enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// AWS region for sandbox instances (e.g. <c>us-east-1</c>). Required:
    /// empty refuses provisioning. The service endpoint is derived as
    /// <c>https://ec2.{region}.amazonaws.com</c> unless <see cref="ServiceUrl"/>
    /// overrides it (loopback test fakes only).
    /// </summary>
    public string Region { get; init; } = string.Empty;

    /// <summary>
    /// Optional availability zone pin (e.g. <c>us-east-1a</c>). Empty means
    /// the configured subnet decides placement. When set it must belong to
    /// <see cref="Region"/> and agree with the subnet's zone.
    /// </summary>
    public string Zone { get; init; } = string.Empty;

    /// <summary>
    /// Explicit EC2 endpoint override (fake handlers in isolated tests).
    /// Empty (production) derives <c>https://ec2.{region}.amazonaws.com</c>.
    /// Must be https (http only for loopback test URLs under
    /// <see cref="AllowUnsafeHttp"/>).
    /// </summary>
    public string ServiceUrl { get; init; } = string.Empty;

    /// <summary>
    /// Pinned AMI id each sandbox boots (e.g. <c>ami-0abcdef1234567890</c>).
    /// Required: empty refuses provisioning. Only concrete <c>ami-</c> ids are
    /// accepted — names, aliases, and SSM aliases move, pins do not. A spec
    /// <c>ImageReference</c> overrides this per acquisition with the same rules.
    /// </summary>
    public string AmiId { get; init; } = string.Empty;

    /// <summary>
    /// EC2 instance type each sandbox boots (e.g. <c>t3.medium</c>).
    /// Required: empty refuses provisioning. No spot, fleet, or autoscaling
    /// options exist: every acquisition is one on-demand instance.
    /// </summary>
    public string InstanceType { get; init; } = string.Empty;

    /// <summary>
    /// Explicit subnet id the sandbox boots in (e.g.
    /// <c>subnet-0abcdef1234567890</c>). Required: empty refuses
    /// provisioning. There is deliberately no default-VPC fallback — the
    /// operator names the subnet every sandbox attaches to.
    /// </summary>
    public string SubnetId { get; init; } = string.Empty;

    /// <summary>
    /// VPC id used when <see cref="CreateSecurityGroup"/> is set. Required
    /// then; ignored (but still validated when non-empty) otherwise. Never
    /// defaulted: the provider never assumes the account default VPC.
    /// </summary>
    public string VpcId { get; init; } = string.Empty;

    /// <summary>
    /// Caller-owned security group ids attached in addition to (or instead
    /// of) the per-sandbox group. These are never created, modified, or
    /// deleted by the provider — ownership stays with the caller.
    /// </summary>
    public IReadOnlyList<string> SecurityGroupIds { get; init; } = [];

    /// <summary>
    /// Create one per-sandbox security group (SSH ingress from
    /// <see cref="OrchestratorSshCidrs"/> only, explicit egress rules) and
    /// delete it on teardown. Default true. Requires <see cref="VpcId"/>.
    /// </summary>
    public bool CreateSecurityGroup { get; init; } = true;

    /// <summary>
    /// Attach a public IPv4 address at launch via the network interface.
    /// Default true. Disable only when <see cref="AllocateElasticIp"/> gives
    /// the sandbox a reachable address another way.
    /// </summary>
    public bool AssociatePublicIp { get; init; } = true;

    /// <summary>
    /// Allocate one VPC Elastic IP per sandbox, associate it to the instance,
    /// and release it on teardown. Default false: the sandbox is reached on
    /// its launch-time public address.
    /// </summary>
    public bool AllocateElasticIp { get; init; }

    /// <summary>
    /// Root EBS volume size in GiB (8–512). Applied as an explicit block
    /// device mapping on <c>RunInstances</c>; the AMI default applies when
    /// equal to the AMI's size semantics — the value is always sent so
    /// capacity is bounded and visible.
    /// </summary>
    public int VolumeSizeGb { get; init; } = 20;

    /// <summary>
    /// Root EBS volume type (e.g. <c>gp3</c>). Compared verbatim against the
    /// allowlist <c>gp2/gp3/io1/io2/st1/sc1/standard</c>.
    /// </summary>
    public string VolumeType { get; init; } = "gp3";

    /// <summary>
    /// Root device name for the block device mapping (e.g.
    /// <c>/dev/sda1</c>). Must match the AMI's root device or the launch is
    /// refused by the service.
    /// </summary>
    public string RootDeviceName { get; init; } = "/dev/sda1";

    /// <summary>
    /// Delete the root EBS volume when the instance terminates. Default true.
    /// When false the provider deletes the owned volume explicitly after the
    /// termination is confirmed, and the orphan sweep reaps leftovers.
    /// </summary>
    public bool DeleteOnTermination { get; init; } = true;

    /// <summary>
    /// Whether guest IMDS access is left enabled. The provider always sets
    /// <c>HttpTokens=require</c> (IMDSv2) and a hop limit of 1; credentials
    /// are never present because instances launch with no IAM profile.
    /// </summary>
    public bool EnableInstanceMetadata { get; init; } = true;

    /// <summary>
    /// Guest login user for SSH (the AMI's default user, e.g.
    /// <c>ubuntu</c> for Ubuntu cloud images — cloud-init injects the
    /// per-sandbox public key into that user's authorized_keys).
    /// </summary>
    public string SshUser { get; init; } = "ubuntu";

    /// <summary>
    /// Owner host id stamped into instance/volume/address/keypair/security
    /// group tags so leak reaping after a restart only touches this host's
    /// sandboxes. Empty falls back to the machine name at call time.
    /// </summary>
    public string OwnerId { get; init; } = string.Empty;

    /// <summary>
    /// Name prefix for every instance (and its Name tag) this provider
    /// creates. Must start with <c>codeybox-</c> so leak reaping can scope
    /// candidates — though a prefix alone never authorizes deletion: tags are
    /// re-verified every time.
    /// </summary>
    public string InstanceNamePrefix { get; init; } = "codeybox-";

    /// <summary>Name prefix for per-sandbox EC2 key pairs (must start with <c>codeybox-</c>).</summary>
    public string KeyNamePrefix { get; init; } = "codeybox-";

    /// <summary>Name prefix for per-sandbox security groups (must start with <c>codeybox-</c>).</summary>
    public string SecurityGroupNamePrefix { get; init; } = "codeybox-sg-";

    /// <summary>
    /// Name of the environment variable holding the AWS access key id.
    /// The value is never read from configuration files.
    /// </summary>
    public string AccessKeyEnvVar { get; init; } = Ec2CredentialChain.AccessKeyEnvVarName;

    /// <summary>
    /// Name of the environment variable holding the AWS secret access key.
    /// The value is never read from configuration files, never logged, and
    /// never persisted — and never copied into guest user-data.
    /// </summary>
    public string SecretKeyEnvVar { get; init; } = Ec2CredentialChain.SecretKeyEnvVarName;

    /// <summary>
    /// Name of the environment variable holding the optional AWS session
    /// token. Empty value means long-lived credentials; a configured name
    /// with a missing value is not an error.
    /// </summary>
    public string SessionTokenEnvVar { get; init; } = Ec2CredentialChain.SessionTokenEnvVarName;

    /// <summary>
    /// CIDR list allowed to reach TCP/22 by the per-sandbox security group —
    /// the orchestrator egress addresses. Compared and stored verbatim; each
    /// entry must parse as a CIDR. Required non-empty when
    /// <see cref="CreateSecurityGroup"/> is set: without it neither the
    /// provider nor any operator could SSH in, so provisioning refuses to run.
    /// </summary>
    public IReadOnlyList<string> OrchestratorSshCidrs { get; init; } = [];

    /// <summary>
    /// DNS server IPs permitted for outbound UDP/TCP 53 (best-effort defence
    /// in depth; the kind stays NotEnforced). Empty means no DNS egress rule.
    /// </summary>
    public IReadOnlyList<string> DnsServerIps { get; init; } = ["1.1.1.1", "8.8.8.8"];

    /// <summary>
    /// NTP server IPs permitted for outbound UDP 123 (best-effort defence in
    /// depth). Empty means no NTP egress rule.
    /// </summary>
    public IReadOnlyList<string> NtpServerIps { get; init; } = ["1.1.1.1", "8.8.8.8"];

    /// <summary>Upper bound on security-group rules managed per sandbox (1–1024).</summary>
    public int MaxSecurityGroupRules { get; init; } = 128;

    /// <summary>Per-request HTTP timeout for EC2 calls, in seconds (1–600).</summary>
    public int HttpTimeoutSeconds { get; init; } = Ec2ClientLimits.DefaultHttpTimeoutSeconds;

    /// <summary>Default ceiling for an instance-status wait, in seconds (30–3600).</summary>
    public int ReadyTimeoutSeconds { get; init; } = 600;

    /// <summary>Base delay between instance-status polls, in milliseconds (200–60000). Backs off exponentially.</summary>
    public int PollIntervalMilliseconds { get; init; } = Ec2ClientLimits.DefaultPollIntervalMilliseconds;

    /// <summary>Ceiling for the exponential poll backoff, in milliseconds (1000–120000).</summary>
    public int MaxPollIntervalMilliseconds { get; init; } = Ec2ClientLimits.DefaultMaxPollIntervalMilliseconds;

    /// <summary>Upper bound on a single decoded API response body, in bytes (64 KiB–64 MiB).</summary>
    public int MaxResponseBytes { get; init; } = Ec2ClientLimits.DefaultMaxResponseBytes;

    /// <summary>Upper bound on items collected from one Describe* operation, across pages (1–100000).</summary>
    public int MaxListItems { get; init; } = Ec2ClientLimits.DefaultMaxListItems;

    /// <summary>Maximum Describe* pages walked by a paged listing (1–500). Hitting the cap fails loudly.</summary>
    public int MaxListPages { get; init; } = Ec2ClientLimits.DefaultMaxListPages;

    /// <summary>Upper bound on cloud-config user-data bytes sent on RunInstances (1 KiB–1 MiB).</summary>
    public int MaxUserDataBytes { get; init; } = Ec2ClientLimits.DefaultMaxUserDataBytes;

    /// <summary>Ceiling for the SSH-readiness wait, in seconds (30–3600).</summary>
    public int SshReadyTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// Base backoff (seconds, 5–3600) stamped on provisioning-deferred
    /// exceptions so the orchestrator rechecks quota/capacity after a bounded
    /// wait rather than hot-looping against a rejecting cloud.
    /// </summary>
    public int ProvisioningRecheckSeconds { get; init; } = 60;

    /// <summary>Maximum RunInstances attempts after an ambiguous (unknown-outcome) create (1–5).</summary>
    public int MaxRunAttempts { get; init; } = 2;

    /// <summary>OpenSSH client binary for the data plane. Resolved via $PATH when bare.</summary>
    public string SshBinary { get; init; } = "ssh";

    /// <summary>ssh-keygen binary used for ephemeral per-sandbox keypairs. Resolved via $PATH when bare.</summary>
    public string SshKeygenBinary { get; init; } = "ssh-keygen";

    /// <summary>SSH port on the guest (1–65535).</summary>
    public int SshPort { get; init; } = 22;

    /// <summary>OpenSSH ConnectTimeout for data-plane connections, in seconds (1–300).</summary>
    public int SshConnectTimeoutSeconds { get; init; } = 10;

    /// <summary>Per-host DNS timeout when resolving AllowedHosts to egress IPs, in seconds (1–120).</summary>
    public int DnsTimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// Test hook: allow plain-http EC2 endpoints, but only for loopback hosts
    /// (localhost / 127.0.0.1 / ::1). Remote http URLs are refused even with
    /// this set — signed requests still carry credential-derived signatures.
    /// Never set in production.
    /// </summary>
    public bool AllowUnsafeHttp { get; init; }

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults via <see cref="PluginConfigReaders"/>
    /// so a bad hot-reload never crashes an operation.
    /// </summary>
    public static Ec2SandboxOptions FromConfiguration(IConfigurationSection? section)
    {
        var defaults = new Ec2SandboxOptions();
        if (section is null)
            return defaults;

        return new Ec2SandboxOptions
        {
            Enabled = PluginConfigReaders.ReadBool(section, "Enabled", defaults.Enabled),
            Region = (section["Region"] ?? string.Empty).Trim(),
            Zone = (section["Zone"] ?? string.Empty).Trim(),
            ServiceUrl = (section["ServiceUrl"] ?? string.Empty).Trim().TrimEnd('/'),
            AmiId = (section["AmiId"] ?? string.Empty).Trim(),
            InstanceType = (section["InstanceType"] ?? string.Empty).Trim(),
            SubnetId = (section["SubnetId"] ?? string.Empty).Trim(),
            VpcId = (section["VpcId"] ?? string.Empty).Trim(),
            SecurityGroupIds = PluginConfigReaders.ReadList(
                section.GetSection("SecurityGroupIds"), defaults.SecurityGroupIds),
            CreateSecurityGroup = PluginConfigReaders.ReadBool(
                section, "CreateSecurityGroup", defaults.CreateSecurityGroup),
            AssociatePublicIp = PluginConfigReaders.ReadBool(
                section, "AssociatePublicIp", defaults.AssociatePublicIp),
            AllocateElasticIp = PluginConfigReaders.ReadBool(
                section, "AllocateElasticIp", defaults.AllocateElasticIp),
            VolumeSizeGb = ReadClampedInt(section, "VolumeSizeGb", defaults.VolumeSizeGb, 8, 512),
            VolumeType = PluginConfigReaders.ReadNonEmpty(section, "VolumeType", defaults.VolumeType),
            RootDeviceName = PluginConfigReaders.ReadNonEmpty(section, "RootDeviceName", defaults.RootDeviceName),
            DeleteOnTermination = PluginConfigReaders.ReadBool(
                section, "DeleteOnTermination", defaults.DeleteOnTermination),
            EnableInstanceMetadata = PluginConfigReaders.ReadBool(
                section, "EnableInstanceMetadata", defaults.EnableInstanceMetadata),
            SshUser = PluginConfigReaders.ReadNonEmpty(section, "SshUser", defaults.SshUser),
            OwnerId = (section["OwnerId"] ?? string.Empty).Trim(),
            InstanceNamePrefix = PluginConfigReaders.ReadNonEmpty(section, "InstanceNamePrefix", defaults.InstanceNamePrefix),
            KeyNamePrefix = PluginConfigReaders.ReadNonEmpty(section, "KeyNamePrefix", defaults.KeyNamePrefix),
            SecurityGroupNamePrefix = PluginConfigReaders.ReadNonEmpty(
                section, "SecurityGroupNamePrefix", defaults.SecurityGroupNamePrefix),
            AccessKeyEnvVar = PluginConfigReaders.ReadNonEmpty(
                section, "AccessKeyEnvVar", defaults.AccessKeyEnvVar),
            SecretKeyEnvVar = PluginConfigReaders.ReadNonEmpty(
                section, "SecretKeyEnvVar", defaults.SecretKeyEnvVar),
            SessionTokenEnvVar = PluginConfigReaders.ReadNonEmpty(
                section, "SessionTokenEnvVar", defaults.SessionTokenEnvVar),
            OrchestratorSshCidrs = PluginConfigReaders.ReadList(
                section.GetSection("OrchestratorSshCidrs"), defaults.OrchestratorSshCidrs),
            DnsServerIps = PluginConfigReaders.ReadList(section.GetSection("DnsServerIps"), defaults.DnsServerIps),
            NtpServerIps = PluginConfigReaders.ReadList(section.GetSection("NtpServerIps"), defaults.NtpServerIps),
            MaxSecurityGroupRules = ReadClampedInt(section, "MaxSecurityGroupRules", defaults.MaxSecurityGroupRules, 1, 1024),
            HttpTimeoutSeconds = ReadClampedInt(section, "HttpTimeoutSeconds", defaults.HttpTimeoutSeconds, 1, 600),
            ReadyTimeoutSeconds = ReadClampedInt(section, "ReadyTimeoutSeconds", defaults.ReadyTimeoutSeconds, 30, 3600),
            PollIntervalMilliseconds = ReadClampedInt(section, "PollIntervalMilliseconds", defaults.PollIntervalMilliseconds, 200, 60_000),
            MaxPollIntervalMilliseconds = ReadClampedInt(section, "MaxPollIntervalMilliseconds", defaults.MaxPollIntervalMilliseconds, 1000, 120_000),
            MaxResponseBytes = ReadClampedInt(section, "MaxResponseBytes", defaults.MaxResponseBytes, 64 * 1024, 64 * 1024 * 1024),
            MaxListItems = ReadClampedInt(section, "MaxListItems", defaults.MaxListItems, 1, 100_000),
            MaxListPages = ReadClampedInt(section, "MaxListPages", defaults.MaxListPages, 1, 500),
            MaxUserDataBytes = ReadClampedInt(section, "MaxUserDataBytes", defaults.MaxUserDataBytes, 1024, 1024 * 1024),
            SshReadyTimeoutSeconds = ReadClampedInt(
                section, "SshReadyTimeoutSeconds", defaults.SshReadyTimeoutSeconds, 30, 3600),
            ProvisioningRecheckSeconds = ReadClampedInt(
                section, "ProvisioningRecheckSeconds", defaults.ProvisioningRecheckSeconds, 5, 3600),
            MaxRunAttempts = ReadClampedInt(section, "MaxRunAttempts", defaults.MaxRunAttempts, 1, 5),
            SshBinary = PluginConfigReaders.ReadNonEmpty(section, "SshBinary", defaults.SshBinary),
            SshKeygenBinary = PluginConfigReaders.ReadNonEmpty(section, "SshKeygenBinary", defaults.SshKeygenBinary),
            SshPort = ReadClampedInt(section, "SshPort", defaults.SshPort, 1, 65535),
            SshConnectTimeoutSeconds = ReadClampedInt(
                section, "SshConnectTimeoutSeconds", defaults.SshConnectTimeoutSeconds, 1, 300),
            DnsTimeoutSeconds = ReadClampedInt(
                section, "DnsTimeoutSeconds", defaults.DnsTimeoutSeconds, 1, 120),
            AllowUnsafeHttp = PluginConfigReaders.ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
        };
    }

    private static int ReadClampedInt(
        IConfigurationSection section, string name, int defaultValue, int min, int max) =>
        Math.Clamp(PluginConfigReaders.ReadInt(section, name, defaultValue), min, max);

    /// <summary>Projects the size/timeout knobs onto the EC2 client's bounds record.</summary>
    public Ec2ClientLimits ToClientLimits() => new()
    {
        HttpTimeout = TimeSpan.FromSeconds(HttpTimeoutSeconds),
        PollInterval = TimeSpan.FromMilliseconds(PollIntervalMilliseconds),
        MaxPollInterval = TimeSpan.FromMilliseconds(MaxPollIntervalMilliseconds),
        MaxResponseBytes = MaxResponseBytes,
        MaxListItems = MaxListItems,
        MaxListPages = MaxListPages,
        MaxUserDataBytes = MaxUserDataBytes,
        AllowUnsafeHttp = AllowUnsafeHttp,
    };
}
