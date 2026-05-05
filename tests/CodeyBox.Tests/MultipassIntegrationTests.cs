using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Tests;

/// <summary>
/// Real-runtime tests for the Multipass provider. Skipped unless the
/// <c>multipass</c> CLI is on PATH. These tests are SLOW (each VM launch
/// is 10-30s) and run serially; they're tagged so CI can skip them.
///
/// They assert the actual isolation properties and the env-via-file
/// mechanism — they would catch bugs like "env values leaked into argv"
/// or "mount didn't actually take" that pure unit tests can't see.
/// </summary>
[Collection("Multipass integration")]
public sealed class MultipassIntegrationTests : IDisposable
{
    private static readonly bool _multipassAvailable = LocateMultipass();
    private readonly string _workspace;

    public MultipassIntegrationTests()
    {
        // Multipass-snap is AppArmor-confined and cannot read /tmp. Bind-
        // mount sources we want the VM to see must live under
        // ~/snap/multipass/common/. We mirror the staging-root logic from
        // the provider here.
        var home = Environment.GetEnvironmentVariable("HOME");
        var snapCommon = home is null ? null : Path.Combine(home, "snap", "multipass", "common");
        var baseDir = (snapCommon is not null && Directory.Exists(snapCommon))
            ? Path.Combine(snapCommon, "codeybox-tests")
            : Path.GetTempPath();
        Directory.CreateDirectory(baseDir);
        _workspace = Path.Combine(baseDir, $"mp-test-{Guid.NewGuid():N}"[..16]);
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    private static bool LocateMultipass()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "multipass",
                ArgumentList = { "version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return false;
            p.WaitForExit(5_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private MultipassSandboxProvider NewProvider() => new(
        new MultipassSandboxOptions(),
        NullLogger<MultipassSandboxProvider>.Instance);

    /// <summary>
    /// Minimal end-to-end: launch a VM, exec a command, verify a host
    /// bind-mount is visible inside, verify env-from-file reaches the
    /// command, dispose. ~30-60 seconds.
    /// </summary>
    [Fact]
    public async Task Multipass_LaunchExecMountEnvDispose_EndToEnd()
    {
        if (!_multipassAvailable) return;

        // A host file the sandbox should see via the bind mount.
        var hostMountSource = Path.Combine(_workspace, "shared");
        Directory.CreateDirectory(hostMountSource);
        await File.WriteAllTextAsync(Path.Combine(hostMountSource, "from-host"), "hello-from-host\n");

        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts =
            [
                new SandboxMount { SandboxPath = "/sandbox-shared", HostPath = hostMountSource, ReadOnly = false },
                new SandboxMount { SandboxPath = "/work", Tmpfs = true },
            ],
            Environment = new Dictionary<string, string>
            {
                ["TEST_SECRET"] = "topsecret-12345",
            },
            // Egress filtering is host-side (host nftables on the
            // profile bridge); not asserted here because that needs the
            // operator to have run scripts/setup-host-networks.sh.
            // local/verify-host-firewall.sh exercises that path.
            WorkingDirectory = "/work",
        };

        await using var sb = await NewProvider().CreateAsync(spec, CancellationToken.None);

        // 1. Bind mount: file written on host must be visible inside the VM.
        var mountSeen = await sb.ExecAsync(new SandboxExec
        {
            Argv = ["cat", "/sandbox-shared/from-host"],
        });
        Assert.True(mountSeen.Success, mountSeen.Stderr);
        Assert.Equal("hello-from-host\n", mountSeen.Stdout);

        // 2. Env-from-file: TEST_SECRET reaches the command but should NOT
        //    appear on the multipass exec process's argv on the host.
        //    We confirm reach via printenv; argv-leak is asserted separately.
        var envSeen = await sb.ExecAsync(new SandboxExec
        {
            Argv = ["printenv", "TEST_SECRET"],
        });
        Assert.True(envSeen.Success, envSeen.Stderr);
        Assert.Equal("topsecret-12345\n", envSeen.Stdout);

        // 3. Stdin: contents pipe into the command.
        var stdinEcho = await sb.ExecAsync(new SandboxExec
        {
            Argv = ["wc", "-c"],
            Stdin = "1234567890",
        });
        Assert.True(stdinEcho.Success, stdinEcho.Stderr);
        Assert.Equal("10", stdinEcho.Stdout.Trim());

        // 4. Working directory enforcement.
        var pwd = await sb.ExecAsync(new SandboxExec { Argv = ["pwd"] });
        Assert.True(pwd.Success);
        Assert.Equal("/work\n", pwd.Stdout);

        // 5. Argv leak check: spy on the VM's process listing while running
        //    a command; the exec wrapper should NOT have TEST_SECRET on argv.
        var spawnSpy = await sb.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "ps -ef | grep -F TEST_SECRET= | grep -v grep | wc -l"],
        });
        Assert.True(spawnSpy.Success, spawnSpy.Stderr);
        Assert.Equal("0", spawnSpy.Stdout.Trim());
    }

    // Egress enforcement is exercised by local/verify-host-firewall.sh
    // and local/verify-internet-only.sh (operator-side, requires
    // setup-host-networks.sh to have been run). The provider itself no
    // longer installs an in-VM firewall, so there's nothing to assert
    // about network policy from the provider's CLI surface alone.
}

/// <summary>
/// Integration tests for <see cref="MultipassSandboxProvider.ListAllManagedAsync"/>.
/// Skipped unless the <c>multipass</c> CLI is on PATH. These tests verify
/// that the method correctly shells out to <c>multipass list --format json</c>,
/// filters results to the <c>codeybox-*</c> prefix (excluding non-CodeyBox VMs
/// such as the default "primary" VM), and caches results within the TTL.
/// </summary>
[Collection("Multipass integration")]
public sealed class MultipassListAllTests
{
    private static readonly bool _multipassAvailable = IsMultipassOnPath();

    private static bool IsMultipassOnPath()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "multipass",
                ArgumentList = { "version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return false;
            p.WaitForExit(5_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static MultipassSandboxProvider NewProvider() => new(
        new MultipassSandboxOptions(),
        NullLogger<MultipassSandboxProvider>.Instance);

    /// <summary>
    /// Every entry returned by ListAllManagedAsync must carry the codeybox- prefix.
    /// Non-CodeyBox VMs on the host (e.g. the default "primary" VM, or any
    /// cb-baseline-* image) must not appear in the result.
    /// This verifies the prefix-exclusion boundary of FetchManagedSandboxesAsync.
    /// </summary>
    [Fact]
    public async Task ListAllManagedAsync_FiltersToCodeyboxPrefix()
    {
        if (!_multipassAvailable) return;

        var provider = NewProvider();
        var sandboxes = await provider.ListAllManagedAsync(CancellationToken.None);

        Assert.NotNull(sandboxes);
        Assert.All(sandboxes, s =>
            Assert.StartsWith("codeybox-", s.Name, StringComparison.Ordinal));
    }

    /// <summary>
    /// ListAllManagedAsync must not throw when multipass reports no codeybox-*
    /// VMs (e.g. on a clean host). An empty list is a valid result.
    /// </summary>
    [Fact]
    public async Task ListAllManagedAsync_DoesNotThrow_WhenNoCodeyboxVMs()
    {
        if (!_multipassAvailable) return;

        var provider = NewProvider();
        // Should not throw; result may be empty.
        var sandboxes = await provider.ListAllManagedAsync(CancellationToken.None);
        Assert.NotNull(sandboxes);
    }

    /// <summary>
    /// A second call within the 2-minute cache TTL must return the same list
    /// object (cache hit — no second multipass invocation).
    /// </summary>
    [Fact]
    public async Task ListAllManagedAsync_ReturnsCachedResult_WithinTtl()
    {
        if (!_multipassAvailable) return;

        var provider = NewProvider();
        var first = await provider.ListAllManagedAsync(CancellationToken.None);
        var second = await provider.ListAllManagedAsync(CancellationToken.None);
        // Identical reference confirms the second call used the cache.
        Assert.Same(first, second);
    }
}
