using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Projects;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification tests for the two selectable PR description strategies
/// (<see cref="PrDescriptionStrategy.Completion"/> and
/// <see cref="PrDescriptionStrategy.Agentic"/>) behind
/// <see cref="IPullRequestDescriptionGenerator"/>.
/// </summary>
public sealed class PullRequestDescriptionStrategyTests : IDisposable
{
    private const string TestTokenEnvVar = "PRDESC_STRATEGY_TEST_TOKEN";

    private static readonly WorkItemId TestItemId =
        new(Guid.Parse("00000000-0000-0000-0000-000000000101"));

    private static readonly UpstreamCompletionRequest SampleRequest = new()
    {
        RepositoryId = "repo-id",
        WorkItemId = TestItemId,
        ProjectId = new ProjectId("prdesc-test"),
        WorkBranch = "codeybox/strategy1",
        BaseBranch = "main",
        MergeSha = "deadbeef",
        Title = "Add frobnicate support",
        Description = "Static fallback description",
        DiffStat = " src/Frobnicate.cs | 12 ++++++++++++\n 1 file changed, 12 insertions(+)",
        FullDiff = "diff --git a/src/Frobnicate.cs b/src/Frobnicate.cs\n+added line",
        WorkItemPrompt = "Add frobnicate support.",
    };

    private static readonly GitHubUpstreamOptions BaseOpts = new()
    {
        Owner = "myorg",
        Repository = "myrepo",
        Token = "test-token-not-a-real-pat",
        MergeMethod = "merge",
        AutoMerge = false,
    };

    public PullRequestDescriptionStrategyTests()
    {
        Environment.SetEnvironmentVariable(TestTokenEnvVar, "fake-token-for-tests");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TestTokenEnvVar, null);
    }

    // -------------------------------------------------------------------------
    // Selector defaults
    // -------------------------------------------------------------------------

    [Fact]
    public void Strategy_DefaultsToCompletion()
    {
        Assert.Equal(PrDescriptionStrategy.Completion, new ProjectPrDescription().Strategy);
        Assert.Equal(PrDescriptionStrategy.Completion, new PrDescriptionOptions().Strategy);
    }

    // -------------------------------------------------------------------------
    // Construction gate: each strategy needs only its own configuration
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Factory_CompletionStrategy_WithoutSandboxImage_ProducesGenerator()
    {
        // No SandboxImageReference configured, yet the completion strategy must
        // still produce a generator. Throwing sandboxes prove generation never
        // touches sandbox provisioning.
        var completion = new FakeCompletionClient();
        completion.EnqueueResult(SuccessResult("Completion-generated prose about frobnicate."));

        var project = GitHubProject(new ProjectPrDescription
        {
            Strategy = PrDescriptionStrategy.Completion,
            CompletionEndpoint = "https://llm.example/v1/chat",
            CompletionModel = "test-model",
        });

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(1, "https://github.com/myorg/myrepo/pull/1"));
        var factoryWithHandler = BuildFactory(handler, completion, sandboxes: new ThrowingSandboxProvider());
        var remote = factoryWithHandler.Create(project);

        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.NotNull(outcome.PullRequestUrl);
        Assert.Contains("Completion-generated prose about frobnicate.", handler.RequestBodies[0]);
        Assert.Single(completion.Requests);
    }

    [Fact]
    public async Task Factory_AgenticStrategy_WithoutCompletionEndpoint_ProducesGenerator()
    {
        var runner = new CapturingAgentRunner("Agentic-generated prose about frobnicate.");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(2, "https://github.com/myorg/myrepo/pull/2"));

        var factory = BuildFactory(
            handler,
            completionClient: null,
            sandboxes: new InProcessFakeSandboxProvider(),
            agents: new FakeSingleAgentRegistry(runner));
        var project = GitHubProject(new ProjectPrDescription
        {
            Strategy = PrDescriptionStrategy.Agentic,
            GeneratorAgent = "claude",
            SandboxImageReference = "test-image",
        });

        var remote = factory.Create(project);
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.NotNull(outcome.PullRequestUrl);
        Assert.NotNull(runner.LastPrompt);
        Assert.Contains("Agentic-generated prose about frobnicate.", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Factory_CompletionStrategy_WithoutEndpoint_FallsBackToStatic()
    {
        var completion = new FakeCompletionClient();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(3, "https://github.com/myorg/myrepo/pull/3"));

        var factory = BuildFactory(handler, completion);
        var project = GitHubProject(new ProjectPrDescription
        {
            Strategy = PrDescriptionStrategy.Completion,
            CompletionEndpoint = string.Empty,
            CompletionModel = "test-model",
        });

        var remote = factory.Create(project);
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.NotNull(outcome.PullRequestUrl);
        Assert.Empty(completion.Requests);
        Assert.Contains("Static fallback description", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Factory_AgenticStrategy_WithoutImage_FallsBackToStatic()
    {
        var runner = new CapturingAgentRunner("must never be used");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(4, "https://github.com/myorg/myrepo/pull/4"));

        var factory = BuildFactory(
            handler,
            completionClient: null,
            agents: new FakeSingleAgentRegistry(runner));
        var project = GitHubProject(new ProjectPrDescription
        {
            Strategy = PrDescriptionStrategy.Agentic,
            SandboxImageReference = string.Empty,
        });

        var remote = factory.Create(project);
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.NotNull(outcome.PullRequestUrl);
        Assert.Null(runner.LastPrompt);
        Assert.Contains("Static fallback description", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Factory_DisabledStrategy_FallsBackToStatic()
    {
        var completion = new FakeCompletionClient();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(5, "https://github.com/myorg/myrepo/pull/5"));

        var factory = BuildFactory(handler, completion);
        var project = GitHubProject(new ProjectPrDescription
        {
            Enabled = false,
            Strategy = PrDescriptionStrategy.Completion,
            CompletionEndpoint = "https://llm.example/v1/chat",
            CompletionModel = "test-model",
        });

        var remote = factory.Create(project);
        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.Empty(completion.Requests);
        Assert.Contains("Static fallback description", handler.RequestBodies[0]);
    }

    // -------------------------------------------------------------------------
    // Completion strategy: prompt shape, truncation, redaction, commit messages
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Completion_DiffExceedsMaxDiffBytes_TruncatesFromMiddle()
    {
        var completion = new FakeCompletionClient();
        completion.EnqueueResult(SuccessResult("ok"));
        var truncateOpts = CompletionOpts();
        truncateOpts.MaxDiffBytes = 128;
        var generator = new CompletionPullRequestDescriptionGenerator(
            completion,
            truncateOpts,
            NullLogger<CompletionPullRequestDescriptionGenerator>.Instance);

        var largeDiff = new string('A', 50) + "\n" + new string('B', 100) + "\n" + new string('C', 50);
        var request = GeneratorRequest() with { FullDiff = largeDiff };
        await generator.GenerateAsync(request, CancellationToken.None);

        var prompt = Assert.Single(completion.Requests).Messages[0].Content;
        Assert.Contains("truncated", prompt);
        Assert.Contains("AAAA", prompt);
        Assert.Contains("CCCC", prompt);
    }

    [Fact]
    public async Task Completion_SecretEchoedByModel_RedactedFromOutput()
    {
        const string secret = "ghp_AAABBBCCC12345678";
        var completion = new FakeCompletionClient();
        completion.EnqueueResult(SuccessResult($"Summary mentions token {secret} accidentally."));
        var generator = new CompletionPullRequestDescriptionGenerator(
            completion, CompletionOpts(), NullLogger<CompletionPullRequestDescriptionGenerator>.Instance);

        var result = await generator.GenerateAsync(GeneratorRequest(), CancellationToken.None);

        Assert.DoesNotContain(secret, result);
        Assert.Contains("***", result);
    }

    [Fact]
    public async Task Completion_CommitMessages_ReachPrompt()
    {
        var completion = new FakeCompletionClient();
        completion.EnqueueResult(SuccessResult("ok"));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(6, "https://github.com/myorg/myrepo/pull/6"));

        var remote = BuildRemote(
            handler,
            BaseOpts with { PrDescription = CompletionOpts() },
            new CompletionPullRequestDescriptionGenerator(
                completion, CompletionOpts(), NullLogger<CompletionPullRequestDescriptionGenerator>.Instance));

        var request = SampleRequest with
        {
            CommitMessages = ["feat: frobnicate the widget\n\nAdds the frobnicate entry point."],
        };
        await remote.CompleteAsync(request, CancellationToken.None);

        var prompt = Assert.Single(completion.Requests).Messages[0].Content;
        Assert.Contains("frobnicate the widget", prompt);
    }

    [Fact]
    public async Task Agentic_CommitMessages_ReachPrompt()
    {
        var runner = new CapturingAgentRunner("ok");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(7, "https://github.com/myorg/myrepo/pull/7"));

        var remote = BuildRemote(
            handler,
            BaseOpts with
            {
                PrDescription = new PrDescriptionOptions
                {
                    Enabled = true,
                    Strategy = PrDescriptionStrategy.Agentic,
                    SandboxImageReference = "test-image",
                },
            },
            new LlmPullRequestDescriptionGenerator(
                new InProcessFakeSandboxProvider(),
                new FakeSingleAgentRegistry(runner),
                new NullCredentialProvider(),
                new PrDescriptionOptions
                {
                    Enabled = true,
                    Strategy = PrDescriptionStrategy.Agentic,
                    SandboxImageReference = "test-image",
                },
                NullLogger<LlmPullRequestDescriptionGenerator>.Instance));

        var request = SampleRequest with
        {
            CommitMessages = ["feat: frobnicate the widget\n\nAdds the frobnicate entry point."],
        };
        await remote.CompleteAsync(request, CancellationToken.None);

        Assert.NotNull(runner.LastPrompt);
        Assert.Contains("frobnicate the widget", runner.LastPrompt);
    }

    // -------------------------------------------------------------------------
    // Failure matrix: generation never fails or delays the pull request
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(CompletionStatus.TransportFailed)]
    [InlineData(CompletionStatus.AuthenticationFailed)]
    [InlineData(CompletionStatus.ServerError)]
    [InlineData(CompletionStatus.RateLimited)]
    [InlineData(CompletionStatus.EmptyCompletion)]
    public async Task Completion_FailureStatus_FallsBackToStaticAndOpensPr(CompletionStatus status)
    {
        var completion = new FakeCompletionClient();
        completion.EnqueueResult(new CompletionResult { Status = status, ErrorDetail = "simulated" });
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(10, "https://github.com/myorg/myrepo/pull/10"));

        var remote = BuildRemote(
            handler,
            BaseOpts with { PrDescription = CompletionOpts() },
            new CompletionPullRequestDescriptionGenerator(
                completion, CompletionOpts(), NullLogger<CompletionPullRequestDescriptionGenerator>.Instance));

        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.NotNull(outcome.PullRequestUrl);
        Assert.Contains("Static fallback description", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Completion_Throws_FallsBackToStaticAndOpensPr()
    {
        var completion = new FakeCompletionClient();
        completion.EnqueueBehavior((_, _) => throw new HttpRequestException("connection refused"));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(11, "https://github.com/myorg/myrepo/pull/11"));

        var remote = BuildRemote(
            handler,
            BaseOpts with { PrDescription = CompletionOpts() },
            new CompletionPullRequestDescriptionGenerator(
                completion, CompletionOpts(), NullLogger<CompletionPullRequestDescriptionGenerator>.Instance));

        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.NotNull(outcome.PullRequestUrl);
        Assert.Contains("Static fallback description", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Completion_EmptyText_FallsBackToStaticAndOpensPr()
    {
        var completion = new FakeCompletionClient();
        completion.EnqueueResult(new CompletionResult { Status = CompletionStatus.Succeeded, Text = "   " });
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(12, "https://github.com/myorg/myrepo/pull/12"));

        var remote = BuildRemote(
            handler,
            BaseOpts with { PrDescription = CompletionOpts() },
            new CompletionPullRequestDescriptionGenerator(
                completion, CompletionOpts(), NullLogger<CompletionPullRequestDescriptionGenerator>.Instance));

        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.NotNull(outcome.PullRequestUrl);
        Assert.Contains("Static fallback description", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Completion_Hanging_FallsBackToStaticWithinTimeout()
    {
        var completion = new FakeCompletionClient();
        completion.EnqueueBehavior(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return SuccessResult("too late");
        });
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(13, "https://github.com/myorg/myrepo/pull/13"));

        var opts = CompletionOpts();
        opts.Timeout = TimeSpan.FromMilliseconds(50);
        var remote = BuildRemote(
            handler,
            BaseOpts with { PrDescription = opts },
            new CompletionPullRequestDescriptionGenerator(
                completion, opts, NullLogger<CompletionPullRequestDescriptionGenerator>.Instance));

        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(outcome.PullRequestUrl);
        Assert.Contains("Static fallback description", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Agentic_SandboxProvisioningFailure_FallsBackToStaticAndOpensPr()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(14, "https://github.com/myorg/myrepo/pull/14"));

        var agenticOpts = new PrDescriptionOptions
        {
            Enabled = true,
            Strategy = PrDescriptionStrategy.Agentic,
            SandboxImageReference = "test-image",
        };
        var remote = BuildRemote(
            handler,
            BaseOpts with { PrDescription = agenticOpts },
            new LlmPullRequestDescriptionGenerator(
                new ThrowingSandboxProvider(),
                new FakeSingleAgentRegistry(new FixedOutputAgentRunner("unused")),
                new NullCredentialProvider(),
                agenticOpts,
                NullLogger<LlmPullRequestDescriptionGenerator>.Instance));

        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.NotNull(outcome.PullRequestUrl);
        Assert.Contains("Static fallback description", handler.RequestBodies[0]);
    }

    // -------------------------------------------------------------------------
    // Squash body and rendered facts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Completion_SquashMerge_UsesGeneratedContentForCommitMessage()
    {
        var completion = new FakeCompletionClient();
        completion.EnqueueResult(SuccessResult(
            "This PR adds frobnicate support.\n\n- Adds the Frobnicate entry point.\n- [ ] Verify frobnicate manually"));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(15, "https://github.com/myorg/myrepo/pull/15"));
        handler.Enqueue(PullRequestCommitsResponse(
            """
            [
              {
                "commit": {
                  "message": "feat: add frobnicate support\n\nCodeyBox-Prompt-Revision: 5\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>"
                }
              }
            ]
            """));
        handler.Enqueue(MergeOkResponse("squash-sha"));

        var opts = CompletionOpts();
        var remote = BuildRemote(
            handler,
            BaseOpts with { AutoMerge = true, MergeMethod = "squash", PrDescription = opts },
            new CompletionPullRequestDescriptionGenerator(
                completion, opts, NullLogger<CompletionPullRequestDescriptionGenerator>.Instance));

        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.Equal("squash-sha", outcome.MergedSha);
        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("Add frobnicate support.", message);
        Assert.Contains("Frobnicate entry point", message);
    }

    [Fact]
    public async Task GeneratedBody_ContainsWorkItemIdChangedFilesAndMarker()
    {
        var completion = new FakeCompletionClient();
        completion.EnqueueResult(SuccessResult("Generated narrative about the change."));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(16, "https://github.com/myorg/myrepo/pull/16"));

        var opts = CompletionOpts();
        var remote = BuildRemote(
            handler,
            BaseOpts with { PrDescription = opts },
            new CompletionPullRequestDescriptionGenerator(
                completion, opts, NullLogger<CompletionPullRequestDescriptionGenerator>.Instance));

        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        var body = handler.RequestBodies[0];
        Assert.Contains(TestItemId.ToString(), body);
        Assert.Contains("src/Frobnicate.cs", body);
        Assert.Contains(PrDescriptionBody.MachineGeneratedMarker, body);
        Assert.Contains("Generated narrative about the change.", body);
    }

    [Fact]
    public async Task StaticBody_ContainsWorkItemIdAndChangedFiles()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(17, "https://github.com/myorg/myrepo/pull/17"));

        var remote = BuildRemote(handler, BaseOpts with
        {
            PrDescription = new PrDescriptionOptions { Enabled = false },
        });

        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        var body = handler.RequestBodies[0];
        Assert.Contains(TestItemId.ToString(), body);
        Assert.Contains("src/Frobnicate.cs", body);
        Assert.Contains("Static fallback description", body);
        Assert.DoesNotContain(PrDescriptionBody.MachineGeneratedMarker, body);
    }

    [Fact]
    public async Task RetryWithGeneratedBody_TreatedAsGeneratedForSquash()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        var generatedBody = PrDescriptionBody.BuildGeneratedBody(
            TestItemId.ToString(),
            SampleRequest.DiffStat,
            "Retry narrative about frobnicate.");
        handler.Enqueue(PullRequestResponse(
            42,
            "https://github.com/myorg/myrepo/pull/42",
            "Add frobnicate support (#42)",
            generatedBody + "\n\n---\n*Co-Authored-By: CodeyBox <noreply@codeybox.invalid>*"));
        handler.Enqueue(PullRequestCommitsResponse(
            """
            [
              {
                "commit": {
                  "message": "feat: add frobnicate support\n\nCodeyBox-Prompt-Revision: 6\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>"
                }
              }
            ]
            """));
        handler.Enqueue(MergeOkResponse("merged-after-retry"));

        var remote = BuildRemote(
            handler,
            BaseOpts with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PrDescription = CompletionOpts(),
            },
            descriptionGenerator: null);
        var request = SampleRequest with { ExistingPullRequestNumber = 42 };

        await remote.CompleteAsync(request, CancellationToken.None);

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString()!;
        Assert.Contains("Retry narrative about frobnicate.", message);
    }

    // -------------------------------------------------------------------------
    // Commit message plumbing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GitHost_DefaultCommitMessages_ReturnsEmpty()
    {
        // The default interface implementation keeps existing fakes working.
        IGitHost gitHost = new FakeGitHost();
        var messages = await gitHost.GetCommitMessagesAsync("repo", "main", "work");
        Assert.Empty(messages);
    }

    [Fact]
    public async Task LocalGitHost_GetCommitMessagesAsync_ReturnsWorkBranchMessages()
    {
        if (!HasGit())
            return; // git CLI unavailable — nothing to exercise.

        // The ambient harness may inject GIT_CONFIG_* (e.g.
        // safe.bareRepository=explicit) that changes bare-repo discovery.
        // Snapshot and clear them so this test exercises plain git behaviour.
        using var gitConfigScope = TestSupport.AmbientGitConfigScope.Clear();
        var root = Path.Combine(Path.GetTempPath(), "prdesc-commits-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            const string repoId = "repo123";
            var bare = Path.Combine(root, repoId + ".git");
            await RunGit(root, "init", "--bare", bare);

            var work = Path.Combine(root, "work");
            await RunGit(root, "clone", bare, work);
            await RunGit(work, "config", "user.email", "test@example");
            await RunGit(work, "config", "user.name", "test");
            await RunGit(work, "checkout", "-b", "main");
            await File.WriteAllTextAsync(Path.Combine(work, "base.txt"), "base\n");
            await RunGit(work, "add", ".");
            await RunGit(work, "commit", "-m", "base commit");
            await RunGit(work, "push", "-u", "origin", "main");
            await RunGit(work, "checkout", "-b", "work");
            await File.WriteAllTextAsync(Path.Combine(work, "feat.txt"), "feat\n");
            await RunGit(work, "add", ".");
            await RunGit(work, "commit", "-m", "feat: agent did the thing\n\nBody of the agent commit.");
            await File.WriteAllTextAsync(Path.Combine(work, "feat2.txt"), "feat2\n");
            await RunGit(work, "add", ".");
            await RunGit(work, "commit", "-m", "fix: follow-up polish");
            await RunGit(work, "push", "-u", "origin", "work");

            var host = new LocalGitHost(
                new LocalGitHostOptions { RootDirectory = root },
                NullLogger<LocalGitHost>.Instance);
            var messages = await host.GetCommitMessagesAsync(repoId, "main", "work", CancellationToken.None);

            var joined = string.Join("\n", messages);
            Assert.Contains("feat: agent did the thing", joined);
            Assert.Contains("fix: follow-up polish", joined);
            Assert.DoesNotContain("base commit", joined);
            // Documented contract is oldest first: the first work-branch
            // commit must precede the follow-up.
            Assert.Equal(2, messages.Count);
            Assert.StartsWith("feat: agent did the thing", messages[0]);
            Assert.StartsWith("fix: follow-up polish", messages[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static PrDescriptionOptions CompletionOpts() => new()
    {
        Enabled = true,
        Strategy = PrDescriptionStrategy.Completion,
        Timeout = TimeSpan.FromSeconds(30),
        CompletionEndpoint = "https://llm.example/v1/chat",
        CompletionModel = "test-model",
    };

    private static PullRequestDescriptionRequest GeneratorRequest() => new()
    {
        DiffSummary = "src/Foo.cs | 10 ++++",
        FullDiff = "diff --git a/src/Foo.cs b/src/Foo.cs\n+added line",
        Title = "Add feature X",
        Prompt = "Implement feature X as described.",
    };

    private static CompletionResult SuccessResult(string text) =>
        new() { Status = CompletionStatus.Succeeded, Text = text };

    private static Project GitHubProject(ProjectPrDescription prDescription) => new()
    {
        Id = new ProjectId("prdesc-test"),
        DisplayName = "PRDesc",
        RepositoryUrl = "https://github.com/myorg/myrepo.git",
        Upstream = new ProjectUpstream
        {
            Kind = "github",
            GitHubOwner = "myorg",
            GitHubRepository = "myrepo",
            TokenEnvVar = TestTokenEnvVar,
            PrDescription = prDescription,
        },
    };

    private static UpstreamRemoteFactory BuildFactory(
        FakeHttpMessageHandler handler,
        ICompletionClient? completionClient,
        ISandboxProvider? sandboxes = null,
        IAgentRegistry? agents = null,
        ICredentialProvider? credentials = null) =>
        new(
            gitHost: new FakeGitHost(),
            httpClientFactory: new FakeHttpClientFactory(handler),
            githubLog: NullLogger<GitHubUpstreamRemote>.Instance,
            sandboxes: sandboxes ?? new InProcessFakeSandboxProvider(),
            agents: agents ?? new FakeSingleAgentRegistry(new FixedOutputAgentRunner("unused")),
            credentials: credentials ?? new NullCredentialProvider(),
            generatorLog: NullLogger<LlmPullRequestDescriptionGenerator>.Instance,
            completionClient: completionClient,
            completionLog: NullLogger<CompletionPullRequestDescriptionGenerator>.Instance);

    private static GitHubUpstreamRemote BuildRemote(
        FakeHttpMessageHandler handler,
        GitHubUpstreamOptions opts,
        IPullRequestDescriptionGenerator? descriptionGenerator = null) =>
        new(
            new FakeGitHost(),
            new FakeHttpClientFactory(handler),
            NullLogger<GitHubUpstreamRemote>.Instance,
            opts,
            descriptionGenerator: descriptionGenerator);

    private static HttpResponseMessage PrCreatedResponse(int number, string htmlUrl) =>
        new(HttpStatusCode.Created)
        {
            Content = new StringContent(
                $$"""{"number":{{number}},"html_url":"{{htmlUrl}}"}""",
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage PullRequestResponse(int number, string htmlUrl, string title, string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { number, html_url = htmlUrl, title, body }),
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage PullRequestCommitsResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage MergeOkResponse(string sha) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"sha":"{{sha}}","merged":true,"message":"Pull Request successfully merged"}""",
                Encoding.UTF8, "application/json"),
        };

    private static bool HasGit()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {p.ExitCode}): {stderr}");
    }
}

internal sealed class FakeCompletionClient : ICompletionClient
{
    public List<CompletionRequest> Requests { get; } = [];
    private readonly Queue<Func<CompletionRequest, CancellationToken, Task<CompletionResult>>> _behaviors = new();

    public void EnqueueResult(CompletionResult result) =>
        _behaviors.Enqueue((_, _) => Task.FromResult(result));

    public void EnqueueBehavior(Func<CompletionRequest, CancellationToken, Task<CompletionResult>> behavior) =>
        _behaviors.Enqueue(behavior);

    public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        if (_behaviors.Count == 0)
            return Task.FromResult(new CompletionResult { Status = CompletionStatus.ServerError, ErrorDetail = "no queued result" });
        return _behaviors.Dequeue()(request, ct);
    }
}

internal sealed class ThrowingSandboxProvider : ISandboxProvider
{
    public string Name => "throwing";
    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
        throw new InvalidOperationException("Simulated sandbox provisioning failure");
    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
    public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
}
