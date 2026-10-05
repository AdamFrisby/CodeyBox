using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the commit-attribution policy (<c>CodeyBox:CommitAttribution</c>
/// + per-project override): every writer (trailer composers, prompts, PR
/// bodies, squash messages) routes through <see cref="CommitAttributionPolicy"/>,
/// disabled lines disappear everywhere, and prompt-revision tracking keeps
/// working from the DB when trailers are off.
/// </summary>
public sealed class CommitAttributionTests
{
    private static readonly WorkItemId TestItemId =
        new(Guid.Parse("22222222-3333-4444-5555-666666666666"));

    private static CommitAttributionPolicy Policy(
        bool coAuthoredBy = true,
        bool codeyBoxTrailers = true,
        bool pullRequestFooter = true)
        => new(new CommitAttributionSnapshot(new CommitAttributionOptions
        {
            CoAuthoredBy = coAuthoredBy,
            CodeyBoxTrailers = codeyBoxTrailers,
            PullRequestFooter = pullRequestFooter,
        }));

    private static Project TestProject(CommitAttributionOverride? attribution = null) => new()
    {
        Id = new ProjectId("alpha"),
        DisplayName = "Alpha",
        RepositoryUrl = "https://example.com/repo.git",
        Audit = new ProjectAudit { Languages = [], AuditTypes = [] },
        CommitAttribution = attribution,
    };

    // -- trailer composition ------------------------------------------------

    [Fact]
    public void Compose_CoAuthoredByOff_OmitsCoAuthorKeepsCodeyBoxLines()
    {
        var trailer = CodeyBoxTrailers.Compose(
            TestItemId, AgentKind.Claude, "m",
            includeCoAuthoredBy: false, includeCodeyBoxTrailers: true);
        Assert.Contains($"CodeyBox-WorkItem: {TestItemId}", trailer);
        Assert.Contains("CodeyBox-Agent:", trailer);
        Assert.DoesNotContain("Co-Authored-By:", trailer);
    }

    [Fact]
    public void Compose_CodeyBoxTrailersOff_OmitsCodeyBoxLinesKeepsCoAuthor()
    {
        var trailer = CodeyBoxTrailers.Compose(
            TestItemId, AgentKind.Claude, "m",
            fallbackHistory: null, promptRevisionAtDispatch: 7,
            includeCoAuthoredBy: true, includeCodeyBoxTrailers: false);
        Assert.DoesNotContain("CodeyBox-WorkItem:", trailer);
        Assert.DoesNotContain("CodeyBox-Agent:", trailer);
        Assert.DoesNotContain("CodeyBox-Prompt-Revision:", trailer);
        Assert.Contains("Co-Authored-By: CodeyBox", trailer);
    }

    [Fact]
    public void Compose_BothOff_ReturnsEmpty()
    {
        var trailer = CodeyBoxTrailers.Compose(
            TestItemId, AgentKind.Claude, "m",
            includeCoAuthoredBy: false, includeCodeyBoxTrailers: false);
        Assert.True(string.IsNullOrWhiteSpace(trailer));
    }

    [Fact]
    public void ComposeMechanical_CodeyBoxTrailersOff_OmitsWorkItemAndFixer()
    {
        var trailer = CodeyBoxTrailers.ComposeMechanical(
            TestItemId, "fixer",
            promptRevisionAtDispatch: 3,
            includeCoAuthoredBy: true, includeCodeyBoxTrailers: false);
        Assert.DoesNotContain("CodeyBox-WorkItem:", trailer);
        Assert.DoesNotContain("CodeyBox-Mechanical-Fixer:", trailer);
        Assert.DoesNotContain("CodeyBox-Prompt-Revision:", trailer);
        Assert.Contains("Co-Authored-By: CodeyBox", trailer);
    }

    // -- host strip ----------------------------------------------------------

    [Fact]
    public void Strip_RemovesDisabledTrailerByExactKey_LeavesProseMention()
    {
        var message = "Fix foo\n\nCodeyBox-Prompt-Revision: 5\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>\n";
        var stripped = CommitAttributionPolicy.StripDisabledTrailers(
            message, includeCoAuthoredBy: true, includeCodeyBoxTrailers: false);
        Assert.DoesNotContain("CodeyBox-Prompt-Revision: 5", stripped);
        Assert.Contains("Co-Authored-By: CodeyBox", stripped);
    }

    [Fact]
    public void Strip_LeavesProseThatMerelyMentionsKey()
    {
        var message = "See CodeyBox-Prompt-Revision for details\n\nNothing to strip here";
        var stripped = CommitAttributionPolicy.StripDisabledTrailers(
            message, includeCoAuthoredBy: false, includeCodeyBoxTrailers: false);
        Assert.Contains("See CodeyBox-Prompt-Revision for details", stripped);
    }

    [Fact]
    public void Strip_CoAuthoredByOff_RemovesOnlyCoAuthorLine()
    {
        var message = "Subject\n\nCodeyBox-WorkItem: 123\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>\n";
        var stripped = CommitAttributionPolicy.StripDisabledTrailers(
            message, includeCoAuthoredBy: false, includeCodeyBoxTrailers: true);
        Assert.Contains("CodeyBox-WorkItem: 123", stripped);
        Assert.DoesNotContain("Co-Authored-By:", stripped);
    }

    // -- policy resolution ----------------------------------------------------

    [Fact]
    public void Resolve_PerProjectOverrideBeatsHostDefault()
    {
        var policy = Policy(coAuthoredBy: true, codeyBoxTrailers: true, pullRequestFooter: true);
        var project = TestProject(new CommitAttributionOverride { CoAuthoredBy = false });
        var resolved = policy.Resolve(project);
        Assert.False(resolved.IncludeCoAuthoredBy);
        Assert.True(resolved.IncludeCodeyBoxTrailers);
        Assert.True(resolved.IncludePullRequestFooter);
    }

    [Fact]
    public void Resolve_HotReloadTakesEffectForNextCommit()
    {
        var snapshot = new CommitAttributionSnapshot(new CommitAttributionOptions());
        var policy = new CommitAttributionPolicy(snapshot);
        Assert.True(policy.CurrentDefault.IncludeCoAuthoredBy);
        snapshot.Replace(new CommitAttributionOptions { CoAuthoredBy = false });
        Assert.False(policy.Resolve((Project?)null).IncludeCoAuthoredBy);
    }

    [Fact]
    public void ComposeMessage_EmptyTrailerBlock_ReturnsSubjectAlone()
    {
        var policy = Policy(coAuthoredBy: false, codeyBoxTrailers: false);
        var message = policy.ComposeMessage("codeybox: title", string.Empty, policy.Resolve((Project?)null));
        Assert.Equal("codeybox: title", message);
    }

    // -- prompts ---------------------------------------------------------------

    [Fact]
    public void InitialPrompt_IncludesTrailerInstructionsByDefault()
    {
        var prompt = new PromptComposer().BuildInitialWorkPrompt("do things");
        Assert.Contains(CodeyBoxTrailers.PromptRevisionTrailerKey, prompt);
        Assert.Contains("Co-Authored-By: CodeyBox", prompt);
    }

    [Fact]
    public void InitialPrompt_OmitsTrailerInstructionsWhenDisabled()
    {
        var off = new CommitAttribution(false, false, true);
        var prompt = new PromptComposer().BuildInitialWorkPrompt("do things", attribution: off);
        Assert.DoesNotContain(CodeyBoxTrailers.PromptRevisionTrailerKey, prompt);
        Assert.DoesNotContain("Co-Authored-By: CodeyBox", prompt);
        Assert.Contains("do things", prompt);
    }

    [Fact]
    public void InitialPrompt_CoAuthorOff_KeepsPromptRevisionOmitsCoAuthor()
    {
        var partial = new CommitAttribution(false, true, true);
        var prompt = new PromptComposer().BuildInitialWorkPrompt("do things", attribution: partial);
        Assert.Contains(CodeyBoxTrailers.PromptRevisionTrailerKey, prompt);
        Assert.DoesNotContain("Co-Authored-By: CodeyBox", prompt);
    }

    [Fact]
    public void ReworkPrompt_OmitsTrailerInstructionsWhenDisabled()
    {
        var off = new CommitAttribution(false, false, true);
        var prompt = ReworkPromptBuilder.Build("orig", [], 1, 3, attribution: off);
        Assert.DoesNotContain(CodeyBoxTrailers.PromptRevisionTrailerKey, prompt);
        Assert.DoesNotContain("Co-Authored-By: CodeyBox", prompt);
    }

    [Fact]
    public void ReworkPrompt_IncludesTrailerInstructionsByDefault()
    {
        var prompt = ReworkPromptBuilder.Build("orig", [], 1, 3);
        Assert.Contains(CodeyBoxTrailers.PromptRevisionTrailerKey, prompt);
        Assert.Contains("Co-Authored-By: CodeyBox", prompt);
    }

    [Fact]
    public void SessionDirective_OmittedWhenTrailersDisabled()
    {
        var off = new CommitAttribution(true, false, true);
        var prompt = PipelineRunner.AppendSessionPromptRevisionDirective("base", 4, off);
        Assert.Equal("base", prompt);
    }

    [Fact]
    public void SelfReviewPrompt_OmitsRevisionDirectiveWhenTrailersDisabled()
    {
        var off = new CommitAttribution(true, false, true);
        var prompt = PipelineRunner.BuildPreemptiveSelfReviewPrompt("criteria", 4, off);
        Assert.DoesNotContain(CodeyBoxTrailers.PromptRevisionTrailerKey, prompt);
        Assert.Contains("criteria", prompt);
    }

    // -- auditor -----------------------------------------------------------------

    private static AuditContext Ctx(int? dispatched, bool trailersEnabled, int? current = null) => new(
        WorkItemId.New(), "work", "main", Iteration: 1, OriginalPrompt: "p",
        PromptRevisionAtDispatch: dispatched,
        CodeyBoxTrailersEnabled: trailersEnabled,
        PromptRevisionCurrent: current);

    [Fact]
    public async Task Auditor_TrailersDisabled_SkippedWithExplicitReason()
    {
        var sandbox = new StubSandbox(_ => new SandboxExecResult(0, "", ""));
        var result = await new PromptRevisionTrailerAuditor()
            .RunAsync(sandbox, "/work", Ctx(dispatched: 5, trailersEnabled: false, current: 5));
        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("skipped: trailers disabled by config", finding.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Auditor_TrailersDisabled_StaleDetectedFromDb()
    {
        var sandbox = new StubSandbox(_ => new SandboxExecResult(0, "", ""));
        var result = await new PromptRevisionTrailerAuditor()
            .RunAsync(sandbox, "/work", Ctx(dispatched: 1, trailersEnabled: false, current: 2));
        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("stale", finding.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Composer_TrailersDisabled_RemovesAuditorFromPanel()
    {
        var trailerAuditor = new PromptRevisionTrailerAuditor();
        var policy = Policy(codeyBoxTrailers: false);
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            new IAuditor[] { trailerAuditor },
            NullLogger<ProjectAuditorComposer>.Instance,
            policy);
        var project = TestProject();
        var auditors = composer.Compose(project, new StubAgent());
        Assert.DoesNotContain(auditors, a => a.Name == PromptRevisionTrailerAuditor.AuditorName);
    }

    [Fact]
    public void Composer_Default_KeepsAuditorInPanel()
    {
        var trailerAuditor = new PromptRevisionTrailerAuditor();
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            new IAuditor[] { trailerAuditor },
            NullLogger<ProjectAuditorComposer>.Instance);
        var project = TestProject();
        var auditors = composer.Compose(project, new StubAgent());
        Assert.Contains(auditors, a => a.Name == PromptRevisionTrailerAuditor.AuditorName);
    }

    // -- GitHub remote ---------------------------------------------------------------

    private static GitHubUpstreamRemote BuildRemote(
        IGitHost gitHost,
        FakeHttpMessageHandler handler,
        CommitAttribution attribution)
    {
        var factory = new FakeHttpClientFactory(handler, userAgent: "codeybox");
        return new GitHubUpstreamRemote(
            gitHost,
            factory,
            NullLogger<GitHubUpstreamRemote>.Instance,
            new GitHubUpstreamOptions
            {
                Owner = "myorg",
                Repository = "myrepo",
                Token = "test-token-not-a-real-pat",
                MergeMethod = "squash",
                AutoMerge = true,
            },
            descriptionGenerator: null,
            attribution: attribution);
    }

    private static UpstreamCompletionRequest SquashRequest => new()
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

    private static System.Net.Http.HttpResponseMessage JsonCreated(string json) =>
        new(System.Net.HttpStatusCode.Created)
        { Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GitHub_AllDisabled_PrBodyHasNoFooter_AndSquashHasNoTrailers()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonCreated("""{"number":7,"html_url":"https://github.com/myorg/myrepo/pull/7"}"""));
        handler.Enqueue(JsonCreated("[]"));
        handler.Enqueue(JsonCreated("""{"sha":"abc123sha","merged":true,"message":"ok"}"""));

        var remote = BuildRemote(gitHost, handler, new CommitAttribution(false, false, false));
        await remote.CompleteAsync(SquashRequest, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        using var prBody = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[0]);
        var body = prBody.RootElement.GetProperty("body").GetString() ?? string.Empty;
        Assert.DoesNotContain("Co-Authored-By: CodeyBox", body);
        Assert.DoesNotContain("Generated with", body);

        using var mergeBody = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString() ?? string.Empty;
        Assert.DoesNotContain("Co-Authored-By: CodeyBox", message);
        Assert.DoesNotContain("CodeyBox-Prompt-Revision:", message);
    }

    [Fact]
    public async Task GitHub_CoAuthorOff_TrailersOn_SquashKeepsRevisionOmitsCoAuthor()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonCreated("""{"number":7,"html_url":"https://github.com/myorg/myrepo/pull/7"}"""));
        handler.Enqueue(JsonCreated(
            """[{"commit": {"message": "feat: x\n\nCodeyBox-Prompt-Revision: 4\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>"}}]"""));
        handler.Enqueue(JsonCreated("""{"sha":"abc123sha","merged":true,"message":"ok"}"""));

        var remote = BuildRemote(gitHost, handler, new CommitAttribution(false, true, true));
        await remote.CompleteAsync(SquashRequest, CancellationToken.None);

        using var mergeBody = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[2]);
        var message = mergeBody.RootElement.GetProperty("commit_message").GetString() ?? string.Empty;
        Assert.Contains("CodeyBox-Prompt-Revision: 4", message);
        Assert.DoesNotContain("Co-Authored-By: CodeyBox", message);
    }

    private sealed class StubSandbox : ISandbox
    {
        private readonly Func<SandboxExec, SandboxExecResult> _handler;
        public StubSandbox(Func<SandboxExec, SandboxExecResult> handler) => _handler = handler;
        public string Id => "stub";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(_handler(exec));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(true, "ok", "done", null));
    }
}
