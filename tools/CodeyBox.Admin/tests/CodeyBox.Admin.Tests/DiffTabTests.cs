using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using WorkItemDiffPage = CodeyBox.Admin.Web.Components.Pages.WorkItemDiff;

namespace CodeyBox.Admin.Tests;

public sealed class DiffTabTests : BunitContext
{
    private const string ItemId = "aabbccdd-0000-0000-0000-000000000001";

    public DiffTabTests()
    {
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    }

    private static WorkItemDiffDto MakeDiff(
        int filesChanged = 2,
        int linesAdded = 10,
        int linesRemoved = 3,
        bool truncated = false,
        string? diff = null)
    {
        diff ??= "diff --git a/foo.cs b/foo.cs\nindex abc..def 100644\n--- a/foo.cs\n+++ b/foo.cs\n@@ -1,1 +1,1 @@\n-old line\n+new line\n";
        return new WorkItemDiffDto
        {
            WorkItemId = ItemId,
            BaseBranch = "main",
            WorkBranch = "codeybox/aabbccdd",
            BaseCommitSha = "aaa000",
            WorkCommitSha = "bbb111",
            FilesChanged = filesChanged,
            LinesAdded = linesAdded,
            LinesRemoved = linesRemoved,
            Diff = diff,
            Truncated = truncated,
        };
    }

    [Fact]
    public void DiffTab_ShowsSummaryStats()
    {
        var fake = new FakeApiClient([]);
        fake.DiffOverride[ItemId] = MakeDiff(filesChanged: 3, linesAdded: 42, linesRemoved: 7);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemDiffPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("+42", cut.Markup);
        Assert.Contains("-7", cut.Markup);
        Assert.Contains("3 files", cut.Markup);
    }

    [Fact]
    public void DiffTab_ShowsFileList()
    {
        var fake = new FakeApiClient([]);
        fake.DiffOverride[ItemId] = MakeDiff();
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemDiffPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("foo.cs", cut.Markup);
        Assert.Contains("diff-file-list", cut.Markup);
    }

    [Fact]
    public void DiffTab_ShowsCopyAsPatchLink()
    {
        var fake = new FakeApiClient([]);
        fake.DiffOverride[ItemId] = MakeDiff();
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemDiffPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains($"/workitems/{ItemId}/diff", cut.Markup);
    }

    [Fact]
    public void DiffTab_TruncatedDiff_ShowsBanner()
    {
        var fake = new FakeApiClient([]);
        fake.DiffOverride[ItemId] = MakeDiff(truncated: true);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemDiffPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("truncated", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("diff-truncated-banner", cut.Markup);
    }

    [Fact]
    public void DiffTab_NoDiff_ShowsEmptyMessage()
    {
        var fake = new FakeApiClient([]);
        // No DiffOverride → returns null → no diff available.
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemDiffPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("diff-empty", cut.Markup);
        Assert.DoesNotContain("diff-toolbar", cut.Markup);
    }

    [Fact]
    public void DiffTab_ShowsLinkBackToWorkItem()
    {
        var fake = new FakeApiClient([]);
        fake.DiffOverride[ItemId] = MakeDiff();
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemDiffPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains($"/work-items/{ItemId}", cut.Markup);
    }

    [Fact]
    public void DiffTab_RendersDiffLines()
    {
        var fake = new FakeApiClient([]);
        fake.DiffOverride[ItemId] = MakeDiff();
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemDiffPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("diff-add", cut.Markup);
        Assert.Contains("diff-del", cut.Markup);
        Assert.Contains("diff-hunk", cut.Markup);
    }

    [Fact]
    public void DiffTab_SingleFile_UsesSingularLabel()
    {
        var fake = new FakeApiClient([]);
        fake.DiffOverride[ItemId] = MakeDiff(filesChanged: 1);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemDiffPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("1 file", cut.Markup);
        Assert.DoesNotContain("1 files", cut.Markup);
    }
}
