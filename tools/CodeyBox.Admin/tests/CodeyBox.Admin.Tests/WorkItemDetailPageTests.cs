using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using WorkItemDetailPage = CodeyBox.Admin.Web.Components.Pages.WorkItemDetail;

namespace CodeyBox.Admin.Tests;

public sealed class WorkItemDetailPageTests : TestContext
{
    public WorkItemDetailPageTests()
    {
        // OrchestratorHubSettings is injected by WorkItemDetail; empty URL skips the live hub connection.
        Services.AddSingleton(new OrchestratorHubSettings("", null));
    }

    private static WorkItemDto MakeItem(string id, string title, string state = "Queued") => new()
    {
        Id = id,
        ProjectId = "proj",
        Title = title,
        Prompt = "Some prompt text",
        Agent = "claude",
        State = state,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        QueuePosition = 1,
    };

    [Fact]
    public void WorkItemDetail_ShowsTitle()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "My Work Item");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("My Work Item", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_ShowsPromptInCollapsible()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Some prompt text", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_WithPlanArtifact_ShowsPlanPanelAndMetadata()
    {
        var generatedAt = new DateTimeOffset(2026, 6, 1, 2, 3, 4, TimeSpan.Zero);
        var reviewedAt = generatedAt.AddMinutes(5);
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task");
        item.PlanArtifact = "PLAN:\nApproach: render this plan.";
        item.PlanGeneratedAt = generatedAt;
        item.PlanReviewedAt = reviewedAt;
        item.PlanReviewSummary = "Placeholder plan review approved.";
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Plan", cut.Markup);
        Assert.Contains("PLAN:", cut.Markup);
        Assert.Contains("render this plan", cut.Markup);
        Assert.Contains($"Generated {generatedAt:O}", cut.Markup);
        Assert.Contains($"Reviewed {reviewedAt:O}", cut.Markup);
        Assert.Contains("Placeholder plan review approved.", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_QueuedItem_ShowsEditLink()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Queued Task", "Queued");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("edit", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkItemDetail_DoneItem_DoesNotShowEditLink()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Done Task", "Done");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.DoesNotContain("/edit", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_FailedItem_ShowsRetryButtons()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Failed Task", "Failed");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Retry", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_NotFound_ShowsErrorMessage()
    {
        var fake = new FakeApiClient([]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p =>
            p.Add(x => x.Id, "aabbccdd-0000-0000-0000-000000000099"));

        Assert.Contains("not found", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkItemDetail_DoesNotLoadTwice_WhenIdUnchanged()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));
        // Re-render with the same Id — LoadAsync should not fire again.
        cut.SetParametersAndRender(p => p.Add(x => x.Id, item.Id));

        // GetWorkItemAsync was called exactly once, not twice.
        Assert.Equal(1, fake.GetWorkItemCallCount);
    }

    [Fact]
    public void WorkItemDetail_ShowsTimelineButton_ForAnyItem()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task", "Working");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Timeline", cut.Markup);
        Assert.Contains("/timeline", cut.Markup);
    }

    private static QuestionDto MakeOpenQuestion(string workItemId, string questionId, string text) => new()
    {
        Id = Guid.NewGuid().ToString(),
        WorkItemId = workItemId,
        QuestionId = questionId,
        QuestionText = text,
        State = "open",
        AskedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void WorkItemDetail_WithOpenQuestion_ShowsQuestionsSection()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task", "NeedsOperatorInput");
        var fake = new FakeApiClient([item]);
        fake.QuestionsOverride[item.Id] = [MakeOpenQuestion(item.Id, "q-001", "Which approach to use?")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Questions", cut.Markup);
        Assert.Contains("q-001", cut.Markup);
        Assert.Contains("Which approach to use?", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_WithOpenQuestion_ShowsAnswerTextareaAndButtons()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task", "NeedsOperatorInput");
        var fake = new FakeApiClient([item]);
        fake.QuestionsOverride[item.Id] = [MakeOpenQuestion(item.Id, "q-001", "Which approach?")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("textarea", cut.Markup);
        Assert.Contains("Submit", cut.Markup);
        Assert.Contains("Dismiss", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_WithAnsweredQuestion_ShowsAnswerText()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task", "Reworking");
        var fake = new FakeApiClient([item]);
        fake.QuestionsOverride[item.Id] =
        [
            new QuestionDto
            {
                Id = Guid.NewGuid().ToString(),
                WorkItemId = item.Id,
                QuestionId = "q-001",
                QuestionText = "Which approach?",
                State = "answered",
                AskedAt = DateTimeOffset.UtcNow,
                AnsweredAt = DateTimeOffset.UtcNow,
                AnswerText = "Use approach B.",
            }
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Answered", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Use approach B.", cut.Markup);
        Assert.DoesNotContain("Submit", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_WithDismissedQuestion_ShowsDismissedMessage()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task", "Reworking");
        var fake = new FakeApiClient([item]);
        fake.QuestionsOverride[item.Id] =
        [
            new QuestionDto
            {
                Id = Guid.NewGuid().ToString(),
                WorkItemId = item.Id,
                QuestionId = "q-002",
                QuestionText = "Which library?",
                State = "dismissed",
                AskedAt = DateTimeOffset.UtcNow,
                DismissedAt = DateTimeOffset.UtcNow,
                DismissReason = "Out of scope for this PR.",
            }
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Dismissed", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Out of scope for this PR.", cut.Markup);
        Assert.DoesNotContain("Submit", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_ShowsReplayButton_ForTerminalItem()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task", "Done");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.DoesNotContain("question-block", cut.Markup);
        Assert.Contains("Replay", cut.Markup);
    }
}
