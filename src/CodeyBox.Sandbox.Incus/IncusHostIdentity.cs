using System.Runtime.InteropServices;
using CodeyBox.Core;

namespace CodeyBox.Sandbox.Incus;

internal static partial class IncusHostIdentity
{
    internal static void ValidateHostMountIdentity(
        IncusSandboxOptions options,
        IReadOnlyList<SandboxMount> mounts,
        uint effectiveUserId,
        uint effectiveGroupId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mounts);
        if (!mounts.Any(static mount => mount.HostPath is not null))
            return;
        if (options.GuestUserId != effectiveUserId || options.GuestGroupId != effectiveGroupId)
        {
            throw new InvalidOperationException(
                "Incus virtiofs host mounts require GuestUserId and GuestGroupId to exactly match " +
                $"the CodeyBox host process identity ({effectiveUserId}:{effectiveGroupId}); configured " +
                $"guest identity is {options.GuestUserId}:{options.GuestGroupId}.");
        }
    }

    internal static void ValidateHostMountIdentity(
        IncusSandboxOptions options,
        IReadOnlyList<SandboxMount> mounts)
    {
        var identity = GetEffectiveIdentity();
        ValidateHostMountIdentity(options, mounts, identity.UserId, identity.GroupId);
    }

    internal static (uint UserId, uint GroupId) GetEffectiveIdentity()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Incus provider requires Linux.");
        return (GetEffectiveUserId(), GetEffectiveGroupId());
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint GetEffectiveGroupId();
}
