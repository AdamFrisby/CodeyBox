using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Tests.Uat.SandboxProviders;

namespace CodeyBox.Tests;

/// <summary>
/// Regression coverage for the destructive-sink guard in
/// <see cref="MultipassSandboxProvider.DisposeLeakedAsync(string, CancellationToken)"/>.
/// Before the live-VM-reap incident fix this provider had no
/// refuse-when-active check at the sink at all: the local tracked-active
/// registry was authoritative upstream but nothing re-verified it adjacent
/// to <c>multipass delete --purge</c>, so a lost or never-registered entry
/// let a live work VM be purged. The guard must refuse before any driver
/// call reaches the VM.
/// </summary>
public sealed class MultipassLeakDisposeGuardTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-leak-guard-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task DisposeLeakedAsync_RefusesTrackedActiveSandboxBeforeAnyDelete()
    {
        // A sandbox created through the real CreateAsync flow is marked
        // tracked-active before the VM is even visible to multipass. Routing
        // that name into DisposeLeakedAsync must refuse at entry — and no
        // `multipass delete` call may reach the driver, because by then the
        // running VM would already be purged.
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var deleteCalls = new ConcurrentQueue<string>();
        var runner = NewRunner(states, deleteCalls);
        var provider = NewProvider(runner);
        var baselineRef = provider.ResolveBaselineRef("claude", SandboxProfileFlavor.Headless)!;
        states[baselineRef] = "Running";

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            WorkingDirectory = "/work",
            BaselineImageRef = baselineRef,
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.DisposeLeakedAsync(sandbox.Id, CancellationToken.None));

        Assert.Contains("tracked as active", ex.Message);
        Assert.DoesNotContain(deleteCalls, name => name == sandbox.Id);

        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task DisposeLeakedAsync_StillReclaimsUntrackedOrphan()
    {
        // Positive control: an untracked name (a VM from a crashed instance,
        // or one whose tracking was released) is not caught by the guard and
        // reaches multipass delete --purge.
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var deleteCalls = new ConcurrentQueue<string>();
        var runner = NewRunner(states, deleteCalls);
        var provider = NewProvider(runner);
        var orphan = "codeybox-orphan000000000";
        states[orphan] = "Running";

        await provider.DisposeLeakedAsync(orphan, CancellationToken.None);

        Assert.Contains(orphan, deleteCalls);
    }

    private MultipassSandboxProvider NewProvider(RecordingMultipassRunner runner)
    {
        var opts = new MultipassSandboxOptions
        {
            MultipassBinary = "/bin/false",
            StagingDirectory = Path.Combine(_workspace, "staging-" + Guid.NewGuid().ToString("N")),
            NetworkProfiles = new Dictionary<string, string> { ["claude"] = "cb-claude" },
            UseBaselineImages = true,
        };
        return new MultipassSandboxProvider(opts, NullLogger<MultipassSandboxProvider>.Instance, null, runner);
    }

    /// <summary>
    /// Fake multipass driver: enough of the CreateAsync clone flow (baseline
    /// lookup, clone, start, env transfer, host-address info) plus the
    /// delete path, so tracked-active state comes from the real provider
    /// flow rather than a seeded dictionary. Every multipass argv is
    /// recorded by <see cref="RecordingMultipassRunner.Calls"/>.
    /// </summary>
    private static RecordingMultipassRunner NewRunner(
        ConcurrentDictionary<string, string> states,
        ConcurrentQueue<string> deleteCalls)
    {
        return new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var name, "--format=csv"])
                return Task.FromResult(states.TryGetValue(name, out var s)
                    ? new ProcessRunResult(0, s, "")
                    : new ProcessRunResult(1, "", "not found"));
            if (argv is [_, "info", var jsonName, "--format=json"])
                return Task.FromResult(states.ContainsKey(jsonName)
                    ? new ProcessRunResult(0, "{}", "")
                    : new ProcessRunResult(1, "", $"instance \"{jsonName}\" does not exist"));
            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(states.ContainsKey(execName) ? 0 : 1, "", ""));
            if (argv is [_, "clone", _, "--name", var cloneName])
            {
                states[cloneName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deleteCalls.Enqueue(deleteName);
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
    }
}
