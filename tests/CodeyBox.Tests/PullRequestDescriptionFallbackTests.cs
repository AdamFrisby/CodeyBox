using System.Net;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that when <see cref="IPullRequestDescriptionGenerator"/> throws,
/// <see cref="GitHubUpstreamRemote.CompleteAsync"/> falls back to the static
/// description from <see cref="UpstreamCompletionRequest.Description"/> and
/// still successfully creates the pull request.
/// </summary>
public sealed class PullRequestDescriptionFallbackTests
{
    private static readonly GitHubUpstreamOptions DefaultOpts = new()
    {
        Owner = "myorg",
        Repository = "myrepo",
        Token = "test-token-not-a-real-pat",
        MergeMethod = "merge",
        AutoMerge = false,
        PrDescription = new PrDescriptionOptions { Enabled = true, Timeout = TimeSpan.FromSeconds(30) },
    };

    private static readonly UpstreamCompletionRequest SampleRequest = new()
    {
        RepositoryId = "repo-id",
        WorkItemId = new WorkItemId(Guid.Parse("00000000-0000-0000-0000-000000000010")),
        ProjectId = new ProjectId("test-project"),
        WorkBranch = "codeybox/fallback1",
        BaseBranch = "main",
        MergeSha = "deadbeef",
        Title = "Add feature",
        Description = "Static fallback description",
    };

    private static HttpResponseMessage PrCreatedResponse(int number, string htmlUrl) =>
        new(HttpStatusCode.Created)
        {
            Content = new StringContent(
                $$"""{"number":{{number}},"html_url":"{{htmlUrl}}"}""",
                Encoding.UTF8, "application/json"),
        };

    private static GitHubUpstreamRemote BuildRemote(
        FakeHttpMessageHandler handler,
        IPullRequestDescriptionGenerator? generator = null)
    {
        var gitHost = new FakeGitHost();
        var factory = new FakeHttpClientFactory(handler, userAgent: "codeybox");
        return new GitHubUpstreamRemote(
            gitHost, factory, NullLogger<GitHubUpstreamRemote>.Instance,
            DefaultOpts, timings: null, descriptionGenerator: generator);
    }

    [Fact]
    public async Task CompleteAsync_GeneratorThrows_FallsBackToStaticDescription()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(1, "https://github.com/myorg/myrepo/pull/1"));

        var throwingGenerator = new ThrowingDescriptionGenerator();
        var remote = BuildRemote(handler, throwingGenerator);

        // Must not throw — fallback is silent.
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.True(outcome.BranchPushed);
        Assert.Equal("https://github.com/myorg/myrepo/pull/1", outcome.PullRequestUrl);
        // The PR body sent to GitHub should contain the static fallback text.
        Assert.Single(handler.RequestBodies);
        Assert.Contains("Static fallback description", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task CompleteAsync_GeneratorThrows_PrCreationSucceeds()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(2, "https://github.com/myorg/myrepo/pull/2"));

        var remote = BuildRemote(handler, new ThrowingDescriptionGenerator());
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        // PR was still opened despite generator failure.
        Assert.NotNull(outcome.PullRequestUrl);
        Assert.Equal(2, outcome.PullRequestNumber);
    }

    [Fact]
    public async Task CompleteAsync_FooterAlwaysPresentEvenOnFallback()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(3, "https://github.com/myorg/myrepo/pull/3"));

        var remote = BuildRemote(handler, new ThrowingDescriptionGenerator());
        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.Single(handler.RequestBodies);
        Assert.Contains("Co-Authored-By: CodeyBox", handler.RequestBodies[0]);
    }
}

internal sealed class ThrowingDescriptionGenerator : IPullRequestDescriptionGenerator
{
    public Task<string> GenerateAsync(PullRequestDescriptionRequest request, CancellationToken ct)
        => throw new InvalidOperationException("Simulated generator failure");
}
