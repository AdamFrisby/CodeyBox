using System.Net;
using CodeyBox.Core;
using CodeyBox.HetznerSandboxPlugin;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the hetzner provider's pure seams: cloud-init rendering,
/// firewall planning, options binding/clamps, the token credential chain,
/// mount validation, and label/ownership helpers.
/// </summary>
public sealed class HetznerPolicyTests
{
    private const string ClientKey = "ssh-ed25519 AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HostKey = "ssh-ed25519 BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private static HetznerCloudInitSpec ValidSpec() => new(
        "codeybox-abc123",
        "tester",
        ClientKey,
        "-----BEGIN OPENSSH PRIVATE KEY-----\nfake-host-private\n-----END OPENSSH PRIVATE KEY-----\n",
        HostKey,
        [new HetznerTmpfsMount("/run/codeybox/creds", 8L * 1024 * 1024)]);

    [Fact]
    public void CloudInit_Renders_PinnedHostKey_Tmpfs_And_NoTokenField()
    {
        var rendered = HetznerCloudInit.Build(ValidSpec());
        Assert.Contains("hostname: codeybox-abc123", rendered, StringComparison.Ordinal);
        Assert.Contains("name: tester", rendered, StringComparison.Ordinal);
        Assert.Contains(ClientKey, rendered, StringComparison.Ordinal);
        Assert.Contains(HostKey, rendered, StringComparison.Ordinal);
        Assert.Contains("ssh_genkeytypes: ['ed25519']", rendered, StringComparison.Ordinal);
        Assert.Contains("/run/codeybox/creds", rendered, StringComparison.Ordinal);
        Assert.Contains("mkdir, -p, /work", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("HCLOUD", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudInit_Rejects_BadInputs()
    {
        Assert.Throws<ArgumentException>(() => HetznerCloudInit.Build(ValidSpec() with { Hostname = "BAD HOST!" }));
        Assert.Throws<ArgumentException>(() => HetznerCloudInit.Build(ValidSpec() with { Hostname = "" }));
        Assert.Throws<ArgumentException>(() => HetznerCloudInit.Build(ValidSpec() with { SshUser = "0bad" }));
        Assert.Throws<ArgumentException>(() => HetznerCloudInit.Build(ValidSpec() with { ClientPublicKey = "ssh-rsa AAAA" }));
        Assert.Throws<ArgumentException>(() => HetznerCloudInit.Build(ValidSpec() with { ClientPublicKey = "ssh-ed25519 a\nb" }));
        Assert.Throws<ArgumentException>(() => HetznerCloudInit.Build(ValidSpec() with { HostPrivateKey = "not-a-key" }));
        Assert.Throws<ArgumentException>(() => HetznerCloudInit.Build(ValidSpec() with
        {
            TmpfsMounts = [new HetznerTmpfsMount("relative/path", 1024)],
        }));
        Assert.Throws<ArgumentException>(() => HetznerCloudInit.Build(ValidSpec() with
        {
            TmpfsMounts = [new HetznerTmpfsMount("/run/../escape", 1024)],
        }));
        Assert.Throws<ArgumentException>(() => HetznerCloudInit.Build(ValidSpec() with
        {
            TmpfsMounts = [new HetznerTmpfsMount("/run/codeybox/creds", 0)],
        }));
    }

    [Fact]
    public void FirewallPolicy_Plans_Ssh_Dns_Ntp_And_Allowlist()
    {
        var rules = HetznerFirewallPolicy.BuildRules(
            ["203.0.113.0/24"],
            [IPAddress.Parse("93.184.216.34")],
            ["1.1.1.1"],
            ["9.9.9.9"],
            maxRules: 128);

        var ssh = Assert.Single(rules, r => r.Direction == "in");
        Assert.Equal("tcp", ssh.Protocol);
        Assert.Equal("22", ssh.Port);
        Assert.Equal(["203.0.113.0/24"], ssh.SourceIps);

        Assert.Contains(rules, r =>
            r.Direction == "out" && r.Protocol == "udp" && r.Port == "53"
            && r.DestinationIps.Contains("1.1.1.1/32"));
        Assert.Contains(rules, r =>
            r.Direction == "out" && r.Protocol == "tcp" && r.Port == "53"
            && r.DestinationIps.Contains("1.1.1.1/32"));
        Assert.Contains(rules, r =>
            r.Direction == "out" && r.Protocol == "udp" && r.Port == "123"
            && r.DestinationIps.Contains("9.9.9.9/32"));
        // Full-range openings say so explicitly — never a null port.
        Assert.Contains(rules, r =>
            r.Direction == "out" && r.Protocol == "tcp" && r.Port == "1-65535"
            && r.DestinationIps.Contains("93.184.216.34/32"));
        Assert.DoesNotContain(rules, r => r.Port is null);
    }

    [Fact]
    public void FirewallPolicy_EmptyAllowlist_Opens_Nothing_Beyond_Ssh()
    {
        var rules = HetznerFirewallPolicy.BuildRules(
            ["203.0.113.0/24"], [], [], [], maxRules: 128);
        Assert.Single(rules);
        Assert.Equal("in", rules[0].Direction);
    }

    [Fact]
    public void FirewallPolicy_Rejects_BadCidr_And_Overflow()
    {
        Assert.Throws<ArgumentException>(() => HetznerFirewallPolicy.BuildRules(
            ["not-a-cidr"], [], [], [], maxRules: 128));
        Assert.Throws<InvalidOperationException>(() => HetznerFirewallPolicy.BuildRules(
            ["203.0.113.0/24"], [IPAddress.Parse("93.184.216.34")], [], [], maxRules: 1));
    }

    [Fact]
    public void FirewallRule_Rejects_IcmpPorts_And_BadShapes()
    {
        Assert.Throws<ArgumentException>(() =>
            new HetznerFirewallRule("in", "icmp", "22", ["0.0.0.0/0"], [], "x").Validate());
        Assert.Throws<ArgumentException>(() =>
            new HetznerFirewallRule("out", "tcp", null, [], ["0.0.0.0/0"], "x").Validate());
        Assert.Throws<ArgumentException>(() =>
            new HetznerFirewallRule("sideways", "tcp", "22", ["0.0.0.0/0"], [], "x").Validate());
        Assert.Throws<ArgumentException>(() =>
            new HetznerFirewallRule("in", "tcp", "abc", ["0.0.0.0/0"], [], "x").Validate());
        Assert.Throws<ArgumentException>(() =>
            new HetznerFirewallRule("in", "tcp", "0-70000", ["0.0.0.0/0"], [], "x").Validate());
        Assert.Throws<ArgumentException>(() =>
            new HetznerFirewallRule("in", "tcp", "22", [], [], "x").Validate());
        new HetznerFirewallRule("in", "esp", null, ["0.0.0.0/0"], [], "x").Validate();
        new HetznerFirewallRule("in", "gre", null, ["0.0.0.0/0"], [], "x").Validate();
    }

    [Fact]
    public void FirewallPolicy_Normalizes_BareIps_To_Singletons()
    {
        Assert.Equal("203.0.113.7/32", HetznerFirewallPolicy.NormalizeCidr("203.0.113.7", "test"));
        Assert.Equal("2001:db8::1/128", HetznerFirewallPolicy.NormalizeCidr("2001:db8::1", "test"));
        Assert.Throws<ArgumentException>(() => HetznerFirewallPolicy.NormalizeCidr("203.0.113.0/33", "test"));
    }

    [Fact]
    public void Options_Bind_And_Clamp()
    {
        Assert.False(HetznerSandboxOptions.FromConfiguration(null).Enabled);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Test:Enabled"] = "true",
            ["Test:Image"] = "  ",
            ["Test:ReadyTimeoutSeconds"] = "-5",
            ["Test:MaxFirewallRules"] = "1",
            ["Test:MaxListItems"] = "999999",
            ["Test:MaxResponseBytes"] = "-1",
            ["Test:OrchestratorSshCidrs:0"] = " 203.0.113.0/24 ",
        }).Build();
        var options = HetznerSandboxOptions.FromConfiguration(config.GetSection("Test"));
        Assert.True(options.Enabled);
        Assert.Equal(string.Empty, options.Image);
        Assert.Equal(30, options.ReadyTimeoutSeconds);
        Assert.Equal(8, options.MaxFirewallRules);
        Assert.Equal(100000, options.MaxListItems);
        Assert.Equal(65536, options.MaxResponseBytes);
        Assert.Equal(["203.0.113.0/24"], options.OrchestratorSshCidrs);
        Assert.Equal(HetznerClientLimits.DefaultMaxResponseBytes, new HetznerSandboxOptions().MaxResponseBytes);
    }

    [Fact]
    public void Credentials_MissingToken_NamesVariable_And_RedactsToString()
    {
        var options = new HetznerSandboxOptions { Enabled = true };
        var ex = Assert.Throws<InvalidOperationException>(
            () => HetznerCredentials.Resolve(options, _ => null));
        Assert.Contains("HCLOUD_TOKEN", ex.Message, StringComparison.Ordinal);

        var creds = HetznerCredentials.Resolve(options, name => name == "HCLOUD_TOKEN" ? "s3cret" : null);
        Assert.Equal(new Uri("https://api.hetzner.cloud/v1"), creds.ApiBaseUrl);
        Assert.DoesNotContain("s3cret", creds.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ServerIpv6_Uses_FirstHostAddress_And_Refuses_Garbage()
    {
        Assert.Equal(
            "2001:db8::1",
            HetznerSandboxProvider.ServerIpv6Address("2001:db8::/64"));
        Assert.Equal(
            "2001:db8::5",
            HetznerSandboxProvider.ServerIpv6Address("2001:db8::5"));
        Assert.Null(HetznerSandboxProvider.ServerIpv6Address("not-an-address"));
        Assert.Null(HetznerSandboxProvider.ServerIpv6Address("192.0.2.1"));
    }

    [Fact]
    public void OwnershipLabels_Shape_And_Sanitize()
    {
        var labels = HetznerSandboxProvider.OwnershipLabels(
            "test-host", "work:item/1", "req123", DateTimeOffset.FromUnixTimeSeconds(1700000000));
        Assert.Equal("true", labels["codeybox-owned"]);
        Assert.Equal("test-host", labels["codeybox-owner"]);
        Assert.Equal("req123", labels["codeybox-request"]);
        Assert.Equal("1700000000", labels["codeybox-created"]);
        Assert.Equal("work-item-1", labels["codeybox-work-item"]);
        Assert.Equal("none", HetznerSandboxProvider.SanitizeLabelValue("   "));
    }

    [Fact]
    public void Provider_Identity_Is_Honest()
    {
        using var harness = new HetznerHarness();
        Assert.Equal("hetzner", harness.Provider.Name);
        Assert.Equal(SandboxIsolationLevel.DedicatedKernel, harness.Provider.IsolationLevel);
        Assert.Equal([SandboxCapabilities.Teardown], harness.Provider.DeclaredCapabilities);
        Assert.True(harness.Provider.MightOwnSandbox("codeybox-abc", null));
        Assert.False(harness.Provider.MightOwnSandbox("someone-else", null));
    }

    [Fact]
    public void NormalizeEgressHost_Handles_Urls_Brackets_Ports()
    {
        Assert.Equal(
            "example.com",
            HetznerSandboxProvider.NormalizeEgressHost("https://example.com:8443/path"));
        Assert.Equal(
            "2001:db8::1",
            HetznerSandboxProvider.NormalizeEgressHost("[2001:db8::1]"));
        Assert.Equal("example.com", HetznerSandboxProvider.NormalizeEgressHost("example.com:443"));
        Assert.Null(HetznerSandboxProvider.NormalizeEgressHost("   "));
        Assert.Throws<ArgumentException>(() => HetznerSandboxProvider.NormalizeEgressHost("has space.com"));
    }
}
