using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Components.Pages;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Composer behaviour: a pasted plan becomes a chain filed in one act with
/// the outlined edges; structure is a default the operator can flip;
/// inference is shown and overridable without silent re-infer; the review
/// is exactly what gets created; failure is reported honestly.
/// </summary>
public sealed class WorkItemComposerTests : BunitContext
{
    private static ProjectDto SampleProject() => new()
    {
        Id = "proj-1",
        DisplayName = "My Project",
        RepositoryUrl = "https://github.com/example/repo",
        DefaultAgent = "claude",
        DefaultBaseBranch = "main",
        AuditMaxIterations = 8,
    };

    private static WorkItemDto QueuedItem(string id, string title, string project = "proj-1", DateTimeOffset? created = null) => new()
    {
        Id = id,
        ProjectId = project,
        Title = title,
        Prompt = "p",
        Agent = "claude",
        State = "Queued",
        CreatedAt = created ?? DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        QueuePosition = 1,
    };

    private IRenderedComponent<NewWorkItem> RenderComposer(string query = "")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/work-items/new" + query);
        return Render<NewWorkItem>();
    }

    private static FakeApiClient Client(List<WorkItemDto>? items = null, List<ProjectDto>? projects = null) =>
        new(items ?? [], projects ?? [SampleProject()]);

    private static FakeApiClient Recording(List<CreateWorkItemRequest> seen, Func<int, Exception?>? failAt = null)
    {
        var fake = Client();
        var calls = 0;
        fake.CreateHandler = req =>
        {
            calls++;
            if (failAt?.Invoke(calls) is { } ex)
            {
                throw ex;
            }

            seen.Add(req);
            return new WorkItemDto
            {
                Id = Guid.NewGuid().ToString(),
                ProjectId = req.ProjectId,
                Title = req.Title,
                Prompt = req.Prompt,
                Agent = req.Agent ?? "claude",
                State = "Queued",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ExternalId = req.ExternalId,
            };
        };
        return fake;
    }

    private const string ThreeStepPlan =
        "1. Lay the foundation\nPour concrete.\n\n2. Raise the walls\nDepends on: 1\nBricks.\n\n3. Paint\nDepends on: 1, 2\nColour.";

    [Fact]
    public void PastedPlan_BecomesChainOutline_FiledInOneActWithOutlinedEdges()
    {
        var seen = new List<CreateWorkItemRequest>();
        Services.AddSingleton<ICodeyBoxApiClient>(Recording(seen));

        var cut = RenderComposer("?projectId=proj-1");
        cut.Find("textarea#prompt").Input(ThreeStepPlan);

        Assert.Contains("Reads as a plan of 3 items", cut.Find("#structure-strip").TextContent);
        Assert.Equal(3, cut.FindAll("#chain-preview .chain-item").Count);
        Assert.Equal("Lay the foundation", cut.Find("#chain-title-1").GetAttribute("value"));
        Assert.Equal("1, 2", cut.Find("#chain-waits-3").GetAttribute("value"));
        Assert.Contains("Files 3 items into My Project as a chain", cut.Find("#review-sentence").TextContent);
        Assert.Equal("File 3 items", cut.Find("button#file").TextContent.Trim());

        cut.Find("button#file").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, seen.Count));
        Assert.Equal(["Lay the foundation", "Raise the walls", "Paint"], seen.Select(r => r.Title));
        Assert.All(seen, r => Assert.NotNull(r.ExternalId));
        Assert.Empty(seen[0].DependsOn);
        Assert.Equal([seen[0].ExternalId!], seen[1].DependsOn);
        Assert.Equal([seen[0].ExternalId!, seen[1].ExternalId!], seen[2].DependsOn);
    }

    [Fact]
    public void LongPromptWithSections_DefaultsToOneItem_SplitIsOneClick()
    {
        var seen = new List<CreateWorkItemRequest>();
        Services.AddSingleton<ICodeyBoxApiClient>(Recording(seen));

        var cut = RenderComposer("?projectId=proj-1");
        cut.Find("textarea#prompt").Input("# Fix the redirect\n## Context\nUsers loop.\n## Acceptance criteria\n- No loop");

        Assert.Contains("probably one item", cut.Find("#structure-strip").TextContent);
        Assert.Empty(cut.FindAll("#chain-preview"));
        Assert.Equal("Fix the redirect", cut.Find("input#title").GetAttribute("value"));
        Assert.Contains("Files 1 item", cut.Find("#review-sentence").TextContent);

        cut.Find("button#file-as-chain").Click();
        Assert.Equal(2, cut.FindAll("#chain-preview .chain-item").Count);

        cut.Find("button#file-as-one").Click();
        Assert.Empty(cut.FindAll("#chain-preview"));

        cut.Find("button#file").Click();
        cut.WaitForAssertion(() => Assert.Single(seen));
        Assert.Contains("## Acceptance criteria", seen[0].Prompt);
        Assert.Equal("Fix the redirect", seen[0].Title);
    }

    [Fact]
    public void OutlineEdits_TitleBodyAndEdges_AreWhatGetsCreated()
    {
        var seen = new List<CreateWorkItemRequest>();
        Services.AddSingleton<ICodeyBoxApiClient>(Recording(seen));

        var cut = RenderComposer("?projectId=proj-1");
        cut.Find("textarea#prompt").Input("1. Original title\nBody one.\n\n2. Second\nBody two.");

        cut.Find("input#chain-title-1").Change("Edited title");
        cut.Find("button#chain-expand-2").Click();
        cut.Find("textarea#chain-body-2").Change("Edited body");
        cut.Find("input#chain-waits-2").Change("");

        cut.Find("button#file").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, seen.Count));

        Assert.Equal("Edited title", seen[0].Title);
        Assert.Equal("Edited body", seen[1].Prompt);
        Assert.Empty(seen[1].DependsOn);
    }

    [Fact]
    public void OutlineEdits_SurviveRetypingWhileCountUnchanged()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(Recording([]));

        var cut = RenderComposer("?projectId=proj-1");
        cut.Find("textarea#prompt").Input("1. First\nA.\n\n2. Second\nB.");
        cut.Find("input#chain-title-2").Change("Renamed");

        cut.Find("textarea#prompt").Input("1. First\nA. More.\n\n2. Second\nB.");

        Assert.Equal("Renamed", cut.Find("input#chain-title-2").GetAttribute("value"));
    }

    [Fact]
    public void ShapeButtons_FanOutAfterFoundation_AndTypedEdgesAreValidated()
    {
        var seen = new List<CreateWorkItemRequest>();
        Services.AddSingleton<ICodeyBoxApiClient>(Recording(seen));

        var cut = RenderComposer("?projectId=proj-1");
        cut.Find("textarea#prompt").Input(string.Join("\n\n", Enumerable.Range(1, 5).Select(n => $"{n}. Item {n}\nBody {n}.")));

        cut.Find("input#fanout-root").Change("2");
        cut.Find("button#shape-fanout").Click();

        Assert.Equal("2", cut.Find("#chain-waits-5").GetAttribute("value"));
        Assert.Contains("1 → 2, then 3–5 in parallel after 2", cut.Find("#review-sentence").TextContent);

        cut.Find("input#chain-waits-4").Change("9");
        Assert.Contains("no item 9", cut.Find("#review-problems").TextContent);
        Assert.True(cut.Find("button#file").HasAttribute("disabled"));

        cut.Find("input#chain-waits-4").Change("1, 2");
        Assert.Empty(cut.FindAll("#review-problems"));

        cut.Find("button#file").Click();
        cut.WaitForAssertion(() => Assert.Equal(5, seen.Count));
        Assert.Equal([seen[1].ExternalId!], seen[4].DependsOn);
        Assert.Equal([seen[0].ExternalId!, seen[1].ExternalId!], seen[3].DependsOn);
    }

    [Fact]
    public void Cycle_IsNamedAndBlocksFiling()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(Recording([]));

        var cut = RenderComposer("?projectId=proj-1");
        cut.Find("textarea#prompt").Input("1. A\nx\n\n2. B\nx");
        cut.Find("input#chain-waits-1").Change("2");

        Assert.Contains("Items 1, 2 wait for each other", cut.Find("#review-problems").TextContent);
        Assert.True(cut.Find("button#file").HasAttribute("disabled"));
    }

    [Fact]
    public void RemovingAnOutlineItem_RenumbersEdges()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(Recording([]));

        var cut = RenderComposer("?projectId=proj-1");
        cut.Find("textarea#prompt").Input("1. A\nx\n\n2. B\nx\n\n3. C\nx");

        cut.Find("button#chain-remove-2").Click();

        Assert.Equal(2, cut.FindAll("#chain-preview .chain-item").Count);
        Assert.Equal("C", cut.Find("input#chain-title-2").GetAttribute("value"));
        Assert.Equal("1", cut.Find("input#chain-waits-2").GetAttribute("value"));
    }

    [Fact]
    public void ExistingDependencies_AttachToChainRoots()
    {
        var seen = new List<CreateWorkItemRequest>();
        var existing = QueuedItem("eeeeeeee-0000-0000-0000-000000000001", "Existing");
        var fake = Client([existing]);
        fake.CreateHandler = Recording(seen).CreateHandler;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComposer("?followUp=" + existing.Id);
        cut.Find("textarea#prompt").Input("1. Root one\nx\n\n2. Root two\nx\n\n3. Child\nDepends on: 1, 2\nx");
        cut.Find("input#chain-waits-2").Change("");

        Assert.Contains("the chain's roots wait for eeeeeeee", cut.Find("#review-sentence").TextContent);
        cut.Find("button#file").Click();
        cut.WaitForAssertion(() => Assert.Equal(3, seen.Count));

        Assert.Equal([existing.Id], seen[0].DependsOn);
        Assert.Equal([existing.Id], seen[1].DependsOn);
        Assert.DoesNotContain(existing.Id, seen[2].DependsOn);
    }

    [Fact]
    public void InferredAgent_IsOverridableAndSurvivesProjectSwitch()
    {
        var other = SampleProject();
        other.Id = "proj-2";
        other.DisplayName = "Second";
        other.DefaultAgent = "copilot";
        Services.AddSingleton<ICodeyBoxApiClient>(Client(projects: [SampleProject(), other]));

        var cut = RenderComposer("?projectId=proj-1");

        Assert.Contains("project default", cut.Find("label[for=agent]").TextContent);

        cut.Find("select#agent").Change("codex");
        Assert.Contains("you", cut.Find("label[for=agent]").TextContent);

        cut.Find("select#project").Change("proj-2");

        Assert.Contains("you", cut.Find("label[for=agent]").TextContent);
        Assert.Equal("codex", cut.Find("select#agent").GetAttribute("value"));
        Assert.Contains("project default", cut.Find("label[for=baseBranch]").TextContent);
    }

    [Fact]
    public void Reset_HandsFieldBackToInference()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(Client());

        var cut = RenderComposer("?projectId=proj-1&agent=codex");

        Assert.Contains("from link", cut.Find("label[for=agent]").TextContent);

        cut.Find("select#agent").Change("copilot");
        Assert.Contains("you", cut.Find("label[for=agent]").TextContent);

        cut.Find("button#reset-agent").Click();
        Assert.Contains("from link", cut.Find("label[for=agent]").TextContent);
        Assert.Equal("codex", cut.Find("select#agent").GetAttribute("value"));
    }

    [Fact]
    public void AuditBudget_InferredFromProject_OverridableInline()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(Client());

        var cut = RenderComposer("?projectId=proj-1");

        Assert.Equal("8", cut.Find("input#auditMaxIterations").GetAttribute("value"));
        Assert.Contains("project default", cut.Find("label[for=auditMaxIterations]").TextContent);

        cut.Find("input#auditMaxIterations").Change("3");
        Assert.Contains("you", cut.Find("label[for=auditMaxIterations]").TextContent);
        cut.Find("textarea#prompt").Input("Something");
        Assert.Contains("audit ×3", cut.Find("#review-sentence").TextContent);

        cut.Find("button#reset-auditMaxIterations").Click();
        Assert.Equal("8", cut.Find("input#auditMaxIterations").GetAttribute("value"));
    }

    [Fact]
    public void RailSettings_FlowIntoTheCreate_AndReadBackInTheSentence()
    {
        var seen = new List<CreateWorkItemRequest>();
        Services.AddSingleton<ICodeyBoxApiClient>(Recording(seen));

        var cut = RenderComposer("?projectId=proj-1");
        cut.Find("textarea#prompt").Input("Do it well.");
        cut.Find("input#priority").Change("7");
        cut.Find("input#minModelScore").Change("80");
        cut.Find("input#requiredCapabilities").Change("gpu, large-mem");
        cut.Find("select#knob-changeScope").Change("surgical");
        cut.Find("textarea#knobs").Change("retries=3");
        cut.Find("input#auditComplexity").Change("high");
        cut.Find("input#pushUpstream").Change(false);

        var sentence = cut.Find("#review-sentence").TextContent;
        Assert.Contains("priority 7", sentence);
        Assert.Contains("model score ≥ 80", sentence);
        Assert.Contains("needs gpu, large-mem", sentence);
        Assert.Contains("changeScope=surgical", sentence);
        Assert.Contains("no upstream push", sentence);

        cut.Find("button#file").Click();

        cut.WaitForAssertion(() => Assert.Single(seen));
        var sent = seen[0];
        Assert.Equal(7, sent.Priority);
        Assert.Equal(80, sent.MinModelScore);
        Assert.Equal(["gpu", "large-mem"], sent.RequiredCapabilities);
        Assert.Equal("surgical", sent.Knobs!["changeScope"]);
        Assert.Equal("3", sent.Knobs["retries"]);
        Assert.Equal("high", sent.AuditComplexity);
        Assert.False(sent.PushUpstream);
        Assert.Null(sent.IsRefactor);
    }

    [Fact]
    public void BadKnobValue_IsAProblemBeforeThePost()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(Recording([]));

        var cut = RenderComposer("?projectId=proj-1");
        cut.Find("textarea#prompt").Input("x");
        cut.Find("textarea#knobs").Change("plan=maybe");

        Assert.Contains("Plan first must be one of off, on", cut.Find("#review-problems").TextContent);
        Assert.True(cut.Find("button#file").HasAttribute("disabled"));
    }

    [Fact]
    public void PartialChainFailure_ReportedHonestlyWithOutlineIntact()
    {
        var seen = new List<CreateWorkItemRequest>();
        Services.AddSingleton<ICodeyBoxApiClient>(Recording(seen, call => call == 2 ? new HttpRequestException("orchestrator exploded") : null));

        var cut = RenderComposer("?projectId=proj-1");
        cut.Find("textarea#prompt").Input("1. First\nA.\n\n2. Second\nB.");
        cut.Find("button#file").Click();

        cut.WaitForAssertion(() => Assert.Contains("Filed 1 of 2", cut.Find("#composer-error").TextContent));
        Assert.Contains("orchestrator exploded", cut.Find("#composer-error").TextContent);
        Assert.Equal(2, cut.FindAll("#chain-preview .chain-item").Count);
    }

    [Fact]
    public void Templates_QueuedForSelectedProjectFromTheStrip()
    {
        var fake = Client();
        fake.TemplatesOverride = [new TaskTemplateDto { Name = "nightly-checks", Path = "nightly-checks.json", CheckCount = 4 }];
        fake.QueueTemplateResponse = new QueuedTaskTemplateResponse { Template = "nightly-checks", Enqueued = 4 };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComposer("?projectId=proj-1");

        Assert.Contains("nightly-checks", cut.Find("#templates").TextContent);
        cut.Find("button#queue-template").Click();

        cut.WaitForAssertion(() => Assert.Contains("Queued 4 items", cut.Find("#template-message").TextContent));
        Assert.Equal("nightly-checks", fake.LastQueueTemplateRequest?.Template);
        Assert.Equal("proj-1", fake.LastQueueTemplateRequest?.ProjectId);
    }

    [Fact]
    public void DependencyPicker_OrdersSameProjectFirst_Searches_AndFoldsTheLongTail()
    {
        var items = new List<WorkItemDto>
        {
            QueuedItem("11111111-0000-0000-0000-000000000001", "Foreign task", "proj-9"),
            QueuedItem("22222222-0000-0000-0000-000000000002", "Local database migration"),
        };
        items.AddRange(Enumerable.Range(0, 12).Select(i => QueuedItem($"33333333-0000-0000-0000-{i:000000000000}", $"Filler {i}")));
        var projects = new List<ProjectDto>
        {
            SampleProject(),
            new() { Id = "proj-9", DisplayName = "Far Away", RepositoryUrl = "https://example.invalid/r", DefaultAgent = "claude" },
        };
        Services.AddSingleton<ICodeyBoxApiClient>(Client(items, projects));

        var cut = RenderComposer("?projectId=proj-1");

        var labels = cut.FindAll(".dep-row label").Select(l => l.TextContent).ToList();
        Assert.Equal(8, labels.Count);
        Assert.DoesNotContain(labels, l => l.Contains("Foreign task"));
        Assert.Contains("more — show all", cut.Find("button#dep-more").TextContent);

        cut.Find("button#dep-more").Click();
        labels = cut.FindAll(".dep-row label").Select(l => l.TextContent).ToList();
        Assert.Equal(14, labels.Count);
        Assert.Contains("Foreign task", labels[^1]);

        cut.Find("input#dep-search").Input("migrat");
        labels = cut.FindAll(".dep-row label").Select(l => l.TextContent).ToList();
        Assert.Single(labels);
        Assert.Contains("Local database migration", labels[0]);

        cut.Find("input#dep-22222222-0000-0000-0000-000000000002").Change(true);
        Assert.NotNull(cut.Find("#dep-chip-22222222-0000-0000-0000-000000000002"));
        cut.Find("button#dep-remove-22222222-0000-0000-0000-000000000002").Click();
        Assert.Empty(cut.FindAll("#dep-chip-22222222-0000-0000-0000-000000000002"));
    }

    [Fact]
    public void FollowUp_InfersRelationshipNotWords()
    {
        var target = QueuedItem("aaaaaaaa-0000-0000-0000-000000000001", "Original quest");
        Services.AddSingleton<ICodeyBoxApiClient>(Client([target]));

        var cut = RenderComposer("?followUp=" + target.Id);

        Assert.Contains("Original quest", cut.Find(".composer-context").TextContent);
        Assert.Contains("follow-up", cut.Find("label[for=project]").TextContent);
        Assert.NotNull(cut.Find("#dep-chip-aaaaaaaa-0000-0000-0000-000000000001"));
        Assert.Equal(string.Empty, cut.Find("input#title").GetAttribute("value"));
        Assert.Equal(string.Empty, cut.Find("textarea#prompt").TextContent);
    }

    [Fact]
    public void Project_InferredFromMostRecentItemWhenNothingElseSaysSo()
    {
        var other = SampleProject();
        other.Id = "proj-2";
        other.DisplayName = "Second";
        var items = new List<WorkItemDto>
        {
            QueuedItem("11111111-0000-0000-0000-000000000001", "Older", "proj-1", DateTimeOffset.UtcNow.AddHours(-2)),
            QueuedItem("22222222-0000-0000-0000-000000000002", "Newest", "proj-2", DateTimeOffset.UtcNow),
        };
        Services.AddSingleton<ICodeyBoxApiClient>(Client(items, [SampleProject(), other]));

        var cut = RenderComposer();

        Assert.Equal("proj-2", cut.Find("select#project").GetAttribute("value"));
        Assert.Contains("most recent item", cut.Find("label[for=project]").TextContent);
    }

    [Fact]
    public void Promote_ShowsOnlyWhatPromotionAccepts()
    {
        var fake = Client();
        fake.SuggestionsOverride =
        [
            new SuggestionDto { Id = "s-1", ProjectId = "proj-1", Title = "Tighten the cache", Category = "perf", Severity = "low", Rationale = "r" },
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComposer("?fromSuggestion=s-1");

        Assert.Contains("Tighten the cache", cut.Find(".composer-context").TextContent);
        Assert.Contains("suggestion", cut.Find("label[for=project]").TextContent);
        Assert.Empty(cut.FindAll("input#title"));
        Assert.Empty(cut.FindAll("input#priority"));
        Assert.NotNull(cut.Find("select#agent"));
        Assert.NotNull(cut.Find("input#externalId"));
        Assert.Equal("Promote to work item", cut.Find("button#file").TextContent.Trim());
    }
}
