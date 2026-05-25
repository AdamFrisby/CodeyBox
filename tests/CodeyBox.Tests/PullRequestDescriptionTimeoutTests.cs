using System.Net;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that when the <see cref="IPullRequestDescriptionGenerator"/> does
/// not respond within <see cref="PrDescriptionOptions.Timeout"/>, the call is
/// cancelled and <see cref="GitHubUpstreamRemote.CompleteAsync"/> falls back
/// to the static template without blocking PR creation.
/// </summary>
public sealed class PullRequestDescriptionTimeoutTests
{
    private static readonly GitHubUpstreamOptions TimeoutOpts = new()
    {
        Owner = "myorg",
        Repository = "myrepo",
        Token = "test-token-not-a-real-pat",
        PrDescription = new PrDescriptionOptions
        {
            Enabled = true,
            // Extremely short timeout so tests do not take long.
            Timeout = TimeSpan.FromMilliseconds(50),
        },
    };

    private static readonly UpstreamCompletionRequest SampleRequest = new()
    {
        RepositoryId = "repo-id",
        WorkItemId = new WorkItemId(Guid.Parse("00000000-0000-0000-0000-000000000030")),
        ProjectId = new ProjectId("test-project"),
        WorkBranch = "codeybox/timeout1",
        BaseBranch = "main",
        MergeSha = "deadbeef",
        Title = "Test timeout",
        Description = "Static description — timeout fallback",
    };

    private static HttpResponseMessage PrCreatedResponse(int number, string htmlUrl) =>
        new(HttpStatusCode.Created)
        {
            Content = new StringContent(
                $$"""{"number":{{number}},"html_url":"{{htmlUrl}}"}""",
                Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task CompleteAsync_GeneratorExceedsTimeout_FallsBackToStaticAndSucceeds()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(1, "https://github.com/myorg/myrepo/pull/1"));

        var hangingGenerator = new HangingDescriptionGenerator();
        var gitHost = new FakeGitHost();
        var factory = new FakeHttpClientFactory(handler, userAgent: "codeybox");
        var remote = new GitHubUpstreamRemote(
            gitHost, factory, NullLogger<GitHubUpstreamRemote>.Instance,
            TimeoutOpts, timings: null, descriptionGenerator: hangingGenerator);

        // Must not hang indefinitely — the timeout cancels the generator.
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30)); // Guard so the test itself doesn't hang.

        Assert.True(outcome.BranchPushed);
        Assert.NotNull(outcome.PullRequestUrl);
    }

    [Fact]
    public async Task CompleteAsync_GeneratorExceedsTimeout_UsesStaticTemplate()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(2, "https://github.com/myorg/myrepo/pull/2"));

        var gitHost = new FakeGitHost();
        var factory = new FakeHttpClientFactory(handler, userAgent: "codeybox");
        var remote = new GitHubUpstreamRemote(
            gitHost, factory, NullLogger<GitHubUpstreamRemote>.Instance,
            TimeoutOpts, timings: null, descriptionGenerator: new HangingDescriptionGenerator());

        await remote.CompleteAsync(SampleRequest, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(handler.RequestBodies);
        Assert.Contains("Static description", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task CompleteAsync_GeneratorExceedsTimeout_FooterStillPresent()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(3, "https://github.com/myorg/myrepo/pull/3"));

        var gitHost = new FakeGitHost();
        var factory = new FakeHttpClientFactory(handler, userAgent: "codeybox");
        var remote = new GitHubUpstreamRemote(
            gitHost, factory, NullLogger<GitHubUpstreamRemote>.Instance,
            TimeoutOpts, timings: null, descriptionGenerator: new HangingDescriptionGenerator());

        await remote.CompleteAsync(SampleRequest, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Co-Authored-By: CodeyBox", handler.RequestBodies[0]);
    }
}

/// <summary>
/// Generator that waits indefinitely until its cancellation token fires,
/// then throws <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed class HangingDescriptionGenerator : IPullRequestDescriptionGenerator
{
    public async Task<string> GenerateAsync(PullRequestDescriptionRequest request, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return string.Empty; // unreachable
    }
}
