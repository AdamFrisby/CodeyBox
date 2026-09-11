using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using CapacityPage = CodeyBox.Admin.Web.Components.Pages.Capacity;
using PluginsPage = CodeyBox.Admin.Web.Components.Pages.Plugins;
using StatisticsPage = CodeyBox.Admin.Web.Components.Pages.Statistics;
using SuggestionsPage = CodeyBox.Admin.Web.Components.Pages.Suggestions;
using WorkItemDetailPage = CodeyBox.Admin.Web.Components.Pages.WorkItemDetail;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Renders the seeded admin instance's content across the Blazor UI: the
/// same projects / work items / quota / audits the <c>admin-seeded</c>
/// harness serves, asserted through real component rendering. Targets the
/// pages with the lowest branch coverage (Capacity, Statistics, Plugins)
/// plus seeded-state action flows (answer/dismiss/replay).
/// </summary>
public sealed class SeededAdminUiTests : BunitContext
{
    public SeededAdminUiTests()
    {
        Services.AddSingleton(new OrchestratorHubSettings("", null));
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static WorkItemDto SeededItem(string id, string title, string state, string project = "seeded-shop") => new()
    {
        Id = id,
        ProjectId = project,
        Title = title,
        Prompt = $"Seeded prompt for {title}",
        Agent = "seeded-fake",
        State = state,
        CreatedAt = Now.AddHours(-5),
        UpdatedAt = Now.AddHours(-1),
        QueuePosition = state == "Queued" ? 1 : 0,
    };

    private static List<WorkItemDto> SeededItems() =>
    [
        SeededItem("11111111-0000-0000-0000-000000000001", "Seeded Queued item", "Queued"),
        SeededItem("11111111-0000-0000-0000-000000000002", "Seeded Working item", "Working"),
        SeededItem("11111111-0000-0000-0000-000000000003", "Seeded Done item A", "Done"),
        SeededItem("11111111-0000-0000-0000-000000000004", "Seeded Done item B", "Done", "seeded-portal"),
        SeededItem("11111111-0000-0000-0000-000000000005", "Seeded Failed item", "Failed"),
    ];

    private static CapacityReportDto SeededCapacity() => new()
    {
        GeneratedAt = Now,
        FromUtc = Now.AddDays(-7),
        ToUtc = Now,
        Entries =
        [
            new CapacityEntryDto
            {
                Agent = "seeded-fake",
                WindowName = "five_hour",
                ModelId = "seeded-model",
                SampleIntervals = 12,
                CurrentPct = 62.5,
                InputTokensPerPercent = 1200.5,
                OutputTokensPerPercent = 300,
                RequestsPerPercent = 4,
                EstimatedFullWindowInputTokens = 120050,
                Confidence = "High",
                Notes = ["seeded note: burn stable"],
                Intervals =
                [
                    new CapacityIntervalDto
                    {
                        FromUtc = Now.AddHours(-2),
                        ToUtc = Now.AddHours(-1),
                        DeltaPct = 5,
                        InputTokens = 6000,
                        OutputTokens = 1500,
                        Requests = 20,
                    },
                ],
            },
            new CapacityEntryDto
            {
                Agent = "seeded-fake",
                WindowName = "weekly",
                SampleIntervals = 3,
                Confidence = "Low",
            },
        ],
    };

    // ── Capacity ──────────────────────────────────────────────────────────

    [Fact]
    public void Capacity_RichReport_RendersEntriesIntervalsAndNotes()
    {
        var fake = new FakeApiClient([]);
        fake.CapacityOverride = SeededCapacity();
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<CapacityPage>();

        Assert.Contains("Per-window capacity", cut.Markup);
        Assert.Contains("seeded-fake", cut.Markup);
        Assert.Contains("five_hour", cut.Markup);
        Assert.Contains("seeded note: burn stable", cut.Markup);
        Assert.Contains("burn-rate over time", cut.Markup);
        Assert.DoesNotContain("No capacity data yet", cut.Markup);
    }

    [Fact]
    public void Capacity_EmptyReport_ShowsNoDataMessage()
    {
        var fake = new FakeApiClient([]);
        fake.CapacityOverride = new CapacityReportDto { GeneratedAt = Now };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<CapacityPage>();

        Assert.Contains("No capacity data yet", cut.Markup);
    }

    [Fact]
    public void Capacity_NullReport_ShowsUnavailableError()
    {
        var fake = new FakeApiClient([]);
        fake.CapacityOverride = null;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<CapacityPage>();

        Assert.Contains("Capacity analysis unavailable", cut.Markup);
    }

    [Fact]
    public void Capacity_Failure_ShowsErrorBanner()
    {
        var fake = new FakeApiClient([]);
        fake.CapacityFailure = new InvalidOperationException("seeded boom");
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<CapacityPage>();

        Assert.Contains("Failed to load capacity report", cut.Markup);
    }

    [Fact]
    public void Capacity_AgentFilter_ReloadsWithAgentParam()
    {
        var fake = new FakeApiClient([]);
        fake.CapacityOverride = SeededCapacity();
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<CapacityPage>();
        cut.Find("input").Change("seeded-fake");

        Assert.NotNull(fake.LastCapacityCall);
        Assert.Equal("seeded-fake", fake.LastCapacityCall.Value.Agent);
        Assert.Equal(168, fake.LastCapacityCall.Value.Hours);
    }

    [Fact]
    public void Capacity_HorizonChange_ReloadsWithHoursParam()
    {
        var fake = new FakeApiClient([]);
        fake.CapacityOverride = SeededCapacity();
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<CapacityPage>();
        cut.Find("select").Change("24");

        Assert.NotNull(fake.LastCapacityCall);
        Assert.Equal(24, fake.LastCapacityCall.Value.Hours);
    }

    // ── Statistics ────────────────────────────────────────────────────────

    private static FakeApiClient SeededStatisticsFake()
    {
        var fake = new FakeApiClient(SeededItems());
        fake.QuotaOverride = new QuotaReportDto
        {
            GeneratedAt = Now,
            Probes =
            [
                new QuotaProbeDto
                {
                    Agent = "seeded-fake",
                    ClassId = "seeded",
                    ModelId = "seeded-model",
                    Billing = "Subscription",
                    LatestSnapshot = new QuotaSnapshotDto
                    {
                        AvailablePct = 62,
                        IsKnown = true,
                        ResetAt = Now.AddHours(5),
                    },
                },
                new QuotaProbeDto
                {
                    Agent = "seeded-fake",
                    AgentInstanceId = "seeded-fake/acct-b",
                    LatestSnapshot = new QuotaSnapshotDto { AvailablePct = -1, IsKnown = false },
                },
            ],
        };
        fake.WorkersOverride = new WorkersStatusDto
        {
            MaxConcurrent = 4,
            CurrentlyRunning = 1,
            QueuedCount = 1,
        };
        fake.ConcurrencyOverride = new ConcurrencyDto
        {
            GlobalMaxConcurrent = 4,
            CurrentlyRunningTotal = 1,
            CurrentlyRunningPerAgent = new Dictionary<string, int> { ["seeded-fake"] = 1 },
        };
        foreach (var item in SeededItems())
        {
            fake.CostsOverride[item.Id] = new WorkItemCostsDto
            {
                WorkItemId = item.Id,
                Totals = new CostTotalsDto
                {
                    InputTokens = 1000,
                    OutputTokens = 200,
                    EstimatedUsd = 0.012,
                },
                ByAgent =
                [
                    new AgentCostBreakdownDto
                    {
                        Agent = "seeded-fake",
                        ModelId = "seeded-model",
                        InputTokens = 1000,
                        OutputTokens = 200,
                        EstimatedUsd = 0.012,
                    },
                ],
            };
        }
        return fake;
    }

    [Fact]
    public void Statistics_SeededData_RendersQuotaWorkersAndCosts()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(SeededStatisticsFake());

        var cut = Render<StatisticsPage>();

        Assert.Contains("Agent quota", cut.Markup);
        Assert.Contains("seeded-fake", cut.Markup);
        Assert.Contains("unknown", cut.Markup);
        Assert.Contains("Token usage", cut.Markup);
        Assert.Contains("seeded-model", cut.Markup);
        Assert.Contains("Last 5 completed", cut.Markup);
        Assert.Contains("Seeded Done item A", cut.Markup);
    }

    [Fact]
    public void Statistics_NoData_ShowsEmptyMessages()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient([]));

        var cut = Render<StatisticsPage>();

        Assert.Contains("No quota data.", cut.Markup);
        Assert.Contains("No concurrency data.", cut.Markup);
        Assert.Contains("No cost records in the last 7 days.", cut.Markup);
    }

    [Fact]
    public void Statistics_Failure_ShowsErrorBanner()
    {
        var fake = new FakeApiClient([]);
        fake.QuotaFailure = new InvalidOperationException("seeded quota boom");
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<StatisticsPage>();

        Assert.Contains("Failed to load statistics", cut.Markup);
    }

    // ── Plugins ───────────────────────────────────────────────────────────

    [Fact]
    public void Plugins_WithPlugins_RendersTableAndSnippet()
    {
        var fake = new FakeApiClient([]);
        fake.PluginsOverride =
        [
            new PluginDto("seeded-size-limits", "Seeded Size Limits"),
            new PluginDto("seeded-stats", "Seeded Statistics"),
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<PluginsPage>();

        Assert.Contains("seeded-size-limits", cut.Markup);
        Assert.Contains("Seeded Statistics", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent.Contains("Add to project")).Click();

        Assert.Contains("\"PluginId\": \"seeded-size-limits\"", cut.Markup);
    }

    [Fact]
    public void Plugins_Empty_ShowsEmptyMessage()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient([]));

        var cut = Render<PluginsPage>();

        Assert.Contains("No auditor plugins are currently loaded", cut.Markup);
    }

    [Fact]
    public void Plugins_Failure_ShowsError()
    {
        var fake = new FakeApiClient([]);
        fake.PluginsFailure = new InvalidOperationException("seeded plugin boom");
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<PluginsPage>();

        Assert.Contains("Error loading plugins", cut.Markup);
        Assert.Contains("seeded plugin boom", cut.Markup);
    }

    // ── Suggestions dismiss ───────────────────────────────────────────────

    [Fact]
    public void Suggestions_DismissOne_ReloadsWithoutError()
    {
        var fake = new FakeApiClient([]);
        fake.SuggestionsOverride =
        [
            new SuggestionDto
            {
                Id = "seed-sugg-1",
                SourceWorkItemId = "11111111-0000-0000-0000-000000000001",
                ProjectId = "seeded-shop",
                Title = "Seeded follow-up",
                Category = "usability",
                Severity = "notable",
                CreatedAt = Now,
            },
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<SuggestionsPage>();
        Assert.Contains("Seeded follow-up", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent == "dismiss").Click();

        Assert.DoesNotContain("error-banner", cut.Markup);
        Assert.Contains("Seeded follow-up", cut.Markup);
    }

    // ── Work-item detail actions ──────────────────────────────────────────

    [Fact]
    public void WorkItemDetail_SubmitAnswer_CallsApiWithAnswer()
    {
        var item = SeededItem("22222222-0000-0000-0000-000000000001", "Seeded NeedsInput item", "NeedsOperatorInput");
        var fake = new FakeApiClient([item]);
        fake.QuestionsOverride[item.Id] =
        [
            new QuestionDto
            {
                Id = "q1",
                WorkItemId = item.Id,
                QuestionId = "q-1",
                QuestionText = "Which branch?",
                AskedAt = Now,
            },
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));
        cut.Find("textarea").Change("seeded answer");
        cut.FindAll("button").First(b => b.TextContent == "Submit").Click();

        Assert.Equal(1, fake.AnswerQuestionCallCount);
        Assert.Equal((item.Id, "q-1", "seeded answer"), fake.LastAnswerCall);
    }

    [Fact]
    public void WorkItemDetail_DismissQuestion_CallsApi()
    {
        var item = SeededItem("22222222-0000-0000-0000-000000000002", "Seeded NeedsInput item", "NeedsOperatorInput");
        var fake = new FakeApiClient([item]);
        fake.QuestionsOverride[item.Id] =
        [
            new QuestionDto
            {
                Id = "q1",
                WorkItemId = item.Id,
                QuestionId = "q-1",
                QuestionText = "Which branch?",
                AskedAt = Now,
            },
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));
        cut.FindAll("button").First(b => b.TextContent == "Dismiss").Click();

        Assert.Equal(1, fake.DismissQuestionCallCount);
    }

    [Fact]
    public async Task WorkItemDetail_ReplayTerminal_CreatesReplayItem()
    {
        var item = SeededItem("22222222-0000-0000-0000-000000000003", "Seeded Failed item", "Failed");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));
        cut.FindAll("button").First(b => b.TextContent == "Replay").Click();
        cut.FindAll("button").First(b => b.TextContent == "Create replay").Click();

        var items = await fake.GetWorkItemsAsync();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.ReplayOfWorkItemId == item.Id && i.State == "Queued");
    }
}
