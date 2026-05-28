using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Tests.Uat.SandboxProviders;

namespace CodeyBox.Tests;

/// <summary>
/// B1: <see cref="MultipassSandboxProvider.ListBaselineImagesAsync"/> is the
/// reaper's discovery surface — its filter (StartsWith baseline prefix) is the
/// security boundary that bounds <c>multipass delete --purge</c> to baseline
/// VMs only. Bugs here either expose operator VMs to the reaper's blast
/// radius (inverted/missing prefix filter) or hide stale baselines from the
/// GC entirely (swallowed JSON parse failure not returning empty, prefix
/// captured under a stale config reload). The reaper-side tests substitute
/// a fake resolver, so this test exercises the real implementation.
/// </summary>
public sealed class MultipassBaselineListTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-bsl-list-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    /// <summary>
    /// The prefix filter is the security boundary that bounds the reaper to
    /// baseline VMs only. A correct implementation returns ONLY entries whose
    /// name starts with BaselineNamePrefix — non-baseline VMs (user VMs,
    /// per-workitem clones whose names start with a different prefix) must
    /// not appear.
    /// </summary>
    [Fact]
    public async Task ListBaselineImagesAsync_ReturnsOnlyEntriesMatchingBaselinePrefix()
    {
        var json = """
        {
            "list": [
                { "name": "cb-baseline-aabbccddeeff", "state": "Stopped" },
                { "name": "codeybox-clone-1234", "state": "Running" },
                { "name": "user-personal-vm", "state": "Running" },
                { "name": "cb-baseline-112233445566", "state": "Stopped" }
            ]
        }
        """;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
            argv is [_, "list", "--format=json"]
                ? Task.FromResult(new RunResult(0, json, ""))
                : Task.FromResult(new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var provider = NewProvider(runner);

        var result = await ((IBaselineImageResolver)provider).ListBaselineImagesAsync(CancellationToken.None);

        var names = result.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "cb-baseline-112233445566", "cb-baseline-aabbccddeeff" }, names);
        // CreatedAt is intentionally null — multipass list --format=json doesn't
        // expose it; the reaper's grace window is enforced by its own
        // first-seen bookkeeping. If this ever changes, the reaper grace
        // logic needs to be revisited.
        Assert.All(result, info => Assert.Null(info.CreatedAt));
    }

    /// <summary>
    /// When multipass exits non-zero (e.g. daemon down, permission denied),
    /// the method must return an empty list rather than throw. The reaper
    /// treats an empty result as "nothing to reap this sweep" — propagating
    /// a transient multipass failure as an exception would crash the
    /// background service.
    /// </summary>
    [Fact]
    public async Task ListBaselineImagesAsync_NonZeroExit_ReturnsEmpty()
    {
        var runner = new RecordingMultipassRunner((argv, _, _) =>
            argv is [_, "list", "--format=json"]
                ? Task.FromResult(new RunResult(2, "", "multipass: connect: Connection refused"))
                : Task.FromResult(new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var provider = NewProvider(runner);

        var result = await ((IBaselineImageResolver)provider).ListBaselineImagesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    /// <summary>
    /// If multipass returns valid JSON without a "list" property (schema
    /// drift on a future multipass release), the method must return an empty
    /// list — not throw, not include garbage.
    /// </summary>
    [Fact]
    public async Task ListBaselineImagesAsync_JsonMissingListProperty_ReturnsEmpty()
    {
        var runner = new RecordingMultipassRunner((argv, _, _) =>
            argv is [_, "list", "--format=json"]
                ? Task.FromResult(new RunResult(0, """{"version": "1.0"}""", ""))
                : Task.FromResult(new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var provider = NewProvider(runner);

        var result = await ((IBaselineImageResolver)provider).ListBaselineImagesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    /// <summary>
    /// Malformed JSON (truncated output, corrupted multipass response) must
    /// be caught and surface as an empty list — JsonException propagating up
    /// would crash the reaper background service on every sweep.
    /// </summary>
    [Fact]
    public async Task ListBaselineImagesAsync_InvalidJson_ReturnsEmpty()
    {
        var runner = new RecordingMultipassRunner((argv, _, _) =>
            argv is [_, "list", "--format=json"]
                ? Task.FromResult(new RunResult(0, """{"list": [{"name": "cb-baseline-foo""", ""))
                : Task.FromResult(new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var provider = NewProvider(runner);

        var result = await ((IBaselineImageResolver)provider).ListBaselineImagesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    /// <summary>
    /// Entries missing a "name" property, or with an empty name, must be
    /// silently skipped — they are not valid VM identifiers and cannot be a
    /// baseline. Skipping them keeps a single malformed entry from poisoning
    /// the entire sweep.
    /// </summary>
    [Fact]
    public async Task ListBaselineImagesAsync_EntriesWithMissingOrEmptyName_AreSkipped()
    {
        var json = """
        {
            "list": [
                { "state": "Stopped" },
                { "name": "", "state": "Stopped" },
                { "name": "cb-baseline-realone12345", "state": "Stopped" }
            ]
        }
        """;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
            argv is [_, "list", "--format=json"]
                ? Task.FromResult(new RunResult(0, json, ""))
                : Task.FromResult(new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var provider = NewProvider(runner);

        var result = await ((IBaselineImageResolver)provider).ListBaselineImagesAsync(CancellationToken.None);

        var info = Assert.Single(result);
        Assert.Equal("cb-baseline-realone12345", info.Name);
    }

    /// <summary>
    /// Empty "list" array — fresh host with no VMs — returns an empty result
    /// without error.
    /// </summary>
    [Fact]
    public async Task ListBaselineImagesAsync_EmptyList_ReturnsEmpty()
    {
        var runner = new RecordingMultipassRunner((argv, _, _) =>
            argv is [_, "list", "--format=json"]
                ? Task.FromResult(new RunResult(0, """{"list": []}""", ""))
                : Task.FromResult(new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var provider = NewProvider(runner);

        var result = await ((IBaselineImageResolver)provider).ListBaselineImagesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    /// <summary>
    /// The prefix is read from current options at call time — a non-default
    /// prefix configured by an operator must be honoured by the filter,
    /// otherwise the reaper would scan a different namespace from the one
    /// the dispose guard accepts and silently produce zero work.
    /// </summary>
    [Fact]
    public async Task ListBaselineImagesAsync_CustomPrefix_FilteredAccordingly()
    {
        var json = """
        {
            "list": [
                { "name": "custom-baseline-aaa111", "state": "Stopped" },
                { "name": "cb-baseline-bbb222", "state": "Stopped" }
            ]
        }
        """;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
            argv is [_, "list", "--format=json"]
                ? Task.FromResult(new RunResult(0, json, ""))
                : Task.FromResult(new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var opts = new MultipassSandboxOptions
        {
            MultipassBinary = "/bin/false",
            StagingDirectory = Path.Combine(_workspace, "staging-" + Guid.NewGuid().ToString("N")),
            NetworkProfiles = new Dictionary<string, string> { ["claude"] = "cb-claude" },
            BaselineNamePrefix = "custom-baseline-",
        };
        var provider = new MultipassSandboxProvider(opts, NullLogger<MultipassSandboxProvider>.Instance, null, runner);

        var result = await ((IBaselineImageResolver)provider).ListBaselineImagesAsync(CancellationToken.None);

        var info = Assert.Single(result);
        Assert.Equal("custom-baseline-aaa111", info.Name);
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
