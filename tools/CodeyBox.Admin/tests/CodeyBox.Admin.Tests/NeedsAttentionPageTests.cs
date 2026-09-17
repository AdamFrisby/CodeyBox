using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using NeedsAttentionPage = CodeyBox.Admin.Web.Components.Pages.NeedsAttention;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// The "what needs me?" queue: covered states appear with inline evidence,
/// zero-findings parks are labelled, infra reads differently from rejection,
/// the repeat/shrink signal shows, cancel warns with dependant names, and
/// every inline action issues the same API call the detail page would.
/// </summary>
public sealed class NeedsAttentionPageTests : BunitContext
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private static WorkItemDto MakeItem(
        string id,
        string state,
        string? failureKind = null,
        string? lastError = null) => new()
        {
            Id = id,
            ProjectId = "proj",
            Title = $"Work {id}",
            State = state,
            Agent = "Claude",
            CreatedAt = T0.AddHours(-3),
            UpdatedAt = T0.AddMinutes(-10),
            FailureKind = failureKind,
            LastError = lastError,
        };

    private static AuditReportsDto Reports(
        string id,
        int iteration,
        params (string Auditor, string Id, string Severity, string Title)[] findings) => new()
        {
            WorkItemId = id,
            Iterations =
            [
                new AuditReportIterationDto
                {
                    Target = "code",
                    Iteration = iteration,
                    BlockingCount = findings.Count(f => f.Severity == "Error"),
                    NonBlockingCount = findings.Count(f => f.Severity != "Error"),
                    Auditors = findings
                        .GroupBy(f => f.Auditor)
                        .Select(g => new AuditReportAuditorDto
                        {
                            Name = g.Key,
                            Kind = "test",
                            WorstSeverity = "error",
                            Findings = g.Select(f => new AuditReportFindingDto
                            {
                                Id = f.Id,
                                Severity = f.Severity,
                                Title = f.Title,
                            }).ToList(),
                        }).ToList(),
                },
            ],
        };

    private static AuditProgressListDto Progress(string id, params (int Iter, string[] Ids)[] rows) => new()
    {
        WorkItemId = id,
        Progress = rows.Select((r, i) => new AuditProgressRowDto
        {
            Id = $"row-{r.Iter}",
            WorkAttemptKey = "",
            Iteration = r.Iter,
            MaxIterations = 5,
            Status = "complete",
            BlockingFindings = r.Ids.Length,
            BlockingFindingIds = [.. r.Ids],
            RecordedAt = T0.AddMinutes(10 * (i + 1)),
        }).ToList(),
    };

    private static FakeApiClient SeededClient(params WorkItemDto[] items) =>
        new([.. items]);

    private static AngleSharp.Dom.IElement ButtonWithText(
        IRenderedComponent<NeedsAttentionPage> cut, string text) =>
        cut.FindAll("button").First(b => string.Equals(b.TextContent.Trim(), text, StringComparison.Ordinal));

    [Fact]
    public void EmptyQueue_ReadsAsReassuring_NotBroken()
    {
        var fake = SeededClient(MakeItem("a", "Done"), MakeItem("b", "Queued"));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NeedsAttentionPage>();

        Assert.Contains("All clear", cut.Markup);
        Assert.Contains("nothing needs you", cut.Markup);
        Assert.DoesNotContain("error-banner", cut.Markup);
        Assert.DoesNotContain("needs-card", cut.Markup);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("AuditFailed")]
    [InlineData("MergeConflictResolutionFailed")]
    [InlineData("AbandonedAfterRecoveryAttempts")]
    [InlineData("NeedsOperatorInput")]
    [InlineData("WaitingForQuotaReset")]
    [InlineData("WaitingForAgentResume")]
    [InlineData("WaitingForTransientRetry")]
    public void EveryCoveredState_AppearsWithEvidence(string state)
    {
        var fake = SeededClient(MakeItem("x", state, lastError: "boom"));
        fake.AuditReportsByItem["x"] = Reports("x", 1, ("security", "f1", "Error", "SQLi"));
        fake.AuditProgressOverride["x"] = Progress("x", (1, ["f1"]));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NeedsAttentionPage>();

        Assert.Contains("Work x", cut.Markup);
        Assert.Contains("security", cut.Markup);
        Assert.Contains("SQLi", cut.Markup);
        Assert.Contains("1 attempt(s)", cut.Markup);
    }

    [Fact]
    public void Queue_IsOrderedByAttentionScore_FailedBeforeParked()
    {
        var fake = SeededClient(
            MakeItem("parked", "NeedsOperatorInput"),
            MakeItem("failed", "Failed", failureKind: "infrastructure"));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NeedsAttentionPage>();
        var markup = cut.Markup;

        Assert.True(
            markup.IndexOf("Work failed", StringComparison.Ordinal)
            < markup.IndexOf("Work parked", StringComparison.Ordinal),
            "Failed should order before NeedsOperatorInput.");
    }

    [Fact]
    public void ZeroBlockingFindings_IsLabelledExplicitly()
    {
        var fake = SeededClient(MakeItem("x", "NeedsOperatorInput"));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NeedsAttentionPage>();

        Assert.Contains("Parked with no blocking findings", cut.Markup);
    }

    [Fact]
    public void InfrastructureFailure_ReadsDifferentlyFromRejectedChange()
    {
        var fake = SeededClient(
            MakeItem("infra", "Failed", failureKind: "infrastructure"),
            MakeItem("rejected", "AuditFailed"));
        fake.AuditReportsByItem["rejected"] = Reports("rejected", 2, ("tests", "f1", "Error", "Red suite"));
        fake.AuditProgressOverride["rejected"] = Progress("rejected", (1, ["f1"]), (2, ["f1"]));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NeedsAttentionPage>();

        Assert.Contains("Infrastructure failure", cut.Markup);
        Assert.Contains("Rejected change", cut.Markup);
    }

    [Fact]
    public void RepeatingFindings_SignalDisplayed()
    {
        var fake = SeededClient(MakeItem("x", "AuditFailed"));
        fake.AuditReportsByItem["x"] = Reports("x", 2, ("tests", "f1", "Error", "Red suite"));
        fake.AuditProgressOverride["x"] = Progress("x", (1, ["f1"]), (2, ["f1"]));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NeedsAttentionPage>();

        Assert.Contains("will not help", cut.Markup);
    }

    [Fact]
    public void ShrinkingFindings_SignalDisplayed()
    {
        var fake = SeededClient(MakeItem("x", "AuditFailed"));
        fake.AuditReportsByItem["x"] = Reports("x", 3, ("tests", "f1", "Error", "Red suite"));
        fake.AuditProgressOverride["x"] = Progress("x", (1, ["f1", "f2", "f3"]), (2, ["f1", "f2"]), (3, ["f1"]));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NeedsAttentionPage>();

        Assert.Contains("shrinking", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancelWithDependants_WarnsNamingThemBeforeProceeding()
    {
        var fake = SeededClient(MakeItem("parent", "Failed"));
        fake.DependentsOverride["parent"] = [MakeItem("child-1", "Queued"), MakeItem("child-2", "Queued")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NeedsAttentionPage>();

        // The consequence is visible inline before any action is taken.
        Assert.Contains("child-1", cut.Markup);
        Assert.Contains("child-2", cut.Markup);

        // And again in the confirmation, which names them explicitly.
        ButtonWithText(cut, "Cancel").Click();
        Assert.Contains("child-1", cut.Markup);
        Assert.Contains("child-2", cut.Markup);
        Assert.Contains("2 dependants", cut.Markup);

        ButtonWithText(cut, "Cancel item").Click();
        Assert.Contains("parent", fake.CancelledIds);
    }

    [Fact]
    public void CancelWithoutDependants_SaysSo()
    {
        var fake = SeededClient(MakeItem("lonely", "Failed"));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NeedsAttentionPage>();
        ButtonWithText(cut, "Cancel").Click();

        Assert.Contains("strands nothing", cut.Markup);
    }

    [Fact]
    public void RetryAction_IssuesTheSameApiCallAsTheDetailPage()
    {
        var fake = SeededClient(MakeItem("x", "Failed", failureKind: "infrastructure"));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NeedsAttentionPage>();
        ButtonWithText(cut, "Retry").Click();

        Assert.Contains("x", fake.RetriedIds);
        Assert.Null(fake.RetriedFrom.Last());
    }

    [Fact]
    public void ConflictItem_RetryRunsTheResolutionPathFromMerge()
    {
        var fake = SeededClient(MakeItem("c", "MergeConflictResolutionFailed"));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NeedsAttentionPage>();

        Assert.Contains("Resolution path", cut.Markup);
        ButtonWithText(cut, "Retry from merge").Click();

        Assert.Contains("c", fake.RetriedIds);
        Assert.Equal("merge", fake.RetriedFrom.Last());
    }

    [Fact]
    public void DelegateAction_IssuesTheSameApiCallAsTheDetailPage()
    {
        var fake = SeededClient(MakeItem("x", "AuditFailed"));
        fake.AuditReportsByItem["x"] = Reports("x", 1, ("tests", "f1", "Error", "Red suite"));
        fake.AuditProgressOverride["x"] = Progress("x", (1, ["f1"]));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<NeedsAttentionPage>();
        ButtonWithText(cut, "Delegate to one-shot").Click();
        ButtonWithText(cut, "Delegate").Click();

        var call = Assert.Single(fake.DelegatedCalls);
        Assert.Equal("x", call.Id);
    }
}
