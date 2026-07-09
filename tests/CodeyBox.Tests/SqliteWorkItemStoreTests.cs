using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class SqliteWorkItemStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-test-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public SqliteWorkItemStoreTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem Sample() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        Agent = AgentKind.Claude,
    };

    [Fact]
    public async Task RoundTrip_PreservesAllFields()
    {
        var item = Sample() with
        {
            BaseBranch = "main",
            WorkBranch = "feature/x",
            WorkTimeout = TimeSpan.FromMinutes(7),
            MergeTimeout = TimeSpan.FromMinutes(3),
            PushUpstream = false,
            UpstreamPushAttempts = 2,
            AuditorProfile = "uat",
        };
        await _store.CreateAsync(item);
        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(item.Title, read!.Title);
        Assert.Equal(item.BaseBranch, read.BaseBranch);
        Assert.Equal(item.WorkTimeout, read.WorkTimeout);
        Assert.Equal(item.PushUpstream, read.PushUpstream);
        Assert.Equal(item.UpstreamPushAttempts, read.UpstreamPushAttempts);
        Assert.Equal(item.Agent, read.Agent);
        Assert.Equal("uat", read.AuditorProfile);
    }

    [Fact]
    public async Task CreateAsync_RoundTripsPreserveWorkBranchOnQueuedPickup()
    {
        var item = Sample() with
        {
            WorkBranch = "feature/operator-resume",
            PreserveWorkBranchOnQueuedPickup = true,
        };

        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.True(read!.PreserveWorkBranchOnQueuedPickup);
        Assert.Equal(item.WorkBranch, read.WorkBranch);
    }

    [Fact]
    public async Task UpdateAsync_PersistsTransitions()
    {
        var item = Sample();
        await _store.CreateAsync(item);
        await _store.UpdateAsync(item.With(WorkItemState.Working));
        await _store.UpdateAsync(item.With(WorkItemState.Failed, "broken"));
        var read = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, read!.State);
        Assert.Equal("broken", read.LastError);
    }

    [Fact]
    public async Task UpdateAsync_PreservesExistingKnobsMap()
    {
        var item = Sample() with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["changeScope"] = "refactor",
            },
        };
        await _store.CreateAsync(item);

        var updated = item with
        {
            State = WorkItemState.Working,
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["changeScope"] = "surgical",
            },
        };
        await _store.UpdateAsync(updated);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.Working, read!.State);
        Assert.Single(read!.Knobs);
        Assert.Equal("refactor", read.Knobs["changeScope"]);
    }

    [Fact]
    public async Task RecordAuditProgressAsync_ReplacesExistingIterationRow()
    {
        var item = Sample();
        await _store.CreateAsync(item);
        var attemptStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        await _store.RecordAuditProgressAsync(
            item.Id,
            attemptStartedAt,
            new AuditProgressRecord(
                Iteration: 2,
                MaxIterations: 3,
                BlockingFindings: 1,
                NonBlockingFindings: 0,
                BlockingFindingIds: ["old-id"],
                BlockingFindingsDetails:
                [
                    new AuditProgressFinding("old-auditor", AuditSeverity.Error, "old blocker", "old", "src/Old.cs:1"),
                ],
                Findings:
                [
                    new AuditProgressFinding("old-auditor", AuditSeverity.Error, "old blocker", "old", "src/Old.cs:1"),
                ],
                WorkBranchTip: "old-tip"),
            DateTimeOffset.UtcNow);
        await _store.RecordAuditProgressAsync(
            item.Id,
            attemptStartedAt,
            new AuditProgressRecord(
                Iteration: 2,
                MaxIterations: 4,
                BlockingFindings: 1,
                NonBlockingFindings: 1,
                BlockingFindingIds: ["new-id"],
                BlockingFindingsDetails:
                [
                    new AuditProgressFinding("new-auditor", AuditSeverity.Error, "new blocker", "new", "src/New.cs:2"),
                ],
                Findings:
                [
                    new AuditProgressFinding("new-auditor", AuditSeverity.Error, "new blocker", "new", "src/New.cs:2"),
                    new AuditProgressFinding("new-auditor", AuditSeverity.Warning, "new warning", "warn", "src/Warn.cs:3"),
                ],
                WorkBranchTip: "new-tip"),
            DateTimeOffset.UtcNow.AddSeconds(1));

        var records = await _store.GetAuditProgressAsync(item.Id, attemptStartedAt);

        var record = Assert.Single(records);
        Assert.Equal(2, record.Iteration);
        Assert.Equal(4, record.MaxIterations);
        Assert.Equal(["new-id"], record.BlockingFindingIds);
        Assert.Equal("new-tip", record.WorkBranchTip);
        Assert.Equal("new blocker", Assert.Single(record.BlockingFindingsDetails).Title);
        Assert.Equal(2, record.Findings.Count);
        Assert.Contains(record.Findings, f => f.Title == "new warning" && f.Severity == AuditSeverity.Warning);
    }

    [Fact]
    public async Task ListByStateAsync_FiltersCorrectly()
    {
        var working = Sample();
        var done = Sample();
        await _store.CreateAsync(working with { State = WorkItemState.Working });
        await _store.CreateAsync(done with { State = WorkItemState.Done });

        var results = new List<WorkItem>();
        await foreach (var w in _store.ListByStateAsync(WorkItemState.Working)) results.Add(w);
        Assert.Single(results);
        Assert.Equal(working.Id, results[0].Id);
    }

    [Fact]
    public async Task ListWaitingForQuotaResetByPriorityAsync_AppliesLimitAndPriorityOrder()
    {
        var now = DateTimeOffset.UtcNow;
        var highOlder = Sample() with
        {
            State = WorkItemState.WaitingForQuotaReset,
            Priority = 10,
            CreatedAt = now.AddMinutes(-3),
        };
        var highNewer = Sample() with
        {
            State = WorkItemState.WaitingForQuotaReset,
            Priority = 10,
            CreatedAt = now.AddMinutes(-1),
        };
        var low = Sample() with
        {
            State = WorkItemState.WaitingForQuotaReset,
            Priority = 1,
            CreatedAt = now.AddMinutes(-5),
        };
        var queued = Sample() with
        {
            State = WorkItemState.Queued,
            Priority = 100,
            CreatedAt = now.AddMinutes(-10),
        };
        await _store.CreateAsync(low);
        await _store.CreateAsync(highNewer);
        await _store.CreateAsync(queued);
        await _store.CreateAsync(highOlder);

        var results = new List<WorkItem>();
        await foreach (var item in _store.ListWaitingForQuotaResetByPriorityAsync(limit: 2))
            results.Add(item);

        Assert.Equal([highOlder.Id, highNewer.Id], results.Select(item => item.Id));
    }

    [Fact]
    public async Task ReadMethods_WaitBehindSharedConnectionGate()
    {
        var queued = Sample();
        var now = DateTimeOffset.UtcNow;
        var attemptStartedAt = now.AddMinutes(-2);
        var releaseId = ReleaseId.New();
        var source = Sample() with { Title = "source" };
        var working = Sample() with
        {
            State = WorkItemState.Working,
            StartedAt = now.AddMinutes(-1),
            SuspendedVmName = "vm-gated-read",
            SuspendedAt = now,
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["jobtrack"] = "EXT-123",
                ["legacy"] = "LEG-123",
            },
            BaselineImageRef = "cb-baseline-gated-read",
            ReplayOfWorkItemId = source.Id,
            ReleaseId = releaseId,
        };
        await _store.CreateAsync(queued);
        await _store.CreateAsync(source);
        await _store.CreateAsync(working);
        await _store.RecordIterationDispatchAsync(working.Id, iteration: 1, promptRevisionAtDispatch: 1, dispatchedAt: now);
        await _store.RecordAuditProgressAsync(
            working.Id,
            attemptStartedAt,
            new AuditProgressRecord(
                Iteration: 1,
                MaxIterations: 1,
                BlockingFindings: 0,
                NonBlockingFindings: 0,
                BlockingFindingIds: [],
                BlockingFindingsDetails: [],
                Findings: [],
                WorkBranchTip: null),
            now);

        using var gate = _store.AcquireConnectionGateForTesting();
        var reads = new Dictionary<string, Task>(StringComparer.Ordinal)
        {
            ["GetAsync"] = _store.GetAsync(queued.Id),
            ["ListAsync"] = DrainAsync(_store.ListAsync()),
            ["ListByStateAsync"] = DrainAsync(_store.ListByStateAsync(WorkItemState.Working)),
            ["CountByStateAsync"] = _store.CountByStateAsync(WorkItemState.Queued),
            ["ListDispatchEligibleByPriorityAsync"] = DrainAsync(
                _store.ListDispatchEligibleByPriorityAsync(new HashSet<WorkItemId>())),
            ["CountStartedInWindowAsync"] = _store.CountStartedInWindowAsync(working.ProjectId, now.AddHours(-1)),
            ["CountInFlightAsync"] = _store.CountInFlightAsync(working.ProjectId),
            ["CountInFlightSplitByRefactorAsync"] = _store.CountInFlightSplitByRefactorAsync(working.ProjectId),
            ["GetByExternalIdAsync"] = _store.GetByExternalIdAsync(working.ProjectId, "EXT-123"),
            ["GetByNamespacedExternalIdAsync"] = _store.GetByNamespacedExternalIdAsync(working.ProjectId, "jobtrack", "EXT-123"),
            ["GetFleetStateCountsAsync"] = _store.GetFleetStateCountsAsync(),
            ["GetFleetRecentOutcomesAsync"] = _store.GetFleetRecentOutcomesAsync(),
            ["GetFleetPauseStatesAsync"] = _store.GetFleetPauseStatesAsync(),
            ["ListSuspendedAsync"] = DrainAsync(_store.ListSuspendedAsync()),
            ["GetActiveBaselineImageRefsAsync"] = _store.GetActiveBaselineImageRefsAsync(),
            ["ListWorkItemsForBaselineAsync"] = _store.ListWorkItemsForBaselineAsync("cb-baseline-gated-read"),
            ["ListByReplaySourceAsync"] = DrainAsync(_store.ListByReplaySourceAsync(source.Id)),
            ["ListByReleaseAsync"] = DrainAsync(_store.ListByReleaseAsync(releaseId)),
            ["GetIterationsAsync"] = _store.GetIterationsAsync(working.Id),
            ["GetAuditProgressAsync"] = _store.GetAuditProgressAsync(working.Id, attemptStartedAt),
        };

        await Task.Delay(100);

        var completedBeforeRelease = reads
            .Where(kv => kv.Value.IsCompleted)
            .Select(kv => kv.Key)
            .ToArray();
        Assert.Empty(completedBeforeRelease);

        gate.Dispose();
        await Task.WhenAll(reads.Values).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RoundTrip_NonEmptyDependsOn_Preserved()
    {
        var dep1 = Sample();
        var dep2 = Sample();
        await _store.CreateAsync(dep1);
        await _store.CreateAsync(dep2);

        var item = Sample() with { DependsOn = [dep1.Id, dep2.Id] };
        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(2, read!.DependsOn.Count);
        Assert.Contains(dep1.Id, read.DependsOn);
        Assert.Contains(dep2.Id, read.DependsOn);
    }

    [Fact]
    public async Task RoundTrip_EmptyDependsOn_Preserved()
    {
        var item = Sample() with { DependsOn = [] };
        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Empty(read!.DependsOn);
    }

    [Fact]
    public async Task RoundTrip_RequiredCapabilities_Preserved()
    {
        var item = Sample() with { RequiredCapabilities = new[] { "sensitive", "architectural" } };
        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(new[] { "sensitive", "architectural" }, read!.RequiredCapabilities);
    }

    [Fact]
    public async Task RoundTrip_EmptyRequiredCapabilities_Preserved()
    {
        var item = Sample() with { RequiredCapabilities = Array.Empty<string>() };
        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Empty(read!.RequiredCapabilities);
    }

    [Fact]
    public async Task UpdateAsync_PersistsRequiredCapabilities()
    {
        var item = Sample() with { RequiredCapabilities = Array.Empty<string>() };
        await _store.CreateAsync(item);
        var updated = item with { RequiredCapabilities = new[] { "sensitive" } };
        await _store.UpdateAsync(updated);
        var read = await _store.GetAsync(item.Id);
        Assert.Equal(new[] { "sensitive" }, read!.RequiredCapabilities);
    }

    [Fact]
    public async Task RoundTrip_CheckAndActFields_Preserved()
    {
        // Pin the four new columns added for the check-and-act job type:
        // job_type, check_spec_json, check_verdict_json, origin_check_work_item_id.
        // Use a fully-populated CheckAndActSpec (with OnYes carrying every
        // optional field including DependsOn) so a serialiser shape mismatch
        // would surface as a missing/changed value on read.
        var deps = new[] { "JIRA-101", "github:PR-7" };
        var spec = new CheckAndActSpec
        {
            Question = "Is the code vulnerable?",
            ActionableAnswer = false,
            OnYes = new OnYesActionSpec
            {
                Title = "Fix all the things",
                Prompt = "Remediate",
                MinModelScore = 75,
                Priority = 250,
                Agent = "claude",
                AgentClassId = "secure-class",
                DependsOn = deps,
            },
        };
        var verdict = new CheckVerdict
        {
            Answer = true,
            Evidence = "src/Foo.cs:42 builds SQL via interpolation",
            Confidence = "medium",
        };
        var originId = WorkItemId.New();
        var item = Sample() with
        {
            JobType = JobType.CheckAndAct,
            Check = spec,
            Verdict = verdict,
            OriginCheckWorkItemId = originId,
            TemplateName = "security",
            TemplateEntryIndex = 3,
        };
        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(JobType.CheckAndAct, read!.JobType);
        Assert.Equal(originId, read.OriginCheckWorkItemId);
        Assert.Equal("security", read.TemplateName);
        Assert.Equal(3, read.TemplateEntryIndex);

        Assert.NotNull(read.Check);
        Assert.Equal(spec.Question, read.Check!.Question);
        Assert.False(read.Check.ActionableAnswer);
        Assert.Equal("Fix all the things", read.Check.OnYes.Title);
        Assert.Equal("Remediate", read.Check.OnYes.Prompt);
        Assert.Equal(75, read.Check.OnYes.MinModelScore);
        Assert.Equal(250, read.Check.OnYes.Priority);
        Assert.Equal("claude", read.Check.OnYes.Agent);
        Assert.Equal("secure-class", read.Check.OnYes.AgentClassId);
        Assert.NotNull(read.Check.OnYes.DependsOn);
        Assert.Equal(deps, read.Check.OnYes.DependsOn);

        Assert.NotNull(read.Verdict);
        Assert.True(read.Verdict!.Answer);
        Assert.Contains("Foo.cs", read.Verdict.Evidence);
        Assert.Equal("medium", read.Verdict.Confidence);
    }

    [Fact]
    public async Task TemplateProvenance_SurvivesStateUpdates()
    {
        var item = Sample() with
        {
            TemplateName = "security",
            TemplateEntryIndex = 2,
        };
        await _store.CreateAsync(item);

        await _store.UpdateAsync(item.With(WorkItemState.Working));

        var afterUpdate = await _store.GetAsync(item.Id);
        Assert.NotNull(afterUpdate);
        Assert.Equal(WorkItemState.Working, afterUpdate!.State);
        Assert.Equal("security", afterUpdate.TemplateName);
        Assert.Equal(2, afterUpdate.TemplateEntryIndex);

        var updated = await _store.TryUpdateIfStateAsync(
            afterUpdate.With(WorkItemState.Done),
            WorkItemState.Working);
        Assert.True(updated);

        var afterConditionalUpdate = await _store.GetAsync(item.Id);
        Assert.NotNull(afterConditionalUpdate);
        Assert.Equal(WorkItemState.Done, afterConditionalUpdate!.State);
        Assert.Equal("security", afterConditionalUpdate.TemplateName);
        Assert.Equal(2, afterConditionalUpdate.TemplateEntryIndex);
    }

    [Fact]
    public async Task TryUpdateIfStateAndUpdatedAtAsync_PersistsTransientRetryFields()
    {
        var item = Sample();
        await _store.CreateAsync(item);
        var persisted = await _store.GetAsync(item.Id);
        Assert.NotNull(persisted);

        var firstFailedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var nextRetryAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var updated = persisted! with
        {
            State = WorkItemState.WaitingForTransientRetry,
            LastError = "Agent claude reported transient transport failure",
            FailureKind = "transient",
            NextTransientRetryAt = nextRetryAt,
            TransientRetryAttempts = 2,
            TransientRetryFirstFailedAt = firstFailedAt,
            TransientRetryFrom = "merge",
            UpdatedAt = persisted.UpdatedAt.AddSeconds(1),
        };

        var wrote = await _store.TryUpdateIfStateAndUpdatedAtAsync(
            updated,
            WorkItemState.Queued,
            persisted.UpdatedAt);

        Assert.True(wrote);
        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, read!.State);
        Assert.Equal("transient", read.FailureKind);
        Assert.Equal(nextRetryAt, read.NextTransientRetryAt);
        Assert.Equal(2, read.TransientRetryAttempts);
        Assert.Equal(firstFailedAt, read.TransientRetryFirstFailedAt);
        Assert.Equal("merge", read.TransientRetryFrom);
    }

    [Fact]
    public async Task RoundTrip_NormalItem_DefaultsToJobTypeNormal_NoCheckOrVerdict()
    {
        // The migration defaults legacy / new-row job_type to 'Normal' and the
        // check/verdict columns to NULL. Reads must surface those as JobType.Normal
        // and null Check/Verdict — never as a phantom CheckAndAct row.
        var item = Sample();
        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(JobType.Normal, read!.JobType);
        Assert.Null(read.Check);
        Assert.Null(read.Verdict);
        Assert.Null(read.OriginCheckWorkItemId);
    }

    [Fact]
    public async Task ReadRow_CorruptCheckSpecJson_ReturnsNullSpec()
    {
        // ReadCheckSpec catches JsonException and returns null so a corrupt
        // payload doesn't kill the row entirely. Unlike required_capabilities
        // (a clearance gate that fails closed), the check spec is operator
        // metadata — surfacing null is the documented behaviour.
        var item = Sample() with
        {
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "is x?",
                OnYes = new OnYesActionSpec { Title = "fix", Prompt = "go" },
            },
        };
        await _store.CreateAsync(item);

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE work_items SET check_spec_json = $junk WHERE id = $id";
            cmd.Parameters.AddWithValue("$junk", "not-json");
            cmd.Parameters.AddWithValue("$id", item.Id.ToString());
            cmd.ExecuteNonQuery();
        }

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Null(read!.Check);
    }

    [Fact]
    public async Task ReadRow_CorruptCheckVerdictJson_ReturnsNullVerdict()
    {
        var item = Sample() with
        {
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "is x?",
                OnYes = new OnYesActionSpec { Title = "fix", Prompt = "go" },
            },
            Verdict = new CheckVerdict { Answer = true, Evidence = "e" },
        };
        await _store.CreateAsync(item);

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE work_items SET check_verdict_json = $junk WHERE id = $id";
            cmd.Parameters.AddWithValue("$junk", "not-json");
            cmd.Parameters.AddWithValue("$id", item.Id.ToString());
            cmd.ExecuteNonQuery();
        }

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Null(read!.Verdict);
        // The check spec is still valid and must survive a corrupt verdict.
        Assert.NotNull(read.Check);
    }

    [Fact]
    public async Task ReadRow_UnknownJobType_FallsBackToNormal()
    {
        // ReadJobType defends against schema drift (a column value the enum
        // doesn't know about) by falling back to JobType.Normal — keeps the
        // row pickable instead of failing the whole query.
        var item = Sample();
        await _store.CreateAsync(item);

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE work_items SET job_type = $junk WHERE id = $id";
            cmd.Parameters.AddWithValue("$junk", "FutureKind");
            cmd.Parameters.AddWithValue("$id", item.Id.ToString());
            cmd.ExecuteNonQuery();
        }

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(JobType.Normal, read!.JobType);
    }

    [Fact]
    public async Task RoundTrip_ReCheckVerdicts_OrderedHistoryPreserved()
    {
        // The post-act re-validation loop appends one CheckVerdict per
        // iteration to ReCheckVerdicts. Pin: ordering is preserved (the
        // first entry is the initial post-act re-check, subsequent entries
        // are after each rework), AND each verdict's fields round-trip
        // intact (Answer + Evidence + optional Confidence).
        var history = new List<CheckVerdict>
        {
            new() { Answer = true,  Evidence = "iter1 still vulnerable", Confidence = "high" },
            new() { Answer = true,  Evidence = "iter2 still vulnerable", Confidence = "medium" },
            new() { Answer = false, Evidence = "iter3 clean", Confidence = "high" },
        };
        var item = Sample() with
        {
            OriginCheckWorkItemId = WorkItemId.New(),
            ReCheckVerdicts = history,
        };
        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(3, read!.ReCheckVerdicts.Count);
        Assert.True(read.ReCheckVerdicts[0].Answer);
        Assert.True(read.ReCheckVerdicts[1].Answer);
        Assert.False(read.ReCheckVerdicts[2].Answer);
        Assert.Equal("iter1 still vulnerable", read.ReCheckVerdicts[0].Evidence);
        Assert.Equal("iter2 still vulnerable", read.ReCheckVerdicts[1].Evidence);
        Assert.Equal("iter3 clean", read.ReCheckVerdicts[2].Evidence);
        Assert.Equal("high", read.ReCheckVerdicts[0].Confidence);
        Assert.Equal("medium", read.ReCheckVerdicts[1].Confidence);
    }

    [Fact]
    public async Task RoundTrip_NoReCheckVerdicts_ReadsAsEmpty()
    {
        // Default for new rows: empty history (never re-validated). Stored
        // as '[]' per the migration default; reads must surface as an
        // empty IReadOnlyList<CheckVerdict>, never as null.
        var item = Sample();
        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.NotNull(read!.ReCheckVerdicts);
        Assert.Empty(read.ReCheckVerdicts);
    }

    [Fact]
    public async Task ReadRow_CorruptReCheckVerdictsJson_ReadsAsEmpty()
    {
        // Corruption-tolerant: a poisoned re-check history shouldn't
        // strand the work item. Reads return empty (the next re-validation
        // iteration appends a fresh entry), matching the documented
        // behaviour of ReadReCheckVerdicts.
        var item = Sample();
        await _store.CreateAsync(item);

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE work_items SET re_check_verdicts_json = $junk WHERE id = $id";
            cmd.Parameters.AddWithValue("$junk", "not-json");
            cmd.Parameters.AddWithValue("$id", item.Id.ToString());
            cmd.ExecuteNonQuery();
        }

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Empty(read!.ReCheckVerdicts);
    }

    [Fact]
    public async Task ReadRow_CorruptRequiredCapabilitiesJson_FailsClosed()
    {
        // Persist normally, then poison the column directly via raw SQLite.
        var item = Sample();
        await _store.CreateAsync(item);

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE work_items SET required_capabilities_json = $junk WHERE id = $id";
            cmd.Parameters.AddWithValue("$junk", "not-json");
            cmd.Parameters.AddWithValue("$id", item.Id.ToString());
            cmd.ExecuteNonQuery();
        }

        // Fail closed: surfacing the corruption beats silently routing the item
        // as if no clearance were required.
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.GetAsync(item.Id));
    }

    [Fact]
    public async Task ReadRow_CorruptKnobsJson_FailsClosed()
    {
        var item = Sample();
        await _store.CreateAsync(item);

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE work_items SET knobs_json = $junk WHERE id = $id";
            cmd.Parameters.AddWithValue("$junk", "not-json");
            cmd.Parameters.AddWithValue("$id", item.Id.ToString());
            cmd.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _store.GetAsync(item.Id));
    }

    [Fact]
    public async Task TryUpdateIfStateAndUpdatedAtAsync_PreservesExistingKnobsMap()
    {
        var knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["changeScope"] = "refactor",
        };
        var item = Sample() with { Knobs = knobs };
        await _store.CreateAsync(item);
        var persisted = await _store.GetAsync(item.Id);
        Assert.NotNull(persisted);

        var updated = persisted! with
        {
            State = WorkItemState.WaitingForTransientRetry,
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["changeScope"] = "surgical",
            },
            UpdatedAt = persisted.UpdatedAt.AddSeconds(1),
        };

        var wrote = await _store.TryUpdateIfStateAndUpdatedAtAsync(
            updated,
            WorkItemState.Queued,
            persisted.UpdatedAt);

        Assert.True(wrote);
        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, read!.State);
        Assert.Single(read!.Knobs);
        Assert.Equal("refactor", read.Knobs["changeScope"]);
    }

    [Fact]
    public async Task TryUpdateIfStateAsync_PreservesExistingKnobsMap()
    {
        var item = Sample() with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["changeScope"] = "refactor",
            },
        };
        await _store.CreateAsync(item);
        var persisted = await _store.GetAsync(item.Id);
        Assert.NotNull(persisted);

        var updated = persisted! with
        {
            State = WorkItemState.Working,
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["changeScope"] = "surgical",
            },
            UpdatedAt = persisted.UpdatedAt.AddSeconds(1),
        };

        var wrote = await _store.TryUpdateIfStateAsync(updated, WorkItemState.Queued);

        Assert.True(wrote);
        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.Working, read!.State);
        Assert.Single(read!.Knobs);
        Assert.Equal("refactor", read.Knobs["changeScope"]);
    }

    [Fact]
    public async Task TryReplaceKnobsIfStateAndUpdatedAtAsync_PersistsKnobsMap()
    {
        var item = Sample() with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["changeScope"] = "refactor",
            },
        };
        await _store.CreateAsync(item);
        var persisted = await _store.GetAsync(item.Id);
        Assert.NotNull(persisted);

        var wrote = await _store.TryReplaceKnobsIfStateAndUpdatedAtAsync(
            item.Id,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["changeScope"] = "surgical",
            },
            persisted!.UpdatedAt.AddSeconds(1),
            WorkItemState.Queued,
            persisted.UpdatedAt);

        Assert.True(wrote);
        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Single(read!.Knobs);
        Assert.Equal("surgical", read.Knobs["changeScope"]);
    }

    [Fact]
    public async Task TryReplaceKnobsIfStateAndUpdatedAtAsync_GuardMissesReturnFalseWithoutWriting()
    {
        var item = Sample() with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["changeScope"] = "refactor",
            },
        };
        await _store.CreateAsync(item);
        var persisted = await _store.GetAsync(item.Id);
        Assert.NotNull(persisted);

        var replacement = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["changeScope"] = "surgical",
        };
        var nextUpdatedAt = persisted!.UpdatedAt.AddSeconds(1);

        var staleUpdatedAt = await _store.TryReplaceKnobsIfStateAndUpdatedAtAsync(
            item.Id,
            replacement,
            nextUpdatedAt,
            WorkItemState.Queued,
            persisted.UpdatedAt.AddTicks(-1));
        var wrongState = await _store.TryReplaceKnobsIfStateAndUpdatedAtAsync(
            item.Id,
            replacement,
            nextUpdatedAt,
            WorkItemState.Working,
            persisted.UpdatedAt);
        var missingRow = await _store.TryReplaceKnobsIfStateAndUpdatedAtAsync(
            WorkItemId.New(),
            replacement,
            nextUpdatedAt,
            WorkItemState.Queued,
            persisted.UpdatedAt);

        Assert.False(staleUpdatedAt);
        Assert.False(wrongState);
        Assert.False(missingRow);
        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(persisted.UpdatedAt, read!.UpdatedAt);
        Assert.Single(read.Knobs);
        Assert.Equal("refactor", read.Knobs["changeScope"]);
    }

    [Fact]
    public async Task TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync_GuardMissesReturnFalseWithoutWriting()
    {
        var item = Sample() with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["changeScope"] = "refactor",
            },
        };
        await _store.CreateAsync(item);
        var persisted = await _store.GetAsync(item.Id);
        Assert.NotNull(persisted);

        var update = persisted! with
        {
            Title = "patched title",
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["changeScope"] = "surgical",
            },
            UpdatedAt = persisted.UpdatedAt.AddSeconds(1),
        };

        var staleUpdatedAt = await _store.TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync(
            update,
            WorkItemState.Queued,
            persisted.UpdatedAt.AddTicks(-1));
        var wrongState = await _store.TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync(
            update,
            WorkItemState.Working,
            persisted.UpdatedAt);
        var missingRow = await _store.TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync(
            update with { Id = WorkItemId.New() },
            WorkItemState.Queued,
            persisted.UpdatedAt);

        Assert.False(staleUpdatedAt);
        Assert.False(wrongState);
        Assert.False(missingRow);
        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal("t", read!.Title);
        Assert.Equal(persisted.UpdatedAt, read.UpdatedAt);
        Assert.Single(read.Knobs);
        Assert.Equal("refactor", read.Knobs["changeScope"]);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotClobberQueuedKnobEditFromStaleSnapshot()
    {
        var item = Sample() with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["changeScope"] = "refactor",
            },
        };
        await _store.CreateAsync(item);
        var stalePickupSnapshot = await _store.GetAsync(item.Id);
        Assert.NotNull(stalePickupSnapshot);

        var knobWrite = await _store.TryReplaceKnobsIfStateAndUpdatedAtAsync(
            item.Id,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["changeScope"] = "surgical",
            },
            stalePickupSnapshot!.UpdatedAt.AddSeconds(1),
            WorkItemState.Queued,
            stalePickupSnapshot.UpdatedAt);
        Assert.True(knobWrite);

        await _store.UpdateAsync(stalePickupSnapshot with
        {
            State = WorkItemState.Working,
            StartedAt = stalePickupSnapshot.UpdatedAt.AddSeconds(2),
            UpdatedAt = stalePickupSnapshot.UpdatedAt.AddSeconds(2),
        });

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.Working, read!.State);
        Assert.Single(read.Knobs);
        Assert.Equal("surgical", read.Knobs["changeScope"]);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotResurrectPlanClearedByPromptReplacementFromStaleSnapshot()
    {
        var approvedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var item = Sample() with
        {
            State = WorkItemState.PlanApproved,
            PlanArtifact = """
                {
                  "approach": "old prompt plan",
                  "files": ["old.txt"],
                  "testStrategy": "old tests",
                  "risks": "old risks",
                  "satisfiesTask": "old task"
                }
                """,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            PlanReviewedAt = approvedAt,
            PlanReviewSummary = "approved",
        };
        await _store.CreateAsync(item);
        var stalePickupSnapshot = await _store.GetAsync(item.Id);
        Assert.NotNull(stalePickupSnapshot);

        var replace = await _store.TryReplacePromptAsync(
            item.Id,
            "new prompt",
            stalePickupSnapshot!.UpdatedAt.AddSeconds(1));
        Assert.Equal(PromptReplaceOutcome.Updated, replace.Outcome);

        await _store.UpdateAsync(stalePickupSnapshot with
        {
            LocalSquashSha = "abc123",
            UpdatedAt = stalePickupSnapshot.UpdatedAt.AddSeconds(2),
        });

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal("new prompt", read!.Prompt);
        Assert.Equal(2, read.PromptRevision);
        Assert.Equal(WorkItemState.Queued, read.State);
        Assert.Null(read.PlanArtifact);
        Assert.Null(read.PlanGeneratedAt);
        Assert.Null(read.PlanReviewedAt);
        Assert.Null(read.PlanReviewSummary);
        Assert.Equal("abc123", read.LocalSquashSha);
    }

    [Fact]
    public async Task ReadKnobs_TolerantOfCaseInsensitiveDuplicatesInPersistedJson()
    {
        // Hand-edited / pre-migration rows can carry a knobs_json blob with
        // two keys that differ only in casing. Read must not crash the whole
        // row load — last-write-wins should resolve the duplicate.
        var item = Sample();
        await _store.CreateAsync(item);

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE work_items SET knobs_json = $junk WHERE id = $id";
            cmd.Parameters.AddWithValue("$junk", "{\"changeScope\":\"surgical\",\"CHANGESCOPE\":\"refactor\"}");
            cmd.Parameters.AddWithValue("$id", item.Id.ToString());
            cmd.ExecuteNonQuery();
        }

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal("refactor", read!.Knobs["changeScope"]);
    }

    private static async Task DrainAsync(IAsyncEnumerable<WorkItem> items)
    {
        await foreach (var _ in items)
        {
        }
    }
}
