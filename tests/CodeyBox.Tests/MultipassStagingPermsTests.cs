using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Tests;

/// <summary>
/// The Multipass provider stages cloud-init files and bind-mount sources
/// under a per-sandbox subdirectory. All sandboxes' subdirs share one
/// parent on the host, so OS-level perms matter — we want operator-only
/// access (0700) so a bug in the orchestrator OR another process running
/// as a different user can't read another sandbox's staging.
///
/// This is a fast unit test — doesn't actually run multipass, just
/// inspects the directory tree the provider creates.
/// </summary>
public sealed class MultipassStagingPermsTests : IDisposable
{
    private readonly string _customStaging;

    public MultipassStagingPermsTests()
    {
        // Force a tmp staging dir so the test doesn't pollute the user's
        // ~/snap/multipass/common.
        _customStaging = Path.Combine(Path.GetTempPath(), $"codeybox-test-staging-{Guid.NewGuid():N}");
    }

    public void Dispose() { CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_customStaging); }

    [Fact]
    public void StagingRoot_IsCreated_With_OperatorOnlyPerms()
    {
        if (OperatingSystem.IsWindows()) return;

        // Construct provider — it should create the staging root with 0700.
        _ = new MultipassSandboxProvider(
            new MultipassSandboxOptions { StagingDirectory = _customStaging },
            NullLogger<MultipassSandboxProvider>.Instance);

        Assert.True(Directory.Exists(_customStaging));
        var mode = File.GetUnixFileMode(_customStaging);
        // We require the dir to NOT be readable by group or other.
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead), $"staging root has group-read: {mode}");
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead), $"staging root has other-read: {mode}");
        // We DO want owner full access.
        Assert.True(mode.HasFlag(UnixFileMode.UserRead));
        Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
    }
}
