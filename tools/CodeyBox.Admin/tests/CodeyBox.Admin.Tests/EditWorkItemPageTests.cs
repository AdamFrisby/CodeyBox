using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using EditWorkItemPage = CodeyBox.Admin.Web.Components.Pages.EditWorkItem;

namespace CodeyBox.Admin.Tests;

public sealed class EditWorkItemPageTests : TestContext
{
    private static WorkItemDto MakeItem(string id, string title, string state = "Queued",
        string prompt = "Original prompt") => new()
        {
            Id = id,
            ProjectId = "proj",
            Title = title,
            Prompt = prompt,
            Agent = "claude",
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            QueuePosition = 1,
        };

    [Fact]
    public void EditWorkItem_QueuedItem_ShowsForm()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "My Task");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<EditWorkItemPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Save", cut.Markup);
        Assert.Contains("My Task", cut.Markup);
    }

    [Fact]
    public void EditWorkItem_QueuedItem_PrePopulatesTitle()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Prepopulated Title");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<EditWorkItemPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Prepopulated Title", cut.Markup);
    }

    [Fact]
    public void EditWorkItem_QueuedItem_PrePopulatesPrompt()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task", prompt: "My detailed prompt");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<EditWorkItemPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("My detailed prompt", cut.Markup);
    }

    [Fact]
    public void EditWorkItem_NonQueuedItem_ShowsReadOnlyMessage()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Running Task", "Working");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<EditWorkItemPage>(p => p.Add(x => x.Id, item.Id));

        // Should show state message, not the edit form.
        Assert.Contains("Working", cut.Markup);
        Assert.DoesNotContain("Save", cut.Markup);
    }

    [Fact]
    public void EditWorkItem_NotFound_ShowsErrorMessage()
    {
        var fake = new FakeApiClient([]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<EditWorkItemPage>(p =>
            p.Add(x => x.Id, "aabbccdd-0000-0000-0000-000000000099"));

        Assert.Contains("not found", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditWorkItem_DoesNotLoadTwice_WhenIdUnchanged()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<EditWorkItemPage>(p => p.Add(x => x.Id, item.Id));
        // Re-render with same Id — LoadAsync should not fire again.
        cut.SetParametersAndRender(p => p.Add(x => x.Id, item.Id));

        Assert.Equal(1, fake.GetWorkItemCallCount);
    }

    [Fact]
    public void EditWorkItem_DoneItem_ShowsOnlyCannotEditMessage()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Done Task", "Done");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<EditWorkItemPage>(p => p.Add(x => x.Id, item.Id));

        // Form is suppressed for non-Queued items.
        Assert.DoesNotContain("Save", cut.Markup);
        Assert.Contains("Back", cut.Markup);
    }
}
