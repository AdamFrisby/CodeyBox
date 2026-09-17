using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Components.Pages;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Composer behaviour: one submission files a whole reviewed chain with
/// the previewed edges, inference is overridable without silent re-infer,
/// the preview is exactly what gets created, and a two-item chain is
/// authorable keyboard-only from start to finish.
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

    private static WorkItemDto QueuedItem(string id, string title, string project = "proj-1") => new()
    {
        Id = id,
        ProjectId = project,
        Title = title,
        Prompt = "p",
        Agent = "claude",
        State = "Queued",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        QueuePosition = 1,
    };

    private IRenderedComponent<NewWorkItem> RenderComposer(string query = "")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/work-items/new" + query);
        return Render<NewWorkItem>();
    }

    private FakeApiClient Client(
        List<WorkItemDto>? items = null, List<ProjectDto>? projects = null) =>
        new(items ?? [], projects ?? [SampleProject()]);

    [Fact]
    public void Composer_ParsedChain_FiledInSingleSubmissionWithPreviewedEdges()
    {
        var seen = new List<CreateWorkItemRequest>();
        var fake = Client();
        fake.CreateHandler = req =>
        {
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
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComposer("?projectId=proj-1&mode=plan");

        cut.Find("textarea#plan-input").Input(
            "1. Lay the foundation\nPour concrete.\n\n2. Raise the walls\nDepends on: 1\nBricks.\n\n3. Paint\nDepends on: 1, 2\nColour.");
        cut.Find("button#parse-plan").Click();

        cut.WaitForAssertion(() => Assert.Contains("Parsed 3 items", cut.Find("#preview-info").TextContent));
        Assert.Equal("Lay the foundation", cut.Find("#chain-title-1").GetAttribute("value"));
        Assert.Contains("chain: 1", cut.FindAll(".chain-edges-summary")[1].TextContent);

        cut.Find("button#file-chain").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, seen.Count));
        Assert.Equal(["Lay the foundation", "Raise the walls", "Paint"], seen.Select(r => r.Title));
    }

    [Fact]
    public void Composer_ChainEdges_ReferenceSiblingsByGeneratedExternalIds()
    {
        var seen = new List<CreateWorkItemRequest>();
        var fake = Client();
        fake.CreateHandler = req =>
        {
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
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComposer("?projectId=proj-1&mode=plan");

        cut.Find("textarea#plan-input").Input("1. First\nA.\n\n2. Second\nB.");
        cut.Find("button#parse-plan").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#chain-preview .chain-item").Count));
        cut.Find("button#file-chain").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, seen.Count));
        Assert.NotNull(seen[0].ExternalId);
        Assert.NotNull(seen[1].ExternalId);
        Assert.NotEqual(seen[0].ExternalId, seen[1].ExternalId);
        Assert.Empty(seen[0].DependsOn);
        Assert.Equal([seen[0].ExternalId!], seen[1].DependsOn);
    }

    [Fact]
    public void Composer_PreviewEdits_AreWhatGetsCreated()
    {
        var seen = new List<CreateWorkItemRequest>();
        var fake = Client();
        fake.CreateHandler = req =>
        {
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
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComposer("?projectId=proj-1&mode=plan");

        cut.Find("textarea#plan-input").Input("1. Original title\nBody one.\n\n2. Second\nBody two.");
        cut.Find("button#parse-plan").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#chain-preview .chain-item").Count));

        cut.Find("input#chain-title-1").Change("Edited title");
        cut.Find("textarea#chain-body-2").Change("Edited body");
        cut.Find("input#chain-dep-2-1").Change(false);

        cut.Find("button#file-chain").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, seen.Count));

        Assert.Equal("Edited title", seen[0].Title);
        Assert.Equal("Edited body", seen[1].Prompt);
        Assert.Empty(seen[1].DependsOn);
    }

    [Fact]
    public void Composer_InferredAgent_IsOverridableAndSurvivesProjectSwitch()
    {
        var other = SampleProject();
        other.Id = "proj-2";
        other.DisplayName = "Second";
        other.DefaultAgent = "copilot";
        var fake = Client(projects: [SampleProject(), other]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComposer("?projectId=proj-1");

        Assert.Contains("project default", cut.Find("label[for=agent]").TextContent);

        cut.Find("select#agent").Change("codex");
        Assert.Contains("set by you", cut.Find("label[for=agent]").TextContent);

        cut.Find("select#project").Change("proj-2");

        Assert.Contains("set by you", cut.Find("label[for=agent]").TextContent);
        Assert.Equal("codex", cut.Find("select#agent").GetAttribute("value"));
    }

    [Fact]
    public void Composer_ResetHandsFieldBackToInference()
    {
        var fake = Client();
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComposer("?projectId=proj-1&agent=codex");

        Assert.Contains("from link", cut.Find("label[for=agent]").TextContent);

        cut.Find("select#agent").Change("copilot");
        Assert.Contains("set by you", cut.Find("label[for=agent]").TextContent);

        cut.Find("button#reset-agent").Click();
        Assert.Contains("from link", cut.Find("label[for=agent]").TextContent);
        Assert.Equal("codex", cut.Find("select#agent").GetAttribute("value"));
    }

    [Fact]
    public void Composer_AuditBudgetChip_InferredFromProjectAndOverridable()
    {
        var fake = Client();
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComposer("?projectId=proj-1");

        var chip = cut.Find("button#chip-budget");
        Assert.Contains("8", chip.TextContent);
        Assert.Contains("project default", chip.TextContent);

        chip.Click();
        cut.Find("input#chip-budget-input").Change("3");
        cut.Find("button#chip-save").Click();

        Assert.Contains("3", cut.Find("button#chip-budget").TextContent);
        Assert.Contains("you", cut.Find("button#chip-budget").TextContent);
    }

    [Fact]
    public void Composer_AdvancedFields_FlowIntoSingleCreate()
    {
        var seen = new List<CreateWorkItemRequest>();
        var fake = Client();
        fake.CreateHandler = req =>
        {
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
            };
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NewWorkItem>();
        cut.Find("select#project").Change("proj-1");
        cut.Find("input#title").Change("Advanced item");
        cut.Find("textarea#prompt").Change("Do it well.");
        cut.Find("input#priority").Change("7");
        cut.Find("input#minModelScore").Change("80");
        cut.Find("input#requiredCapabilities").Change("gpu, large-mem");
        cut.Find("textarea#knobs").Change("retries=3\ntimeout=fast");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Single(seen));
        var sent = seen[0];
        Assert.Equal(7, sent.Priority);
        Assert.Equal(80, sent.MinModelScore);
        Assert.Equal(["gpu", "large-mem"], sent.RequiredCapabilities);
        Assert.Equal("3", sent.Knobs!["retries"]);
        Assert.Equal("fast", sent.Knobs["timeout"]);
    }

    [Fact]
    public void Composer_PartialChainFailure_ReportedHonestlyWithPreviewIntact()
    {
        var calls = 0;
        var fake = Client();
        fake.CreateHandler = req =>
        {
            calls++;
            if (calls == 2)
            {
                throw new HttpRequestException("orchestrator exploded");
            }

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
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComposer("?projectId=proj-1&mode=plan");

        cut.Find("textarea#plan-input").Input("1. First\nA.\n\n2. Second\nB.");
        cut.Find("button#parse-plan").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#chain-preview .chain-item").Count));
        cut.Find("button#file-chain").Click();

        cut.WaitForAssertion(() => Assert.Contains("Filed 1 of 2", cut.Find(".error-banner").TextContent));
        Assert.Contains("orchestrator exploded", cut.Find(".error-banner").TextContent);
        Assert.Equal(2, cut.FindAll("#chain-preview .chain-item").Count);
    }

    [Fact]
    public void Composer_Templates_ListedAndQueuedForSelectedProject()
    {
        var fake = Client();
        fake.TemplatesOverride =
        [
            new TaskTemplateDto { Name = "nightly-checks", Path = "nightly-checks.json", CheckCount = 4 },
        ];
        fake.QueueTemplateResponse = new QueuedTaskTemplateResponse
        {
            Template = "nightly-checks",
            Enqueued = 4,
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NewWorkItem>();

        Assert.Contains("nightly-checks", cut.Find("#templates").TextContent);
        Assert.True(cut.Find("button#queue-template-0").HasAttribute("disabled"));

        cut.Find("select#project").Change("proj-1");
        cut.Find("button#queue-template-0").Click();

        cut.WaitForAssertion(() => Assert.Contains("Queued 4 items", cut.Find("#template-message").TextContent));
        Assert.Equal("nightly-checks", fake.LastQueueTemplateRequest?.Template);
        Assert.Equal("proj-1", fake.LastQueueTemplateRequest?.ProjectId);
    }

    [Fact]
    public void Composer_DependencyPicker_OrdersSameProjectFirstAndSearches()
    {
        var items = new List<WorkItemDto>
        {
            QueuedItem("11111111-0000-0000-0000-000000000001", "Foreign task", "proj-9"),
            QueuedItem("22222222-0000-0000-0000-000000000002", "Local database migration"),
        };
        var projects = new List<ProjectDto>
        {
            SampleProject(),
            new() { Id = "proj-9", DisplayName = "Far Away", RepositoryUrl = "https://example.invalid/r", DefaultAgent = "claude" },
        };
        Services.AddSingleton<ICodeyBoxApiClient>(Client(items, projects));

        var cut = RenderComposer("?projectId=proj-1");

        var labels = cut.FindAll(".dependency-picker .form-check label").Select(l => l.TextContent).ToList();
        Assert.Contains("Local database migration", labels[0]);
        Assert.Contains("Foreign task", labels[1]);

        cut.Find("input#dep-search").Input("migrat");
        labels = cut.FindAll(".dependency-picker .form-check label").Select(l => l.TextContent).ToList();
        Assert.Single(labels);
        Assert.Contains("Local database migration", labels[0]);
    }

    [Fact]
    public void Composer_FollowUp_InfersRelationshipNotWords()
    {
        var target = QueuedItem("aaaaaaaa-0000-0000-0000-000000000001", "Original quest");
        var fake = Client([target]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComposer("?followUp=" + target.Id);

        Assert.Contains("Original quest", cut.Markup);
        Assert.Contains("follow-up", cut.Find("label[for=project]").TextContent);
        Assert.True(cut.Find("input#dep-aaaaaaaa-0000-0000-0000-000000000001").HasAttribute("checked"));
        Assert.Equal(string.Empty, cut.Find("input#title").GetAttribute("value"));
    }

    /// <summary>
    /// Keyboard-only authoring of a two-item chain, start to finish: focus
    /// and change events plus Enter keydowns — no mouse clicks anywhere.
    /// </summary>
    [Fact]
    public void Composer_KeyboardOnly_TwoItemChainStartToFinish()
    {
        var seen = new List<CreateWorkItemRequest>();
        var fake = Client();
        fake.CreateHandler = req =>
        {
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
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NewWorkItem>();

        // Keyboard-only: native controls are Tab-focusable by construction;
        // selections and typing raise change/input, Enter raises keydown. No Click.
        cut.Find("select#project").Change("proj-1");

        cut.Find("input#mode-plan").Change("plan");

        cut.Find("textarea#plan-input").Input("1. First keyboard item\nDo alpha.\n\n2. Second keyboard item\nDo beta.");

        cut.Find("button#parse-plan").KeyDown("Enter");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#chain-preview .chain-item").Count));

        cut.Find("input#chain-title-2").Change("Second keyboard item (edited)");

        cut.Find("button#file-chain").KeyDown("Enter");

        cut.WaitForAssertion(() => Assert.Equal(2, seen.Count));
        Assert.Equal("First keyboard item", seen[0].Title);
        Assert.Equal("Second keyboard item (edited)", seen[1].Title);
    }
}
