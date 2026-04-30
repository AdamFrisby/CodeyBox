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

    [Fact]
    public void Index_RendersTableWhenItemsExist()
    {
        var fake = new FakeApiClient([MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task A")]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<IndexPage>();

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
}

/// <summary>
/// In-memory fake for ICodeyBoxApiClient used in component tests.
/// </summary>
public sealed class FakeApiClient : ICodeyBoxApiClient
{
    private List<WorkItemDto> _items;
    private List<ProjectDto> _projects;

    public FakeApiClient(List<WorkItemDto> items, List<ProjectDto>? projects = null)
    {
        _items = items;
        _projects = projects ?? [];
    }

    public Task<List<WorkItemDto>> GetWorkItemsAsync(CancellationToken ct = default)
        => Task.FromResult(_items);

    public Task<WorkItemDto?> GetWorkItemAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_items.FirstOrDefault(i => i.Id == id));

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

    public Task<bool> RetryWorkItemAsync(string id, string from = "work", CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<bool> ReorderWorkItemsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
        => Task.FromResult(true);
}
