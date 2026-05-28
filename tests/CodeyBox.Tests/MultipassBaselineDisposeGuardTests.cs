using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Tests.Uat.SandboxProviders;

namespace CodeyBox.Tests;

/// <summary>
/// B1: the prefix-safety guard inside
/// <see cref="MultipassSandboxProvider.DisposeBaselineImageAsync"/> is the
/// only thing preventing the unattended reaper from running
/// <c>multipass delete --purge &lt;arbitrary&gt;</c> on a misconfigured or
/// poisoned name. Because the reaper sweeps on a 6h cadence and the failure
/// mode is destructive (purging an active clone or unrelated user VM), this
/// guard must have a regression test.
/// </summary>
public sealed class MultipassBaselineDisposeGuardTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-bsl-dispose-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    /// <summary>
    /// A name that does not start with the configured baseline prefix must be
    /// rejected with InvalidOperationException — and crucially, no
    /// <c>multipass delete</c> call may be issued, since by then the damage
    /// (the running clone purged) would already be done.
    /// </summary>
    [Theory]
    [InlineData("codeybox-someclone-deadbeef")]   // looks like a work-item clone, not a baseline
    [InlineData("user-personal-vm")]              // operator's unrelated VM
    [InlineData("")]                              // empty argument
    [InlineData("--all")]                         // flag-looking argument
    public async Task DisposeBaselineImageAsync_RejectsNamesWithoutBaselinePrefix(string offendingName)
    {
        var deleteCalls = new ConcurrentQueue<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "delete", "--purge", var name])
            {
                deleteCalls.Enqueue(name);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IBaselineImageResolver)provider).DisposeBaselineImageAsync(offendingName, CancellationToken.None));

        // Crucially: no multipass delete was issued. The guard must run BEFORE
        // any external process spawn.
        Assert.Empty(deleteCalls);
    }

    /// <summary>
    /// Sanity: a properly-prefixed name passes the guard and reaches the
    /// underlying <c>multipass delete --purge</c> call.
    /// </summary>
    [Fact]
    public async Task DisposeBaselineImageAsync_AcceptsNameWithBaselinePrefix()
    {
        var deleteCalls = new ConcurrentQueue<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "delete", "--purge", var name])
            {
                deleteCalls.Enqueue(name);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(runner);

        await ((IBaselineImageResolver)provider).DisposeBaselineImageAsync(
            "cb-baseline-abcdef012345", CancellationToken.None);

        var purged = Assert.Single(deleteCalls);
        Assert.Equal("cb-baseline-abcdef012345", purged);
    }

    /// <summary>
    /// When multipass itself fails (non-zero exit), the provider surfaces the
    /// error as an exception so the reaper's per-baseline try/catch can log
    /// and continue with the rest of the batch instead of silently believing
    /// the dispose succeeded.
    /// </summary>
    [Fact]
    public async Task DisposeBaselineImageAsync_NonZeroExit_ThrowsInvalidOperation()
    {
        var runner = new RecordingMultipassRunner((argv, _, _) =>
            argv is [_, "delete", "--purge", _]
                ? Task.FromResult(new ProcessRunResult(2, "", "multipass: VM is locked"))
                : Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var provider = NewProvider(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IBaselineImageResolver)provider).DisposeBaselineImageAsync(
                "cb-baseline-aabbccddeeff", CancellationToken.None));
    }

    private MultipassSandboxProvider NewProvider(RecordingMultipassRunner runner)
    {
        var opts = new MultipassSandboxOptions
        {
            MultipassBinary = "/bin/false",
            StagingDirectory = Path.Combine(_workspace, "staging-" + Guid.NewGuid().ToString("N")),
            NetworkProfiles = new Dictionary<string, string> { ["claude"] = "cb-claude" },
        };
        return new MultipassSandboxProvider(opts, NullLogger<MultipassSandboxProvider>.Instance, null, runner);
    }
}
