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

    [Fact]
    public void ResolveExecutorHosts_inherits_all_top_level_defaults_for_pool_hosts()
    {
        var networkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Work"] = "cb-work",
        };
        var options = new MultipassRemoteSandboxOptions
        {
            SshTarget = "ubuntu@default",
            SshBinary = "/usr/bin/ssh",
            SshPort = 2222,
            SshKeyPath = "/keys/default",
            ExtraSshOptions = ["Compression=yes"],
            AcceptUnknownHostKeys = true,
            ServerAliveIntervalSeconds = 21,
            ServerAliveCountMax = 22,
            ConnectTimeoutSeconds = 23,
            LocalTarBinary = "/usr/local/bin/tar",
            RemoteMultipassPath = "/remote/multipass",
            RemoteStagingRoot = "/stage/default",
            DefaultImage = "22.04",
            VmStartTimeout = TimeSpan.FromSeconds(24),
            VmStopTimeout = TimeSpan.FromSeconds(25),
            VmStateCheckInterval = TimeSpan.FromSeconds(26),
            VmNamePrefix = "cb-default-",
            MaxConcurrentSandboxes = 9,
            Cordoned = true,
            Healthy = false,
            AllowedNetworkProfiles = ["Work"],
            NetworkProfiles = networkProfiles,
            ExecutorHosts =
            [
                new MultipassRemoteExecutorHostOptions { Id = "a" },
            ],
        };

        var host = Assert.Single(MultipassRemoteSandboxOptions.ResolveExecutorHosts(options));

        Assert.Equal("a", host.HostId);
        Assert.Equal("ubuntu@default", host.SshTarget);
        Assert.Equal("/usr/bin/ssh", host.SshBinary);
        Assert.Equal(2222, host.SshPort);
        Assert.Equal("/keys/default", host.SshKeyPath);
        Assert.Equal(["Compression=yes"], host.ExtraSshOptions);
        Assert.True(host.AcceptUnknownHostKeys);
        Assert.Equal(21, host.ServerAliveIntervalSeconds);
        Assert.Equal(22, host.ServerAliveCountMax);
        Assert.Equal(23, host.ConnectTimeoutSeconds);
        Assert.Equal("/usr/local/bin/tar", host.LocalTarBinary);
        Assert.Equal("/remote/multipass", host.RemoteMultipassPath);
        Assert.Equal("/stage/default", host.RemoteStagingRoot);
        Assert.Equal("22.04", host.DefaultImage);
        Assert.Equal(TimeSpan.FromSeconds(24), host.VmStartTimeout);
        Assert.Equal(TimeSpan.FromSeconds(25), host.VmStopTimeout);
        Assert.Equal(TimeSpan.FromSeconds(26), host.VmStateCheckInterval);
        Assert.Equal("cb-default-", host.VmNamePrefix);
        Assert.Equal(9, host.MaxConcurrentSandboxes);
        Assert.True(host.Cordoned);
        Assert.False(host.Healthy);
        Assert.Equal(["Work"], host.AllowedNetworkProfiles);
        Assert.Same(networkProfiles, host.NetworkProfiles);
        Assert.Empty(host.ExecutorHosts);
    }
}
