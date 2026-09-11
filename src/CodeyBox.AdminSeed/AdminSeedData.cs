using CodeyBox.Core;

namespace CodeyBox.AdminSeed;

/// <summary>
/// Deterministic seed specification: which seed value and clock the seeded
/// content is derived from. The seeder injects a fixed clock in tests; the
/// harness passes real time in production use.
/// </summary>
public sealed record AdminSeedSpec
{
    public int Seed { get; init; } = 42;
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Pure, deterministic builder for the seeded admin instance's content:
/// projects, work items in every lifecycle state, audit reports, releases,
/// and suggestions. Same <see cref="AdminSeedSpec"/> in → byte-identical
/// content out; all IDs derive from the seed so two instances never collide
/// with each other by accident and re-seeds are idempotent at the row level.
/// </summary>
public static class AdminSeedData
{
    public static readonly IReadOnlyList<string> ProjectIds = ["seeded-shop", "seeded-portal"];

    public static IReadOnlyList<WorkItem> BuildWorkItems(AdminSeedSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var items = new List<WorkItem>();
        var states = AllSeedStates();
        for (var i = 0; i < states.Length; i++)
        {
            var state = states[i];
            var project = ProjectIds[i % ProjectIds.Count];
            var id = new WorkItemId(DeterministicGuid(spec.Seed, $"work-item-{i:00}"));
            var created = spec.Now.AddHours(-(states.Length - i));
            items.Add(new WorkItem
            {
                Id = id,
                ProjectId = new ProjectId(project),
                Title = $"Seeded {state} item {i:00}",
                Prompt = SeedPromptFor(state, i),
                Agent = SeededFakeAgentRunner.FakeKind,
                State = state,
                CreatedAt = created,
                UpdatedAt = created.AddMinutes(15),
                QueuePosition = state == WorkItemState.Queued ? i + 1 : 0,
                WorkBranch = state is WorkItemState.Queued or WorkItemState.Failed
                    ? null
                    : $"seeded/work-{i:00}",
                LastError = state is WorkItemState.Failed or WorkItemState.AuditFailed
                    ? "seeded failure: see audit timeline"
                    : null,
                FailureKind = state == WorkItemState.Failed ? "normal" : null,
            });
        }
        return items;
    }

    public static IReadOnlyList<AuditReport> BuildAuditReports(AdminSeedSpec spec, IReadOnlyList<WorkItem> items)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(items);
        var reports = new List<AuditReport>();
        foreach (var item in items)
        {
            if (item.State is not (WorkItemState.Auditing or WorkItemState.Reworking
                or WorkItemState.AuditPassed or WorkItemState.Merging or WorkItemState.Merged
                or WorkItemState.UpstreamPushing or WorkItemState.Done or WorkItemState.AuditFailed))
            {
                continue;
            }
            var worst = item.State == WorkItemState.AuditFailed ? "blocking" : "info";
            reports.Add(new AuditReport
            {
                Id = $"seed-{spec.Seed}-{item.Id}-audit-1",
                WorkItemId = item.Id.ToString(),
                Iteration = 1,
                Target = AuditTarget.Code,
                AuditorName = "seeded-build-auditor",
                AuditorKind = "build",
                WorstSeverity = worst,
                StartedAt = item.UpdatedAt,
                EndedAt = item.UpdatedAt.AddMinutes(2),
                DurationMs = 120_000,
                Findings = worst == "blocking"
                    ? [new AuditReportFinding("seed-f1", "blocking", "Seeded blocking finding", "Deterministic seeded finding.", ["seeded.cs"], [1])]
                    : [new AuditReportFinding("seed-f2", "info", "Seeded info finding", "Deterministic seeded note.", ["seeded.cs"], [2])],
                RawOutput = $"seeded audit output for {item.Id} (seed {spec.Seed})",
            });
        }
        return reports;
    }

    public static IReadOnlyList<Release> BuildReleases(AdminSeedSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return
        [
            new Release
            {
                Id = new ReleaseId(DeterministicGuid(spec.Seed, "release-open")),
                ProjectId = new ProjectId(ProjectIds[0]),
                Name = "v0.9.0-seed",
                Description = "Seeded open release under audit.",
                State = ReleaseState.Open,
                BranchName = "release/v0.9.0-seed",
                CreatedAt = spec.Now.AddDays(-3),
            },
            new Release
            {
                Id = new ReleaseId(DeterministicGuid(spec.Seed, "release-closed")),
                ProjectId = new ProjectId(ProjectIds[1]),
                Name = "v0.8.0-seed",
                Description = "Seeded released version.",
                State = ReleaseState.Released,
                BranchName = "release/v0.8.0-seed",
                CreatedAt = spec.Now.AddDays(-30),
                ClosedAt = spec.Now.AddDays(-20),
                ReleasedAt = spec.Now.AddDays(-20),
            },
        ];
    }

    public static IReadOnlyList<Suggestion> BuildSuggestions(AdminSeedSpec spec, IReadOnlyList<WorkItem> items)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(items);
        var source = items.Count > 0 ? items[0].Id.ToString() : "seeded-source";
        return
        [
            new Suggestion
            {
                Id = $"seed-{spec.Seed}-suggestion-1",
                SourceWorkItemId = source,
                ProjectId = ProjectIds[0],
                Title = "Seeded follow-up: tighten admin queue filters",
                Rationale = "Deterministic seeded suggestion for admin E2E.",
                Category = "usability",
                Severity = "medium",
                EstimatedEffort = "small",
                CreatedAt = spec.Now.AddDays(-1),
            },
        ];
    }

    internal static WorkItemState[] AllSeedStates() =>
    [
        WorkItemState.Queued,
        WorkItemState.Working,
        WorkItemState.WorkComplete,
        WorkItemState.Auditing,
        WorkItemState.Reworking,
        WorkItemState.AuditPassed,
        WorkItemState.Merging,
        WorkItemState.Merged,
        WorkItemState.UpstreamPushing,
        WorkItemState.Done,
        WorkItemState.Failed,
        WorkItemState.Cancelled,
        WorkItemState.AuditFailed,
        WorkItemState.WaitingForQuotaReset,
        WorkItemState.WaitingForAgentResume,
        WorkItemState.WaitingForTransientRetry,
        WorkItemState.NeedsOperatorInput,
    ];

    private static string SeedPromptFor(WorkItemState state, int index) => state switch
    {
        WorkItemState.Failed => $"Seeded prompt {index:00} [seeded-fake:fail]",
        WorkItemState.WaitingForQuotaReset => $"Seeded prompt {index:00} [seeded-fake:quota]",
        WorkItemState.NeedsOperatorInput => $"Seeded prompt {index:00}: awaiting operator decision",
        _ => $"Seeded prompt {index:00}: implement seeded change {index:00}",
    };

    internal static Guid DeterministicGuid(int seed, string name)
    {
        var hash = SeededFakeBehaviorSelector.Fnv1a32($"{seed}:{name}");
        var hash2 = SeededFakeBehaviorSelector.Fnv1a32($"v2:{seed}:{name}");
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[..4], hash);
        BitConverter.TryWriteBytes(bytes[4..8], hash2);
        BitConverter.TryWriteBytes(bytes[8..12], seed);
        BitConverter.TryWriteBytes(bytes[12..], name.Length);
        return new Guid(bytes);
    }
}
