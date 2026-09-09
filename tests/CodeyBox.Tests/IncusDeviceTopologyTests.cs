using CodeyBox.Sandbox.Incus;

namespace CodeyBox.Tests;

public sealed class IncusDeviceTopologyTests
{
    private static readonly IncusSandboxOptions Options = new()
    {
        StoragePoolName = "codeybox-zfs",
    };

    [Fact]
    public void Verify_AcceptsExactVmNicAndVirtiofsMounts()
    {
        IncusDeviceTopology.Verify(
            BuildJson(),
            Options,
            "cb-net",
            Mounts());
    }

    [Theory]
    [InlineData("\"type\":\"virtual-machine\"", "\"type\":\"container\"")]
    [InlineData("\"pool\":\"codeybox-zfs\"", "\"pool\":\"other-pool\"")]
    [InlineData("\"io.bus\":\"virtiofs\"", "\"io.bus\":\"9p\"")]
    [InlineData("\"readonly\":\"true\"", "\"readonly\":\"false\"")]
    public void Verify_RejectsSecurityDowngrades(string expected, string replacement)
    {
        var json = BuildJson().Replace(expected, replacement, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            IncusDeviceTopology.Verify(json, Options, "cb-net", Mounts()));
    }

    [Fact]
    public void Verify_RejectsInheritedProfileAndExtraNic()
    {
        var inherited = BuildJson().Replace("\"profiles\":[]", "\"profiles\":[\"default\"]", StringComparison.Ordinal);
        var extraNic = BuildJson().Replace(
            "\"expanded_devices\":{",
            "\"expanded_devices\":{\"unexpected\":{\"type\":\"nic\",\"parent\":\"lxdbr0\"},",
            StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            IncusDeviceTopology.Verify(inherited, Options, "cb-net", Mounts()));
        Assert.Throws<InvalidOperationException>(() =>
            IncusDeviceTopology.Verify(extraNic, Options, "cb-net", Mounts()));
    }

    [Theory]
    [InlineData("raw.qemu", "-cpu host")]
    [InlineData("raw.qemu.conf", "[machine]")]
    [InlineData("security.nesting", "true")]
    public void Verify_RejectsUnsafeVmConfiguration(string key, string value)
    {
        var json = BuildJson().Replace(
            "\"config\":{},",
            $"\"config\":{{\"{key}\":\"{value}\"}},",
            StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            IncusDeviceTopology.Verify(json, Options, "cb-net", Mounts()));
    }

    private static IReadOnlyList<IncusPreparedMount> Mounts() =>
    [
        new("/host/repo", "/repo", ReadOnly: true),
        new("/host/work", "/work", ReadOnly: false),
    ];

    private static string BuildJson() =>
        """
        {
          "type":"sync",
          "metadata":{
            "type":"virtual-machine",
            "config":{},
            "expanded_config":{},
            "profiles":[],
            "expanded_devices":{
              "root":{"type":"disk","path":"/","pool":"codeybox-zfs"},
              "codeybox-net":{"type":"nic","nictype":"bridged","parent":"cb-net","name":"eth0"},
              "m000":{"type":"disk","source":"/host/repo","path":"/repo","io.bus":"virtiofs","readonly":"true"},
              "m001":{"type":"disk","source":"/host/work","path":"/work","io.bus":"virtiofs"}
            }
          }
        }
        """;
}
