using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Tests.Uat.SandboxProviders;

namespace CodeyBox.Tests;

/// <summary>
/// B1: the headline behavior the design rests on — when the caller passes a
/// pinned <see cref="SandboxSpec.BaselineImageRef"/>, the provider reuses
/// that name verbatim even when live config now hashes to a different value.
/// Also verifies the null-pin fallback path (signature backward-compat).
///
/// These tests drive the provider through the public
/// <see cref="MultipassSandboxProvider.CreateAsync"/> entry point with a
/// recording runner so we can assert exactly which baseline name multipass
/// was asked about and which name was used as the clone source.
/// </summary>
public sealed class MultipassBaselinePinningTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-bsl-pin-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    /// <summary>
    /// When SandboxSpec.BaselineImageRef is set AND the named baseline already
    /// exists on the host, the provider must reuse it verbatim — even when
    /// live config would compose a different name. This is the protection
    /// against the bug B1 was filed to fix: an operator edits MultipassExtraRuncmd
    /// mid-flight, and in-flight items must keep their original baseline.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WithPinnedBaselineRef_ReusesPinnedNameDespiteLiveConfigDrift()
    {
        var initialOpts = MakeOptions(extraRuncmd: ["touch /opt/codeybox-initial"]);
        var optsRef = initialOpts;
        var provider = new MultipassSandboxProvider(() => optsRef, NullLogger<MultipassSandboxProvider>.Instance, null,
            NewRecordingRunner(
                new ConcurrentDictionary<string, string>(StringComparer.Ordinal),
                new ConcurrentQueue<string>(),
                new ConcurrentQueue<string>(),
                new ConcurrentQueue<string>()));
        var pinnedName = provider.ResolveBaselineRef("claude", SandboxProfileFlavor.Headless)!;

        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        // Seed the pinned baseline so BaselineVmExistsAsync sees it as already
        // baked — the provider must not call launch/install for the baseline.
        states[pinnedName] = "Running";

        var infoQueries = new ConcurrentQueue<string>();
        var launchNames = new ConcurrentQueue<string>();
        var cloneSources = new ConcurrentQueue<string>();

        var runner = NewRecordingRunner(states, infoQueries, launchNames, cloneSources);
        provider = new MultipassSandboxProvider(() => optsRef, NullLogger<MultipassSandboxProvider>.Instance, null, runner);

        // Live config carries an ExtraRuncmd value that would yield a different
        // content hash than the pinned name — so if pinning is not respected,
        // the launch/clone source would not be PinnedName.
        optsRef = MakeOptions(extraRuncmd: ["touch /opt/codeybox-LIVE-drift"]);

        // Sanity: live config would not produce PinnedName by itself — without
        // the pin, the provider would bake a different baseline name.
        var liveComposed = MultipassSandboxProvider.ComposeBaselineNameFromLiveConfig(
            optsRef, "claude", SandboxProfileFlavor.Headless);
        Assert.NotEqual(pinnedName, liveComposed);

        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            WorkingDirectory = "/work",
            BaselineImageRef = pinnedName,
        };

        await using var _ = await provider.CreateAsync(spec, CancellationToken.None);

        // The provider asked about the PINNED name — not the live-config hash.
        Assert.Contains(pinnedName, infoQueries);
        Assert.DoesNotContain(liveComposed, infoQueries);

        // No baseline launch — the seeded baseline was reused as-is.
        Assert.DoesNotContain(launchNames, n => n.StartsWith("cb-baseline-", StringComparison.Ordinal));

        // Clone source is the pinned name — the per-item sandbox cloned from
        // the existing baseline rather than from a fresh live-config bake.
        var cloneSource = Assert.Single(cloneSources);
        Assert.Equal(pinnedName, cloneSource);
    }

    /// <summary>
    /// Backward compatibility: a spec with BaselineImageRef = null must still
    /// launch correctly. The provider falls back to live-config composition,
    /// matching pre-B1 behavior — this is the signature-backward-compat path
    /// the design called out for items predating the stamping logic.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NoPin_FallsBackToLiveConfigComposition()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var infoQueries = new ConcurrentQueue<string>();
        var launchNames = new ConcurrentQueue<string>();
        var cloneSources = new ConcurrentQueue<string>();

        var runner = NewRecordingRunner(states, infoQueries, launchNames, cloneSources);
        var opts = MakeOptions(extraRuncmd: ["touch /opt/codeybox-fallback"]);
        var provider = new MultipassSandboxProvider(opts, NullLogger<MultipassSandboxProvider>.Instance, null, runner);

        var expectedFallbackName = MultipassSandboxProvider.ComposeBaselineNameFromLiveConfig(
            opts, "claude", SandboxProfileFlavor.Headless);

        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            WorkingDirectory = "/work",
            // BaselineImageRef intentionally left null — legacy / no-pin path.
        };

        await using var _ = await provider.CreateAsync(spec, CancellationToken.None);

        // The provider used the live-config name — same outcome the pre-B1
        // code path produced.
        Assert.Contains(launchNames, n => n == expectedFallbackName);
        var cloneSource = Assert.Single(cloneSources);
        Assert.Equal(expectedFallbackName, cloneSource);
    }

    [Fact]
    public async Task EnsureBaselineImageAsync_WithPinnedBaselineRef_BakesPinnedName()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var infoQueries = new ConcurrentQueue<string>();
        var launchNames = new ConcurrentQueue<string>();
        var cloneSources = new ConcurrentQueue<string>();

        var runner = NewRecordingRunner(states, infoQueries, launchNames, cloneSources);
        var initialOpts = MakeOptions(extraRuncmd: ["touch /opt/codeybox-initial"]);
        var optsRef = initialOpts;
        var provider = new MultipassSandboxProvider(() => optsRef, NullLogger<MultipassSandboxProvider>.Instance, null, runner);
        var pinnedName = provider.ResolveBaselineRef("claude", SandboxProfileFlavor.Headless)!;
        optsRef = MakeOptions(extraRuncmd: ["touch /opt/codeybox-LIVE-drift"]);

        var liveComposed = MultipassSandboxProvider.ComposeBaselineNameFromLiveConfig(
            optsRef, "claude", SandboxProfileFlavor.Headless);
        Assert.NotEqual(pinnedName, liveComposed);

        var ensured = await ((IBaselineImageProvisioner)provider).EnsureBaselineImageAsync(
            "claude",
            SandboxProfileFlavor.Headless,
            pinnedName,
            CancellationToken.None);

        Assert.Equal(pinnedName, ensured);
        Assert.Contains(pinnedName, infoQueries);
        Assert.DoesNotContain(liveComposed, infoQueries);
        Assert.Contains(pinnedName, launchNames);
        Assert.DoesNotContain(liveComposed, launchNames);
        Assert.Empty(cloneSources);
    }

    [Fact]
    public async Task EnsureBaselineImageAsync_BakeFailure_PurgesPartialBaseline()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var infoQueries = new ConcurrentQueue<string>();
        var launchNames = new ConcurrentQueue<string>();
        var cloneSources = new ConcurrentQueue<string>();
        var deleteNames = new ConcurrentQueue<string>();

        var runner = NewRecordingRunner(
            states,
            infoQueries,
            launchNames,
            cloneSources,
            deleteNames,
            failBaselineInstall: true);
        var provider = new MultipassSandboxProvider(
            MakeOptions(extraRuncmd: ["touch /opt/codeybox-fail"]),
            NullLogger<MultipassSandboxProvider>.Instance,
            null,
            runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IBaselineImageProvisioner)provider).EnsureBaselineImageAsync(
                "claude",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));

        var baselineName = Assert.Single(launchNames);
        Assert.StartsWith("cb-baseline-", baselineName, StringComparison.Ordinal);
        Assert.Contains(baselineName, deleteNames);
        Assert.False(states.ContainsKey(baselineName));
    }

    [Fact]
    public async Task EnsureBaselineImageAsync_BakeFailureAndPurgeFailure_RetainsAdmissionViaDeferredException()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var infoQueries = new ConcurrentQueue<string>();
        var launchNames = new ConcurrentQueue<string>();
        var cloneSources = new ConcurrentQueue<string>();
        var deleteNames = new ConcurrentQueue<string>();

        var runner = NewRecordingRunner(
            states,
            infoQueries,
            launchNames,
            cloneSources,
            deleteNames,
            failBaselineInstall: true,
            failDeletePurge: true);
        var provider = new MultipassSandboxProvider(
            MakeOptions(extraRuncmd: ["touch /opt/codeybox-fail-purge"]),
            NullLogger<MultipassSandboxProvider>.Instance,
            null,
            runner);

        // info before delete fails (queries the existing partial baseline);
        // info after delete must report the VM is still present to trigger
        // the deferred-exception retention path.
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            ((IBaselineImageProvisioner)provider).EnsureBaselineImageAsync(
                "claude",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));

        var baselineName = Assert.Single(launchNames);
        Assert.StartsWith("cb-baseline-", baselineName, StringComparison.Ordinal);
        Assert.Equal(baselineName, ex.RetainedSandboxName);
        Assert.Equal("baseline-bake-cleanup", ex.Operation);
        Assert.Contains(baselineName, deleteNames);
        // States still has the baseline because purge failed.
        Assert.True(states.ContainsKey(baselineName));
    }

    [Fact]
    public async Task EnsureBaselineImageAsync_BakeFailureAndPurgeFailureButAbsentInventory_RethrowsOriginal()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var infoQueries = new ConcurrentQueue<string>();
        var launchNames = new ConcurrentQueue<string>();
        var cloneSources = new ConcurrentQueue<string>();
        var deleteNames = new ConcurrentQueue<string>();

        // The runner reports "not found" via info exit 1+stderr after delete is
        // called (states drop happens before failDeletePurge returns), so the
        // post-delete info check (--format=json) treats the baseline as gone
        // and we rethrow the original install failure rather than the deferred
        // exception.
        var runner = NewRecordingRunner(
            states,
            infoQueries,
            launchNames,
            cloneSources,
            deleteNames,
            failBaselineInstall: true,
            failDeletePurge: true,
            dropStateOnDeleteAttempt: true);
        var provider = new MultipassSandboxProvider(
            MakeOptions(extraRuncmd: ["touch /opt/codeybox-fail-purge-absent"]),
            NullLogger<MultipassSandboxProvider>.Instance,
            null,
            runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IBaselineImageProvisioner)provider).EnsureBaselineImageAsync(
                "claude",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));

        var baselineName = Assert.Single(launchNames);
        Assert.Contains(baselineName, deleteNames);
    }

    [Fact]
    public async Task CreateAsync_WithPinnedBaselineForDifferentProfile_FailsClosedBeforeClone()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var infoQueries = new ConcurrentQueue<string>();
        var launchNames = new ConcurrentQueue<string>();
        var cloneSources = new ConcurrentQueue<string>();
        var opts = MakeOptions(extraRuncmd: ["touch /opt/codeybox-profile-bind"]);
        var metadataProvider = new MultipassSandboxProvider(
            opts,
            NullLogger<MultipassSandboxProvider>.Instance,
            null,
            NewRecordingRunner(states, infoQueries, launchNames, cloneSources));
        var workProfilePin = metadataProvider.ResolveBaselineRef("claude", SandboxProfileFlavor.Headless)!;

        var runner = NewRecordingRunner(states, infoQueries, launchNames, cloneSources);
        var provider = new MultipassSandboxProvider(
            opts,
            NullLogger<MultipassSandboxProvider>.Instance,
            null,
            runner);
        states[workProfilePin] = "Running";

        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "audit" },
            WorkingDirectory = "/work",
            BaselineImageRef = workProfilePin,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(spec, CancellationToken.None));

        Assert.Contains("different network attachment", ex.Message);
        Assert.Empty(cloneSources);
        Assert.Empty(infoQueries);
        Assert.Empty(launchNames);
    }

    [Fact]
    public async Task CreateAsync_WithUnpersistedStalePinnedBaseline_FailsClosedBeforeClone()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var infoQueries = new ConcurrentQueue<string>();
        var launchNames = new ConcurrentQueue<string>();
        var cloneSources = new ConcurrentQueue<string>();
        var runner = NewRecordingRunner(states, infoQueries, launchNames, cloneSources);
        var provider = new MultipassSandboxProvider(
            MakeOptions(extraRuncmd: ["touch /opt/codeybox-unknown-pin"]),
            NullLogger<MultipassSandboxProvider>.Instance,
            null,
            runner);

        var unknownPinnedRef = "cb-baseline-stale";
        states[unknownPinnedRef] = "Running";

        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "audit" },
            WorkingDirectory = "/work",
            BaselineImageRef = unknownPinnedRef,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(spec, CancellationToken.None));

        Assert.Contains("unknown network attachment", ex.Message);
        Assert.Empty(cloneSources);
        Assert.Empty(infoQueries);
        Assert.Empty(launchNames);
    }

    private MultipassSandboxOptions MakeOptions(IReadOnlyList<string> extraRuncmd) => new()
    {
        MultipassBinary = "/bin/false",
        StagingDirectory = Path.Combine(_workspace, "staging-" + Guid.NewGuid().ToString("N")),
        NetworkProfiles = new Dictionary<string, string> { ["claude"] = "cb-claude", ["audit"] = "cb-audit" },
        UseBaselineImages = true,
        ExtraRuncmd = extraRuncmd,
    };

    /// <summary>
    /// Common runner: handles every multipass call the provider issues during
    /// CreateAsync. Records info queries, baseline launch names, and clone
    /// sources so tests can verify which name was actually used.
    /// </summary>
    private static RecordingMultipassRunner NewRecordingRunner(
        ConcurrentDictionary<string, string> states,
        ConcurrentQueue<string> infoQueries,
        ConcurrentQueue<string> launchNames,
        ConcurrentQueue<string> cloneSources,
        ConcurrentQueue<string>? deleteNames = null,
        bool failBaselineInstall = false,
        bool failDeletePurge = false,
        bool dropStateOnDeleteAttempt = false)
    {
        return new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var name, "--format=csv"])
            {
                infoQueries.Enqueue(name);
                return Task.FromResult(states.TryGetValue(name, out var s)
                    ? new ProcessRunResult(0, s, "")
                    : new ProcessRunResult(1, "", "not found"));
            }
            if (argv is [_, "info", var jsonName, "--format=json"])
            {
                infoQueries.Enqueue(jsonName);
                return Task.FromResult(states.ContainsKey(jsonName)
                    ? new ProcessRunResult(0, "{}", "")
                    : new ProcessRunResult(1, "", "instance \"" + jsonName + "\" does not exist"));
            }
            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                var launched = argv[3];
                launchNames.Enqueue(launched);
                states[launched] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(states.ContainsKey(execName) ? 0 : 1, "", ""));
            if (argv is [_, "exec", var installName, "--", "sudo", "bash", "-c", ..]
                && installName.StartsWith("cb-baseline-", StringComparison.Ordinal))
            {
                return Task.FromResult(failBaselineInstall
                    ? new ProcessRunResult(42, "", "install failed")
                    : new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "clone", var source, "--name", var cloneName])
            {
                cloneSources.Enqueue(source);
                states[cloneName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deleteNames?.Enqueue(deleteName);
                if (dropStateOnDeleteAttempt)
                    states.TryRemove(deleteName, out _);
                if (failDeletePurge)
                    return Task.FromResult(new ProcessRunResult(1, "", "delete --purge failed"));
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
    }
}
