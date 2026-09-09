using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Http;

namespace CodeyBox.Tests;

public sealed class WorkInitiatorTests
{
    private static readonly WorkInitiator GitHubInitiator = new()
    {
        Issuer = "jobtrack",
        Subject = "user-42",
        DisplayName = "Ada Lovelace",
        ProviderIdentities =
        [
            new WorkInitiatorProviderIdentity
            {
                Provider = "github",
                AccountId = "1234",
                Login = "ada",
            },
        ],
    };

    [Fact]
    public void ResolveGitIdentity_UsesLinkedGitHubNoreplyIdentity()
    {
        var project = new Project
        {
            Id = new ProjectId("test"),
            DisplayName = "Test",
            RepositoryUrl = "https://example.invalid/repo.git",
            GitAuthorName = "Project",
            GitAuthorEmail = "project@example.invalid",
        };

        var identity = PipelineRunner.ResolveGitIdentity(
            project, new HostGitIdentity("Host", "host@example.invalid"), GitHubInitiator);

        Assert.Equal(("Ada Lovelace", "1234+ada@users.noreply.github.com"), identity);
    }

    [Fact]
    public void ResolveInitiator_RejectsDelegationFromLegacyOperator()
    {
        var context = new DefaultHttpContext();
        context.Items[ApiKeyAuth.PrincipalItemKey] = new ApiClientPrincipal(
            "operator",
            new WorkInitiator
            {
                Issuer = "codeybox",
                Subject = "operator",
                DisplayName = "Operator",
            },
            CanDelegateInitiator: false);

        var result = ApiKeyAuth.ResolveInitiator(context, GitHubInitiator);

        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ResolveInitiator_AcceptsDelegationOnlyForTrustedClient()
    {
        var context = new DefaultHttpContext();
        context.Items[ApiKeyAuth.PrincipalItemKey] = new ApiClientPrincipal(
            "jobtrack",
            new WorkInitiator
            {
                Issuer = "jobtrack",
                Subject = "service",
                DisplayName = "JobTrack",
            },
            CanDelegateInitiator: true);

        var result = ApiKeyAuth.ResolveInitiator(context, GitHubInitiator);

        Assert.Null(result.Error);
        Assert.Same(GitHubInitiator, result.Value);
    }
}
