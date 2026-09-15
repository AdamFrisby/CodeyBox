using CodeyBox.Api;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Asserts the documented host-platform matrix (README + docs/concepts/host-platforms.md)
/// matches what the code registers: a provider claimed supported on a platform
/// must be registrable there, and isolation must never be claimed where the
/// enforcement mechanism has not been exercised.
/// </summary>
public sealed class HostPlatformSupportTests
{
    private static readonly HostPlatformSupport.HostOperatingSystem Linux = new(true, false, false);
    private static readonly HostPlatformSupport.HostOperatingSystem MacOS = new(false, true, false);
    private static readonly HostPlatformSupport.HostOperatingSystem Windows = new(false, false, true);

    [Fact]
    public void Matrix_CoversEveryRegisteredProviderId()
    {
        var registered = new HashSet<string>(StringComparer.Ordinal)
        {
            "incus", "multipass", "multipass-remote", "sprites", "bubblewrap", "process",
        };
        Assert.Equal(registered, new HashSet<string>(HostPlatformSupport.AllProviderIds, StringComparer.Ordinal));
    }

    [Fact]
    public void Linux_SupportsAllProviders()
    {
        foreach (var id in HostPlatformSupport.AllProviderIds)
            Assert.True(HostPlatformSupport.IsProviderSupportedOnHost(id, Linux), id);
    }

    [Fact]
    public void MacOS_SupportsOnlyRemoteExecutorTopology()
    {
        Assert.Equal(
            ["multipass-remote", "sprites"],
            HostPlatformSupport.SupportedProvidersOnHost(MacOS));
    }

    [Fact]
    public void Windows_SupportsOnlyRemoteExecutorTopology()
    {
        Assert.Equal(
            ["multipass-remote", "sprites"],
            HostPlatformSupport.SupportedProvidersOnHost(Windows));
    }

    [Fact]
    public void LocalProviders_RejectedOnNonLinux_WithActionableReason()
    {
        foreach (var host in new[] { MacOS, Windows })
        {
            foreach (var id in new[] { "incus", "multipass", "bubblewrap", "process" })
            {
                Assert.False(HostPlatformSupport.IsProviderSupportedOnHost(id, host), $"{id} on {host.Name}");
                var reason = HostPlatformSupport.GetUnsupportedReason(id, host);
                Assert.Contains("multipass-remote", reason, StringComparison.Ordinal);
                Assert.Contains("not supported on", reason, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void SupportedProvider_HasEmptyReason()
    {
        Assert.Equal(string.Empty, HostPlatformSupport.GetUnsupportedReason("multipass-remote", Windows));
        Assert.Equal(string.Empty, HostPlatformSupport.GetUnsupportedReason("incus", Linux));
    }

    [Fact]
    public void UnknownProvider_YieldsUnknownReason()
    {
        var reason = HostPlatformSupport.GetUnsupportedReason("hyperv-local", Linux);
        Assert.Contains("Unknown sandbox provider", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderIdMatching_IsExact_NotSubstring()
    {
        Assert.False(HostPlatformSupport.IsProviderSupportedOnHost("multipass-remote-evil", Linux));
        Assert.False(HostPlatformSupport.IsProviderSupportedOnHost(" incus ", MacOS));
        Assert.True(HostPlatformSupport.IsProviderSupportedOnHost(" Multipass-Remote ", Windows));
    }

    [Fact]
    public void EgressEnforcement_LocalProvidersRequireOrchestratorHost()
    {
        Assert.Equal(EgressEnforcementLocation.EnforcedOnOrchestratorHost, HostPlatformSupport.GetEgressEnforcement("incus"));
        Assert.Equal(EgressEnforcementLocation.EnforcedOnOrchestratorHost, HostPlatformSupport.GetEgressEnforcement("multipass"));
        Assert.Equal(EgressEnforcementLocation.EnforcedOnRemoteExecutorHost, HostPlatformSupport.GetEgressEnforcement("multipass-remote"));
        Assert.Equal(EgressEnforcementLocation.EnforcedOnRemoteExecutorHost, HostPlatformSupport.GetEgressEnforcement("sprites"));
        Assert.Equal(EgressEnforcementLocation.NotEnforced, HostPlatformSupport.GetEgressEnforcement("bubblewrap"));
        Assert.Equal(EgressEnforcementLocation.NotEnforced, HostPlatformSupport.GetEgressEnforcement("process"));
    }

    [Fact]
    public void IsolationClaimed_OnlyWhereEnforcementExercised()
    {
        // Local VMs claim isolation only on Linux (nftables bridges present).
        Assert.True(HostPlatformSupport.ClaimsNetworkIsolation("incus", Linux));
        Assert.True(HostPlatformSupport.ClaimsNetworkIsolation("multipass", Linux));
        Assert.False(HostPlatformSupport.ClaimsNetworkIsolation("incus", MacOS));
        Assert.False(HostPlatformSupport.ClaimsNetworkIsolation("multipass", Windows));

        // Remote topology claims isolation via the Linux executor host.
        Assert.True(HostPlatformSupport.ClaimsNetworkIsolation("multipass-remote", Windows, remoteExecutorIsLinuxEnforced: true));
        Assert.True(HostPlatformSupport.ClaimsNetworkIsolation("sprites", MacOS, remoteExecutorIsLinuxEnforced: true));
        Assert.False(HostPlatformSupport.ClaimsNetworkIsolation("multipass-remote", Windows, remoteExecutorIsLinuxEnforced: false));

        // Dev/shared-kernel providers never claim isolation.
        Assert.False(HostPlatformSupport.ClaimsNetworkIsolation("bubblewrap", Linux));
        Assert.False(HostPlatformSupport.ClaimsNetworkIsolation("process", Linux));
    }

    [Fact]
    public void DocumentedMatrix_FileMatchesCodeMatrix()
    {
        var root = FindRepoRoot();
        var doc = File.ReadAllText(Path.Combine(root, "docs", "concepts", "host-platforms.md"));
        foreach (var id in HostPlatformSupport.AllProviderIds)
            Assert.Contains(id, doc, StringComparison.Ordinal);
        Assert.Contains("multipass-remote", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsValidator_RejectsLocalProvider_WithoutBypassingMatrix()
    {
        // The validator must agree with the matrix on the current host.
        // Other config requirements (e.g. multipass-remote needing an
        // SshTarget) may also fail; isolate the host-support signal by
        // looking for its message rather than overall success.
        var validator = new CodeyBoxOptionsValidator();
        foreach (var id in HostPlatformSupport.AllProviderIds)
        {
            var options = new CodeyBoxOptions
            {
                SandboxProvider = id,
                GitRootDirectory = Path.Combine(Path.GetTempPath(), "cb-test-repos"),
                StateDatabasePath = Path.Combine(Path.GetTempPath(), "cb-test-state.db"),
                MultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = "user@executor" },
            };
            var result = validator.Validate(null, options);
            var message = result.FailureMessage ?? string.Empty;
            var hostRejected = message.Contains("is not supported on", StringComparison.Ordinal);
            var supported = HostPlatformSupport.IsProviderSupportedOnHost(
                id, HostPlatformSupport.HostOperatingSystem.Current);
            Assert.Equal(!supported, hostRejected);
        }
    }

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
}
