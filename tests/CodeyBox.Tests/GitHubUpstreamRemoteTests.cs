using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="GitHubUpstreamRemote.CompleteAsync"/>. Uses a
/// fake <see cref="HttpMessageHandler"/> so no real GitHub API is called.
/// </summary>
public sealed class GitHubUpstreamRemoteTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static readonly GitHubUpstreamOptions DefaultOpts = new()
    {
        Owner = "myorg",
        Repository = "myrepo",
        Token = "test-token-not-a-real-pat",
        MergeMethod = "merge",
        AutoMerge = false,
    };

    private static readonly UpstreamCompletionRequest SampleRequest = new()
    {
        RepositoryId = "repo-id",
        WorkItemId = new WorkItemId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
        ProjectId = new ProjectId("test-project"),
        WorkBranch = "codeybox/abc123",
        BaseBranch = "main",
        MergeSha = "deadbeef",
        Title = "Add feature X",
        Description = "Automated via CodeyBox",
    };

    private static GitHubUpstreamRemote BuildRemote(
        IGitHost gitHost,
        FakeHttpMessageHandler handler,
        GitHubUpstreamOptions? opts = null,
        IPullRequestDescriptionGenerator? descriptionGenerator = null)
    {
        opts ??= DefaultOpts;
        var factory = new FakeHttpClientFactory(handler, userAgent: "codeybox");
        return new GitHubUpstreamRemote(
            gitHost,
            factory,
            NullLogger<GitHubUpstreamRemote>.Instance,
            opts,
            descriptionGenerator: descriptionGenerator);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage PrCreatedResponse(int number, string htmlUrl) =>
        JsonResponse(HttpStatusCode.Created,
            $$"""{"number":{{number}},"html_url":"{{htmlUrl}}"}""");

    private static HttpResponseMessage PullRequestResponse(int number, string htmlUrl, string title, string body) =>
        JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            number,
            html_url = htmlUrl,
            title,
            body,
        }));

    private static HttpResponseMessage MergeOkResponse(string sha) =>
        JsonResponse(HttpStatusCode.OK,
            $$"""{"sha":"{{sha}}","merged":true,"message":"Pull Request successfully merged"}""");

    private static HttpResponseMessage PullRequestCommitsResponse(string json) =>
        JsonResponse(HttpStatusCode.OK, json);

    private static HttpResponseMessage PullRequestCommitsResponse(params string[] messages) =>
        PullRequestCommitsResponse(JsonSerializer.Serialize(
            messages.Select(message => new { commit = new { message } })));

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CompleteAsync_PrOnlyFlow_PushesWorkBranchAndOpensPr()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(42, "https://github.com/myorg/myrepo/pull/42"));

        var remote = BuildRemote(gitHost, handler, DefaultOpts with { AutoMerge = false });
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        // Work branch pushed
        Assert.Single(gitHost.Pushes);
        Assert.Equal(SampleRequest.WorkBranch, gitHost.Pushes[0].Branch);

        // POST /pulls called, not /merge
        Assert.Single(handler.Requests);
        Assert.Contains("/pulls", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.DoesNotContain("/merge", handler.Requests[0].RequestUri!.PathAndQuery);

        // Outcome
        Assert.True(outcome.BranchPushed);
        Assert.Equal("https://github.com/myorg/myrepo/pull/42", outcome.PullRequestUrl);
        Assert.Equal(42, outcome.PullRequestNumber);
        Assert.Null(outcome.MergedSha);
    }

    [Fact]
    public async Task CompleteAsync_AutoMergeFlow_OpensPrThenMerges()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(7, "https://github.com/myorg/myrepo/pull/7"));
        handler.Enqueue(PullRequestCommitsResponse(
            """
            [
              {
                "commit": {
                  "message": "feat: add feature X\n\nImplement feature X.\n\nCodeyBox-Prompt-Revision: 4\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>"
                }
              }
            ]
            """));
        handler.Enqueue(MergeOkResponse("abc123sha"));

        var remote = BuildRemote(gitHost, handler, DefaultOpts with { AutoMerge = true, MergeMethod = "squash" });
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        // Three HTTP calls: POST /pulls, GET /pulls/7/commits, then PUT /pulls/7/merge.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("/pulls", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("/pulls/7/commits", handler.Requests[1].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Contains("/pulls/7/merge", handler.Requests[2].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Put, handler.Requests[2].Method);

        // Merge body contains the configured method plus explicit squash title/body.
        using (var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]))
        {
            Assert.Equal("squash", mergeBody.RootElement.GetProperty("merge_method").GetString());
            Assert.Equal("Add feature X (#7)", mergeBody.RootElement.GetProperty("commit_title").GetString());
            var message = mergeBody.RootElement.GetProperty("commit_message").GetString();
            Assert.Contains("Implement feature X.", message);
            Assert.Contains("CodeyBox-Prompt-Revision: 4", message);
            Assert.Equal(1, CountOccurrences(message!, "Co-Authored-By: CodeyBox"));
        }

        Assert.True(outcome.BranchPushed);
        Assert.Equal("https://github.com/myorg/myrepo/pull/7", outcome.PullRequestUrl);
        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.Equal("abc123sha", outcome.MergedSha);
    }

    [Fact]
    public async Task CompleteAsync_NonSquashMerge_DoesNotSendSquashTitleOrMessage()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(7, "https://github.com/myorg/myrepo/pull/7"));
        handler.Enqueue(MergeOkResponse("merge-sha"));

        var remote = BuildRemote(gitHost, handler, DefaultOpts with { AutoMerge = true, MergeMethod = "merge" });
        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("merge", mergeBody.RootElement.GetProperty("merge_method").GetString());
        Assert.False(mergeBody.RootElement.TryGetProperty("commit_title", out _));
        Assert.False(mergeBody.RootElement.TryGetProperty("commit_message", out _));
    }

    [Fact]
    public async Task CompleteAsync_SquashMerge_UsesGeneratedPrDescriptionForCommitMessage()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(8, "https://github.com/myorg/myrepo/pull/8"));
        handler.Enqueue(PullRequestCommitsResponse(
            """
            [
              {
                "commit": {
                  "message": "feat: merge description body\n\nCodeyBox-Prompt-Revision: 5\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>"
                }
              }
            ]
            """));
        handler.Enqueue(MergeOkResponse("generated-body-sha"));

        var generator = new StaticDescriptionGenerator(
            """
            This PR adds explicit squash merge messages.

            ## Changes
            - Updates the GitHub merge payload.
            - [ ] Run manual verification

            CodeyBox-Prompt-Revision: 99
            Co-Authored-By: CodeyBox <noreply@codeybox.invalid>
            """);
        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = true },
            },
            generator);

        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("Add explicit squash merge messages.", message);
        Assert.Contains("Update the GitHub merge payload.", message);
        Assert.DoesNotContain("## Changes", message);
        Assert.DoesNotContain("[ ]", message);
        Assert.Equal(1, CountOccurrences(message, "CodeyBox-Prompt-Revision:"));
        Assert.Equal(1, CountOccurrences(message, "Co-Authored-By: CodeyBox"));
        Assert.Contains("CodeyBox-Prompt-Revision: 5", message);
    }

    [Fact]
    public async Task CompleteAsync_SquashMerge_StripsCiSkipControlsFromTitleAndMessage()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(9, "https://github.com/myorg/myrepo/pull/9"));
        handler.Enqueue(PullRequestCommitsResponse(
            """
            [
              {
                "commit": {
                  "message": "feat: add release workflow\n\nImplement the release workflow.\n\nCodeyBox-Prompt-Revision: 7\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>"
                }
              }
            ]
            """));
        handler.Enqueue(MergeOkResponse("ci-skip-sanitized"));

        var generator = new StaticDescriptionGenerator(
            """
            This PR adds release workflow automation. [skip actions]

            skip-checks: true

            Updates deployment metadata without bypassing checks. [ci skip]
            """);
        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = true },
            },
            generator);

        await remote.CompleteAsync(
            SampleRequest with { Title = "feat: publish release [skip ci]" },
            CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("feat: publish release (#9)",
            mergeBody.RootElement.GetProperty("commit_title").GetString());
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.DoesNotContain("[skip ci]", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[ci skip]", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[skip actions]", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("skip-checks", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Add release workflow automation.", message);
        Assert.Contains("CodeyBox-Prompt-Revision: 7", message);
    }

    [Fact]
    public async Task CompleteAsync_SquashMerge_GeneratorTimeoutUsesCommitFallbackForCommitMessage()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(22, "https://github.com/myorg/myrepo/pull/22"));
        handler.Enqueue(PullRequestCommitsResponse(
            [
                """
                feat: preserve timeout fallback

                Compose the squash body from commit messages after description generation times out.

                CodeyBox-Prompt-Revision: 51
                Co-Authored-By: CodeyBox <noreply@codeybox.invalid>
                """,
            ]));
        handler.Enqueue(MergeOkResponse("timeout-squash-fallback-sha"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions
                {
                    Enabled = true,
                    Timeout = TimeSpan.FromMilliseconds(50),
                },
            },
            new HangingDescriptionGenerator());

        await remote.CompleteAsync(
                SampleRequest with
                {
                    Description = "This static timeout fallback body should only be used for the PR page.",
                },
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("Add feature X (#22)", mergeBody.RootElement.GetProperty("commit_title").GetString());
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        var normalizedMessage = message.Replace("\n", " ", StringComparison.Ordinal);
        Assert.Contains(
            "Compose the squash body from commit messages after description generation times out.",
            normalizedMessage);
        Assert.DoesNotContain("static timeout fallback", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CodeyBox-Prompt-Revision: 51", message);
        Assert.Equal(1, CountOccurrences(message, "CodeyBox-Prompt-Revision:"));
        Assert.Equal(1, CountOccurrences(message, "Co-Authored-By: CodeyBox"));
    }

    [Fact]
    public async Task CompleteAsync_SquashMerge_GeneratorExceptionUsesCommitFallbackForCommitMessage()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(23, "https://github.com/myorg/myrepo/pull/23"));
        handler.Enqueue(PullRequestCommitsResponse(
            [
                """
                feat: preserve exception fallback

                Compose the squash body from commit messages after description generation throws.

                CodeyBox-Prompt-Revision: 52
                Co-Authored-By: CodeyBox <noreply@codeybox.invalid>
                """,
            ]));
        handler.Enqueue(MergeOkResponse("exception-squash-fallback-sha"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = true },
            },
            new ThrowingDescriptionGenerator());

        await remote.CompleteAsync(
            SampleRequest with
            {
                Description = "This static exception fallback body should only be used for the PR page.",
            },
            CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("Add feature X (#23)", mergeBody.RootElement.GetProperty("commit_title").GetString());
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        var normalizedMessage = message.Replace("\n", " ", StringComparison.Ordinal);
        Assert.Contains(
            "Compose the squash body from commit messages after description generation throws.",
            normalizedMessage);
        Assert.DoesNotContain("static exception fallback", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CodeyBox-Prompt-Revision: 52", message);
        Assert.Equal(1, CountOccurrences(message, "CodeyBox-Prompt-Revision:"));
        Assert.Equal(1, CountOccurrences(message, "Co-Authored-By: CodeyBox"));
    }

    [Fact]
    public async Task CompleteAsync_SquashMerge_AppendsCurrentPrNumberWhenTitleEndsWithIssueReference()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(11, "https://github.com/myorg/myrepo/pull/11"));
        handler.Enqueue(PullRequestCommitsResponse(
            [
                "fix: handle timeout\n\nHandle timeout failures.\n\nCodeyBox-Prompt-Revision: 7\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>",
            ]));
        handler.Enqueue(MergeOkResponse("exact-pr-suffix-sha"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with { AutoMerge = true, MergeMethod = "squash" });

        await remote.CompleteAsync(
            SampleRequest with { Title = "fix: handle timeout (#123)" },
            CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("fix: handle timeout (#123) (#11)",
            mergeBody.RootElement.GetProperty("commit_title").GetString());
    }

    [Theory]
    [InlineData("[skip ci]")]
    [InlineData("   ")]
    public async Task CompleteAsync_SquashMerge_UsesFallbackTitleWhenCleanedTitleIsEmpty(string title)
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(10, "https://github.com/myorg/myrepo/pull/10"));
        handler.Enqueue(PullRequestCommitsResponse(
            [
                "feat: keep fallback title body\n\nKeep a useful body while the title falls back.\n\nCodeyBox-Prompt-Revision: 9\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>",
            ]));
        handler.Enqueue(MergeOkResponse("fallback-title-sha"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with { AutoMerge = true, MergeMethod = "squash" });

        await remote.CompleteAsync(SampleRequest with { Title = title }, CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("chore: merge CodeyBox pull request (#10)",
            mergeBody.RootElement.GetProperty("commit_title").GetString());
        Assert.Contains("Keep a useful body while the title falls back.",
            mergeBody.RootElement.GetProperty("commit_message").GetString());
    }

    [Fact]
    public async Task CompleteAsync_SquashMergeFallback_CleansCapturedMultiCommitShape()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(187, "https://github.com/myorg/myrepo/pull/187"));
        handler.Enqueue(PullRequestCommitsResponse(
            File.ReadAllText("Fixtures/GitHub/pr-187-commits.redacted.json")));
        handler.Enqueue(MergeOkResponse("fallback-sha"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = false },
            });

        await remote.CompleteAsync(SampleRequest with { PromptRevision = 12 }, CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString();
        Assert.Equal(
            """
            Add squash merge message composition.

            Build an explicit squash body from the generated PR description.

            Cover squash merge fallback.

            Build a captured multi-commit shape for the no-LLM fallback path.

            CodeyBox-Prompt-Revision: 13
            Co-Authored-By: CodeyBox <noreply@codeybox.invalid>
            """,
            message);
    }

    [Fact]
    public async Task CompleteAsync_SquashMergeFallback_AllNoiseUsesLastResortBody()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(18, "https://github.com/myorg/myrepo/pull/18"));
        handler.Enqueue(PullRequestCommitsResponse(
            "codeybox: stamp prompt-revision trailer\n\nCodeyBox-Prompt-Revision: 40\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>",
            "chore: restamp prompt revision trailer\n\nCodeyBox-Prompt-Revision: 41\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>",
            "codeybox rework: address audit findings\n\nCodeyBox-Prompt-Revision: 42\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>"));
        handler.Enqueue(MergeOkResponse("last-resort-body-sha"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = false },
            });

        await remote.CompleteAsync(
            SampleRequest with { PromptRevision = 39, Description = null },
            CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString();
        Assert.Equal(
            """
            Apply the CodeyBox work item changes.

            CodeyBox-Prompt-Revision: 42
            Co-Authored-By: CodeyBox <noreply@codeybox.invalid>
            """,
            message);
    }

    [Fact]
    public async Task CompleteAsync_SquashMergeFallback_WrapsCommitBodyParagraphs()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(19, "https://github.com/myorg/myrepo/pull/19"));
        handler.Enqueue(PullRequestCommitsResponse(
            [
                """
                feat: add wrapped squash body

                Update the squash merge fallback body with a deliberately long paragraph that should wrap across several conventional commit message lines before the trailer block is appended.

                CodeyBox-Prompt-Revision: 43
                Co-Authored-By: CodeyBox <noreply@codeybox.invalid>
                """,
            ]));
        handler.Enqueue(MergeOkResponse("wrapped-body-sha"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = false },
            });

        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        var paragraphs = message.Split("\n\n", StringSplitOptions.None);
        Assert.True(paragraphs.Length >= 3);
        Assert.Contains('\n', paragraphs[1]);
        Assert.All(
            paragraphs[1].Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.True(line.Length <= 72, $"Line was {line.Length} chars: {line}"));
    }

    [Fact]
    public async Task CompleteAsync_SquashMerge_CommitsEndpointFailureStillSendsExplicitMessage()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(12, "https://github.com/myorg/myrepo/pull/12"));
        handler.Enqueue(JsonResponse(HttpStatusCode.InternalServerError, """{"message":"backend unavailable"}"""));
        handler.Enqueue(MergeOkResponse("fallback-without-commits-sha"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = false },
            });

        await remote.CompleteAsync(
            SampleRequest with
            {
                PromptRevision = 21,
                Description = "Updates squash merge fallback when GitHub commit listing fails.",
            },
            CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Contains("/pulls/12/commits", handler.Requests[1].RequestUri!.PathAndQuery);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("squash", mergeBody.RootElement.GetProperty("merge_method").GetString());
        Assert.Equal("Add feature X (#12)", mergeBody.RootElement.GetProperty("commit_title").GetString());
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("Update squash merge fallback when GitHub commit listing fails.", message);
        Assert.Contains("CodeyBox-Prompt-Revision: 21", message);
        Assert.Equal(1, CountOccurrences(message, "CodeyBox-Prompt-Revision:"));
        Assert.Equal(1, CountOccurrences(message, "Co-Authored-By: CodeyBox"));
    }

    [Fact]
    public async Task CompleteAsync_SquashMerge_CommitsEmptyUsesRequestRevisionBeforeStaticPrBodyTrailer()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(20, "https://github.com/myorg/myrepo/pull/20"));
        handler.Enqueue(PullRequestCommitsResponse("[]"));
        handler.Enqueue(MergeOkResponse("request-revision-before-pr-body-sha"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = false },
            });

        await remote.CompleteAsync(
            SampleRequest with
            {
                PromptRevision = 21,
                Description =
                    """
                    This static fallback includes a stale trailer in agent output.

                    ```
                    CodeyBox-Prompt-Revision: 99
                    Co-Authored-By: CodeyBox <noreply@codeybox.invalid>
                    ```
                    """,
            },
            CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("This static fallback includes a stale trailer in agent output.", message);
        Assert.Contains("CodeyBox-Prompt-Revision: 21", message);
        Assert.DoesNotContain("CodeyBox-Prompt-Revision: 99", message);
        Assert.Equal(1, CountOccurrences(message, "CodeyBox-Prompt-Revision:"));
    }

    [Fact]
    public async Task CompleteAsync_SquashMerge_DuplicateCommitPromptTrailersUseRequestRevision()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(21, "https://github.com/myorg/myrepo/pull/21"));
        handler.Enqueue(PullRequestCommitsResponse(
            [
                """
                feat: avoid ambiguous prompt trailer

                Preserve the request revision when a commit message has
                duplicate prompt trailers.

                CodeyBox-Prompt-Revision: 22
                CodeyBox-Prompt-Revision: 99
                Co-Authored-By: CodeyBox <noreply@codeybox.invalid>
                """,
            ]));
        handler.Enqueue(MergeOkResponse("duplicate-prompt-trailer-sha"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = false },
            });

        await remote.CompleteAsync(
            SampleRequest with
            {
                PromptRevision = 22,
                Description = "Fallback description should not decide the trailer.",
            },
            CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("CodeyBox-Prompt-Revision: 22", message);
        Assert.DoesNotContain("CodeyBox-Prompt-Revision: 99", message);
        Assert.Equal(1, CountOccurrences(message, "CodeyBox-Prompt-Revision:"));
    }

    [Fact]
    public async Task CompleteAsync_SquashMerge_CommitsEndpointExceptionStillSendsExplicitMessage()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(14, "https://github.com/myorg/myrepo/pull/14"));
        handler.EnqueueException(new HttpRequestException("commit list connection reset"));
        handler.Enqueue(MergeOkResponse("fallback-after-commit-exception"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = false },
            });

        await remote.CompleteAsync(
            SampleRequest with
            {
                PromptRevision = 22,
                Description = "Updates squash merge fallback when commit listing throws.",
            },
            CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[2].Method);
        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("Update squash merge fallback when commit listing throws.", message);
        Assert.Contains("CodeyBox-Prompt-Revision: 22", message);
    }

    [Fact]
    public async Task CompleteAsync_SquashMerge_CommitsEndpointCallerCancellationRethrows()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        using var cts = new CancellationTokenSource();
        handler.Enqueue(PrCreatedResponse(15, "https://github.com/myorg/myrepo/pull/15"));
        handler.EnqueueCallback(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with { AutoMerge = true, MergeMethod = "squash" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            remote.CompleteAsync(SampleRequest, cts.Token));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/pulls/15/commits", handler.Requests[1].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task CompleteAsync_SquashMerge_UsesPromptRevisionWhenCommitsAreEmpty()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(13, "https://github.com/myorg/myrepo/pull/13"));
        handler.Enqueue(PullRequestCommitsResponse("[]"));
        handler.Enqueue(MergeOkResponse("prompt-revision-fallback-sha"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = false },
            });

        await remote.CompleteAsync(
            SampleRequest with
            {
                PromptRevision = 34,
                Description = "Ships a clean fallback trailer.",
            },
            CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("Ship a clean fallback trailer.", message);
        Assert.Contains("CodeyBox-Prompt-Revision: 34", message);
        Assert.Equal(1, CountOccurrences(message, "CodeyBox-Prompt-Revision:"));
    }

    [Fact]
    public async Task CompleteAsync_SquashMerge_FetchesLaterCommitPagesForFallback()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(16, "https://github.com/myorg/myrepo/pull/16"));
        handler.Enqueue(PullRequestCommitsResponse(
            Enumerable.Repeat(
                "codeybox rework: address audit findings\n\nCodeyBox-Prompt-Revision: 1\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>",
                100).ToArray()));
        handler.Enqueue(PullRequestCommitsResponse(
            [
                "feat: add paged fallback\n\nCompose squash fallback from page two.\n\nCodeyBox-Prompt-Revision: 2\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>",
            ]));
        handler.Enqueue(MergeOkResponse("paged-fallback-sha"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = false },
            });

        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains("page=1", handler.Requests[1].RequestUri!.Query);
        Assert.Contains("page=2", handler.Requests[2].RequestUri!.Query);
        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[3]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("Compose squash fallback from page two.", message);
        Assert.Contains("CodeyBox-Prompt-Revision: 2", message);
    }

    [Fact]
    public async Task CompleteAsync_SquashMerge_TruncatesOversizedCommitMessages()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(17, "https://github.com/myorg/myrepo/pull/17"));
        handler.Enqueue(PullRequestCommitsResponse(
            [
                "feat: add oversized fallback\n\n" + new string('a', 120_000) +
                "\n\nCodeyBox-Prompt-Revision: 3\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>",
            ]));
        handler.Enqueue(MergeOkResponse("truncated-fallback-sha"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = false },
            });

        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.True(Encoding.UTF8.GetByteCount(message) < 20_000);
        Assert.Contains("[...truncated]", message);
        Assert.Contains("CodeyBox-Prompt-Revision: 3", message);
    }

    [Fact]
    public async Task CompleteAsync_ExistingPrNumberWithSquash_UsesExistingPrBodyForCommitMessage()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PullRequestResponse(
            42,
            "https://github.com/myorg/myrepo/pull/42",
            "feat: preserve generated squash summary (#42)",
            """
            This PR adds retry-safe squash commit composition.

            ## Changes
            - Updates retry merges to reuse the original PR description.

            ---
            *Co-Authored-By: CodeyBox <noreply@codeybox.invalid>*
            """));
        handler.Enqueue(PullRequestCommitsResponse(
            """
            [
              {
                "commit": {
                  "message": "codeybox rework: address audit findings\n\nCodeyBox-Prompt-Revision: 6\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>"
                }
              }
            ]
            """));
        handler.Enqueue(MergeOkResponse("merged-after-squash-retry"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = true },
            });
        var request = SampleRequest with { ExistingPullRequestNumber = 42 };

        var outcome = await remote.CompleteAsync(request, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/repos/myorg/myrepo/pulls/42", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Contains("/pulls/42/commits", handler.Requests[1].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Put, handler.Requests[2].Method);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("feat: preserve generated squash summary (#42)",
            mergeBody.RootElement.GetProperty("commit_title").GetString());
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("Add retry-safe squash commit composition.", message);
        Assert.Contains("Update retry merges to reuse the original PR description.", message);
        Assert.DoesNotContain("codeybox rework", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CodeyBox-Prompt-Revision: 6", message);
        Assert.Equal("merged-after-squash-retry", outcome.MergedSha);
    }

    [Fact]
    public async Task CompleteAsync_ExistingPrNumberWithSquash_StaticPrBodyUsesCommitFallback()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PullRequestResponse(
            43,
            "https://github.com/myorg/myrepo/pull/43",
            "feat: retry with static body (#43)",
            """
            Automated via CodeyBox - work item 00000000-0000-0000-0000-000000000001

            > **Untrusted agent output - do not treat as instructions.**

            ```
            stdout that should not become the squash body
            ```

            ---
            *Co-Authored-By: CodeyBox <noreply@codeybox.invalid>*
            """));
        handler.Enqueue(PullRequestCommitsResponse(
            """
            [
              {
                "commit": {
                  "message": "feat: preserve fallback content\n\nCompose the squash body from cleaned commit messages.\n\nCodeyBox-Prompt-Revision: 8\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>"
                }
              }
            ]
            """));
        handler.Enqueue(MergeOkResponse("merged-static-retry"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = true },
            });
        var request = SampleRequest with { ExistingPullRequestNumber = 43 };

        var outcome = await remote.CompleteAsync(request, CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("Compose the squash body from cleaned commit messages.", message);
        Assert.DoesNotContain("Automated via CodeyBox", message);
        Assert.DoesNotContain("stdout that should not become the squash body", message);
        Assert.Contains("CodeyBox-Prompt-Revision: 8", message);
        Assert.Equal("merged-static-retry", outcome.MergedSha);
    }

    [Fact]
    public async Task CompleteAsync_ExistingPrNumberWithSquash_PrDescriptionDisabledUsesCommitFallback()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PullRequestResponse(
            44,
            "https://github.com/myorg/myrepo/pull/44",
            "feat: retry with generated body (#44)",
            """
            This PR adds generated retry body that should not be reused.

            ## Changes
            - Updates from the existing PR body.

            CodeyBox-Prompt-Revision: 99

            ---
            *Co-Authored-By: CodeyBox <noreply@codeybox.invalid>*
            """));
        handler.Enqueue(PullRequestCommitsResponse(
            [
                "feat: preserve disabled fallback\n\nCompose the squash body from commit messages when PR description generation is disabled.\n\nCodeyBox-Prompt-Revision: 10\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>",
            ]));
        handler.Enqueue(MergeOkResponse("merged-disabled-existing-pr-retry"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = false },
            });
        var request = SampleRequest with { ExistingPullRequestNumber = 44, PromptRevision = 45 };

        var outcome = await remote.CompleteAsync(request, CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("Compose the squash body from commit messages", message);
        Assert.DoesNotContain("generated retry body", message);
        Assert.DoesNotContain("Updates from the existing PR body", message);
        Assert.Contains("CodeyBox-Prompt-Revision: 10", message);
        Assert.DoesNotContain("CodeyBox-Prompt-Revision: 99", message);
        Assert.Equal("merged-disabled-existing-pr-retry", outcome.MergedSha);
    }

    [Fact]
    public async Task CompleteAsync_ExistingPrNumberWithSquash_PrFetchNonSuccessUsesLocalFallback()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.InternalServerError, """{"message":"backend unavailable"}"""));
        handler.Enqueue(PullRequestCommitsResponse("[]"));
        handler.Enqueue(MergeOkResponse("merged-after-pr-fetch-500"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = true },
            });
        var request = SampleRequest with
        {
            ExistingPullRequestNumber = 42,
            PromptRevision = 44,
            Description = "Updates existing PR retry fallback after PR fetch returns an error.",
        };

        var outcome = await remote.CompleteAsync(request, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/repos/myorg/myrepo/pulls/42", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Contains("/pulls/42/commits", handler.Requests[1].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Put, handler.Requests[2].Method);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("Add feature X (#42)", mergeBody.RootElement.GetProperty("commit_title").GetString());
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("Update existing PR retry fallback after PR fetch returns an error.", message);
        Assert.Contains("CodeyBox-Prompt-Revision: 44", message);
        Assert.Equal("merged-after-pr-fetch-500", outcome.MergedSha);
        Assert.Equal("https://github.com/myorg/myrepo/pull/42", outcome.PullRequestUrl);
    }

    [Fact]
    public async Task CompleteAsync_ExistingPrNumberWithSquash_PrFetchExceptionUsesLocalFallback()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueException(new HttpRequestException("connection reset"));
        handler.Enqueue(PullRequestCommitsResponse("[]"));
        handler.Enqueue(MergeOkResponse("merged-after-pr-fetch-exception"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = true },
            });
        var request = SampleRequest with
        {
            ExistingPullRequestNumber = 43,
            PromptRevision = 45,
            Description = "Updates existing PR retry fallback after PR fetch throws.",
        };

        var outcome = await remote.CompleteAsync(request, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/repos/myorg/myrepo/pulls/43", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Contains("/pulls/43/commits", handler.Requests[1].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Put, handler.Requests[2].Method);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("Add feature X (#43)", mergeBody.RootElement.GetProperty("commit_title").GetString());
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("Update existing PR retry fallback after PR fetch throws.", message);
        Assert.Contains("CodeyBox-Prompt-Revision: 45", message);
        Assert.Equal("merged-after-pr-fetch-exception", outcome.MergedSha);
        Assert.Equal("https://github.com/myorg/myrepo/pull/43", outcome.PullRequestUrl);
    }

    [Fact]
    public async Task CompleteAsync_ExistingPrNumberWithSquash_PrFetchCancellationRethrows()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueException(new OperationCanceledException("cancelled"));

        var remote = BuildRemote(
            gitHost,
            handler,
            DefaultOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = new PrDescriptionOptions { Enabled = true },
            });
        var request = SampleRequest with { ExistingPullRequestNumber = 44 };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            remote.CompleteAsync(request, cts.Token));
    }

    [Fact]
    public async Task CompleteAsync_PullsReturns422_ReturnsGracefulOutcomeWithoutThrow()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"message":"A pull request already exists"}]}"""));

        var remote = BuildRemote(gitHost, handler);
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        // Branch was still pushed
        Assert.True(outcome.BranchPushed);
        // PR info absent — graceful
        Assert.Null(outcome.PullRequestUrl);
        Assert.Null(outcome.PullRequestNumber);
        Assert.NotNull(outcome.Notes);
        Assert.Contains("422", outcome.Notes);
    }

    [Fact]
    public async Task CompleteAsync_MergeReturns405_FlagsAutoMergeRacedSoOrchestratorCanRecover()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(99, "https://github.com/myorg/myrepo/pull/99"));
        handler.Enqueue(JsonResponse(HttpStatusCode.MethodNotAllowed,
            """{"message":"Pull Request is not mergeable"}"""));

        var remote = BuildRemote(gitHost, handler, DefaultOpts with { AutoMerge = true });
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.True(outcome.BranchPushed);
        Assert.Equal("https://github.com/myorg/myrepo/pull/99", outcome.PullRequestUrl);
        Assert.Equal(99, outcome.PullRequestNumber);
        Assert.Null(outcome.MergedSha);  // PR left open for the orchestrator's race recovery
        // Notes must be populated so operators get an orchestrator-level diagnostic.
        Assert.NotNull(outcome.Notes);
        Assert.Contains("405", outcome.Notes);
        // AutoMergeRaced is the signal the orchestrator's retry loop watches
        // to trigger the "re-fetch base + re-run merge phase + retry merge"
        // recovery path — without it, the item would be parked at the cap.
        Assert.True(outcome.AutoMergeRaced);
    }

    [Fact]
    public async Task CompleteAsync_ExistingPrNumber_SkipsCreatePrAndCallsMergeDirectly()
    {
        // Simulates the orchestrator's race-recovery retry: it re-runs the merge
        // phase locally and then re-invokes CompleteAsync, passing the PR number
        // from the prior attempt so we don't re-create (which would 422).
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(MergeOkResponse("merged-after-retry"));

        var remote = BuildRemote(gitHost, handler, DefaultOpts with { AutoMerge = true });
        var request = SampleRequest with { ExistingPullRequestNumber = 42 };
        var outcome = await remote.CompleteAsync(request, CancellationToken.None);

        // Push still happens — we may have advanced the work branch locally
        // to the new merge sha and need to publish it.
        Assert.Single(gitHost.Pushes);
        Assert.Equal(SampleRequest.WorkBranch, gitHost.Pushes[0].Branch);

        // Only the PUT /merge call — no POST /pulls re-creation.
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Contains("/pulls/42/merge", handler.Requests[0].RequestUri!.PathAndQuery);

        Assert.True(outcome.BranchPushed);
        Assert.Equal(42, outcome.PullRequestNumber);
        Assert.Equal("merged-after-retry", outcome.MergedSha);
        Assert.False(outcome.AutoMergeRaced);
        // URL must be synthesized for the existing PR so consumers (webhooks,
        // logging, operator surface) can link back to the forge view.
        Assert.Equal("https://github.com/myorg/myrepo/pull/42", outcome.PullRequestUrl);
    }

    [Fact]
    public async Task CompleteAsync_ExistingPrNumberWith405_StillFlagsAutoMergeRaced()
    {
        // The race may recur — re-running the merge phase doesn't help if a
        // third writer is hammering base. The orchestrator caps total attempts;
        // each retry CompleteAsync still surfaces AutoMergeRaced.
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.MethodNotAllowed,
            """{"message":"Pull Request is not mergeable"}"""));

        var remote = BuildRemote(gitHost, handler, DefaultOpts with { AutoMerge = true });
        var request = SampleRequest with { ExistingPullRequestNumber = 7 };
        var outcome = await remote.CompleteAsync(request, CancellationToken.None);

        Assert.True(outcome.BranchPushed);
        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.Null(outcome.MergedSha);
        Assert.True(outcome.AutoMergeRaced);
    }

    [Fact]
    public async Task CompleteAsync_RequestsCarryUserAgentHeader()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(1, "https://github.com/myorg/myrepo/pull/1"));

        var remote = BuildRemote(gitHost, handler);
        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.All(handler.Requests, req =>
            Assert.True(
                req.Headers.UserAgent.ToString().Contains("codeybox", StringComparison.OrdinalIgnoreCase),
                $"Expected User-Agent 'codeybox' but got '{req.Headers.UserAgent}'"));
    }

    [Fact]
    public async Task CompleteAsync_RequestsCarryTokenAuthorizationHeader()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(1, "https://github.com/myorg/myrepo/pull/1"));

        var remote = BuildRemote(gitHost, handler);
        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.All(handler.Requests, req =>
        {
            var auth = req.Headers.Authorization;
            Assert.NotNull(auth);
            Assert.Equal("token", auth!.Scheme);
            Assert.Equal(DefaultOpts.Token, auth.Parameter);
        });
    }

    [Fact]
    public async Task CompleteAsync_PullRequestTitleTemplate_SubstitutesPlaceholders()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(5, "https://github.com/myorg/myrepo/pull/5"));

        var opts = DefaultOpts with { PullRequestTitleTemplate = "[bot] {title} ({branch})" };
        var remote = BuildRemote(gitHost, handler, opts);
        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        // The POST /pulls body should contain the resolved title
        Assert.Contains("[bot] Add feature X (codeybox/abc123)", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task FetchBaseBranchAsync_RejectsBranchWithWhitespaceOrControl()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        var remote = BuildRemote(gitHost, handler);

        await Assert.ThrowsAsync<ArgumentException>(
            () => remote.FetchBaseBranchAsync("repo-id", "main\n", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => remote.FetchBaseBranchAsync("repo-id", "main with space", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => remote.FetchBaseBranchAsync("repo-id", "main", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => remote.FetchBaseBranchAsync("repo-id", string.Empty, CancellationToken.None));

        // Validation must short-circuit before the git host runs — no fetch was
        // dispatched so an attacker can't bypass argv validation by piggybacking
        // on the askpass plumbing.
        Assert.Empty(gitHost.Fetches);
    }

    [Fact]
    public async Task FetchBaseBranchAsync_DelegatesToGitHostWithRepoUrlAndAskpassEnv()
    {
        var gitHost = new FakeGitHost { FetchUpstreamShaToReturn = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef" };
        var handler = new FakeHttpMessageHandler();
        var remote = BuildRemote(gitHost, handler);

        var sha = await remote.FetchBaseBranchAsync("repo-id-x", "main", CancellationToken.None);

        Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", sha);
        var call = Assert.Single(gitHost.Fetches);
        Assert.Equal("repo-id-x", call.RepositoryId);
        // Bare URL only (no credentials embedded — those flow via askpass env).
        Assert.Equal("https://github.com/myorg/myrepo.git", call.Url);
        Assert.Equal("main", call.Branch);
        // The askpass env must carry the configured token so git can authenticate
        // the fetch against private repos. Validates the credential plumbing
        // didn't silently change to e.g. ambient environment.
        Assert.True(call.Env.ContainsKey("GIT_ASKPASS"));
        Assert.Equal(DefaultOpts.Token, call.Env["CODEYBOX_GIT_PASS"]);
        Assert.Equal("x-access-token", call.Env["CODEYBOX_GIT_USER"]);
    }

    [Fact]
    public async Task FetchBaseBranchAsync_PropagatesNullFromGitHost()
    {
        var gitHost = new FakeGitHost { FetchUpstreamShaToReturn = null };
        var handler = new FakeHttpMessageHandler();
        var remote = BuildRemote(gitHost, handler);

        // Upstream not advertising the branch → propagated as null so the
        // orchestrator can park with a distinct "upstream does not advertise"
        // message rather than treating it as a successful fetch.
        var sha = await remote.FetchBaseBranchAsync("repo-id", "main", CancellationToken.None);
        Assert.Null(sha);
        Assert.Single(gitHost.Fetches);
    }

    [Fact]
    public async Task CompleteAsync_PushToUpstreamThrows_PropagatesExceptionWithoutCallingGitHubApi()
    {
        // Verifies that a PushToUpstreamAsync failure is rethrown so the
        // orchestrator retry loop can engage, and that no GitHub API calls
        // are made when the push step itself fails.
        var gitHost = new ThrowingFakeGitHost(new InvalidOperationException("git push failed: connection refused"));
        var handler = new FakeHttpMessageHandler();

        var remote = BuildRemote(gitHost, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.CompleteAsync(SampleRequest, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }
}

// -------------------------------------------------------------------------
// Test infrastructure
// -------------------------------------------------------------------------

internal sealed class FakeGitHost : IGitHost
{
    public List<(string RepositoryId, string Url, string Branch, UpstreamPushReconcileStrategy ReconcileStrategy)> Pushes { get; } = new();
    public List<(string RepositoryId, string Url, string Branch, IReadOnlyDictionary<string, string> Env)> Fetches { get; } = new();

    /// <summary>
    /// When set, <see cref="FetchUpstreamBranchAsync"/> returns this sha rather
    /// than the default-interface null. Lets tests assert the sha is propagated
    /// out of <see cref="GitHubUpstreamRemote.FetchBaseBranchAsync"/>.
    /// </summary>
    public string? FetchUpstreamShaToReturn { get; set; }

    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => Task.FromResult(id.ToString());
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
        => EnsureRepositoryAsync(id, seedFromUrl, ct);

    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => throw new NotSupportedException();

    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => Task.FromResult("main");

    public Task PushToUpstreamAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
    {
        Pushes.Add((repositoryId, upstreamUrl, branch, reconcileStrategy));
        return Task.CompletedTask;
    }

    public Task<string?> FetchUpstreamBranchAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        CancellationToken ct = default)
    {
        Fetches.Add((repositoryId, upstreamUrl, branch, upstreamEnv));
        return Task.FromResult(FetchUpstreamShaToReturn);
    }

    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => Task.FromResult(("", ""));
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<object> _queue = new();
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> RequestBodies { get; } = new();

    public void Enqueue(HttpResponseMessage response) => _queue.Enqueue(response);
    public void EnqueueException(Exception exception) => _queue.Enqueue(exception);
    public void EnqueueCallback(Func<CancellationToken, HttpResponseMessage> callback) => _queue.Enqueue(callback);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var body = request.Content is not null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : string.Empty;
        RequestBodies.Add(body);
        if (_queue.Count == 0)
            return new HttpResponseMessage(HttpStatusCode.OK);

        var next = _queue.Dequeue();
        if (next is Exception exception)
            throw exception;
        if (next is Func<CancellationToken, HttpResponseMessage> callback)
            return callback(cancellationToken);

        return (HttpResponseMessage)next;
    }
}

internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    public FakeHttpClientFactory(HttpMessageHandler handler, string? userAgent = null)
    {
        _client = new HttpClient(handler);
        if (!string.IsNullOrEmpty(userAgent))
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public HttpClient CreateClient(string name)
    {
        Assert.Equal("github-upstream", name);
        return _client;
    }
}

internal sealed class StaticDescriptionGenerator : IPullRequestDescriptionGenerator
{
    private readonly string _body;

    public StaticDescriptionGenerator(string body) => _body = body;

    public Task<string> GenerateAsync(PullRequestDescriptionRequest request, CancellationToken ct)
        => Task.FromResult(_body);
}

internal sealed class ThrowingFakeGitHost : IGitHost
{
    private readonly Exception _ex;
    public ThrowingFakeGitHost(Exception ex) => _ex = ex;

    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => Task.FromResult(id.ToString());
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
        => EnsureRepositoryAsync(id, seedFromUrl, ct);

    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => throw new NotSupportedException();

    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => Task.FromResult("main");

    public Task PushToUpstreamAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
        => throw _ex;

    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => Task.FromResult(("", ""));
}
