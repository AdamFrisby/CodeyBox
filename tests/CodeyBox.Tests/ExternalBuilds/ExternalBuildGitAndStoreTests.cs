using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Orchestrator.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

/// <summary>Git-based provider shape, operator adoption, fencing, and
/// persistence upgrade paths.</summary>
public sealed class ExternalBuildGitAndStoreTests
{
    [Fact]
    public async Task GitProvider_SubmitQueueTerminal_ThroughRealPath()
    {
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var opts = ExternalBuildTestKit.Options();
        var store = new InMemoryExternalBuildStore();
        var provider = new FakeGitBuildProvider { PollsToTerminal = 1 };
        var service = new ExternalBuildService(store, [provider], () => opts, clock);

        var record = await service.StartAsync(
            ExternalBuildTestKit.Request("fake-git-target"), new ExternalBuildSubmitInput());
        Assert.Equal(ExternalBuildState.Queued, record.State);
        Assert.StartsWith("fake-git-run-", record.ProviderRunId);
        var terminal = await service.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, terminal.State);
    }

    [Fact]
    public async Task Adopt_AttachesOperatorKnownRun()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, store, _, _) = ExternalBuildTestKit.BuildService(provider);
        var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var adopted = await service.AdoptAsync(record.Id, "operator-known-run-7");
        Assert.Equal("operator-known-run-7", adopted.ProviderRunId);
        Assert.Equal(ExternalBuildState.Queued, adopted.State);
        var stored = await store.GetAsync(record.Id);
        Assert.Equal("operator-known-run-7", stored!.ProviderRunId);
    }

    [Fact]
    public async Task Adopt_TerminalBuild_Rejected()
    {
        var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 1 };
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(provider);
        var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        await service.ReconcileAsync(record.Id);
        await Assert.ThrowsAsync<ExternalBuildConflictException>(() =>
            service.AdoptAsync(record.Id, "late-run"));
    }

    [Fact]
    public async Task Store_CompareAndSet_FencesConcurrentWriters()
    {
        var store = new InMemoryExternalBuildStore();
        var now = DateTimeOffset.UtcNow;
        var record = new ExternalBuildRecord
        {
            Id = "xb-cas",
            ProjectId = "proj",
            WorkItemId = "w1",
            Phase = "work",
            State = ExternalBuildState.Queued,
            Target = new ExternalBuildTargetKey { ProviderId = "p", TargetId = "t" },
            Source = new ExternalBuildSourceIdentity
            {
                SourceDigestSha256 = new string('a', 64),
                BaseDigestSha256 = new string('b', 64),
            },
            ConfigDigest = "cfg",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.CreateAsync(record);
        var ownerA = record with { FenceOwner = "worker-a", FenceEpoch = 1 };
        Assert.True(await store.TryClaimAsync(record.Id, ExternalBuildState.Queued, null, ownerA));
        var ownerB = record with { FenceOwner = "worker-b", FenceEpoch = 2 };
        Assert.False(await store.TryClaimAsync(record.Id, ExternalBuildState.Queued, null, ownerB));
        Assert.False(await store.TryClaimAsync(record.Id, ExternalBuildState.Running, "worker-a", ownerB));
        Assert.True(await store.TryClaimAsync(record.Id, ExternalBuildState.Queued, "worker-a", ownerB));
        Assert.Equal("worker-b", (await store.GetAsync(record.Id))!.FenceOwner);
    }

    [Fact]
    public async Task SqliteStore_NewerSchema_FailsClosed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xb-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "state.db");
            using (var store = new SqliteExternalBuildStore(path))
            {
                var now = DateTimeOffset.UtcNow;
                await store.CreateAsync(new ExternalBuildRecord
                {
                    Id = "xb-old",
                    ProjectId = "proj",
                    WorkItemId = "w1",
                    Phase = "work",
                    State = ExternalBuildState.Queued,
                    Target = new ExternalBuildTargetKey { ProviderId = "p", TargetId = "t" },
                    Source = new ExternalBuildSourceIdentity
                    {
                        SourceDigestSha256 = new string('a', 64),
                        BaseDigestSha256 = new string('b', 64),
                    },
                    ConfigDigest = "cfg",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE external_builds SET schema_version = 99 WHERE id = 'xb-old';";
                cmd.ExecuteNonQuery();
            }
            using (var reopened = new SqliteExternalBuildStore(path))
            {
                Assert.Throws<InvalidOperationException>(() =>
                    reopened.GetAsync("xb-old").GetAwaiter().GetResult());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GhostBuilds_NoneAfterRestart_AllRecordsReconcileOrTerminal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xb-ghost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "state.db");
            var clock = new ControllableClock(DateTimeOffset.UtcNow);
            var opts = ExternalBuildTestKit.Options();
            var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 1 };
            using (var store = new SqliteExternalBuildStore(path))
            {
                var service = new ExternalBuildService(store, [provider], () => opts, clock);
                await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
            }
            using (var reopened = new SqliteExternalBuildStore(path))
            {
                var active = await reopened.ListActiveAsync("proj");
                Assert.Single(active);
                var service2 = new ExternalBuildService(reopened, [provider], () => opts, clock);
                foreach (var build in active)
                    await service2.ReconcileAsync(build.Id);
                Assert.Empty(await reopened.ListActiveAsync("proj"));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
