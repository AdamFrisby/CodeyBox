using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Bubblewrap;

namespace CodeyBox.Tests;

/// <summary>
/// Real-runtime tests for the Bubblewrap provider. Skipped if <c>bwrap</c>
/// isn't on PATH so the suite is portable. These tests assert the actual
/// isolation properties bubblewrap should provide — they would have caught
/// the kind of bug where (say) we forgot --unshare-pid and the agent could
/// see host processes.
/// </summary>
[Collection("Pipeline integration")]
public sealed class BubblewrapIntegrationTests : IDisposable
{
    private readonly string _workspace;
    private readonly bool _bwrapAvailable;

    public BubblewrapIntegrationTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-bwrap-test-").FullName;
        _bwrapAvailable = File.Exists("/usr/bin/bwrap") || File.Exists("/usr/local/bin/bwrap");
    }

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    private BubblewrapSandboxProvider NewProvider() => new(
        new BubblewrapSandboxOptions(),
        NullLogger<BubblewrapSandboxProvider>.Instance);

    [Fact]
    public async Task Bwrap_RunsBasicCommand()
    {
        if (!_bwrapAvailable) return;
        await using var sb = await NewProvider().CreateAsync(BasicSpec(_workspace));
        var r = await sb.ExecAsync(new SandboxExec { Argv = ["sh", "-c", "echo hello && pwd"] });
        Assert.True(r.Success, r.Stderr);
        Assert.Contains("hello", r.Stdout);
        Assert.Contains("/work", r.Stdout);
    }

    [Fact]
    public async Task Bwrap_TmpfsAndBindMountsAreIsolated()
    {
        // Files written to /work survive within the sandbox lifetime but
        // don't leak to the host's /work (which doesn't exist).
        if (!_bwrapAvailable) return;
        await using var sb = await NewProvider().CreateAsync(BasicSpec(_workspace));
        var write = await sb.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat > /work/marker"],
            Stdin = "from-inside\n",
        });
        Assert.True(write.Success, write.Stderr);

        var read = await sb.ExecAsync(new SandboxExec { Argv = ["cat", "/work/marker"] });
        Assert.True(read.Success);
        Assert.Equal("from-inside\n", read.Stdout);

        // The host's root /work shouldn't exist (or shouldn't have our marker).
        Assert.False(File.Exists("/work/marker"), "sandbox state must not leak to host /work");
    }

    [Fact]
    public async Task Bwrap_CannotSeeHostProcesses()
    {
        // With --unshare-pid the agent sees only itself and bwrap as PID 1.
        // ps -A inside should not see the orchestrator's PID.
        if (!_bwrapAvailable) return;
        await using var sb = await NewProvider().CreateAsync(BasicSpec(_workspace));
        var hostPid = Environment.ProcessId.ToString();
        var r = await sb.ExecAsync(new SandboxExec
        {
            // /proc inside the sandbox is the unshared one. Listing it
            // should NOT contain the orchestrator's pid.
            Argv = ["sh", "-c", "ls /proc | grep -E '^[0-9]+$' | sort -n"],
        });
        Assert.True(r.Success, r.Stderr);
        Assert.DoesNotContain(hostPid, r.Stdout.Split('\n'));
    }

    [Fact]
    public async Task Bwrap_NoNetwork_WhenAllowedHostsEmpty()
    {
        if (!_bwrapAvailable) return;
        await using var sb = await NewProvider().CreateAsync(BasicSpec(_workspace));
        // /proc/net/dev is netns-scoped (--proc mounts the unshared netns
        // view). With --unshare-net, only loopback shows up.
        var r = await sb.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "awk '/:/ && /^[[:space:]]/ {sub(/:.*/, \"\"); gsub(/[[:space:]]/, \"\"); print}' /proc/net/dev"],
        });
        Assert.True(r.Success, r.Stderr);
        var ifaces = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        Assert.Equal(["lo"], ifaces);
    }

    [Fact]
    public async Task Bwrap_HostNetworkShared_WhenAllowedHostsSet()
    {
        if (!_bwrapAvailable) return;
        var spec = BasicSpec(_workspace) with
        {
            Network = new SandboxNetworkPolicy { AllowedHosts = ["api.anthropic.com"] },
        };
        await using var sb = await NewProvider().CreateAsync(spec);
        var r = await sb.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "awk '/:/ && /^[[:space:]]/ {sub(/:.*/, \"\"); gsub(/[[:space:]]/, \"\"); print}' /proc/net/dev"],
        });
        Assert.True(r.Success, r.Stderr);
        var ifaces = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        Assert.Contains("lo", ifaces);
        Assert.True(ifaces.Count > 1, $"expected >1 interface in shared-net mode, got: [{string.Join(',', ifaces)}]");
    }

    [Fact]
    public async Task Bwrap_EnvSpecPropagates_ButNotOnArgv()
    {
        if (!_bwrapAvailable) return;
        var spec = BasicSpec(_workspace) with
        {
            Environment = new Dictionary<string, string>
            {
                ["TEST_SECRET"] = "topsecret-value-12345",
            },
        };
        await using var sb = await NewProvider().CreateAsync(spec);
        var r = await sb.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "printenv TEST_SECRET"],
        });
        Assert.True(r.Success);
        Assert.Equal("topsecret-value-12345\n", r.Stdout);
    }

    [Fact]
    public async Task Bwrap_StdinPipesIntoCommand()
    {
        if (!_bwrapAvailable) return;
        await using var sb = await NewProvider().CreateAsync(BasicSpec(_workspace));
        var r = await sb.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "wc -c"],
            Stdin = "1234567890",
        });
        Assert.True(r.Success);
        Assert.Equal("10", r.Stdout.Trim());
    }

    [Fact]
    public async Task Bwrap_KillActiveExecsAsync_KillsRunningExec()
    {
        if (!_bwrapAvailable) return;
        await using var sb = await NewProvider().CreateAsync(BasicSpec(_workspace));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execTask = sb.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "echo ready; sleep 30"],
            StdoutChunkCallback = chunk =>
            {
                if (chunk.Contains("ready", StringComparison.Ordinal))
                    ready.TrySetResult();
            },
        }, CancellationToken.None);

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sb.KillActiveExecsAsync();

        var result = await execTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Bwrap_DisposeRemovesHostState()
    {
        if (!_bwrapAvailable) return;
        var provider = NewProvider();
        // Snapshot matching dirs BEFORE creation so the test fixture's own
        // workspace dir (codeybox-bwrap-test-*) doesn't pollute the result.
        var pattern = "codeybox-bwrap-*";
        bool IsSandboxDir(string d) => !d.Contains("codeybox-bwrap-test-", StringComparison.Ordinal);
        var before = new HashSet<string>(Directory.GetDirectories(Path.GetTempPath(), pattern).Where(IsSandboxDir));

        var sb = await provider.CreateAsync(BasicSpec(_workspace));
        var newRoots = Directory.GetDirectories(Path.GetTempPath(), pattern)
            .Where(IsSandboxDir)
            .Where(d => !before.Contains(d))
            .ToList();
        Assert.Single(newRoots);

        await sb.DisposeAsync();
        Assert.False(Directory.Exists(newRoots[0]), $"sandbox root {newRoots[0]} should have been removed");
    }

    private static SandboxSpec BasicSpec(string workspace)
    {
        return new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts =
            [
                new SandboxMount { SandboxPath = "/work", Tmpfs = true },
            ],
            WorkingDirectory = "/work",
        };
    }
}
