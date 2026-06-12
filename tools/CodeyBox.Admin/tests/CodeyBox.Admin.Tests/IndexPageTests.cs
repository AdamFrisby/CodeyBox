using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using IndexPage = CodeyBox.Admin.Web.Components.Pages.Index;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Renders the Index component with a fake API client and asserts that the
/// queue table reflects the returned items.
/// </summary>
public sealed class IndexPageTests : TestContext
{
    private static WorkItemDto MakeItem(string id, string title, string state = "Queued") => new()
    {
        Id = id,
        ProjectId = "proj",
        Title = title,
        Prompt = "p",
        Agent = "claude",
        State = state,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        QueuePosition = 1,
    };

    // The Index page defaults to the "Active" tab (Working/Auditing/…). Single-item
    // tests use Queued/Failed/etc. which live under other tabs, so select "All"
    // (which shows every item regardless of state) before asserting on the table.
    private static void SelectAllTab(IRenderedComponent<IndexPage> cut) =>
        cut.FindAll("button[role='tab']").Single(b => b.TextContent.Contains("All")).Click();

    [Fact]
    public void Index_RendersTableWhenItemsExist()
    {
        var fake = new FakeApiClient([MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task A")]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();
        SelectAllTab(cut);

        Assert.Contains("Task A", cut.Markup);
        Assert.Contains("queue-table", cut.Markup);
    }

    [Fact]
    public void Index_ShowsShortIdInTable()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "My Task");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();
        SelectAllTab(cut);

        // Short ID is first 8 chars
        Assert.Contains("aabbccdd", cut.Markup);
    }

    [Fact]
    public void Index_ShowsEmptyMessageWhenNoItems()
    {
        var fake = new FakeApiClient([]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();

        Assert.Contains("No work items", cut.Markup);
    }

    [Fact]
    public void Index_ShowsMultipleRows()
    {
        var items = new[]
        {
            MakeItem("aabbccdd-0000-0000-0000-000000000001", "Alpha"),
            MakeItem("aabbccdd-0000-0000-0000-000000000002", "Beta"),
            MakeItem("aabbccdd-0000-0000-0000-000000000003", "Gamma"),
        };
        var fake = new FakeApiClient([.. items]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();
        SelectAllTab(cut);

        Assert.Contains("Alpha", cut.Markup);
        Assert.Contains("Beta", cut.Markup);
        Assert.Contains("Gamma", cut.Markup);
    }

    [Fact]
    public void Index_QueuedItems_ShowEditAndReorderButtons()
    {
        var fake = new FakeApiClient([MakeItem("aabbccdd-0000-0000-0000-000000000001", "Queued Task", "Queued")]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();
        SelectAllTab(cut);

        // Edit link present for queued items
        Assert.Contains("edit", cut.Markup);
        // Up/down buttons present
        Assert.Contains("▲", cut.Markup);
        Assert.Contains("▼", cut.Markup);
    }

    [Fact]
    public void Index_DoneItems_DoNotShowCancelButton()
    {
        var fake = new FakeApiClient([MakeItem("aabbccdd-0000-0000-0000-000000000001", "Done Task", "Done")]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();

        // Cancel button only shown for non-terminal items
        Assert.DoesNotContain("cancel", cut.Markup);
    }

    [Fact]
    public void Index_FailedItem_ShowsRetryButton()
    {
        var fake = new FakeApiClient([MakeItem("aabbccdd-0000-0000-0000-000000000001", "Failed Task", "Failed")]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();
        SelectAllTab(cut);

        Assert.Contains("retry", cut.Markup);
    }

    [Fact]
    public void Index_ShowsStateForEachItem()
    {
        var fake = new FakeApiClient([
            MakeItem("aabbccdd-0000-0000-0000-000000000001", "A", "Working"),
            MakeItem("aabbccdd-0000-0000-0000-000000000002", "B", "Done"),
        ]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();

        Assert.Contains("Working", cut.Markup);
        Assert.Contains("Done", cut.Markup);
    }

    // ── Queue state banner tests ─────────────────────────────────────────────

    [Fact]
    public void Index_PausedQueue_ShowsPausedBanner()
    {
        var fake = new FakeApiClient([]);
        fake.QueueStatusOverride = new QueueStatusDto
        {
            State = "Paused",
            PausedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
            PausedReason = "maintenance window",
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();

        Assert.Contains("QUEUE PAUSED", cut.Markup);
        Assert.Contains("maintenance window", cut.Markup);
        Assert.Contains("Resume queue", cut.Markup);
        Assert.Contains("queue-banner-paused", cut.Markup);
    }

    [Fact]
    public void Index_RunningQueue_ShowsRunningBannerAndPauseButton()
    {
        var fake = new FakeApiClient([]);
        fake.QueueStatusOverride = new QueueStatusDto { State = "Running" };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();

        Assert.Contains("queue-banner-running", cut.Markup);
        Assert.Contains("Pause queue", cut.Markup);
        Assert.DoesNotContain("QUEUE PAUSED", cut.Markup);
    }

    [Fact]
    public void Index_PauseButton_OpensPauseModal()
    {
        var fake = new FakeApiClient([]);
        fake.QueueStatusOverride = new QueueStatusDto { State = "Running" };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();
        cut.Find(".btn-sm").Click();

        Assert.Contains("modal-overlay", cut.Markup);
        Assert.Contains("Reason", cut.Markup);
    }

    [Fact]
    public void Index_PauseModal_EmptyReason_ShowsValidationError()
    {
        var fake = new FakeApiClient([]);
        fake.QueueStatusOverride = new QueueStatusDto { State = "Running" };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();
        cut.Find(".btn-sm").Click(); // open modal

        // Click Pause without entering a reason.
        cut.Find(".modal-box .btn-danger").Click();

        Assert.Contains("reason is required", cut.Markup);
        Assert.Contains("modal-overlay", cut.Markup); // modal stays open
    }

    [Fact]
    public void Index_PauseSuccess_CloseModalAndShowPausedBanner()
    {
        var fake = new FakeApiClient([]);
        fake.QueueStatusOverride = new QueueStatusDto { State = "Running" };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();

        // Open pause modal
        cut.Find(".btn-sm").Click();
        Assert.Contains("modal-overlay", cut.Markup);

        // Enter a reason and submit
        cut.Find(".modal-input").Change("incident response");
        cut.Find(".modal-box .btn-danger").Click();

        // Wait for async update
        cut.WaitForState(() => !cut.Markup.Contains("modal-overlay"), TimeSpan.FromSeconds(2));

        Assert.DoesNotContain("modal-overlay", cut.Markup);
        Assert.Contains("QUEUE PAUSED", cut.Markup);
        Assert.Contains("incident response", cut.Markup);
    }

    [Fact]
    public void Index_ResumeSuccess_ShowsRunningBanner()
    {
        var fake = new FakeApiClient([]);
        fake.QueueStatusOverride = new QueueStatusDto
        {
            State = "Paused",
            PausedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            PausedReason = "test",
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();
        Assert.Contains("QUEUE PAUSED", cut.Markup);

        cut.Find(".btn-resume").Click();

        cut.WaitForState(() => cut.Markup.Contains("queue-banner-running"), TimeSpan.FromSeconds(2));

        Assert.DoesNotContain("QUEUE PAUSED", cut.Markup);
        Assert.Contains("queue-banner-running", cut.Markup);
    }

    // ── Budget usage bar tests ───────────────────────────────────────────────

    [Fact]
    public void Index_BudgetBars_RenderedForNonTerminalProjectItems()
    {
        var fake = new FakeApiClient(
            [MakeItem("aabbccdd-0000-0000-0000-000000000001", "Active Task", "Working")]);
        fake.BudgetUsageOverrides["proj"] = new BudgetUsageDto
        {
            LastHour = 3,
            Last24h = 5,
            CurrentlyInFlight = 1,
            Limits = new BudgetLimitsDto { PerHour = 10, PerDay = 50, Concurrent = 2 },
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();

        Assert.Contains("budget-bar", cut.Markup);
        Assert.Contains("3/10/h", cut.Markup);
    }

    [Fact]
    public void Index_BudgetBars_WarnCss_AtEightyPercent()
    {
        var fake = new FakeApiClient(
            [MakeItem("aabbccdd-0000-0000-0000-000000000001", "Busy Task", "Working")]);
        fake.BudgetUsageOverrides["proj"] = new BudgetUsageDto
        {
            LastHour = 8,
            Last24h = 0,
            CurrentlyInFlight = 0,
            Limits = new BudgetLimitsDto { PerHour = 10, PerDay = 0, Concurrent = 0 },
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();

        Assert.Contains("budget-warn", cut.Markup);
    }

    [Fact]
    public void Index_BudgetBars_FullCss_AtHundredPercent()
    {
        var fake = new FakeApiClient(
            [MakeItem("aabbccdd-0000-0000-0000-000000000001", "Maxed Task", "Working")]);
        fake.BudgetUsageOverrides["proj"] = new BudgetUsageDto
        {
            LastHour = 10,
            Last24h = 0,
            CurrentlyInFlight = 0,
            Limits = new BudgetLimitsDto { PerHour = 10, PerDay = 0, Concurrent = 0 },
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();

        Assert.Contains("budget-full", cut.Markup);
    }

    [Fact]
    public void Index_BudgetBars_NotRendered_WhenNoNonTerminalItems()
    {
        var fake = new FakeApiClient(
            [MakeItem("aabbccdd-0000-0000-0000-000000000001", "Done Task", "Done")]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();

        Assert.DoesNotContain("budget-bar-wrap", cut.Markup);
    }
}

/// <summary>
/// In-memory fake for ICodeyBoxApiClient used in component tests.
/// </summary>
public sealed class FakeApiClient : ICodeyBoxApiClient
{
    private List<WorkItemDto> _items;
    private List<ProjectDto> _projects;

    public int GetWorkItemCallCount { get; private set; }

    public FakeApiClient(List<WorkItemDto> items, List<ProjectDto>? projects = null)
    {
        _items = items;
        _projects = projects ?? [];
    }

    public Task<List<WorkItemDto>> GetWorkItemsAsync(CancellationToken ct = default)
        => Task.FromResult(_items);

    public Task<WorkItemDto?> GetWorkItemAsync(string id, CancellationToken ct = default)
    {
        GetWorkItemCallCount++;
        return Task.FromResult(_items.FirstOrDefault(i => i.Id == id));
    }

    public Task<List<ProjectDto>> GetProjectsAsync(CancellationToken ct = default)
        => Task.FromResult(_projects);

    public Task<WorkItemDto?> CreateWorkItemAsync(CreateWorkItemRequest req, CancellationToken ct = default)
    {
        var item = new WorkItemDto
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = req.ProjectId,
            Title = req.Title,
            Prompt = req.Prompt,
            Agent = req.Agent ?? "claude",
            State = "Queued",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _items.Add(item);
        return Task.FromResult<WorkItemDto?>(item);
    }

    public Task<WorkItemDto?> PatchWorkItemAsync(string id, PatchWorkItemRequest req, CancellationToken ct = default)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null || !item.IsQueued) return Task.FromResult<WorkItemDto?>(null);
        if (req.Title is not null) item.Title = req.Title;
        if (req.Prompt is not null) item.Prompt = req.Prompt;
        if (req.Agent is not null) item.Agent = req.Agent;
        return Task.FromResult<WorkItemDto?>(item);
    }

    public Task<bool> DeleteWorkItemAsync(string id, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<bool> RetryWorkItemAsync(string id, string? from = null, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<bool> ReorderWorkItemsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
        => Task.FromResult(true);

    public QueueStatusDto? QueueStatusOverride { get; set; }
    public Dictionary<string, BudgetUsageDto> BudgetUsageOverrides { get; set; } = [];

    public Task<QueueStatusDto?> GetQueueStatusAsync(CancellationToken ct = default)
        => Task.FromResult(QueueStatusOverride);

    public Task<QueueStatusDto?> PauseQueueAsync(string reason, CancellationToken ct = default)
    {
        QueueStatusOverride = new QueueStatusDto
        {
            State = "Paused",
            PausedAt = DateTimeOffset.UtcNow,
            PausedReason = reason,
        };
        return Task.FromResult<QueueStatusDto?>(QueueStatusOverride);
    }

    public Task<QueueStatusDto?> ResumeQueueAsync(CancellationToken ct = default)
    {
        QueueStatusOverride = new QueueStatusDto { State = "Running" };
        return Task.FromResult<QueueStatusDto?>(QueueStatusOverride);
    }

    public List<AgentPauseStateDto> PausedAgentsOverride { get; set; } = [];
    public string? AgentPauseKindCaptured { get; private set; }
    public string? AgentPauseReasonCaptured { get; private set; }
    public double? AgentPauseDurationCaptured { get; private set; }
    public string? AgentResumeKindCaptured { get; private set; }

    public Task<List<AgentPauseStateDto>> GetPausedAgentsAsync(CancellationToken ct = default)
        => Task.FromResult(PausedAgentsOverride);

    public Task<AgentPauseStateDto?> PauseAgentAsync(string agent, string reason, double? durationSeconds = null, CancellationToken ct = default)
    {
        AgentPauseKindCaptured = agent;
        AgentPauseReasonCaptured = reason;
        AgentPauseDurationCaptured = durationSeconds;
        var state = new AgentPauseStateDto
        {
            Agent = agent,
            Paused = true,
            PausedAt = DateTimeOffset.UtcNow,
            PausedReason = reason,
            ExpiresAt = durationSeconds is null ? null : DateTimeOffset.UtcNow.AddSeconds(durationSeconds.Value),
        };
        PausedAgentsOverride.Add(state);
        return Task.FromResult<AgentPauseStateDto?>(state);
    }

    public Task<bool> ResumeAgentAsync(string agent, CancellationToken ct = default)
    {
        AgentResumeKindCaptured = agent;
        PausedAgentsOverride.RemoveAll(p => string.Equals(p.Agent, agent, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(true);
    }

    public Task<BudgetUsageDto?> GetBudgetUsageAsync(string projectId, CancellationToken ct = default)
        => Task.FromResult(BudgetUsageOverrides.TryGetValue(projectId, out var u) ? (BudgetUsageDto?)u : null);

    public WorkItemTimelineDto? TimelineOverride { get; set; }

    public Task<WorkItemTimelineDto?> GetWorkItemTimelineAsync(
        string id, string? kind = null, string? since = null, int? iteration = null,
        CancellationToken ct = default)
        => Task.FromResult(TimelineOverride);

    public List<SuggestionDto> SuggestionsOverride { get; set; } = [];

    public Task<List<SuggestionDto>> GetSuggestionsAsync(
        string? projectId = null, string? category = null, string? severity = null,
        CancellationToken ct = default)
        => Task.FromResult(SuggestionsOverride);

    public Task<int> GetSuggestionsCountAsync(CancellationToken ct = default)
        => Task.FromResult(SuggestionsOverride.Count);

    public Task<SuggestionDto?> GetSuggestionAsync(string id, CancellationToken ct = default)
        => Task.FromResult(SuggestionsOverride.FirstOrDefault(s => s.Id == id));

    public Task<SuggestionDto?> DismissSuggestionAsync(string id, string? reason = null,
        CancellationToken ct = default)
        => Task.FromResult<SuggestionDto?>(null);

    public Task<string?> PromoteSuggestionAsync(
        string id, string? extraInstructions = null, string? agent = null,
        string? workBranch = null, string? baseBranch = null, bool? pushUpstream = null,
        string? agentClassId = null, string? externalId = null, CancellationToken ct = default)
        => Task.FromResult<string?>("fake-work-item-id");

    public AuditReportsDto? AuditReportsOverride { get; set; }
    public Dictionary<(int, string), string?> RawOutputOverrides { get; set; } = [];

    public Task<AuditReportsDto?> GetAuditReportsAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult(AuditReportsOverride);

    public Task<string?> GetAuditReportRawOutputAsync(
        string workItemId, int iteration, string auditorName, CancellationToken ct = default)
        => Task.FromResult(RawOutputOverrides.TryGetValue((iteration, auditorName), out var r) ? r : null);

    public Dictionary<string, WorkItemTimingsDto> TimingsOverride { get; } = [];
    public AggregateTimingsDto? AggregateTimingsOverride { get; set; }

    public Task<WorkItemTimingsDto?> GetWorkItemTimingsAsync(string id, CancellationToken ct = default)
        => Task.FromResult(TimingsOverride.TryGetValue(id, out var t) ? (WorkItemTimingsDto?)t : null);

    public Task<AggregateTimingsDto?> GetAggregateTimingsAsync(int? n = null, CancellationToken ct = default)
        => Task.FromResult(AggregateTimingsOverride);

    public Dictionary<string, AgentStreamAggregateDto> AgentStreamAggregateOverride { get; } = [];
    public AgentStreamAggregateDto? FleetAgentStreamAggregateOverride { get; set; }

    public Task<AgentStreamAggregateDto?> GetWorkItemAgentStreamAggregateAsync(string id, CancellationToken ct = default)
        => Task.FromResult(AgentStreamAggregateOverride.TryGetValue(id, out var a) ? (AgentStreamAggregateDto?)a : null);

    public Task<AgentStreamAggregateDto?> GetFleetAgentStreamAggregateAsync(int? n = null, CancellationToken ct = default)
        => Task.FromResult(FleetAgentStreamAggregateOverride);

    public Dictionary<string, WorkItemCostsDto> CostsOverride { get; } = [];

    public Task<WorkItemCostsDto?> GetWorkItemCostsAsync(string id, CancellationToken ct = default)
        => Task.FromResult(CostsOverride.TryGetValue(id, out var c) ? (WorkItemCostsDto?)c : null);

    public Task<ProjectCostsDto?> GetProjectCostsAsync(string projectId, string? from = null, string? to = null, CancellationToken ct = default)
        => Task.FromResult<ProjectCostsDto?>(null);

    public Dictionary<string, List<QuestionDto>> QuestionsOverride { get; set; } = [];

    public Task<List<QuestionDto>> GetQuestionsAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult(QuestionsOverride.TryGetValue(workItemId, out var qs) ? qs : []);

    public int AnswerQuestionCallCount { get; private set; }
    public (string workItemId, string questionId, string answer)? LastAnswerCall { get; private set; }

    public Task<bool> AnswerQuestionAsync(string workItemId, string questionId, string answer, CancellationToken ct = default)
    {
        AnswerQuestionCallCount++;
        LastAnswerCall = (workItemId, questionId, answer);
        return Task.FromResult(true);
    }

    public int DismissQuestionCallCount { get; private set; }

    public Task<bool> DismissQuestionAsync(string workItemId, string questionId, string reason, CancellationToken ct = default)
    {
        DismissQuestionCallCount++;
        return Task.FromResult(true);
    }
    public List<FleetSummaryDto> FleetSummaryOverride { get; set; } = [];
    public string? FleetSummaryPauseProjectIdCaptured { get; private set; }
    public string? FleetSummaryPauseReasonCaptured { get; private set; }

    public Task<List<FleetSummaryDto>> GetFleetSummaryAsync(CancellationToken ct = default)
        => Task.FromResult(FleetSummaryOverride);

    public Task<bool> PauseProjectAsync(string projectId, string? reason = null, CancellationToken ct = default)
    {
        FleetSummaryPauseProjectIdCaptured = projectId;
        FleetSummaryPauseReasonCaptured = reason;
        return Task.FromResult(true);
    }

    public Task<bool> ResumeProjectAsync(string projectId, CancellationToken ct = default)
        => Task.FromResult(true);
    public Task<ProjectBudgetDto?> GetProjectBudgetAsync(string projectId, CancellationToken ct = default)
        => Task.FromResult<ProjectBudgetDto?>(null);

    public Task<ProjectQueueStateDto?> PauseProjectQueueAsync(string projectId, string reason, CancellationToken ct = default)
        => Task.FromResult<ProjectQueueStateDto?>(null);

    public Task<ProjectQueueStateDto?> ResumeProjectQueueAsync(string projectId, CancellationToken ct = default)
        => Task.FromResult<ProjectQueueStateDto?>(null);
    public Task<List<PluginDto>> GetAuditorPluginsAsync(CancellationToken ct = default)
        => Task.FromResult(new List<PluginDto>());
    public WorkItemReplaysDto? ReplaysOverride { get; set; }

    public Task<WorkItemDto?> ReplayWorkItemAsync(string id, ReplayWorkItemRequest req, CancellationToken ct = default)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null || !item.IsTerminal) return Task.FromResult<WorkItemDto?>(null);
        var replay = new WorkItemDto
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = item.ProjectId,
            Title = item.Title,
            Prompt = item.Prompt,
            Agent = req.Agent ?? item.Agent,
            State = "Queued",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ReplayOfWorkItemId = item.Id,
        };
        _items.Add(replay);
        return Task.FromResult<WorkItemDto?>(replay);
    }

    public Task<WorkItemReplaysDto?> GetReplaysAsync(string id, CancellationToken ct = default)
        => Task.FromResult(ReplaysOverride);
    public Dictionary<string, WorkItemDiffDto> DiffOverride { get; } = [];

    public Task<WorkItemDiffDto?> GetWorkItemDiffAsync(string id, CancellationToken ct = default)
        => Task.FromResult(DiffOverride.TryGetValue(id, out var d) ? (WorkItemDiffDto?)d : null);
    public Dictionary<string, string> StdoutTailOverride { get; } = [];

    public Task<string?> GetStdoutTailAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult<string?>(StdoutTailOverride.TryGetValue(workItemId, out var t) ? t : null);

    public List<ReleaseDto> ReleasesOverride { get; set; } = [];
    public ReleaseDto? ReleaseOverride { get; set; }
    public List<object> ReleaseWorkItemsOverride { get; set; } = [];

    public Task<List<ReleaseDto>> GetReleasesAsync(string? projectId = null, string? state = null, int? limit = null, int? offset = null, CancellationToken ct = default)
        => Task.FromResult(ReleasesOverride);
    public Task<int> GetOpenReleasesCountAsync(CancellationToken ct = default)
        => Task.FromResult(ReleasesOverride.Count(r => r.State == "Open"));
    public Task<ReleaseDto?> GetReleaseAsync(string id, CancellationToken ct = default)
        => Task.FromResult(ReleaseOverride);
    public Task<List<object>> GetReleaseWorkItemsAsync(string id, CancellationToken ct = default)
        => Task.FromResult(ReleaseWorkItemsOverride);
    public Task<List<ReleaseAuditIterationDto>> GetReleaseAuditIterationsAsync(string id, CancellationToken ct = default)
        => Task.FromResult(new List<ReleaseAuditIterationDto>());
    public Task<ReleaseDto?> CreateReleaseAsync(CreateReleaseRequest req, CancellationToken ct = default) => Task.FromResult<ReleaseDto?>(null);
    public Task<ReleaseDto?> CloseReleaseAsync(string id, CancellationToken ct = default) => Task.FromResult<ReleaseDto?>(null);
    public Task<ReleaseDto?> ReopenReleaseAsync(string id, string reason, CancellationToken ct = default) => Task.FromResult<ReleaseDto?>(null);
    public Task<ReleaseDto?> AbandonReleaseAsync(string id, CancellationToken ct = default) => Task.FromResult<ReleaseDto?>(null);
    public Task<ReleaseDto?> TriggerReleaseAsync(string id, CancellationToken ct = default) => Task.FromResult<ReleaseDto?>(null);
}
