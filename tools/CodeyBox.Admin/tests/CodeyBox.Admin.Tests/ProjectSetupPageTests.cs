using Bunit;
using CodeyBox.Admin.Web.Components.Pages;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Project setup page: the wizard as a form — invalid answers explain
/// themselves, valid answers produce the paste-ready snippet.
/// </summary>
public sealed class ProjectSetupPageTests : BunitContext
{
    [Fact]
    public void SetupPage_ValidAnswers_ProduceSnippet()
    {
        var cut = Render<ProjectSetup>();

        cut.Find("input#setup-id").Change("my-app");
        cut.Find("input#setup-name").Change("My App");
        cut.Find("input#setup-url").Change("https://github.com/me/my-app.git");
        cut.Find("select#setup-upstream").Change("github");
        cut.Find("input#setup-owner").Change("me");
        cut.Find("input#setup-repo").Change("my-app");
        cut.Find("input#setup-lang-node").Change(true);
        cut.Find("button#setup-generate").Click();

        var snippet = cut.Find("textarea#setup-json").TextContent;
        Assert.Contains("\"Id\": \"my-app\"", snippet);
        Assert.Contains("\"GitHubOwner\": \"me\"", snippet);
        Assert.Contains("\"node\"", snippet);
    }

    [Fact]
    public void SetupPage_InvalidId_ExplainsItselfWithoutSnippet()
    {
        var cut = Render<ProjectSetup>();

        cut.Find("input#setup-id").Change("bad id!");
        cut.Find("input#setup-name").Change("My App");
        cut.Find("input#setup-url").Change("https://github.com/me/my-app.git");
        cut.Find("button#setup-generate").Click();

        Assert.Contains("Project ID must be", cut.Find("#setup-errors").TextContent);
        Assert.Throws<Bunit.ElementNotFoundException>(() => cut.Find("textarea#setup-json"));
    }
}
