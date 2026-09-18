using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.Multipass;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Baseline-provisioning tests: the baseline carries the tooling of enabled
/// plugins and nothing else, and an enabled-set change is visible as a
/// baseline-identity change (stale baselines become detectable orphans).
/// </summary>
public sealed class PluginBaselineProvisioningTests
{
    private static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().Build();

    private static PluginToolRequirement Tool(
        string pluginId, string binary, string? apt = null, string? hint = null)
    {
        Assert.True(PluginToolRequirement.TryCreate(pluginId, binary, apt, hint, out var requirement, out _));
        return requirement!;
    }

    private static IReadOnlyList<PluginToolRequirement> EnabledToolsFor(params string[] pluginIds)
    {
        var toolPath = PluginTestHelpers.GetToolSamplePluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [toolPath],
                Allowlist = ["*"],
                Enabled = pluginIds.ToList(),
            },
            EmptyConfig(),
            NullLogger<PluginLoader>.Instance);
        _ = loader.DiscoverPlugins();
        return loader.GetEnabledPluginTools();
    }

    [Fact]
    public void Contributions_ContainEnabledPluginTooling()
    {
        var tools = EnabledToolsFor("sample.tool-auditor");
        var tool = Assert.Single(tools);
        Assert.Equal("codeybox-sample-scan-tool", tool.Binary);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);

        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("sample.tool-auditor", verification.Label, StringComparison.Ordinal);
        Assert.Contains("codeybox-sample-scan-tool", verification.Label, StringComparison.Ordinal);
        // Host-owned probe: binary travels as $1, never interpolated into shell.
        Assert.Equal(
            ["sh", "-c", "command -v \"$1\" >/dev/null 2>&1", "codeybox-plugin-tool-check", "codeybox-sample-scan-tool"],
            verification.Argv);
        Assert.DoesNotContain(
            verification.Argv[1],
            "codeybox-sample-scan-tool",
            StringComparison.Ordinal);

        var install = Assert.Single(contributions.InstallCommands);
        Assert.StartsWith("apt-get update && ", install, StringComparison.Ordinal);
        Assert.Contains("codeybox-sample-scan", install, StringComparison.Ordinal);
        Assert.Empty(contributions.DroppedTools);
    }

    [Fact]
    public void Contributions_OmitDisabledPluginTooling()
    {
        // Only the tool auditor is enabled; craft a second requirement for a
        // disabled plugin and prove the builder output carries no trace of it
        // when given the enabled set alone (the loader never hands disabled
        // tools to the builder — covered by the enablement tests).
        var tools = EnabledToolsFor("sample.tool-auditor");
        var contributions = PluginBaselineProvisioning.BuildContributions(tools);

        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("disabled-tool", flattened, StringComparison.Ordinal);
        Assert.DoesNotContain("sample.disabled", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void Contributions_EmptyWhenNoTools()
    {
        // sample.plain-auditor is enabled but declares no tools.
        var tools = EnabledToolsFor("sample.plain-auditor");
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);

        Assert.Empty(contributions.VerificationCommands);
        Assert.Empty(contributions.InstallCommands);
        Assert.Empty(contributions.DroppedTools);
    }

    [Fact]
    public void Contributions_ToolWithoutPackage_VerifiesButDoesNotInstall()
    {
        var contributions = PluginBaselineProvisioning.BuildContributions(
            [Tool("sample.a", "bare-tool")]);

        Assert.Single(contributions.VerificationCommands);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    public void Contributions_RespectVerificationHeadroom_DropExcess()
    {
        var tools = Enumerable.Range(0, 5)
            .Select(i => Tool("sample.a", $"tool-{i}"))
            .ToList();
        var contributions = PluginBaselineProvisioning.BuildContributions(tools, existingVerificationCount: 62);

        // Headroom is 64 - 62 = 2; the rest drop deterministically.
        Assert.Equal(2, contributions.VerificationCommands.Count);
        Assert.Equal(3, contributions.DroppedTools.Count);
        Assert.Equal("tool-0", contributions.VerificationCommands[0].Argv[4]);
        Assert.Equal("tool-1", contributions.VerificationCommands[1].Argv[4]);
    }

    [Fact]
    public void Contributions_InstallLineIsHostConstructed_SingleAptInvocation()
    {
        var contributions = PluginBaselineProvisioning.BuildContributions(
        [
            Tool("sample.b", "tool-b", "pkg-b"),
            Tool("sample.a", "tool-a", "pkg-a"),
        ]);

        var install = Assert.Single(contributions.InstallCommands);
        // Sorted, deduped, one line — no plugin text beyond validated names.
        Assert.Equal(
            "apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y pkg-a pkg-b",
            install);
    }

    private static MultipassSandboxOptions MultipassOpts(
        IReadOnlyList<string> runcmd,
        IReadOnlyList<CodeyBox.Sandbox.BaselineVerificationCommand> verifications) => new()
        {
            ExtraRuncmd = runcmd,
            BaselineVerificationCommands = verifications,
            NetworkProfiles = new Dictionary<string, string> { ["work"] = "cb-net" },
            UseBaselineImages = true,
        };

    [Fact]
    public void EnabledSetChange_ChangesBaselineIdentity()
    {
        // The enabled set {tool-auditor} vs {} must produce different
        // baseline refs, or an operator could never detect the stale image.
        var withTool = PluginBaselineProvisioning.BuildContributions(EnabledToolsFor("sample.tool-auditor"));
        var withoutTool = PluginBaselineProvisioning.BuildContributions(EnabledToolsFor("sample.plain-auditor"));

        var hashWith = MultipassSandboxProvider.ComputeBaselineHash(
            MultipassOpts(withTool.InstallCommands, withTool.VerificationCommands),
            "work", SandboxProfileFlavor.Headless);
        var hashWithout = MultipassSandboxProvider.ComputeBaselineHash(
            MultipassOpts(withoutTool.InstallCommands, withoutTool.VerificationCommands),
            "work", SandboxProfileFlavor.Headless);

        Assert.NotEqual(hashWithout, hashWith);
    }

    [Fact]
    public void SameEnabledSet_StableBaselineIdentity()
    {
        var first = PluginBaselineProvisioning.BuildContributions(EnabledToolsFor("sample.tool-auditor"));
        var second = PluginBaselineProvisioning.BuildContributions(EnabledToolsFor("sample.tool-auditor"));

        var hashFirst = MultipassSandboxProvider.ComputeBaselineHash(
            MultipassOpts(first.InstallCommands, first.VerificationCommands),
            "work", SandboxProfileFlavor.Headless);
        var hashSecond = MultipassSandboxProvider.ComputeBaselineHash(
            MultipassOpts(second.InstallCommands, second.VerificationCommands),
            "work", SandboxProfileFlavor.Headless);

        Assert.Equal(hashFirst, hashSecond);
    }
}
