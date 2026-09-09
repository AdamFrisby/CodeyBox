using System.Text;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

public sealed class AgentTurnScratchpadStoreTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("codeybox-turn-scratchpad-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ArchiveAndCheckpointRef_AreBoundedCanonicalImmutableValues()
    {
        var workItemId = new WorkItemId(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"));
        var input = Encoding.UTF8.GetBytes("private agent session");
        var archive = new AgentTurnScratchpadArchive(input);

        input[0] = (byte)'X';
        var returned = archive.ToArray();
        returned[1] = (byte)'X';
        Assert.Equal("private agent session", Encoding.UTF8.GetString(archive.ToArray()));

        var checkpointRef = AgentTurnCheckpointRef.Create(
            workItemId,
            "ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD",
            archive);

        Assert.Equal(
            $"refs/heads/codeybox/preempt/0123456789abcdef0123456789abcdef/" +
            $"abcdefabcdefabcdefabcdefabcdefabcdefabcd-{archive.Sha256}",
            checkpointRef.Value);
        Assert.Equal(checkpointRef, AgentTurnCheckpointRef.Parse(checkpointRef.Value));
        Assert.True(AgentTurnCheckpointRef.TryParse(checkpointRef.Value, out var reparsed));
        Assert.Equal(checkpointRef, reparsed);
        Assert.False(AgentTurnCheckpointRef.TryParse(checkpointRef.Value.ToUpperInvariant(), out _));

        Assert.Throws<ArgumentException>(() => new AgentTurnScratchpadArchive(ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AgentTurnScratchpadArchive(new byte[AgentTurnScratchpadArchive.MaximumBytes + 1]));
        Assert.Throws<ArgumentException>(
            () => AgentTurnCheckpointRef.Create(workItemId, "short", archive));
        Assert.Throws<FormatException>(
            () => AgentTurnCheckpointRef.Parse(
                checkpointRef.Value.Replace("refs/heads/", "refs/tags/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SaveAndRead_AreIdempotentAndSurviveStoreRestart()
    {
        var path = DatabasePath();
        var draft = Sample();
        var expectedBytes = Encoding.UTF8.GetBytes("durable host-private session bytes");
        var archive = new AgentTurnScratchpadArchive(expectedBytes);
        var checkpointRef = Ref(draft.Id, 'a', archive);
        var item = Checkpointed(draft, checkpointRef);

        using (var store = new SqliteWorkItemStore(path))
        {
            await store.CreateAsync(item);
            IAgentTurnScratchpadStore scratchpads = store;
            await scratchpads.SaveAsync(item.Id, checkpointRef, archive);
            await scratchpads.SaveAsync(item.Id, checkpointRef, archive);
        }

        using (var reopened = new SqliteWorkItemStore(path))
        {
            IAgentTurnScratchpadStore scratchpads = reopened;
            var restored = await scratchpads.ReadAsync(item.Id, checkpointRef);

            Assert.NotNull(restored);
            Assert.Equal(expectedBytes, restored!.ToArray());
            Assert.Equal(archive.Sha256, restored.Sha256);
            Assert.Equal(expectedBytes.Length, restored.SizeBytes);
        }

        using var raw = Open(path);
        using var count = raw.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM agent_turn_scratchpads WHERE work_item_id = $id;";
        count.Parameters.AddWithValue("$id", item.Id.ToString());
        Assert.Equal(1L, count.ExecuteScalar());
    }

    [Fact]
    public async Task Reopen_DeletesArchiveWhoseWorkItemHasNoDurableCheckpointMetadata()
    {
        var path = DatabasePath();
        var item = Sample();
        var archive = Archive("capture abandoned before metadata update");
        var checkpointRef = Ref(item.Id, '0', archive);
        using (var store = new SqliteWorkItemStore(path))
        {
            await store.CreateAsync(item);
            IAgentTurnScratchpadStore scratchpads = store;
            await scratchpads.SaveAsync(item.Id, checkpointRef, archive);
            Assert.NotNull(await scratchpads.ReadAsync(item.Id, checkpointRef));
        }

        using var reopened = new SqliteWorkItemStore(path);
        Assert.Null(await ((IAgentTurnScratchpadStore)reopened).ReadAsync(item.Id, checkpointRef));
    }

    [Fact]
    public async Task Save_RejectsHashMismatchWithoutPersistingAnything()
    {
        var path = DatabasePath();
        var item = Sample();
        var addressedArchive = Archive("addressed archive");
        var differentArchive = Archive("different archive");
        var checkpointRef = Ref(item.Id, 'b', addressedArchive);
        using var store = new SqliteWorkItemStore(path);
        await store.CreateAsync(item);
        IAgentTurnScratchpadStore scratchpads = store;

        await Assert.ThrowsAsync<ArgumentException>(
            () => scratchpads.SaveAsync(item.Id, checkpointRef, differentArchive));

        Assert.Null(await scratchpads.ReadAsync(item.Id, checkpointRef));
    }

    [Fact]
    public async Task TryPublish_AtomicallyCasUpdatesMetadataAndPrunesNonCurrentArchives()
    {
        var path = DatabasePath();
        var item = Sample() with
        {
            State = WorkItemState.Working,
            WorkBranch = "codeybox/atomic-publication",
            Agent = AgentKind.Claude,
            AgentInstanceId = "claude/default",
        };
        var oldArchive = Archive("older private checkpoint");
        var currentArchive = Archive("current private checkpoint");
        var oldRef = Ref(item.Id, '6', oldArchive);
        var currentRef = Ref(item.Id, '7', currentArchive);
        using var store = new SqliteWorkItemStore(path);
        await store.CreateAsync(item);
        IAgentTurnScratchpadStore scratchpads = store;
        await scratchpads.SaveAsync(item.Id, oldRef, oldArchive);
        await scratchpads.SaveAsync(item.Id, currentRef, currentArchive);
        var snapshot = (await store.GetAsync(item.Id))!;
        var published = snapshot with
        {
            PreemptedAt = new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero),
            PreemptCheckpoint = currentRef.Value,
            AgentTurnResumeCheckpoint = ResumeCheckpoint(),
            UpdatedAt = new DateTimeOffset(2026, 7, 12, 1, 0, 1, TimeSpan.Zero),
        };

        Assert.True(await scratchpads.TryPublishAsync(
            published,
            snapshot.State,
            snapshot.UpdatedAt,
            currentRef));

        var persisted = (await store.GetAsync(item.Id))!;
        Assert.Equal(currentRef.Value, persisted.PreemptCheckpoint);
        Assert.Equal(published.AgentTurnResumeCheckpoint, persisted.AgentTurnResumeCheckpoint);
        Assert.Null(await scratchpads.ReadAsync(item.Id, oldRef));
        Assert.NotNull(await scratchpads.ReadAsync(item.Id, currentRef));
        Assert.Equal(0, await scratchpads.DeleteAsync(item.Id, currentRef));
        Assert.NotNull(await scratchpads.ReadAsync(item.Id, currentRef));
    }

    [Fact]
    public async Task TryPublish_WhenLifecycleCasIsStale_PreservesCurrentMetadataAndArchive()
    {
        var path = DatabasePath();
        var item = Sample() with
        {
            State = WorkItemState.Working,
            WorkBranch = "codeybox/stale-publication",
            Agent = AgentKind.Claude,
            AgentInstanceId = "claude/default",
        };
        var currentArchive = Archive("already published checkpoint");
        var candidateArchive = Archive("racing unpublished checkpoint");
        var currentRef = Ref(item.Id, '7', currentArchive);
        var candidateRef = Ref(item.Id, '8', candidateArchive);
        var current = item with
        {
            PreemptedAt = new DateTimeOffset(2026, 7, 12, 2, 0, 0, TimeSpan.Zero),
            PreemptCheckpoint = currentRef.Value,
            AgentTurnResumeCheckpoint = ResumeCheckpoint(),
        };
        using var store = new SqliteWorkItemStore(path);
        await store.CreateAsync(current);
        IAgentTurnScratchpadStore scratchpads = store;
        await scratchpads.SaveAsync(item.Id, currentRef, currentArchive);
        await scratchpads.SaveAsync(item.Id, candidateRef, candidateArchive);
        var candidate = current with
        {
            PreemptCheckpoint = candidateRef.Value,
            UpdatedAt = current.UpdatedAt.AddSeconds(1),
        };

        Assert.False(await scratchpads.TryPublishAsync(
            candidate,
            current.State,
            current.UpdatedAt.AddSeconds(-1),
            candidateRef));

        var persisted = (await store.GetAsync(item.Id))!;
        Assert.Equal(currentRef.Value, persisted.PreemptCheckpoint);
        Assert.NotNull(await scratchpads.ReadAsync(item.Id, currentRef));
        Assert.Equal(1, await scratchpads.DeleteAsync(item.Id, candidateRef));
        Assert.Null(await scratchpads.ReadAsync(item.Id, candidateRef));
    }

    [Fact]
    public async Task TryPublish_WithoutVerifiedArchiveRow_FailsWithoutChangingMetadata()
    {
        var path = DatabasePath();
        var item = Sample() with
        {
            State = WorkItemState.Working,
            WorkBranch = "codeybox/missing-publication-archive",
            Agent = AgentKind.Claude,
            AgentInstanceId = "claude/default",
        };
        var missingArchive = Archive("never saved");
        var missingRef = Ref(item.Id, '9', missingArchive);
        using var store = new SqliteWorkItemStore(path);
        await store.CreateAsync(item);
        var snapshot = (await store.GetAsync(item.Id))!;
        var candidate = snapshot with
        {
            PreemptedAt = new DateTimeOffset(2026, 7, 12, 3, 0, 0, TimeSpan.Zero),
            PreemptCheckpoint = missingRef.Value,
            AgentTurnResumeCheckpoint = ResumeCheckpoint(),
            UpdatedAt = snapshot.UpdatedAt.AddSeconds(1),
        };

        await Assert.ThrowsAsync<AgentTurnScratchpadCorruptException>(
            () => store.TryPublishAsync(
                candidate,
                snapshot.State,
                snapshot.UpdatedAt,
                missingRef));

        var persisted = (await store.GetAsync(item.Id))!;
        Assert.Null(persisted.PreemptCheckpoint);
        Assert.Null(persisted.AgentTurnResumeCheckpoint);
    }

    [Fact]
    public async Task TryPublishRecoveryLease_EnforcesGlobalCapAcrossConcurrentStoreInstancesAndRestart()
    {
        var path = DatabasePath();
        var first = WorkingSample("first-retained");
        var second = WorkingSample("second-retained");
        using (var setup = new SqliteWorkItemStore(path))
        {
            await setup.CreateAsync(first);
            await setup.CreateAsync(second);
        }

        using (var firstStore = new SqliteWorkItemStore(path))
        using (var secondStore = new SqliteWorkItemStore(path))
        {
            var firstSnapshot = (await firstStore.GetAsync(first.Id))!;
            var secondSnapshot = (await secondStore.GetAsync(second.Id))!;
            var firstCandidate = Retained(
                firstSnapshot,
                new SandboxRecoveryLease("incus", "sandbox-first", "token-first"));
            var secondCandidate = Retained(
                secondSnapshot,
                new SandboxRecoveryLease("incus", "sandbox-second", "token-second"));

            var publications = await Task.WhenAll(
                ((IAgentTurnScratchpadStore)firstStore).TryPublishRecoveryLeaseAsync(
                    firstCandidate,
                    firstSnapshot.State,
                    firstSnapshot.UpdatedAt,
                    maximumRetainedSandboxes: 1),
                ((IAgentTurnScratchpadStore)secondStore).TryPublishRecoveryLeaseAsync(
                    secondCandidate,
                    secondSnapshot.State,
                    secondSnapshot.UpdatedAt,
                    maximumRetainedSandboxes: 1));

            Assert.Single(publications, static published => published);
        }

        using var reopened = new SqliteWorkItemStore(path);
        var persisted = new[]
        {
            (await reopened.GetAsync(first.Id))!,
            (await reopened.GetAsync(second.Id))!,
        };
        Assert.Single(persisted, static item => item.AgentTurnRecoveryLease is not null);
        var unretained = Assert.Single(persisted, static item => item.AgentTurnRecoveryLease is null);
        var retryCandidate = Retained(
            unretained,
            new SandboxRecoveryLease("incus", "sandbox-retry", "token-retry"));
        Assert.False(await ((IAgentTurnScratchpadStore)reopened).TryPublishRecoveryLeaseAsync(
            retryCandidate,
            unretained.State,
            unretained.UpdatedAt,
            maximumRetainedSandboxes: 1));
    }

    [Fact]
    public async Task TryPublishRecoveryLease_AllowsExactRefreshButRejectsCapabilityReplacement()
    {
        var path = DatabasePath();
        var item = WorkingSample("lease-capability-cas");
        using var store = new SqliteWorkItemStore(path);
        await store.CreateAsync(item);
        IAgentTurnScratchpadStore scratchpads = store;
        var snapshot = (await store.GetAsync(item.Id))!;
        var lease = new SandboxRecoveryLease("incus", "sandbox-bound", "token-bound");

        Assert.True(await scratchpads.TryPublishRecoveryLeaseAsync(
            Retained(snapshot, lease),
            snapshot.State,
            snapshot.UpdatedAt,
            maximumRetainedSandboxes: 1));

        var retained = (await store.GetAsync(item.Id))!;
        var refreshed = Retained(retained, lease);
        Assert.True(await scratchpads.TryPublishRecoveryLeaseAsync(
            refreshed,
            retained.State,
            retained.UpdatedAt,
            maximumRetainedSandboxes: 1));

        var afterRefresh = (await store.GetAsync(item.Id))!;
        var replacement = Retained(
            afterRefresh,
            new SandboxRecoveryLease("incus", "sandbox-other", "token-other"));
        Assert.False(await scratchpads.TryPublishRecoveryLeaseAsync(
            replacement,
            afterRefresh.State,
            afterRefresh.UpdatedAt,
            maximumRetainedSandboxes: 1));

        Assert.Equal(lease, (await store.GetAsync(item.Id))!.AgentTurnRecoveryLease);
    }

    [Fact]
    public async Task TryPublishImmutableCheckpoint_AtomicallyConsumesRecoveryLease()
    {
        var path = DatabasePath();
        var item = WorkingSample("lease-to-checkpoint");
        using var store = new SqliteWorkItemStore(path);
        await store.CreateAsync(item);
        IAgentTurnScratchpadStore scratchpads = store;
        var snapshot = (await store.GetAsync(item.Id))!;
        var lease = new SandboxRecoveryLease("incus", "sandbox-convert", "token-convert");
        Assert.True(await scratchpads.TryPublishRecoveryLeaseAsync(
            Retained(snapshot, lease),
            snapshot.State,
            snapshot.UpdatedAt,
            maximumRetainedSandboxes: 1));

        var retained = (await store.GetAsync(item.Id))!;
        var archive = Archive("private state recovered from retained sandbox");
        var checkpointRef = Ref(item.Id, 'a', archive);
        await scratchpads.SaveAsync(item.Id, checkpointRef, archive);
        var immutable = retained with
        {
            PreemptCheckpoint = checkpointRef.Value,
            AgentTurnRecoveryLease = null,
            AgentTurnResumeCheckpoint = ResumeCheckpoint(),
            UpdatedAt = retained.UpdatedAt.AddSeconds(1),
        };

        Assert.True(await scratchpads.TryPublishAsync(
            immutable,
            retained.State,
            retained.UpdatedAt,
            checkpointRef));

        var persisted = (await store.GetAsync(item.Id))!;
        Assert.Equal(checkpointRef.Value, persisted.PreemptCheckpoint);
        Assert.Null(persisted.AgentTurnRecoveryLease);
        Assert.NotNull(await scratchpads.ReadAsync(item.Id, checkpointRef));
    }

    [Fact]
    public async Task DeleteOlderAndDeleteAll_PreserveTheRequestedGeneration()
    {
        var path = DatabasePath();
        var item = Sample();
        var firstArchive = Archive("first generation");
        var secondArchive = Archive("second generation");
        var unsavedArchive = Archive("unsaved generation");
        var firstRef = Ref(item.Id, '1', firstArchive);
        var secondRef = Ref(item.Id, '2', secondArchive);
        var unsavedRef = Ref(item.Id, '3', unsavedArchive);
        using var store = new SqliteWorkItemStore(path);
        await store.CreateAsync(item);
        IAgentTurnScratchpadStore scratchpads = store;
        await scratchpads.SaveAsync(item.Id, firstRef, firstArchive);
        await scratchpads.SaveAsync(item.Id, secondRef, secondArchive);

        Assert.Equal(0, await scratchpads.DeleteOlderAsync(item.Id, unsavedRef));
        Assert.NotNull(await scratchpads.ReadAsync(item.Id, firstRef));
        Assert.Equal(1, await scratchpads.DeleteOlderAsync(item.Id, secondRef));
        Assert.Null(await scratchpads.ReadAsync(item.Id, firstRef));
        Assert.NotNull(await scratchpads.ReadAsync(item.Id, secondRef));

        Assert.Equal(1, await scratchpads.DeleteAllAsync(item.Id));
        Assert.Null(await scratchpads.ReadAsync(item.Id, secondRef));
        Assert.Equal(0, await scratchpads.DeleteAllAsync(item.Id));
    }

    [Fact]
    public async Task Delete_RemovesOnlyTheExactCheckpointRef()
    {
        var path = DatabasePath();
        var item = Sample();
        var firstArchive = Archive("failed capture");
        var validArchive = Archive("previous valid capture");
        var failedRef = Ref(item.Id, '4', firstArchive);
        var validRef = Ref(item.Id, '5', validArchive);
        using var store = new SqliteWorkItemStore(path);
        await store.CreateAsync(item);
        IAgentTurnScratchpadStore scratchpads = store;
        await scratchpads.SaveAsync(item.Id, failedRef, firstArchive);
        await scratchpads.SaveAsync(item.Id, validRef, validArchive);

        Assert.Equal(1, await scratchpads.DeleteAsync(item.Id, failedRef));
        Assert.Null(await scratchpads.ReadAsync(item.Id, failedRef));
        Assert.NotNull(await scratchpads.ReadAsync(item.Id, validRef));
        Assert.Equal(0, await scratchpads.DeleteAsync(item.Id, failedRef));
    }

    [Fact]
    public async Task BulkDeletes_PreserveCurrentlyPublishedArchive()
    {
        var path = DatabasePath();
        var draft = Sample();
        var currentArchive = Archive("published current archive");
        var laterArchive = Archive("later unpublished archive");
        var currentRef = Ref(draft.Id, '4', currentArchive);
        var laterRef = Ref(draft.Id, '5', laterArchive);
        var item = Checkpointed(draft, currentRef) with
        {
            PreemptedAt = new DateTimeOffset(2026, 7, 12, 4, 0, 0, TimeSpan.Zero),
        };
        using var store = new SqliteWorkItemStore(path);
        await store.CreateAsync(item);
        IAgentTurnScratchpadStore scratchpads = store;
        await scratchpads.SaveAsync(item.Id, currentRef, currentArchive);
        await scratchpads.SaveAsync(item.Id, laterRef, laterArchive);

        Assert.Equal(0, await scratchpads.DeleteOlderAsync(item.Id, laterRef));
        Assert.NotNull(await scratchpads.ReadAsync(item.Id, currentRef));
        Assert.NotNull(await scratchpads.ReadAsync(item.Id, laterRef));

        Assert.Equal(1, await scratchpads.DeleteAllAsync(item.Id));
        Assert.NotNull(await scratchpads.ReadAsync(item.Id, currentRef));
        Assert.Null(await scratchpads.ReadAsync(item.Id, laterRef));
        Assert.Equal(0, await scratchpads.DeleteAllAsync(item.Id));
    }

    [Fact]
    public async Task UpdateClearingCheckpointMetadata_DeletesPrivateArchiveAtomically()
    {
        var path = DatabasePath();
        var ordinaryItem = Sample();
        var draft = Sample();
        var archive = Archive("private bytes cleared with metadata");
        var checkpointRef = Ref(draft.Id, '9', archive);
        var checkpointedItem = Checkpointed(draft, checkpointRef);
        using var store = new SqliteWorkItemStore(path);

        // A normal work item has NULL checkpoint metadata at creation. The
        // cleanup trigger must not interfere with that common path.
        await store.CreateAsync(ordinaryItem);
        Assert.NotNull(await store.GetAsync(ordinaryItem.Id));

        await store.CreateAsync(checkpointedItem);
        IAgentTurnScratchpadStore scratchpads = store;
        await scratchpads.SaveAsync(checkpointedItem.Id, checkpointRef, archive);
        Assert.NotNull(await scratchpads.ReadAsync(checkpointedItem.Id, checkpointRef));

        await store.UpdateAsync(checkpointedItem with { AgentTurnResumeCheckpoint = null });

        Assert.Null(await scratchpads.ReadAsync(checkpointedItem.Id, checkpointRef));
        Assert.Null((await store.GetAsync(checkpointedItem.Id))!.AgentTurnResumeCheckpoint);
    }

    [Fact]
    public async Task Read_WhenBlobContentIsTampered_FailsHashVerification()
    {
        var path = DatabasePath();
        var draft = Sample();
        var archive = Archive("original private bytes");
        var checkpointRef = Ref(draft.Id, 'c', archive);
        var item = Checkpointed(draft, checkpointRef);
        using (var store = new SqliteWorkItemStore(path))
        {
            await store.CreateAsync(item);
            await ((IAgentTurnScratchpadStore)store).SaveAsync(item.Id, checkpointRef, archive);
        }

        using (var raw = Open(path))
        {
            using var tamper = raw.CreateCommand();
            tamper.CommandText = """
                UPDATE agent_turn_scratchpads
                SET archive_bytes = $bytes
                WHERE work_item_id = $work_item_id AND checkpoint_ref = $checkpoint_ref;
                """;
            tamper.Parameters.Add("$bytes", SqliteType.Blob).Value =
                Encoding.UTF8.GetBytes("tampered private bytes");
            tamper.Parameters.AddWithValue("$work_item_id", item.Id.ToString());
            tamper.Parameters.AddWithValue("$checkpoint_ref", checkpointRef.Value);
            Assert.Equal(1, tamper.ExecuteNonQuery());
        }

        using var reopened = new SqliteWorkItemStore(path);
        var exception = await Assert.ThrowsAsync<AgentTurnScratchpadCorruptException>(
            () => ((IAgentTurnScratchpadStore)reopened).ReadAsync(item.Id, checkpointRef));

        Assert.Equal(item.Id, exception.WorkItemId);
        Assert.Equal(checkpointRef, exception.CheckpointRef);
        Assert.Contains("do not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_WhenBlobExceedsMaximum_RejectsLengthBeforeMaterializingBlob()
    {
        var path = DatabasePath();
        var draft = Sample();
        var archive = Archive("bounded private bytes");
        var checkpointRef = Ref(draft.Id, 'd', archive);
        var item = Checkpointed(draft, checkpointRef);
        using (var store = new SqliteWorkItemStore(path))
        {
            await store.CreateAsync(item);
            await ((IAgentTurnScratchpadStore)store).SaveAsync(item.Id, checkpointRef, archive);
        }

        using (var raw = Open(path))
        {
            using (var constraints = raw.CreateCommand())
            {
                constraints.CommandText = "PRAGMA ignore_check_constraints=ON;";
                constraints.ExecuteNonQuery();
            }
            using var tamper = raw.CreateCommand();
            tamper.CommandText = """
                UPDATE agent_turn_scratchpads
                SET archive_bytes = zeroblob($size)
                WHERE work_item_id = $work_item_id AND checkpoint_ref = $checkpoint_ref;
                """;
            tamper.Parameters.AddWithValue("$size", AgentTurnScratchpadArchive.MaximumBytes + 1);
            tamper.Parameters.AddWithValue("$work_item_id", item.Id.ToString());
            tamper.Parameters.AddWithValue("$checkpoint_ref", checkpointRef.Value);
            Assert.Equal(1, tamper.ExecuteNonQuery());
        }

        using var reopened = new SqliteWorkItemStore(path);
        var exception = await Assert.ThrowsAsync<AgentTurnScratchpadCorruptException>(
            () => ((IAgentTurnScratchpadStore)reopened).ReadAsync(item.Id, checkpointRef));

        Assert.Contains("outside the allowed range", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_WhenDuplicatedMetadataIsTampered_FailsClosed()
    {
        var path = DatabasePath();
        var draft = Sample();
        var archive = Archive("metadata-bound private bytes");
        var checkpointRef = Ref(draft.Id, 'e', archive);
        var item = Checkpointed(draft, checkpointRef);
        using (var store = new SqliteWorkItemStore(path))
        {
            await store.CreateAsync(item);
            await ((IAgentTurnScratchpadStore)store).SaveAsync(item.Id, checkpointRef, archive);
        }

        using (var raw = Open(path))
        {
            using var tamper = raw.CreateCommand();
            tamper.CommandText = """
                UPDATE agent_turn_scratchpads
                SET source_commit_sha = $source_commit_sha
                WHERE work_item_id = $work_item_id AND checkpoint_ref = $checkpoint_ref;
                """;
            tamper.Parameters.AddWithValue("$source_commit_sha", new string('f', 40));
            tamper.Parameters.AddWithValue("$work_item_id", item.Id.ToString());
            tamper.Parameters.AddWithValue("$checkpoint_ref", checkpointRef.Value);
            Assert.Equal(1, tamper.ExecuteNonQuery());
        }

        using var reopened = new SqliteWorkItemStore(path);
        var exception = await Assert.ThrowsAsync<AgentTurnScratchpadCorruptException>(
            () => ((IAgentTurnScratchpadStore)reopened).ReadAsync(item.Id, checkpointRef));

        Assert.Contains("source commit SHA does not match", exception.Message, StringComparison.Ordinal);
    }

    private string DatabasePath() => Path.Combine(_directory, $"{Guid.NewGuid():N}.db");

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }

    private static AgentTurnScratchpadArchive Archive(string value) =>
        new(Encoding.UTF8.GetBytes(value));

    private static AgentTurnCheckpointRef Ref(
        WorkItemId workItemId,
        char commitDigit,
        AgentTurnScratchpadArchive archive) =>
        AgentTurnCheckpointRef.Create(workItemId, new string(commitDigit, 40), archive);

    private static WorkItem Sample() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("scratchpad-persistence"),
        Title = "durable host-private scratchpad",
        Prompt = "continue this turn",
    };

    private static WorkItem WorkingSample(string title) => Sample() with
    {
        State = WorkItemState.Working,
        Title = title,
        WorkBranch = $"codeybox/{title}",
        Agent = AgentKind.Claude,
        AgentInstanceId = "claude/default",
    };

    private static WorkItem Retained(WorkItem item, SandboxRecoveryLease lease) => item with
    {
        PreemptedAt = new DateTimeOffset(2026, 7, 12, 5, 0, 0, TimeSpan.Zero),
        PreemptCheckpoint = null,
        AgentTurnResumeCheckpoint = ResumeCheckpoint(),
        AgentTurnRecoveryLease = lease,
        UpdatedAt = item.UpdatedAt.AddSeconds(1),
    };

    private static WorkItem Checkpointed(WorkItem item, AgentTurnCheckpointRef checkpointRef) => item with
    {
        State = WorkItemState.Failed,
        FailureKind = WorkItemFailureKinds.Infrastructure,
        PreemptCheckpoint = checkpointRef.Value,
        AgentTurnResumeCheckpoint = ResumeCheckpoint(),
    };

    private static AgentTurnResumeCheckpoint ResumeCheckpoint() => new(
        AgentKind.Claude,
        "claude/default",
        modelId: null,
        reasoningMode: null,
        nativeSessionId: null,
        WorkItemState.Working,
        AgentTurnResumePhase.Work,
        iteration: null,
        promptRevision: 1,
        createdAt: new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
}
