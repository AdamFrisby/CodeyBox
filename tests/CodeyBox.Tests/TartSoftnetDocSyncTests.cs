using System.Text;
using System.Text.RegularExpressions;
using CodeyBox.TartSandboxPlugin;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Tests;

/// <summary>
/// Guards against drift between the operator doc
/// (<c>docs/extending/tart-sandbox-plugin.md</c>) and the real
/// <see cref="TartSandboxOptions"/>: the Softnet sample JSON is extracted
/// from the doc verbatim and bound through
/// <see cref="TartSandboxOptions.FromConfiguration"/>. A renamed or removed
/// option key breaks this test instead of shipping a doc sample that no
/// longer binds.
/// </summary>
public sealed class TartSoftnetDocSyncTests
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
        Path.Combine(FindRepoRoot(), "docs", "extending", "tart-sandbox-plugin.md"));

    private static TartSandboxOptions BindSoftnetSample()
    {
        var doc = ReadDoc();
        var blocks = Regex.Matches(doc, "```json(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.NotEmpty(blocks);
        var sample = blocks.FirstOrDefault(b => b.Contains("\"Mode\": \"softnet\"", StringComparison.Ordinal));
        Assert.False(
            string.IsNullOrWhiteSpace(sample),
            "docs/extending/tart-sandbox-plugin.md must contain a ```json Softnet sample with \"Mode\": \"softnet\"");

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sample));
        var root = new ConfigurationBuilder().AddJsonStream(stream).Build();
        return TartSandboxOptions.FromConfiguration(
            root.GetSection("CodeyBox:Plugins:" + TartSandboxOptions.PluginId));
    }

    [Fact]
    public void Doc_SoftnetSample_BindsIntoOptions()
    {
        var options = BindSoftnetSample();

        Assert.True(options.Enabled);
        Assert.Equal(TartNetworkMode.Softnet, options.Network.Mode);
        Assert.Equal("192.168.64.1/32", options.Network.GatewayCidr);
        Assert.Equal(64, options.Network.MaxAllowCidrs);
        Assert.Equal(5, options.Network.DnsTimeoutSeconds);
        Assert.Equal("/usr/local/bin/softnet", options.Network.SoftnetBinaryPath);
    }

    [Fact]
    public void Doc_SoftnetSample_CarriesNoSecrets()
    {
        var doc = ReadDoc();
        var sample = Regex.Matches(doc, "```json(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .First(b => b.Contains("\"Mode\": \"softnet\"", StringComparison.Ordinal));
        Assert.DoesNotContain("TART_SSH_PASSWORD", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", sample, StringComparison.OrdinalIgnoreCase);
    }
}
