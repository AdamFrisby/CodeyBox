using CodeyBox.Core;
using CodeyBox.Sandbox.Incus;

namespace CodeyBox.Tests;

public sealed class IncusHostIdentityTests
{
    private static readonly SandboxMount WritableHostMount = new()
    {
        HostPath = "/host/work",
        SandboxPath = "/work",
        ReadOnly = false,
    };

    [Fact]
    public void WritableHostMount_RequiresExactHostAndGuestIdentity()
    {
        var options = new IncusSandboxOptions { GuestUserId = 1001, GuestGroupId = 1002 };

        IncusHostIdentity.ValidateHostMountIdentity(options, [WritableHostMount], 1001, 1002);
        Assert.Throws<InvalidOperationException>(() =>
            IncusHostIdentity.ValidateHostMountIdentity(options, [WritableHostMount], 1000, 1002));
        Assert.Throws<InvalidOperationException>(() =>
            IncusHostIdentity.ValidateHostMountIdentity(options, [WritableHostMount], 1001, 1000));
    }

    [Fact]
    public void ReadOnlyHostMount_AlsoRequiresIdentityMatch_ButTmpfsDoesNot()
    {
        var options = new IncusSandboxOptions { GuestUserId = 1001, GuestGroupId = 1002 };
        Assert.Throws<InvalidOperationException>(() =>
            IncusHostIdentity.ValidateHostMountIdentity(
                options,
                [WritableHostMount with { ReadOnly = true }],
                4000,
                4000));
        IncusHostIdentity.ValidateHostMountIdentity(
            options,
            [new SandboxMount { SandboxPath = "/run/secrets", Tmpfs = true, ReadOnly = false }],
            4000,
            4000);
    }
}
