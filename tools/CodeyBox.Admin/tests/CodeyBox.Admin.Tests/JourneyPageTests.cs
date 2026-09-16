using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using JourneyPage = CodeyBox.Admin.Web.Components.Pages.WorkItemJourneyPage;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Journey page: the rework-cycle question — cycling productively or stuck —
/// must be answerable from the rendered output, and sandbox links must exist
/// only where retained evidence exists (never dead controls).
/// </summary>
public sealed class JourneyPageTests : BunitContext
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private static WorkItemDto MakeItem(string id, string state = "Reworking", int? auditMax = 5) => new()
    {
        Id = id,
        ProjectId = "proj",
        Title = "Fix login",
        State = state,
        Agent = "Claude",
        CreatedAt = T0.AddHours(-2),
        UpdatedAt = T0,
        AuditMaxIterations = auditMax,
        AgentHistory =
        [
            new AgentInvolvementEntryDto
            {
                Id = "r1", AgentKind = "Claude", Phase = "work",
                StartedAt = T0.AddHours(-2), EndedAt = T0.AddHours(-1), Outcome = "success",
            },
        ],
    };

    private static AuditProgressRowDto MakeRow(int iteration, int blocking, string[] ids, int minutesAfter, string status = "complete") => new()
    {
        Id = $"row-{iteration}-{status}",
        WorkAttemptKey = "",
        Iteration = iteration,
        MaxIterations = 5,
        Status = status,
        BlockingFindings = blocking,
        BlockingFindingIds = [.. ids],
        RecordedAt = T0.AddMinutes(minutesAfter),
    };

    private static WorkItemTimingsDto MakeTimings(long workMs = 120_000)
    {
        using var doc = JsonDocument.Parse($"{{\"durationMs\":{workMs}}}");
        return new WorkItemTimingsDto
        {
            WorkItemId = "x",
            TotalDurationMs = workMs,
            ByPhase = new Dictionary<string, JsonElement> { ["work"] = doc.RootElement.Clone() },
        };
    }

    private static FakeApiClient SeededClient(
        WorkItemDto item,
        AuditProgressListDto? progress = null,
        List<AgentStreamFileDto>? streams = null,
        WorkItemDiffDto? diff = null)
    {
        var fake = new FakeApiClient([item]);
        if (progress is not null) fake.AuditProgressOverride[item.Id] = progress;
        if (streams is not null) fake.AgentStreamFilesOverride[item.Id] = streams;
        if (diff is not null) fake.DiffOverride[item.Id] = diff;
        fake.TimingsOverride[item.Id] = MakeTimings();
        return fake;
    }

    private static AuditProgressListDto Progress(string id, params AuditProgressRowDto[] rows) => new()
    {
        WorkItemId = id,
        Progress = [.. rows],
    };

    [Fact]
    public void StuckItem_RendersUnmistakableStuckBanner()
    {
        var id = Guid.NewGuid().ToString();
        var fake = SeededClient(MakeItem(id), Progress(id,
            MakeRow(1, 3, ["f-a", "f-b", "f-c"], 10),
            MakeRow(2, 2, ["f-a", "f-b"], 20),
            MakeRow(3, 2, ["f-a", "f-b"], 30)));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<JourneyPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("journey-banner--stuck", cut.Markup);
        Assert.Contains("⛔", cut.Markup);
        Assert.Contains("Stuck", cut.Markup);
        Assert.Contains("f-a", cut.Markup);
        Assert.Contains("f-b", cut.Markup);
    }

    [Fact]
    public void ConvergingItem_RendersConvergingBannerAndBudget()
    {
        var id = Guid.NewGuid().ToString();
        var fake = SeededClient(MakeItem(id), Progress(id,
            MakeRow(1, 4, ["f-a", "f-b", "f-c", "f-d"], 10),
            MakeRow(2, 2, ["f-a", "f-b"], 20)));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<JourneyPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("journey-banner--converging", cut.Markup);
        Assert.Contains("Converging", cut.Markup);
        Assert.Contains("2 of 5 used", cut.Markup);
        Assert.Contains("3 remaining", cut.Markup);
    }

    [Fact]
    public void SandboxLink_RendersOnlyForPhasesWithRetainedEvidence()
    {
        var id = Guid.NewGuid().ToString();
        var fake = SeededClient(
            MakeItem(id, state: "Auditing"),
            Progress(id, MakeRow(1, 1, ["f-a"], 10)),
            streams:
            [
                new AgentStreamFileDto { FileName = "work-1-abc123.jsonl", Phase = "work", CapturedAt = T0 },
            ]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<JourneyPage>(p => p.Add(x => x.Id, id));

        // Exactly one sandbox link: the work phase. No dead control for audit.
        var linkCount = cut.FindAll("a[href*='/streams/']").Count;
        Assert.Equal(1, linkCount);
        Assert.Contains($"/work-items/{id}/streams/work-1-abc123.jsonl", cut.Markup);
    }

    [Fact]
    public void InfraInterruption_RendersDistinctlyAndDoesNotConsumeBudget()
    {
        var id = Guid.NewGuid().ToString();
        var fake = SeededClient(MakeItem(id), Progress(id,
            MakeRow(1, 2, ["f-a", "f-b"], 10),
            MakeRow(2, 0, [], 20, status: "incomplete"),
            MakeRow(3, 1, ["f-b"], 30)));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<JourneyPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("journey-infra", cut.Markup);
        Assert.Contains("incomplete-audit", cut.Markup);
        Assert.Contains("2 of 5 used", cut.Markup);
        Assert.DoesNotContain("journey-banner--stuck", cut.Markup);
    }

    [Fact]
    public void DiffButton_RendersWhenDiffExists()
    {
        var withDiff = Guid.NewGuid().ToString();
        var fake = SeededClient(
            MakeItem(withDiff),
            Progress(withDiff, MakeRow(1, 1, ["f-a"], 10)),
            diff: new WorkItemDiffDto { WorkItemId = withDiff, FilesChanged = 2 });
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<JourneyPage>(p => p.Add(x => x.Id, withDiff));
        Assert.Contains($"/work-items/{withDiff}/diff", cut.Markup);
    }

    [Fact]
    public void DiffButton_AbsentWhenNoDiffExists()
    {
        var withoutDiff = Guid.NewGuid().ToString();
        var fake = SeededClient(
            MakeItem(withoutDiff),
            Progress(withoutDiff, MakeRow(1, 1, ["f-a"], 10)));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<JourneyPage>(p => p.Add(x => x.Id, withoutDiff));
        Assert.DoesNotContain($"/work-items/{withoutDiff}/diff", cut.Markup);
    }

    [Fact]
    public void UnknownItem_RendersNotFound()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient([]));

        var cut = Render<JourneyPage>(p => p.Add(x => x.Id, Guid.NewGuid().ToString()));

        Assert.Contains("Work item not found.", cut.Markup);
    }

    [Fact]
    public void AgentHistoryEndpoint_SuppliesRunsWhenItemOmitsThem()
    {
        var id = Guid.NewGuid().ToString();
        var item = MakeItem(id);
        item.AgentHistory = null;
        var fake = SeededClient(item, Progress(id, MakeRow(1, 1, ["f-a"], 10)));
        fake.AgentHistoryOverride[id] = new WorkItemAgentHistoryDto
        {
            WorkItemId = id,
            WorkAgent = "Codex",
            AgentHistory =
            [
                new AgentInvolvementEntryDto
                {
                    Id = "h1", AgentKind = "Codex", Phase = "rework",
                    StartedAt = T0.AddHours(-1), EndedAt = T0.AddMinutes(-30), Outcome = "success",
                },
            ],
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<JourneyPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("Codex", cut.Markup);
        Assert.Contains("Rework", cut.Markup);
    }
}

public sealed class StreamFileNameValidatorTests
{
    [Theory]
    [InlineData("work-1-abc123.jsonl")]
    [InlineData("audit-2-deadbeef.jsonl")]
    [InlineData("conflict_rework-1-00ff.jsonl")]
    public void ValidNames_Pass(string fileName)
        => Assert.True(StreamFileNameValidator.IsValid(fileName));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../secret.jsonl")]
    [InlineData("..\\secret.jsonl")]
    [InlineData("/etc/passwd.jsonl")]
    [InlineData("sub/dir.jsonl")]
    [InlineData(".hidden.jsonl")]
    [InlineData("work-1.ndjson")]
    [InlineData("work-1.jsonl ")]
    [InlineData("work 1.jsonl")]
    public void UnsafeNames_Fail(string? fileName)
        => Assert.False(StreamFileNameValidator.IsValid(fileName));

    [Fact]
    public void OverlongName_Fails()
        => Assert.False(StreamFileNameValidator.IsValid(new string('a', 251) + ".jsonl"));
}
