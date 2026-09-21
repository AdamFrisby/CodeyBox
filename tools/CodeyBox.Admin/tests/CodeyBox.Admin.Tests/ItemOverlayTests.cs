using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Components.Shared;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// The detail overlay: header, actions by state, the "Now" reading, stages
/// from the timeline, lazily loaded findings, and confirmed mutations — all
/// without navigating away from the map.
/// </summary>
public sealed class ItemOverlayTests : BunitContext
{
    private static WorkItemDto Item(string id, string state, List<string>? dependsOn = null) => new()
    {
        Id = id,
        ProjectId = "proj-1",
        Title = $"Work {id}",
        Prompt = $"Prompt {id}",
        Agent = "codex",
        State = state,
        CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
        DependsOn = dependsOn ?? [],
        DependsOnSatisfied = dependsOn is null,
    };

    private IRenderedComponent<ItemOverlay> RenderOverlay(FakeApiClient fake, string id)
    {
        Services.AddSingleton<ICodeyBoxApiClient>(fake);
        JSInterop.Mode = JSRuntimeMode.Loose;
        return Render<ItemOverlay>(p => p.Add(o => o.Id, id));
    }

    [Fact]
    public void RendersHeaderNowStagesAndFullPageLinks()
    {
        var fake = new FakeApiClient([Item("a1", "Auditing"), Item("b1", "Queued", ["a1"])]);
        fake.DependentsOverride["a1"] = [Item("b1", "Queued", ["a1"])];
        var cut = RenderOverlay(fake, "a1");

        cut.WaitForAssertion(() => Assert.Contains("Work a1", cut.Markup));
        Assert.Contains("Auditing", cut.Markup);
        Assert.Contains("1 waiting on this", cut.Markup);
        Assert.Contains("Stages", cut.Markup);
        Assert.Contains("history unavailable", cut.Markup); // no timeline from the fake: honest, not assumed
        Assert.Contains("/work-items/a1/audit-reports", cut.Markup);
        Assert.Contains("+ Add dependent", cut.Markup);
        Assert.Contains("Cancel", cut.Markup);
        Assert.DoesNotContain("Retry from work", cut.Markup);
    }

    [Fact]
    public void FailedItem_OffersRetryAndDelegate_AndConfirmsBeforeActing()
    {
        var fake = new FakeApiClient([Item("f1", "AuditFailed")]);
        var cut = RenderOverlay(fake, "f1");
        cut.WaitForAssertion(() => Assert.Contains("Retry from audit", cut.Markup));
        Assert.Contains("Delegate a repair turn", cut.Markup);
        Assert.DoesNotContain("+ Add dependent", cut.Markup);

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Retry from audit").Click();
        Assert.Contains("Retry from audit?", cut.Markup);
        Assert.Empty(fake.RetriedIds);

        cut.FindAll("button").Where(b => b.TextContent.Trim() == "Retry from audit").Last().Click();
        cut.WaitForAssertion(() => Assert.Equal(["f1"], fake.RetriedIds));
        Assert.Equal(["audit"], fake.RetriedFrom);
    }

    [Fact]
    public void Findings_LoadWhenOpened_AndSayWhenThereAreNone()
    {
        var fake = new FakeApiClient([Item("a1", "Reworking")]);
        fake.AuditReportsByItem["a1"] = new AuditReportsDto
        {
            WorkItemId = "a1",
            Iterations =
            [
                new AuditReportIterationDto
                {
                    Iteration = 2, BlockingCount = 1, NonBlockingCount = 0,
                    Auditors =
                    [
                        new AuditReportAuditorDto
                        {
                            Name = "csharp:test-pass", WorstSeverity = "error", DurationMs = 4000,
                            Findings = [new AuditReportFindingDto { Id = "f-1", Severity = "error", Title = "Test failed: Foo", Files = ["tests/FooTests.cs"] }],
                        },
                    ],
                },
            ],
        };
        var cut = RenderOverlay(fake, "a1");
        cut.WaitForAssertion(() => Assert.Contains("Work a1", cut.Markup));

        cut.Find("details.fm-ov-lazy").TriggerEvent("ontoggle", new EventArgs());
        cut.WaitForAssertion(() => Assert.Contains("Test failed: Foo", cut.Markup));
        Assert.Contains("tests/FooTests.cs", cut.Markup);
        Assert.Contains("1 blocking", cut.Markup);
    }

    [Fact]
    public void Findings_WithNoReports_SaysSoInsteadOfAGreenSummary()
    {
        var quiet = RenderOverlay(new FakeApiClient([Item("z1", "Working")]), "z1");
        quiet.WaitForAssertion(() => Assert.Contains("Work z1", quiet.Markup));

        quiet.Find("details.fm-ov-lazy").TriggerEvent("ontoggle", new EventArgs());

        quiet.WaitForAssertion(() => Assert.Contains("No audit iteration has recorded findings", quiet.Markup));
    }

    [Fact]
    public void UnknownItem_SaysSo()
    {
        var cut = RenderOverlay(new FakeApiClient([]), "nope");
        cut.WaitForAssertion(() => Assert.Contains("Work item not found", cut.Markup));
    }
}
