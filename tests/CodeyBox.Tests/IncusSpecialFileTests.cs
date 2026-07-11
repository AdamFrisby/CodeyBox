using System.Runtime.InteropServices;
using CodeyBox.Core;
using CodeyBox.Sandbox.Incus;

namespace CodeyBox.Tests;

public sealed class IncusSpecialFileTests
{
    [Fact]
    public async Task SnapshotForIsolation_RejectsFifoWithoutBlocking()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-fifo-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var staging = Path.Combine(root, "staging");
        var sandbox = Path.Combine(staging, "sandbox");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(sandbox);
        var fifo = Path.Combine(source, "untrusted.pipe");
        try
        {
            Assert.Equal(0, CreateFifo(fifo, 0x180)); // 0600
            var prepare = Task.Run(() => IncusMountStaging.Prepare(
                    new IncusSandboxOptions
                    {
                        AllowedHostMountRoots = [root],
                        StagingDirectory = staging,
                    },
                    staging,
                    sandbox,
                    [new SandboxMount
                    {
                        HostPath = source,
                        SandboxPath = "/repo",
                        ReadOnly = true,
                        SnapshotForIsolation = true,
                    }],
                    1024 * 1024));

            await Assert.ThrowsAsync<IOException>(() => prepare.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int CreateFifo(string path, uint mode);
}
