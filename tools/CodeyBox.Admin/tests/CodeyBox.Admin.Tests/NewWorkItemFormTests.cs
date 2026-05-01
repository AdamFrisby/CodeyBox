using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Components.Pages;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Tests for the NewWorkItem form: validation messages and submit payload.
/// Uses bunit for Blazor component rendering; relies on bunit's built-in
/// NavigationManager and JSRuntime fakes.
/// </summary>
public sealed class NewWorkItemFormTests : TestContext
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
        var fake = new FakeApiClient([], [SampleProject()]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<NewWorkItem>();

        Assert.Contains("My Project", cut.Markup);
    }

    [Fact]
    public void NewWorkItem_RendersPromptTextarea()
    {
        var fake = new FakeApiClient([], [SampleProject()]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<NewWorkItem>();

        // Prompt textarea must be present with monospace class
        Assert.Contains("prompt-input", cut.Markup);
        Assert.Contains("textarea", cut.Markup.ToLowerInvariant());
    }

    [Fact]
    public void NewWorkItem_ShowsQueuedItemsForDependsOn()
    {
        var fake = new FakeApiClient(
            [QueuedItem("aabbccdd-0000-0000-0000-000000000001", "Dep Task")],
            [SampleProject()]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<NewWorkItem>();

        // Queued items should appear in the depends-on multi-select area
        Assert.Contains("Dep Task", cut.Markup);
    }

    [Fact]
    public void NewWorkItem_ValidationError_WhenProjectNotSelected()
    {
        var fake = new FakeApiClient([], [SampleProject()]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<NewWorkItem>();

        // Submit the form without setting required fields
        cut.Find("form").Submit();

        Assert.Contains("Project is required", cut.Markup);
    }

    [Fact]
    public void NewWorkItem_ValidationError_WhenTitleMissing()
    {
        var fake = new FakeApiClient([], [SampleProject()]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<NewWorkItem>();

        cut.Find("form").Submit();

        Assert.Contains("Title is required", cut.Markup);
    }

    [Fact]
    public void NewWorkItem_ValidationError_WhenPromptMissing()
    {
        var fake = new FakeApiClient([], [SampleProject()]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<NewWorkItem>();

        cut.Find("form").Submit();

        Assert.Contains("Prompt is required", cut.Markup);
    }

    [Fact]
    public async Task NewWorkItem_ValidSubmit_CallsCreateWithDependsOnIds()
    {
        var capturingClient = new CapturingApiClient(
            [QueuedItem("aabbccdd-0000-0000-0000-000000000001", "Dep")],
            [SampleProject()]);
        Services.AddSingleton<ICodeyBoxApiClient>(capturingClient);

        var cut = RenderComponent<NewWorkItem>();

        // Fill required fields
        cut.Find("select#project").Change("proj-1");
        cut.Find("input#title").Change("My Title");
        cut.Find("textarea#prompt").Change("My Prompt");

        // Select a dependency
        cut.Find("input#dep-aabbccdd-0000-0000-0000-000000000001").Change(true);

        cut.Find("form").Submit();

        // Wait for async submit
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Single(capturingClient.CreateRequests);
        var req = capturingClient.CreateRequests[0];
        Assert.Equal("proj-1", req.ProjectId);
        Assert.Equal("My Title", req.Title);
        Assert.Equal("My Prompt", req.Prompt);
        Assert.Contains("aabbccdd-0000-0000-0000-000000000001", req.DependsOn);
    }

    [Fact]
    public void NewWorkItem_AgentDropdown_ContainsKnownAgents()
    {
        var fake = new FakeApiClient([], [SampleProject()]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<NewWorkItem>();

        Assert.Contains("claude", cut.Markup);
        Assert.Contains("copilot", cut.Markup);
        Assert.Contains("codex", cut.Markup);
    }

    [Fact]
    public void NewWorkItem_PushUpstreamCheckbox_DefaultsToChecked()
    {
        var fake = new FakeApiClient([], [SampleProject()]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<NewWorkItem>();

        // pushUpstream checkbox should be present and checked by default
        var checkbox = cut.Find("input#pushUpstream");
        Assert.NotNull(checkbox);
        Assert.True(checkbox.HasAttribute("checked") || checkbox.GetAttribute("value") == "true"
            || cut.Markup.Contains("checked"));
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

    public Task<bool> RetryWorkItemAsync(string id, string from = "work", CancellationToken ct = default)
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
        string? agentClassId = null, CancellationToken ct = default)
        => Task.FromResult<string?>("fake-work-item-id");
}
