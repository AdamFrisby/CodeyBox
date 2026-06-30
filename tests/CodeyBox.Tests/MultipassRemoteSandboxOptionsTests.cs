using CodeyBox.Sandbox.MultipassRemote;

namespace CodeyBox.Tests;

public sealed class MultipassRemoteSandboxOptionsTests
{
    [Fact]
    public void ResolveExecutorHosts_legacy_single_host_uses_default_id_and_top_level_values()
    {
        var options = new MultipassRemoteSandboxOptions
        {
            SshTarget = "ubuntu@executor",
            HostId = "",
            MaxConcurrentSandboxes = 3,
            AllowedNetworkProfiles = ["work"],
        };

        var host = Assert.Single(MultipassRemoteSandboxOptions.ResolveExecutorHosts(options));

        Assert.Equal("default", host.HostId);
        Assert.Equal("ubuntu@executor", host.SshTarget);
        Assert.Equal(3, host.MaxConcurrentSandboxes);
        Assert.Equal(["work"], host.AllowedNetworkProfiles);
    }

    [Fact]
    public void ResolveExecutorHosts_requires_stable_ids_for_explicit_pool_hosts()
    {
        var options = new MultipassRemoteSandboxOptions
        {
            SshTarget = "ubuntu@default",
            ExecutorHosts =
            [
                new MultipassRemoteExecutorHostOptions { SshTarget = "ubuntu@a" },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MultipassRemoteSandboxOptions.ResolveExecutorHosts(options));

        Assert.Contains("must set a stable Id", ex.Message);
    }

    [Fact]
    public void ResolveExecutorHosts_rejects_duplicate_ids()
    {
        var options = new MultipassRemoteSandboxOptions
        {
            SshTarget = "ubuntu@default",
            ExecutorHosts =
            [
                new MultipassRemoteExecutorHostOptions { Id = "a", SshTarget = "ubuntu@a" },
                new MultipassRemoteExecutorHostOptions { Id = " a ", SshTarget = "ubuntu@b" },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MultipassRemoteSandboxOptions.ResolveExecutorHosts(options));

        Assert.Contains("Duplicate MultipassRemoteSandbox executor host id 'a'", ex.Message);
    }

    [Fact]
    public void ResolveExecutorHosts_treats_blank_host_ssh_target_as_inherited()
    {
        var options = new MultipassRemoteSandboxOptions
        {
            SshTarget = "ubuntu@default",
            ExecutorHosts =
            [
                new MultipassRemoteExecutorHostOptions { Id = "a", SshTarget = "   " },
            ],
        };

        var host = Assert.Single(MultipassRemoteSandboxOptions.ResolveExecutorHosts(options));

        Assert.Equal("ubuntu@default", host.SshTarget);
    }
}
