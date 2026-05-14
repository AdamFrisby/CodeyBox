using System.Net;
using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.UpstreamWebhooksAndReleases;

/// <summary>
/// UAT coverage for "Pull request descriptions and templates - Builds static
/// or LLM-generated PR bodies".
/// Plan anchor: docs/uat/00-plan.md#upstream-webhooks-and-releases
/// </summary>
public sealed class PullRequestDescriptionsAndTemplatesUatTests
{
    [Fact]
    public async Task LlmGeneratorPrompt_IncludesDiffFindingsAgentTailAndTruncatesLargeDiff()
    {
        var agent = new CapturingAgentRunner();
        var generator = new LlmPullRequestDescriptionGenerator(
            new NullSandboxProvider(),
            new AgentRegistry([agent]),
            new StaticCredentialProvider(),
            new PrDescriptionOptions
            {
                SandboxImageReference = "uat-image",
                MaxDiffBytes = 120,
                AgentAllowedHosts = ["api.example.invalid"],
            },
            NullLogger<LlmPullRequestDescriptionGenerator>.Instance);

        var generated = await generator.GenerateAsync(new PullRequestDescriptionRequest
        {
            Title = "Add release notes",
            Prompt = "Update the changelog without leaking sk_test_123456.",
            DiffSummary = "CHANGELOG.md | 10 ++++++++++",
            FullDiff = "diff --git a/CHANGELOG.md b/CHANGELOG.md\n" + new string('a', 800) + "\n+done",
            AddressedFindings = ["Document release workflow"],
            AgentReasoningTail = "Completed the changelog update.",
        }, CancellationToken.None);

        Assert.Equal("Generated PR body", generated);
        Assert.NotNull(agent.Prompt);
        Assert.Contains("Add release notes", agent.Prompt);
        Assert.Contains("CHANGELOG.md | 10 ++++++++++", agent.Prompt);
        Assert.Contains("Document release workflow", agent.Prompt);
        Assert.Contains("Completed the changelog update.", agent.Prompt);
        Assert.Contains("bytes truncated", agent.Prompt);
        Assert.DoesNotContain("sk_test_123456", agent.Prompt);
    }

    [Fact]
    public async Task GitHubRemote_UsesGeneratedDescriptionWhenEnabledAndAppendsFooter()
    {
        var handler = new SequenceHttpMessageHandler();
        handler.Enqueue(UpstreamWebhooksAndReleasesHelpers.Json(
            HttpStatusCode.Created,
            """{"number":21,"html_url":"https://github.com/owner/repo/pull/21"}"""));
        var generator = new StubPrDescriptionGenerator((_, _) => Task.FromResult("Generated markdown body"));
        var remote = UpstreamWebhooksAndReleasesHelpers.GitHubRemote(
            new CapturingGitHost(),
            handler,
            UpstreamWebhooksAndReleasesHelpers.GitHubOptions() with
            {
                PrDescription = new PrDescriptionOptions { Enabled = true },
            },
            generator);

        await remote.CompleteAsync(UpstreamWebhooksAndReleasesHelpers.Request());

        Assert.Single(generator.Requests);
        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var prBody = body.RootElement.GetProperty("body").GetString();
        Assert.Contains("Generated markdown body", prBody);
        Assert.Contains("Co-Authored-By: CodeyBox <noreply@codeybox.invalid>", prBody);
    }

    [Fact]
    public async Task GitHubRemote_WhenGeneratorThrows_FallsBackToStaticDescription()
    {
        var handler = new SequenceHttpMessageHandler();
        handler.Enqueue(UpstreamWebhooksAndReleasesHelpers.Json(
            HttpStatusCode.Created,
            """{"number":22,"html_url":"https://github.com/owner/repo/pull/22"}"""));
        var generator = new StubPrDescriptionGenerator((_, _) =>
            throw new InvalidOperationException("generator unavailable"));
        var remote = UpstreamWebhooksAndReleasesHelpers.GitHubRemote(
            new CapturingGitHost(),
            handler,
            UpstreamWebhooksAndReleasesHelpers.GitHubOptions(),
            generator);

        await remote.CompleteAsync(UpstreamWebhooksAndReleasesHelpers.Request());

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var prBody = body.RootElement.GetProperty("body").GetString();
        Assert.Contains("Static PR body from CodeyBox", prBody);
        Assert.DoesNotContain("generator unavailable", prBody);
    }

    [Fact]
    public async Task GitHubRemote_WhenGeneratorTimeoutTokenCancels_FallsBackToStaticDescription()
    {
        var handler = new SequenceHttpMessageHandler();
        handler.Enqueue(UpstreamWebhooksAndReleasesHelpers.Json(
            HttpStatusCode.Created,
            """{"number":23,"html_url":"https://github.com/owner/repo/pull/23"}"""));
        var generator = new StubPrDescriptionGenerator((_, _) =>
            throw new OperationCanceledException("generator deadline exceeded"));
        var remote = UpstreamWebhooksAndReleasesHelpers.GitHubRemote(
            new CapturingGitHost(),
            handler,
            UpstreamWebhooksAndReleasesHelpers.GitHubOptions() with
            {
                PrDescription = new PrDescriptionOptions
                {
                    Enabled = true,
                    Timeout = TimeSpan.Zero,
                },
            },
            generator);

        await remote.CompleteAsync(UpstreamWebhooksAndReleasesHelpers.Request());

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var prBody = body.RootElement.GetProperty("body").GetString();
        Assert.Contains("Static PR body from CodeyBox", prBody);
        Assert.DoesNotContain("should not be used", prBody);
    }
}
