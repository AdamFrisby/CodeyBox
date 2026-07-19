using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Tests.Uat.SandboxProviders;

namespace CodeyBox.Tests;

/// <summary>
/// B1: <see cref="MultipassSandboxProvider.ListBaselineImagesAsync"/> is the
/// reaper's discovery surface — its filter (StartsWith baseline prefix) is the
/// security boundary that bounds <c>multipass delete --purge</c> to baseline
/// VMs only. Bugs here either expose operator VMs to the reaper's blast
/// radius (inverted/missing prefix filter) or hide stale baselines from the
/// GC entirely (swallowed enumeration failure, prefix
/// captured under a stale config reload). The reaper-side tests substitute
/// a fake resolver, so this test exercises the real implementation.
/// </summary>
public sealed class MultipassBaselineListTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-bsl-list-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace);
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
                ? Task.FromResult(new ProcessRunResult(0, json, ""))
                : Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
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
    /// the method must throw so admission reconciliation cannot mistake an
    /// unknown inventory for authoritative absence. The background reaper
    /// already catches enumeration failures at its sweep boundary.
    /// </summary>
    [Fact]
    public async Task ListBaselineImagesAsync_NonZeroExit_ThrowsWithContext()
    {
        var runner = new RecordingMultipassRunner((argv, _, _) =>
            argv is [_, "list", "--format=json"]
                ? Task.FromResult(new ProcessRunResult(2, "", "multipass: connect: Connection refused"))
                : Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var provider = NewProvider(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IBaselineImageResolver)provider).ListBaselineImagesAsync(CancellationToken.None));

        Assert.Contains("exited with code 2", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// If multipass returns valid JSON without a "list" property (schema
    /// drift on a future multipass release), completeness is unknown and the
    /// method must throw.
    /// </summary>
    [Fact]
    public async Task ListBaselineImagesAsync_JsonMissingListProperty_Throws()
    {
        var runner = new RecordingMultipassRunner((argv, _, _) =>
            argv is [_, "list", "--format=json"]
                ? Task.FromResult(new ProcessRunResult(0, """{"version": "1.0"}""", ""))
                : Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var provider = NewProvider(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IBaselineImageResolver)provider).ListBaselineImagesAsync(CancellationToken.None));

        Assert.IsType<JsonException>(error.InnerException);
    }

    /// <summary>
    /// Malformed JSON (truncated output, corrupted multipass response) must
    /// surface as a contextual enumeration failure rather than an empty list.
    /// </summary>
    [Fact]
    public async Task ListBaselineImagesAsync_InvalidJson_ThrowsWithParseCause()
    {
        var runner = new RecordingMultipassRunner((argv, _, _) =>
            argv is [_, "list", "--format=json"]
                ? Task.FromResult(new ProcessRunResult(0, """{"list": [{"name": "cb-baseline-foo""", ""))
                : Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var provider = NewProvider(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IBaselineImageResolver)provider).ListBaselineImagesAsync(CancellationToken.None));

        Assert.IsAssignableFrom<JsonException>(error.InnerException);
    }

    /// <summary>
    /// Entries missing a "name" property, or with an empty name, make the
    /// inventory incomplete and therefore fail the whole enumeration.
    /// </summary>
    [Fact]
    public async Task ListBaselineImagesAsync_EntryMissingName_Throws()
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
                ? Task.FromResult(new ProcessRunResult(0, json, ""))
                : Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var provider = NewProvider(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IBaselineImageResolver)provider).ListBaselineImagesAsync(CancellationToken.None));

        Assert.IsType<JsonException>(error.InnerException);
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
                ? Task.FromResult(new ProcessRunResult(0, """{"list": []}""", ""))
                : Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
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
                ? Task.FromResult(new ProcessRunResult(0, json, ""))
                : Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
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
