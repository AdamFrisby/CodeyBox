using CodeyBox.Core;
using CodeyBox.Sandbox.Multipass;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// B1: thin contract tests around the <see cref="IBaselineImageResolver"/>
/// surface that <see cref="MultipassSandboxProvider"/> implements. These do
/// not launch any VMs — they exercise the pure logic of
/// <c>ResolveBaselineRef</c> and the live-config-based name composition.
/// </summary>
public sealed class MultipassBaselineResolverTests
{
    [Fact]
    public void ResolveBaselineRef_NullProfile_ReturnsNull()
    {
        var provider = new MultipassSandboxProvider(
            new MultipassSandboxOptions { UseBaselineImages = true },
            NullLogger<MultipassSandboxProvider>.Instance);
        Assert.Null(provider.ResolveBaselineRef(null, SandboxProfileFlavor.Headless));
    }

    [Fact]
    public void ResolveBaselineRef_UseBaselineImagesDisabled_ReturnsNull()
    {
        var provider = new MultipassSandboxProvider(
            new MultipassSandboxOptions
            {
                UseBaselineImages = false,
                NetworkProfiles = new Dictionary<string, string> { ["work"] = "cb-net" },
            },
            NullLogger<MultipassSandboxProvider>.Instance);
        Assert.Null(provider.ResolveBaselineRef("work", SandboxProfileFlavor.Headless));
    }

    [Fact]
    public void ResolveBaselineRef_UnknownProfile_ReturnsNull()
    {
        var provider = new MultipassSandboxProvider(
            new MultipassSandboxOptions
            {
                UseBaselineImages = true,
                NetworkProfiles = new Dictionary<string, string> { ["work"] = "cb-net" },
            },
            NullLogger<MultipassSandboxProvider>.Instance);
        Assert.Null(provider.ResolveBaselineRef("unknown", SandboxProfileFlavor.Headless));
    }

    /// <summary>
    /// Two calls with the same live config and same (profile, flavor) return
    /// the same ref — this is what makes a stamped pin look "fresh" until
    /// the operator actually edits config.
    /// </summary>
    [Fact]
    public void ResolveBaselineRef_StableAcrossCalls()
    {
        var provider = new MultipassSandboxProvider(
            new MultipassSandboxOptions
            {
                UseBaselineImages = true,
                NetworkProfiles = new Dictionary<string, string> { ["work"] = "cb-net" },
                ExtraRuncmd = ["apt-get install -y curl"],
            },
            NullLogger<MultipassSandboxProvider>.Instance);

        var a = provider.ResolveBaselineRef("work", SandboxProfileFlavor.Headless);
        var b = provider.ResolveBaselineRef("work", SandboxProfileFlavor.Headless);
        Assert.NotNull(a);
        Assert.Equal(a, b);
    }

    /// <summary>
    /// Live config edits that affect baseline contents change the resolved
    /// ref — the core property the B1 design relies on for "an operator edit
    /// produces a fresh baseline; existing in-flight items keep their pin".
    /// </summary>
    [Fact]
    public void ResolveBaselineRef_ChangesWithLiveConfigEdit()
    {
        var initial = new MultipassSandboxOptions
        {
            UseBaselineImages = true,
            NetworkProfiles = new Dictionary<string, string> { ["work"] = "cb-net" },
            ExtraRuncmd = ["apt-get install -y curl"],
        };
        var edited = initial with { ExtraRuncmd = ["apt-get install -y curl", "npm install -g claude"] };

        var optsRef = initial;
        var provider = new MultipassSandboxProvider(
            () => optsRef,
            NullLogger<MultipassSandboxProvider>.Instance);

        var before = provider.ResolveBaselineRef("work", SandboxProfileFlavor.Headless);
        // Operator hot-reloads MultipassExtraRuncmd.
        optsRef = edited;
        var after = provider.ResolveBaselineRef("work", SandboxProfileFlavor.Headless);

        Assert.NotEqual(before, after);
    }
}
