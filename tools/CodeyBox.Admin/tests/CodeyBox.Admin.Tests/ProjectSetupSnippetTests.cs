using System.Text.Json;
using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Setup snippet: the web mirror validates like the CLI wizard and emits
/// the identical JSON shape, so either surface pastes into
/// CodeyBox.Projects unchanged.
/// </summary>
public sealed class ProjectSetupSnippetTests
{
    private static ProjectSetupInput ValidInput() => new(
        ProjectId: "my-app",
        DisplayName: "My App",
        RepositoryUrl: "https://github.com/me/my-app.git",
        BaseBranch: "main",
        Agent: "claude",
        UpstreamKind: "github",
        GitHubOwner: "me",
        GitHubRepository: "my-app",
        GitHubTokenEnvVar: "MY_APP_GITHUB_TOKEN",
        Languages: ["node"],
        AuditTypes: ["security", "architecture", "quality"],
        PhaseProfiles: new Dictionary<string, string?>
        {
            ["Work"] = "claude",
            ["AuditTool"] = "isolated",
        });

    [Fact]
    public void Validate_ValidInput_HasNoErrors()
    {
        Assert.Empty(ProjectSetupSnippet.Validate(ValidInput()));
    }

    [Theory]
    [InlineData("bad id!")]
    [InlineData("")]
    [InlineData("way-too-long-012345678901234567890123456789012345678901234567890123456789")]
    public void Validate_BadProjectId_Fails(string id)
    {
        var errors = ProjectSetupSnippet.Validate(ValidInput() with { ProjectId = id });

        Assert.Contains(errors, e => e.StartsWith("Project ID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("-leading-dash")]
    [InlineData("a")]
    [InlineData("under_score-1")]
    public void Validate_WizardShapedProjectId_Passes(string id)
    {
        var errors = ProjectSetupSnippet.Validate(ValidInput() with { ProjectId = id });

        Assert.DoesNotContain(errors, e => e.StartsWith("Project ID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("feature..x", "must not contain '..'")]
    [InlineData("release.lock", "must not end with '.lock'")]
    [InlineData("-bad", "must start with a letter or digit")]
    [InlineData("", "must not be empty")]
    public void Validate_BadBranchName_FailsLikeWizard(string branch, string fragment)
    {
        var errors = ProjectSetupSnippet.Validate(ValidInput() with { BaseBranch = branch });

        Assert.Contains(errors, e => e.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_BadRepositoryUrl_Fails()
    {
        var errors = ProjectSetupSnippet.Validate(ValidInput() with { RepositoryUrl = "notaurl" });

        Assert.Contains(errors, e => e.StartsWith("Repository URL", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildJson_MatchesWizardShape()
    {
        var json = ProjectSetupSnippet.BuildJson(ValidInput());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("my-app", root.GetProperty("Id").GetString());
        Assert.Equal("My App", root.GetProperty("DisplayName").GetString());
        Assert.Equal("https://github.com/me/my-app.git", root.GetProperty("RepositoryUrl").GetString());
        Assert.Equal("main", root.GetProperty("BaseBranch").GetString());
        Assert.Equal("claude", root.GetProperty("Agent").GetString());

        var upstream = root.GetProperty("Upstream");
        Assert.Equal("github", upstream.GetProperty("Kind").GetString());
        Assert.Equal("me", upstream.GetProperty("GitHubOwner").GetString());
        Assert.Equal("my-app", upstream.GetProperty("GitHubRepository").GetString());
        Assert.Equal("MY_APP_GITHUB_TOKEN", upstream.GetProperty("TokenEnvVar").GetString());

        var audit = root.GetProperty("Audit");
        Assert.Equal(["node"], audit.GetProperty("Languages").EnumerateArray().Select(e => e.GetString()).ToList());
        Assert.Equal(
            ["security", "architecture", "quality"],
            audit.GetProperty("AuditTypes").EnumerateArray().Select(e => e.GetString()).ToList());

        var profiles = root.GetProperty("NetworkProfiles");
        Assert.Equal("claude", profiles.GetProperty("Work").GetString());
        Assert.Equal("isolated", profiles.GetProperty("AuditTool").GetString());
        Assert.False(profiles.TryGetProperty("Rework", out _));
    }

    [Fact]
    public void BuildJson_NoopUpstream_EncodesChoiceExplicitly()
    {
        var json = ProjectSetupSnippet.BuildJson(ValidInput() with { UpstreamKind = "noop" });
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("noop", doc.RootElement.GetProperty("Upstream").GetProperty("Kind").GetString());
    }

    [Fact]
    public void BuildJson_EmptyAuditAndProfiles_OmitsBlocks()
    {
        var json = ProjectSetupSnippet.BuildJson(
            ValidInput() with { Languages = [], AuditTypes = [], PhaseProfiles = new Dictionary<string, string?>() });
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("Audit", out _));
        Assert.False(doc.RootElement.TryGetProperty("NetworkProfiles", out _));
    }

    [Fact]
    public void BuildJson_BlankBaseBranch_DefaultsToMain()
    {
        var json = ProjectSetupSnippet.BuildJson(ValidInput() with { BaseBranch = "  " });
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("main", doc.RootElement.GetProperty("BaseBranch").GetString());
    }
}
