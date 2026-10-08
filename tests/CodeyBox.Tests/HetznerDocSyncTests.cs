using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.HetznerSandboxPlugin;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Tests;

/// <summary>
/// Guards against drift between the operator doc
/// (<c>docs/extending/hetzner-sandbox-plugin.md</c>) and the real
/// <see cref="HetznerSandboxOptions"/>: the Hetzner Cloud sample JSON is
/// extracted from the doc verbatim and bound through
/// <see cref="HetznerSandboxOptions.FromConfiguration"/>. A renamed or
/// removed option key breaks this test instead of shipping a doc sample that
/// no longer binds.
/// </summary>
public sealed class HetznerDocSyncTests
{
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CodeyBox.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static string ReadDoc() => File.ReadAllText(
        Path.Combine(FindRepoRoot(), "docs", "extending", "hetzner-sandbox-plugin.md"));

    private static HetznerSandboxOptions BindSample()
    {
        var doc = ReadDoc();
        var blocks = Regex.Matches(doc, "```json(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.NotEmpty(blocks);
        var sample = blocks.FirstOrDefault(b => b.Contains("\"MaxFirewallRules\"", StringComparison.Ordinal));
        Assert.False(
            string.IsNullOrWhiteSpace(sample),
            "docs/extending/hetzner-sandbox-plugin.md must contain a full plugin-options ```json sample with MaxFirewallRules");

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sample));
        var root = new ConfigurationBuilder().AddJsonStream(stream).Build();
        return HetznerSandboxOptions.FromConfiguration(
            root.GetSection("CodeyBox:Plugins:" + HetznerSandboxOptions.PluginId));
    }

    [Fact]
    public void Doc_HetznerSample_BindsIntoOptions()
    {
        var options = BindSample();

        Assert.True(options.Enabled);
        Assert.Equal("https://api.hetzner.cloud/v1", options.ApiBaseUrl);
        Assert.Equal("HCLOUD_TOKEN", options.TokenEnvVar);
        Assert.Equal("<hetzner-server-type-name>", options.ServerType);
        Assert.Equal("<approved-image-id-or-exact-name>", options.Image);
        Assert.Equal("<hetzner-location-name>", options.Location);
        Assert.Equal(0, options.NetworkId);
        Assert.True(options.EnablePublicIpv4);
        Assert.False(options.EnablePublicIpv6);
        Assert.Equal(string.Empty, options.FloatingIpHomeLocation);
        Assert.Equal("root", options.SshUser);
        Assert.Equal("<owner-tag-for-this-host>", options.OwnerId);
        Assert.Equal("codeybox-", options.ServerNamePrefix);
        Assert.Equal("codeybox-", options.SshKeyNamePrefix);
        Assert.Equal("codeybox-fw-", options.FirewallNamePrefix);
        Assert.Equal("codeybox-fip-", options.FloatingIpNamePrefix);
        Assert.Equal(["<orchestrator-egress-cidr>"], options.OrchestratorSshCidrs);
        Assert.Equal(["1.1.1.1", "8.8.8.8"], options.DnsServerIps);
        Assert.Equal(["1.1.1.1", "8.8.8.8"], options.NtpServerIps);
        Assert.Equal(128, options.MaxFirewallRules);
        Assert.Equal(600, options.ReadyTimeoutSeconds);
        Assert.Equal(300, options.SshReadyTimeoutSeconds);
        Assert.Equal(60, options.ProvisioningRecheckSeconds);
        Assert.Equal(15, options.DnsTimeoutSeconds);
        Assert.Equal("ssh", options.SshBinary);
        Assert.Equal("ssh-keygen", options.SshKeygenBinary);
        Assert.Equal(22, options.SshPort);
        Assert.Equal(10, options.SshConnectTimeoutSeconds);
    }

    [Fact]
    public void Doc_Makes_No_Unverified_Claims()
    {
        var doc = ReadDoc();
        Assert.Contains("not declared", doc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("baseline-bake", doc, StringComparison.Ordinal);
        Assert.Contains("NotEnforced", doc, StringComparison.Ordinal);
        Assert.Contains("best-effort", doc, StringComparison.Ordinal);
        Assert.Contains("no price advantage", doc, StringComparison.OrdinalIgnoreCase);
    }
}
