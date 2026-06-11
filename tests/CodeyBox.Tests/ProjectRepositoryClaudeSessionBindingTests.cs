using Microsoft.Extensions.Options;
using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the binding from <see cref="ProjectClaudeSessionConfigOptions"/> on
/// the configuration shape into <see cref="Project.ClaudeSession"/> resolved
/// by <see cref="ProjectRepository.Resolve"/>. The resolved value is one of
/// the gates the session worker uses to decide whether a work item takes
/// the resumable-session path (see
/// <c>PipelineRunner.ShouldEnterClaudeSessionMode</c>); a binding bug that
/// always left the flag false would silently disable session mode for every
/// project even when the operator explicitly opted in.
/// </summary>
public sealed class ProjectRepositoryClaudeSessionBindingTests
{
    [Fact]
    public async Task ClaudeSession_Enabled_BindsThroughToResolvedProject()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new()
                {
                    Id = "p",
                    RepositoryUrl = "https://example.com/p.git",
                    ClaudeSession = new ProjectClaudeSessionConfigOptions { Enabled = true },
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("p"));
        Assert.NotNull(p);
        Assert.True(p!.ClaudeSession.Enabled);
    }

    [Fact]
    public async Task ClaudeSession_Disabled_BindsThroughToResolvedProject()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new()
                {
                    Id = "p",
                    RepositoryUrl = "https://example.com/p.git",
                    ClaudeSession = new ProjectClaudeSessionConfigOptions { Enabled = false },
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("p"));
        Assert.NotNull(p);
        Assert.False(p!.ClaudeSession.Enabled);
    }

    [Fact]
    public async Task ClaudeSession_Omitted_DefaultsToDisabled()
    {
        // A project that didn't opt in at all must surface as Enabled=false
        // on the resolved Project — the per-project opt-in is one of the
        // three session-mode gates and "unset = off" is the safe default.
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new()
                {
                    Id = "p",
                    RepositoryUrl = "https://example.com/p.git",
                    // ClaudeSession property left null.
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("p"));
        Assert.NotNull(p);
        Assert.False(p!.ClaudeSession.Enabled);
    }

    [Fact]
    public async Task ClaudeSession_EnabledNull_DefaultsToDisabled()
    {
        // The ClaudeSession sub-section was present but the Enabled field
        // wasn't set explicitly. Same "unset = off" contract.
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new()
                {
                    Id = "p",
                    RepositoryUrl = "https://example.com/p.git",
                    ClaudeSession = new ProjectClaudeSessionConfigOptions(),
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("p"));
        Assert.NotNull(p);
        Assert.False(p!.ClaudeSession.Enabled);
    }
}
