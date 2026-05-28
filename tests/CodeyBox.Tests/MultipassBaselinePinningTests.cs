using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
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
        const string PinnedName = "cb-baseline-pinned123";

        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        // Seed the pinned baseline so BaselineVmExistsAsync sees it as already
        // baked — the provider must not call launch/install for the baseline.
        states[PinnedName] = "Running";

        var infoQueries = new ConcurrentQueue<string>();
        var launchNames = new ConcurrentQueue<string>();
        var cloneSources = new ConcurrentQueue<string>();

        var runner = NewRecordingRunner(states, infoQueries, launchNames, cloneSources);

        // Live config carries an ExtraRuncmd value that would yield a different
        // content hash than the pinned name — so if pinning is not respected,
        // the launch/clone source would not be PinnedName.
        var opts = MakeOptions(extraRuncmd: ["touch /opt/codeybox-LIVE-drift"]);
        var provider = new MultipassSandboxProvider(opts, NullLogger<MultipassSandboxProvider>.Instance, null, runner);

        // Sanity: live config would not produce PinnedName by itself — without
        // the pin, the provider would bake a different baseline name.
        var liveComposed = MultipassSandboxProvider.ComposeBaselineNameFromLiveConfig(
            opts, "claude", SandboxProfileFlavor.Headless);
        Assert.NotEqual(PinnedName, liveComposed);

        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            WorkingDirectory = "/work",
            BaselineImageRef = PinnedName,
        };

        await using var _ = await provider.CreateAsync(spec, CancellationToken.None);

        // The provider asked about the PINNED name — not the live-config hash.
        Assert.Contains(PinnedName, infoQueries);
        Assert.DoesNotContain(liveComposed, infoQueries);

        // No baseline launch — the seeded baseline was reused as-is.
        Assert.DoesNotContain(launchNames, n => n.StartsWith("cb-baseline-", StringComparison.Ordinal));

        // Clone source is the pinned name — the per-item sandbox cloned from
        // the existing baseline rather than from a fresh live-config bake.
        var cloneSource = Assert.Single(cloneSources);
        Assert.Equal(PinnedName, cloneSource);
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

    private MultipassSandboxOptions MakeOptions(IReadOnlyList<string> extraRuncmd) => new()
    {
        MultipassBinary = "/bin/false",
        StagingDirectory = Path.Combine(_workspace, "staging-" + Guid.NewGuid().ToString("N")),
        NetworkProfiles = new Dictionary<string, string> { ["claude"] = "cb-claude" },
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
        ConcurrentQueue<string> cloneSources)
    {
        return new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var name, "--format=csv"])
            {
                infoQueries.Enqueue(name);
                return Task.FromResult(states.TryGetValue(name, out var s)
                    ? new RunResult(0, s, "")
                    : new RunResult(1, "", "not found"));
            }
            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                var launched = argv[3];
                launchNames.Enqueue(launched);
                states[launched] = "Running";
                return Task.FromResult(new RunResult(0, "", ""));
            }
            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new RunResult(states.ContainsKey(execName) ? 0 : 1, "", ""));
            if (argv is [_, "exec", var installName, "--", "sudo", "bash", "-c", ..]
                && installName.StartsWith("cb-baseline-", StringComparison.Ordinal))
                return Task.FromResult(new RunResult(0, "", ""));
            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new RunResult(0, "", ""));
            }
            if (argv is [_, "clone", var source, "--name", var cloneName])
            {
                cloneSources.Enqueue(source);
                states[cloneName] = "Stopped";
                return Task.FromResult(new RunResult(0, "", ""));
            }
            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new RunResult(0, "", ""));
            }
            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new RunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new RunResult(0, "", ""));
            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new RunResult(0, "", ""));
            }
            return Task.FromResult(new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
    }
}
