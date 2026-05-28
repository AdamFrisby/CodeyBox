using CodeyBox.Core;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Tests;

/// <summary>
/// B1: deterministic content-hashed baseline keys. Verifies the BuildBaseline-
/// Key replacement <see cref="MultipassSandboxProvider.ComputeBaselineHash"/>
/// produces the same hash for identical inputs across calls (and would be the
/// same across processes — SHA-256 is not process-randomised), and that
/// changing any contributing input changes the hash.
/// </summary>
public sealed class MultipassBaselineKeyTests
{
    private static MultipassSandboxOptions Opts(
        IReadOnlyList<string>? runcmd = null,
        string? extraCloudInit = null,
        IReadOnlyDictionary<string, string>? profiles = null) => new()
        {
            ExtraRuncmd = runcmd ?? [],
            ExtraCloudInit = extraCloudInit,
            NetworkProfiles = profiles ?? new Dictionary<string, string> { ["work"] = "cb-net" },
            UseBaselineImages = true,
        };

    [Fact]
    public void ComputeHash_IsDeterministic()
    {
        var opts = Opts(runcmd: ["apt-get install -y curl"], extraCloudInit: "packages:\n  - jq");
        var h1 = MultipassSandboxProvider.ComputeBaselineHash(opts, "work", SandboxProfileFlavor.Headless);
        var h2 = MultipassSandboxProvider.ComputeBaselineHash(opts, "work", SandboxProfileFlavor.Headless);
        Assert.Equal(h1, h2);
        // 12 hex characters: deterministic length, lower-case.
        Assert.Equal(12, h1.Length);
        Assert.Equal(h1, h1.ToLowerInvariant());
    }

    [Fact]
    public void ComputeHash_ChangesWhenExtraRuncmdChanges()
    {
        var a = MultipassSandboxProvider.ComputeBaselineHash(
            Opts(runcmd: ["apt-get install -y curl"]), "work", SandboxProfileFlavor.Headless);
        var b = MultipassSandboxProvider.ComputeBaselineHash(
            Opts(runcmd: ["apt-get install -y curl", "npm install -g claude"]), "work", SandboxProfileFlavor.Headless);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeHash_ChangesWhenExtraCloudInitChanges()
    {
        var a = MultipassSandboxProvider.ComputeBaselineHash(
            Opts(extraCloudInit: "packages:\n  - jq"), "work", SandboxProfileFlavor.Headless);
        var b = MultipassSandboxProvider.ComputeBaselineHash(
            Opts(extraCloudInit: "packages:\n  - jq\n  - yq"), "work", SandboxProfileFlavor.Headless);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeHash_ChangesWhenProfileChanges()
    {
        var opts = Opts(profiles: new Dictionary<string, string> { ["work"] = "cb-net", ["audit"] = "cb-net" });
        var work = MultipassSandboxProvider.ComputeBaselineHash(opts, "work", SandboxProfileFlavor.Headless);
        var audit = MultipassSandboxProvider.ComputeBaselineHash(opts, "audit", SandboxProfileFlavor.Headless);
        Assert.NotEqual(work, audit);
    }

    [Fact]
    public void ComputeHash_ChangesWhenFlavorChanges()
    {
        var opts = Opts();
        var headless = MultipassSandboxProvider.ComputeBaselineHash(opts, "work", SandboxProfileFlavor.Headless);
        var graphical = MultipassSandboxProvider.ComputeBaselineHash(opts, "work", SandboxProfileFlavor.Graphical);
        Assert.NotEqual(headless, graphical);
    }

    /// <summary>
    /// The composed baseline name always starts with the configured prefix
    /// and fits within the 24-char multipass instance-name cap.
    /// </summary>
    [Fact]
    public void ComposeBaselineName_FitsWithin24Chars()
    {
        var opts = Opts();
        var name = MultipassSandboxProvider.ComposeBaselineNameFromLiveConfig(opts, "work", SandboxProfileFlavor.Headless);
        Assert.StartsWith(opts.BaselineNamePrefix, name);
        Assert.True(name.Length <= 24, $"Baseline name '{name}' exceeds 24-char multipass instance-name limit");
    }
}
