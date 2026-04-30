using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PipelineRunner.ResolveGitIdentity"/> precedence:
///   1. Project.GitAuthorName / GitAuthorEmail (if both set)
///   2. HostGitIdentity (if available)
///   3. Synthetic fallback ("CodeyBox" / "codeybox@local")
/// </summary>
public sealed class GitIdentityResolutionTests
{
    private static Project MakeProject(string? name = null, string? email = null) => new()
    {
        Id = new ProjectId("test"),
        DisplayName = "Test",
        RepositoryUrl = "https://example.com/repo.git",
        GitAuthorName = name,
        GitAuthorEmail = email,
    };

    // ─── Precedence 1: project override ──────────────────────────────────────

    [Fact]
    public void ProjectOverride_TakesPrecedenceOverHost()
    {
        var project = MakeProject("Project Author", "project@example.com");
        var host = new HostGitIdentity("Host Author", "host@example.com");

        var (name, email) = PipelineRunner.ResolveGitIdentity(project, host);

        Assert.Equal("Project Author", name);
        Assert.Equal("project@example.com", email);
    }

    [Fact]
    public void ProjectOverride_TakesPrecedenceOverNull()
    {
        var project = MakeProject("Project Author", "project@example.com");

        var (name, email) = PipelineRunner.ResolveGitIdentity(project, host: null);

        Assert.Equal("Project Author", name);
        Assert.Equal("project@example.com", email);
    }

    [Fact]
    public void ProjectOverride_OnlyName_DoesNotApply_FallsBackToHost()
    {
        // Both fields must be set for the project override to activate.
        var project = MakeProject(name: "Project Author", email: null);
        var host = new HostGitIdentity("Host Author", "host@example.com");

        var (name, email) = PipelineRunner.ResolveGitIdentity(project, host);

        Assert.Equal("Host Author", name);
        Assert.Equal("host@example.com", email);
    }

    [Fact]
    public void ProjectOverride_OnlyEmail_DoesNotApply_FallsBackToHost()
    {
        var project = MakeProject(name: null, email: "project@example.com");
        var host = new HostGitIdentity("Host Author", "host@example.com");

        var (name, email) = PipelineRunner.ResolveGitIdentity(project, host);

        Assert.Equal("Host Author", name);
        Assert.Equal("host@example.com", email);
    }

    [Fact]
    public void ProjectOverride_WhitespaceOnly_DoesNotApply()
    {
        var project = MakeProject(name: "   ", email: "   ");
        var host = new HostGitIdentity("Host Author", "host@example.com");

        var (name, email) = PipelineRunner.ResolveGitIdentity(project, host);

        Assert.Equal("Host Author", name);
        Assert.Equal("host@example.com", email);
    }

    // ─── Precedence 2: host identity ─────────────────────────────────────────

    [Fact]
    public void HostIdentity_UsedWhenNoProjectOverride()
    {
        var project = MakeProject();
        var host = new HostGitIdentity("Host Author", "host@example.com");

        var (name, email) = PipelineRunner.ResolveGitIdentity(project, host);

        Assert.Equal("Host Author", name);
        Assert.Equal("host@example.com", email);
    }

    // ─── Precedence 3: synthetic fallback ────────────────────────────────────

    [Fact]
    public void Fallback_WhenNoProjectAndNoHost()
    {
        var project = MakeProject();

        var (name, email) = PipelineRunner.ResolveGitIdentity(project, host: null);

        Assert.Equal("CodeyBox", name);
        Assert.Equal("codeybox@local", email);
    }

    // ─── Co-Authored-By trailer ───────────────────────────────────────────────

    [Fact]
    public void CoAuthoredByTrailer_HasExpectedForm()
    {
        Assert.Equal("\n\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>",
            PipelineRunner.CoAuthoredByTrailer);
    }

    [Fact]
    public void CoAuthoredByTrailer_AppendedToInitialCommitMessage()
    {
        const string title = "Add JSON config support";
        var message = $"codeybox: {title}{PipelineRunner.CoAuthoredByTrailer}";

        Assert.StartsWith($"codeybox: {title}", message);
        Assert.Contains("Co-Authored-By: CodeyBox <noreply@codeybox.invalid>", message);
        // Trailer must be separated by a blank line.
        Assert.Contains("\n\nCo-Authored-By:", message);
    }

    [Fact]
    public void CoAuthoredByTrailer_AppendedToReworkCommitMessage()
    {
        var message = $"codeybox rework: address audit findings{PipelineRunner.CoAuthoredByTrailer}";

        Assert.StartsWith("codeybox rework:", message);
        Assert.Contains("Co-Authored-By: CodeyBox <noreply@codeybox.invalid>", message);
        Assert.Contains("\n\nCo-Authored-By:", message);
    }
}
