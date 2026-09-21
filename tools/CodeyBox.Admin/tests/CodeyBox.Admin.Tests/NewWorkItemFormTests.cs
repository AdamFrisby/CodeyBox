using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Components.Pages;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// The composer's basics: what renders, what blocks filing, and that
/// what is on the page is exactly what is sent.
/// </summary>
public sealed class NewWorkItemFormTests : BunitContext
{
    private static ProjectDto SampleProject() => new()
    {
        Id = "proj-1",
        DisplayName = "My Project",
        RepositoryUrl = "https://github.com/example/repo",
        DefaultAgent = "claude",
    };

    private static WorkItemDto QueuedItem(string id, string title) => new()
    {
        Id = id,
        ProjectId = "proj-1",
        Title = title,
        Prompt = "p",
        Agent = "claude",
        State = "Queued",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        QueuePosition = 1,
    };

    [Fact]
    public void NewWorkItem_RendersProjectDropdown()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient([], [SampleProject()]));

        var cut = Render<NewWorkItem>();

        Assert.Contains("My Project", cut.Find("select#project").TextContent);
    }

    [Fact]
    public void NewWorkItem_RendersEditorAsMonospacePrompt()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient([], [SampleProject()]));

        var cut = Render<NewWorkItem>();

        Assert.Contains("prompt-input", cut.Find("textarea#prompt").ClassName);
    }

    [Fact]
    public void NewWorkItem_ShowsQueuedItemsAsDependencyCandidates()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient(
            [QueuedItem("aabbccdd-0000-0000-0000-000000000001", "Dep Task")],
            [SampleProject()]));

        var cut = Render<NewWorkItem>();

        Assert.Contains("Dep Task", cut.Find(".dependency-picker").TextContent);
    }

    [Fact]
    public void NewWorkItem_SingleKnownProject_IsInferredAndFilingNeedsAPrompt()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient([], [SampleProject()]));

        var cut = Render<NewWorkItem>();

        Assert.Equal("proj-1", cut.Find("select#project").GetAttribute("value"));
        Assert.True(cut.Find("button#file").HasAttribute("disabled"));
        Assert.Contains("Write a prompt", cut.Find("#review-problems").TextContent);
    }

    [Fact]
    public void NewWorkItem_SeveralProjectsAndNoSignal_AsksForAProject()
    {
        var other = SampleProject();
        other.Id = "proj-2";
        other.DisplayName = "Other";
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient([], [SampleProject(), other]));

        var cut = Render<NewWorkItem>();
        cut.Find("textarea#prompt").Input("Do the thing.");

        Assert.Equal(string.Empty, cut.Find("select#project").GetAttribute("value"));
        Assert.Contains("Pick a project", cut.Find("#review-problems").TextContent);
        Assert.True(cut.Find("button#file").HasAttribute("disabled"));
    }

    [Fact]
    public void NewWorkItem_TitleFollowsFirstLineUntilTypedOver()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient([], [SampleProject()]));

        var cut = Render<NewWorkItem>();
        cut.Find("textarea#prompt").Input("## Fix the flaky redirect\n\nReproduce it first.");

        Assert.Equal("Fix the flaky redirect", cut.Find("input#title").GetAttribute("value"));
        Assert.Contains("from the first line", cut.Find("label[for=title]").TextContent);

        cut.Find("input#title").Change("Redirect race");
        cut.Find("textarea#prompt").Input("## Something else\n\nBody.");

        Assert.Equal("Redirect race", cut.Find("input#title").GetAttribute("value"));
        Assert.Contains("you", cut.Find("label[for=title]").TextContent);
    }

    [Fact]
    public void NewWorkItem_File_SendsWhatIsOnThePage()
    {
        var client = new CapturingApiClient(
            [QueuedItem("aabbccdd-0000-0000-0000-000000000001", "Dep")],
            [SampleProject()]);
        Services.AddSingleton<ICodeyBoxApiClient>(client);

        var cut = Render<NewWorkItem>();
        cut.Find("textarea#prompt").Input("My Prompt");
        cut.Find("input#title").Change("My Title");
        cut.Find("input#dep-aabbccdd-0000-0000-0000-000000000001").Change(true);
        cut.Find("input#workTimeout").Change("90");
        cut.Find("input#mergeTimeout").Change("20");
        cut.Find("input#isRefactor").Change(true);
        cut.Find("input#externalId").Change("JIRA-7");
        cut.Find("button#file").Click();

        cut.WaitForAssertion(() => Assert.Single(client.CreateRequests));
        var req = client.CreateRequests[0];
        Assert.Equal("proj-1", req.ProjectId);
        Assert.Equal("My Title", req.Title);
        Assert.Equal("My Prompt", req.Prompt);
        Assert.Contains("aabbccdd-0000-0000-0000-000000000001", req.DependsOn);
        Assert.Equal(90, req.WorkTimeoutMinutes);
        Assert.Equal(20, req.MergeTimeoutMinutes);
        Assert.True(req.IsRefactor);
        Assert.Equal("JIRA-7", req.ExternalId);
    }

    [Fact]
    public void NewWorkItem_CtrlEnterInEditor_Files()
    {
        var client = new CapturingApiClient([], [SampleProject()]);
        Services.AddSingleton<ICodeyBoxApiClient>(client);

        var cut = Render<NewWorkItem>();
        cut.Find("textarea#prompt").Input("Keyboard-only item");
        cut.Find("textarea#prompt").KeyDown(new KeyboardEventArgs { Key = "Enter", CtrlKey = true });

        cut.WaitForAssertion(() => Assert.Single(client.CreateRequests));
        Assert.Equal("Keyboard-only item", client.CreateRequests[0].Title);
    }

    [Fact]
    public void NewWorkItem_AgentDropdown_ContainsKnownAgents()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient([], [SampleProject()]));

        var cut = Render<NewWorkItem>();
        var agents = cut.Find("select#agent").TextContent;

        Assert.Contains("claude", agents);
        Assert.Contains("copilot", agents);
        Assert.Contains("codex", agents);
    }

    [Fact]
    public void NewWorkItem_PushUpstreamCheckbox_DefaultsToChecked()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient([], [SampleProject()]));

        var cut = Render<NewWorkItem>();

        Assert.True(cut.Find("input#pushUpstream").HasAttribute("checked"));
    }

    [Fact]
    public void NewWorkItem_EveryCreateOptionHasAControl()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient([], [SampleProject()]));

        var cut = Render<NewWorkItem>();

        foreach (var id in new[]
        {
            "select#project", "input#baseBranch", "input#workBranch", "input#pushUpstream", "select#release",
            "select#agent", "input#agentClass", "input#requiredCapabilities", "input#minModelScore",
            "input#auditMaxIterations", "input#auditorProfile", "input#auditComplexity",
            "input#priority", "input#isRefactor", "input#workTimeout", "input#mergeTimeout",
            "select#knob-changeScope", "select#knob-plan", "textarea#knobs", "input#externalId",
            "input#dep-search", "textarea#prompt", "input#title",
        })
        {
            Assert.NotNull(cut.Find(id));
        }
    }
}

/// <summary>
/// API client implementation that records create requests for assertion.
/// </summary>
public sealed class CapturingApiClient : ICodeyBoxApiClient
{
    public List<CreateWorkItemRequest> CreateRequests { get; } = [];

    private readonly List<WorkItemDto> _items;
    private readonly List<ProjectDto> _projects;

    public CapturingApiClient(List<WorkItemDto> items, List<ProjectDto> projects)
    {
        _items = items;
        _projects = projects;
    }

    public Task<List<WorkItemDto>> GetWorkItemsAsync(CancellationToken ct = default)
        => Task.FromResult(_items);

    public Task<WorkItemDto?> GetWorkItemAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_items.FirstOrDefault(i => i.Id == id));

    public Task<List<ProjectDto>> GetProjectsAsync(CancellationToken ct = default)
        => Task.FromResult(_projects);

    public Task<WorkItemDto?> CreateWorkItemAsync(CreateWorkItemRequest req, CancellationToken ct = default)
    {
        CreateRequests.Add(req);
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
        return Task.FromResult<WorkItemDto?>(item);
    }

    public Task<WorkItemDto?> PatchWorkItemAsync(string id, PatchWorkItemRequest req, CancellationToken ct = default)
        => Task.FromResult<WorkItemDto?>(null);

    public Task<bool> DeleteWorkItemAsync(string id, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<bool> RetryWorkItemAsync(string id, string? from = null, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<bool> ReorderWorkItemsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<QueueStatusDto?> GetQueueStatusAsync(CancellationToken ct = default)
        => Task.FromResult<QueueStatusDto?>(null);

    public Task<QueueStatusDto?> PauseQueueAsync(string reason, CancellationToken ct = default)
        => Task.FromResult<QueueStatusDto?>(null);

    public Task<QueueStatusDto?> ResumeQueueAsync(CancellationToken ct = default)
        => Task.FromResult<QueueStatusDto?>(null);

    public Task<BudgetUsageDto?> GetBudgetUsageAsync(string projectId, CancellationToken ct = default)
        => Task.FromResult<BudgetUsageDto?>(null);

    public Task<WorkItemTimelineDto?> GetWorkItemTimelineAsync(
        string id, string? kind = null, string? since = null, int? iteration = null,
        CancellationToken ct = default)
        => Task.FromResult<WorkItemTimelineDto?>(null);

    public Task<List<SuggestionDto>> GetSuggestionsAsync(
        string? projectId = null, string? category = null, string? severity = null,
        CancellationToken ct = default)
        => Task.FromResult(new List<SuggestionDto>());

    public Task<int> GetSuggestionsCountAsync(CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<SuggestionDto?> GetSuggestionAsync(string id, CancellationToken ct = default)
        => Task.FromResult<SuggestionDto?>(null);

    public Task<SuggestionDto?> DismissSuggestionAsync(string id, string? reason = null,
        CancellationToken ct = default)
        => Task.FromResult<SuggestionDto?>(null);

    public Task<string?> PromoteSuggestionAsync(
        string id, string? extraInstructions = null, string? agent = null,
        string? workBranch = null, string? baseBranch = null, bool? pushUpstream = null,
        string? agentClassId = null, string? externalId = null, CancellationToken ct = default)
        => Task.FromResult<string?>("fake-work-item-id");

    public Task<AuditReportsDto?> GetAuditReportsAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult<AuditReportsDto?>(null);

    public Task<string?> GetAuditReportRawOutputAsync(
        string workItemId, string target, int iteration, string auditorName, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<WorkItemTimingsDto?> GetWorkItemTimingsAsync(string id, CancellationToken ct = default)
        => Task.FromResult<WorkItemTimingsDto?>(null);

    public Task<AggregateTimingsDto?> GetAggregateTimingsAsync(int? n = null, CancellationToken ct = default)
        => Task.FromResult<AggregateTimingsDto?>(null);

    public Task<AgentStreamAggregateDto?> GetWorkItemAgentStreamAggregateAsync(string id, CancellationToken ct = default)
        => Task.FromResult<AgentStreamAggregateDto?>(null);

    public Task<WorkItemCostsDto?> GetWorkItemCostsAsync(string id, CancellationToken ct = default)
        => Task.FromResult<WorkItemCostsDto?>(null);

    public Task<ProjectCostsDto?> GetProjectCostsAsync(string projectId, string? from = null, string? to = null, CancellationToken ct = default)
        => Task.FromResult<ProjectCostsDto?>(null);

    public Task<List<QuestionDto>> GetQuestionsAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult(new List<QuestionDto>());

    public Task<bool> AnswerQuestionAsync(string workItemId, string questionId, string answer, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<bool> DismissQuestionAsync(string workItemId, string questionId, string reason, CancellationToken ct = default)
        => Task.FromResult(true);
    public Task<List<FleetSummaryDto>> GetFleetSummaryAsync(CancellationToken ct = default)
        => Task.FromResult(new List<FleetSummaryDto>());
    public Task<bool> PauseProjectAsync(string projectId, string? reason = null, CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> ResumeProjectAsync(string projectId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<ProjectBudgetDto?> GetProjectBudgetAsync(string projectId, CancellationToken ct = default)
        => Task.FromResult<ProjectBudgetDto?>(null);

    public Task<ProjectQueueStateDto?> PauseProjectQueueAsync(string projectId, string reason, CancellationToken ct = default)
        => Task.FromResult<ProjectQueueStateDto?>(null);

    public Task<ProjectQueueStateDto?> ResumeProjectQueueAsync(string projectId, CancellationToken ct = default)
        => Task.FromResult<ProjectQueueStateDto?>(null);
    public Task<List<PluginDto>> GetAuditorPluginsAsync(CancellationToken ct = default)
        => Task.FromResult(new List<PluginDto>());
    public Task<WorkItemDto?> ReplayWorkItemAsync(string id, ReplayWorkItemRequest req, CancellationToken ct = default)
        => Task.FromResult<WorkItemDto?>(null);
    public Task<WorkItemReplaysDto?> GetReplaysAsync(string id, CancellationToken ct = default)
        => Task.FromResult<WorkItemReplaysDto?>(null);
    public Task<WorkItemDiffDto?> GetWorkItemDiffAsync(string id, CancellationToken ct = default)
        => Task.FromResult<WorkItemDiffDto?>(null);
    public Task<string?> GetStdoutTailAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    public Task<List<ReleaseDto>> GetReleasesAsync(string? projectId = null, string? state = null, int? limit = null, int? offset = null, CancellationToken ct = default) => Task.FromResult(new List<ReleaseDto>());
    public Task<int> GetOpenReleasesCountAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<ReleaseDto?> GetReleaseAsync(string id, CancellationToken ct = default) => Task.FromResult<ReleaseDto?>(null);
    public Task<List<object>> GetReleaseWorkItemsAsync(string id, CancellationToken ct = default) => Task.FromResult(new List<object>());
    public Task<List<ReleaseAuditIterationDto>> GetReleaseAuditIterationsAsync(string id, CancellationToken ct = default) => Task.FromResult(new List<ReleaseAuditIterationDto>());
    public Task<ReleaseDto?> CreateReleaseAsync(CreateReleaseRequest req, CancellationToken ct = default) => Task.FromResult<ReleaseDto?>(null);
    public Task<ReleaseDto?> CloseReleaseAsync(string id, CancellationToken ct = default) => Task.FromResult<ReleaseDto?>(null);
    public Task<ReleaseDto?> ReopenReleaseAsync(string id, string reason, CancellationToken ct = default) => Task.FromResult<ReleaseDto?>(null);
    public Task<ReleaseDto?> AbandonReleaseAsync(string id, CancellationToken ct = default) => Task.FromResult<ReleaseDto?>(null);
    public Task<ReleaseDto?> TriggerReleaseAsync(string id, CancellationToken ct = default) => Task.FromResult<ReleaseDto?>(null);
}
