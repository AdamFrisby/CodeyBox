using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.OpenStackSandboxPlugin;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Tests;

/// <summary>
/// Guards against drift between the operator doc
/// (<c>docs/extending/openstack-sandbox-plugin.md</c>) and the real
/// <see cref="OpenStackSandboxOptions"/>: the Infomaniak sample JSON is
/// extracted from the doc verbatim and bound through
/// <see cref="OpenStackSandboxOptions.FromConfiguration"/>. A renamed or
/// removed option key breaks this test instead of shipping a doc sample that
/// no longer binds.
/// </summary>
public sealed class OpenStackDocSyncTests
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
        Path.Combine(FindRepoRoot(), "docs", "extending", "openstack-sandbox-plugin.md"));

    private static OpenStackSandboxOptions BindSample()
    {
        var doc = ReadDoc();
        var blocks = Regex.Matches(doc, "```json(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.NotEmpty(blocks);
        var sample = blocks.FirstOrDefault(b => b.Contains("\"NetworkId\"", StringComparison.Ordinal));
        Assert.False(
            string.IsNullOrWhiteSpace(sample),
            "docs/extending/openstack-sandbox-plugin.md must contain a full plugin-options ```json sample with NetworkId");

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sample));
        var root = new ConfigurationBuilder().AddJsonStream(stream).Build();
        return OpenStackSandboxOptions.FromConfiguration(
            root.GetSection("CodeyBox:Plugins:" + OpenStackSandboxOptions.PluginId));
    }

    [Fact]
    public void Doc_InfomaniakSample_BindsIntoOptions()
    {
        var options = BindSample();

        Assert.True(options.Enabled);
        Assert.Equal("https://api.<region-endpoint-from-your-openrc>:5000/v3", options.AuthUrl);
        Assert.Equal("<region-from-your-openrc>", options.Region);
        Assert.Equal("public", options.Interface);
        Assert.Equal("<image-name-from-your-openrc-or-horizon>", options.ImageName);
        Assert.Equal("<flavor-name-from-your-openrc-or-horizon>", options.FlavorName);
        Assert.Equal("<neutron-network-id-from-your-openrc>", options.NetworkId);
        Assert.Equal("<floating-ip-network-id-from-your-openrc>", options.FloatingNetworkId);
        Assert.Equal("<image-default-user-from-your-openrc>", options.SshUser);
        Assert.Equal("<owner-tag-from-your-openrc>", options.OwnerId);
        Assert.Equal("codeybox-", options.ServerNamePrefix);
        Assert.Equal("codeybox-", options.KeypairNamePrefix);
        Assert.Equal("codeybox-sg-", options.SecurityGroupNamePrefix);
        Assert.Equal(["<orchestrator-egress-cidr-from-your-openrc>"], options.OrchestratorSshCidrs);
        Assert.Equal(["1.1.1.1", "8.8.8.8"], options.DnsServerIps);
        Assert.Equal(["1.1.1.1", "8.8.8.8"], options.NtpServerIps);
        Assert.Equal(128, options.MaxEgressRules);
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
    public void Doc_SamplePlaceholders_AreNotRealSecrets()
    {
        var doc = ReadDoc();
        using var json = JsonDocument.Parse(
            Regex.Matches(doc, "```json(.*?)```", RegexOptions.Singleline)
                .Select(m => m.Groups[1].Value)
                .First(b => b.Contains("\"NetworkId\"", StringComparison.Ordinal)));
        var text = json.RootElement.GetRawText();
        Assert.DoesNotContain("OS_APPLICATION_CREDENTIAL_SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }
}
