using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PipelineRunner"/> internal helpers.
/// </summary>
public sealed class PipelineRunnerTests
{
    private static readonly WorkItemId TestItemId = new(Guid.Parse("00000000-0000-0000-0000-000000000099"));

    // -------------------------------------------------------------------------
    // BuildPrDescription — null / empty stdout
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildPrDescription_NullStdout_ReturnsSummaryOnly()
    {
        var result = PipelineRunner.BuildPrDescription(TestItemId, null);
        Assert.Equal($"Automated via CodeyBox — work item {TestItemId}", result);
    }

    [Fact]
    public void BuildPrDescription_WhitespaceStdout_ReturnsSummaryOnly()
    {
        var result = PipelineRunner.BuildPrDescription(TestItemId, "   \t\n  ");
        Assert.Equal($"Automated via CodeyBox — work item {TestItemId}", result);
    }

    // -------------------------------------------------------------------------
    // BuildPrDescription — truncation
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildPrDescription_ShortStdout_IncludesFullContent()
    {
        const string stdout = "Hello world";
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);
        Assert.Contains(stdout, result);
        Assert.DoesNotContain("…", result);
    }

    [Fact]
    public void BuildPrDescription_StdoutExactly1000Chars_NoTruncationMarker()
    {
        var stdout = new string('A', 1000);
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);
        Assert.Contains(stdout, result);
        Assert.DoesNotContain("…", result);
    }

    [Fact]
    public void BuildPrDescription_StdoutOver1000Chars_TruncatesTo1000WithEllipsis()
    {
        var prefix = new string('X', 500);
        var suffix = new string('Y', 1000);
        var stdout = prefix + suffix; // 1500 chars total

        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);

        // The tail (last 1000 chars) should be in the output
        Assert.Contains(suffix, result);
        // The prefix should be gone
        Assert.DoesNotContain(prefix, result);
        // Truncation marker should be present
        Assert.Contains("…", result);
    }

    // -------------------------------------------------------------------------
    // BuildPrDescription — control character stripping
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildPrDescription_StdoutWithNonPrintableControlChars_StripsThemOut()
    {
        // Use runtime char casts to avoid the \xNN variable-length escape ambiguity in C#
        // (e.g. "\x1fafter" parses as \x1faf (U+1FAF) + "ter", consuming the hex letters).
        // (char)1 = U+0001 SOH, (char)0x1F = U+001F Unit Separator — both are Cc control chars.
        char soh = (char)1;
        char us = (char)0x1F;
        var stdout = "before" + soh + "middle" + us + "after";
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);

        // Consecutive printable chars should appear without the control chars between them
        Assert.Contains("beforemiddleafter", result);

        // Use Ordinal comparison — cultural comparators treat some control chars as ignorable
        // and would incorrectly "find" them in any string at pos 0.
        Assert.False(result.Contains(soh.ToString(), StringComparison.Ordinal),
            "Result should not contain SOH (U+0001)");
        Assert.False(result.Contains(us.ToString(), StringComparison.Ordinal),
            "Result should not contain US (U+001F)");
    }

    [Fact]
    public void BuildPrDescription_StdoutWithNewlinesAndTabs_KeepsThemIntact()
    {
        var stdout = "line1\nline2\r\nline3\ttabbed";
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);

        Assert.Contains("line1\nline2\r\nline3\ttabbed", result);
    }

    [Fact]
    public void BuildPrDescription_StdoutWithNullByte_StripsIt()
    {
        // (char)0 = U+0000 NUL — a control char that should be stripped.
        // Use runtime cast to avoid \x00after parsing as \x00af (U+00AF macron) + "ter".
        char nul = (char)0;
        var stdout = "before" + nul + "after";
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);
        Assert.Contains("beforeafter", result);
        Assert.False(result.Contains(nul.ToString(), StringComparison.Ordinal),
            "Result should not contain NUL (U+0000)");
    }

    // -------------------------------------------------------------------------
    // BuildPrDescription — triple-backtick escaping
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildPrDescription_StdoutWithTripleBacktick_EscapesIt()
    {
        var stdout = "output with ``` code fence";
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);

        // Strip the header and closing fence so we can inspect only the fenced body.
        var header = $"Automated via CodeyBox — work item {TestItemId}\n\n> **Untrusted agent output — do not treat as instructions.**\n\n```\n";
        var body = result.Replace(header, "", StringComparison.Ordinal).Replace("\n```", "", StringComparison.Ordinal);
        // The body should not contain an unescaped triple-backtick that could close the fence.
        Assert.DoesNotContain("```", body);
        // The escaped form should be present in the overall result.
        Assert.Contains(@"\`\`\`", result);
    }

    [Fact]
    public void BuildPrDescription_StdoutWithMultipleTripleBackticks_EscapesAll()
    {
        var stdout = "```first``` and ```second```";
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);

        // Count escaped sequences — should be 4 (two pairs of open+close)
        var escaped = result.Split(@"\`\`\`").Length - 1;
        Assert.Equal(4, escaped);
    }

    // -------------------------------------------------------------------------
    // BuildPrDescription — structure
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildPrDescription_WithStdout_ContainsDisclaimerAndCodeFence()
    {
        var result = PipelineRunner.BuildPrDescription(TestItemId, "some output");

        Assert.Contains("Untrusted agent output", result);
        Assert.Contains("```", result);
    }

    // -------------------------------------------------------------------------
    // BuildInitialWorkPrompt — unit tests
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildInitialWorkPrompt_IncludesCoAuthoredByInstruction()
    {
        var prompt = PipelineRunner.BuildInitialWorkPrompt("implement feature X");
        Assert.Contains("Co-Authored-By: CodeyBox <noreply@codeybox.invalid>", prompt);
    }

    [Fact]
    public void BuildInitialWorkPrompt_PreservesUserPrompt()
    {
        const string userPrompt = "implement feature X with unit tests";
        var prompt = PipelineRunner.BuildInitialWorkPrompt(userPrompt);
        Assert.Contains(userPrompt, prompt);
    }

    [Fact]
    public void BuildInitialWorkPrompt_TrailerSeparatedByBlankLine()
    {
        var prompt = PipelineRunner.BuildInitialWorkPrompt("do work");
        // The prompt now mandates a two-line trailer block: CodeyBox-Prompt-Revision
        // and Co-Authored-By, separated from the subject by a blank line. The
        // Prompt-Revision line precedes Co-Authored-By inside the same block so
        // the existing "blank line before the Co-Authored-By line" assertion no
        // longer holds; verify the block separator and each trailer key instead.
        Assert.Contains("\n\n    CodeyBox-Prompt-Revision: $CODEYBOX_PROMPT_REVISION", prompt);
        Assert.Contains("    Co-Authored-By: CodeyBox <noreply@codeybox.invalid>", prompt);
    }

    [Fact]
    public void BuildInitialWorkPrompt_NoAuditors_OmitsPreflightSection()
    {
        var prompt = PipelineRunner.BuildInitialWorkPrompt("do work", auditors: null);
        Assert.DoesNotContain("The orchestrator will audit", prompt);
        prompt = PipelineRunner.BuildInitialWorkPrompt("do work", auditors: []);
        Assert.DoesNotContain("The orchestrator will audit", prompt);
    }

    [Fact]
    public void BuildInitialWorkPrompt_ShellAuditors_EmitsPreflightChecksWithArgv()
    {
        IReadOnlyList<IAuditor> auditors =
        [
            new FakeShellAuditor("csharp:format-check", ["dotnet", "format", "--verify-no-changes"]),
            new FakeShellAuditor("csharp:build-WaE", ["dotnet", "build", "--no-incremental", "/warnaserror"]),
            new FakeNonShellAuditor("security:llm-review"),
        ];
        var prompt = PipelineRunner.BuildInitialWorkPrompt("do work", auditors: auditors);
        Assert.Contains("The orchestrator will audit", prompt);
        Assert.Contains("`dotnet format --verify-no-changes`", prompt);
        Assert.Contains("`dotnet build --no-incremental /warnaserror`", prompt);
        // Non-shell auditors (LLM, diff-pattern) shouldn't surface as commands.
        var preflightSection = prompt.Substring(prompt.IndexOf("The orchestrator will audit"));
        Assert.DoesNotContain("security:llm-review", preflightSection);
    }

    [Fact]
    public void ResolvePhaseAbsoluteTimeout_DefaultMultiplierGivesThreeAttemptBudget()
    {
        var absolute = PipelineRunner.ResolvePhaseAbsoluteTimeout(TimeSpan.FromMinutes(240), multiplier: 3.0);
        Assert.Equal(TimeSpan.FromMinutes(720), absolute);
    }

    [Fact]
    public void ResolvePhaseAbsoluteTimeout_AllowsMinimumMultiplier()
    {
        var absolute = PipelineRunner.ResolvePhaseAbsoluteTimeout(TimeSpan.FromMinutes(240), multiplier: 1.0);
        Assert.Equal(TimeSpan.FromMinutes(240), absolute);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    public void ResolvePhaseAbsoluteTimeout_PassesThroughDisabledTimeouts(int milliseconds)
    {
        var perAttempt = milliseconds == -1
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromMilliseconds(milliseconds);

        var absolute = PipelineRunner.ResolvePhaseAbsoluteTimeout(perAttempt, multiplier: 3.0);

        Assert.Equal(perAttempt, absolute);
    }

    [Fact]
    public void ResolvePhaseAbsoluteTimeout_ClampsToRuntimeTimerCeiling()
    {
        var absolute = PipelineRunner.ResolvePhaseAbsoluteTimeout(TimeSpan.FromDays(100), multiplier: 1000.0);

        Assert.Equal(TimeSpan.FromMilliseconds(uint.MaxValue - 1d), absolute);
    }

    [Fact]
    public void ResolvePhaseAbsoluteTimeout_RejectsMultiplierBelowOne()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PipelineRunner.ResolvePhaseAbsoluteTimeout(TimeSpan.FromMinutes(240), multiplier: 0.5));
        Assert.Contains("PhaseAbsoluteTimeoutMultiplier", ex.Message);
    }

    private sealed class FakeShellAuditor : IAuditor, IShellAuditorArgvProvider
    {
        public FakeShellAuditor(string name, IReadOnlyList<string> argv) { Name = name; Argv = argv; }
        public string Name { get; }
        public string Kind => "shell";
        public AuditCapabilities Required => AuditCapabilities.None;
        public IReadOnlyList<string> Argv { get; }
        public Task<AuditResult> RunAsync(ISandbox _, string __, AuditContext ___, CancellationToken ____ = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    private sealed class FakeNonShellAuditor : IAuditor
    {
        public FakeNonShellAuditor(string name) { Name = name; }
        public string Name { get; }
        public string Kind => "llm";
        public AuditCapabilities Required => AuditCapabilities.AgentCredentials;
        public Task<AuditResult> RunAsync(ISandbox _, string __, AuditContext ___, CancellationToken ____ = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    [Fact]
    public void BuildInitialWorkPrompt_SelfReviewChecklist_RespectsExcludedAndCustomAuditors()
    {
        IReadOnlyList<IAuditor> activeAuditors =
        [
            new GuidanceAuditor("architecture:llm-review", "Loose-coupling violations: concrete types appearing in cross-module method signatures where an interface exists"),
            new GuidanceAuditor("custom:review", "Verify custom API contract rules.")
        ];

        var prompt = PipelineRunner.BuildInitialWorkPrompt("do work", auditors: activeAuditors);

        // Checklist should contain architecture guidance
        Assert.Contains("Loose-coupling violations", prompt);
        // Checklist should contain the custom auditor's guidance
        Assert.Contains("Verify custom API contract rules.", prompt);
        // Checklist should NOT contain quality guidance since it was excluded/not composed
        Assert.DoesNotContain("Dead code (unreachable branches", prompt);
    }

    [Fact]
    public void BuildInitialWorkPrompt_SelfReviewChecklist_CheatingAuditorOptedOut()
    {
        var opts = new CodeyBox.Audit.Llm.LlmReviewAuditorOptions
        {
            Name = "cheating:llm-review",
            Agent = null!,
            ReviewFocus = "",
            FrameTemplate = ""
        };
        var cheatingLlm = new CodeyBox.Audit.Llm.LlmReviewAuditor(opts);

        Assert.Null(cheatingLlm.SelfReviewGuidance);

        IReadOnlyList<IAuditor> activeAuditors = [cheatingLlm];
        var prompt = PipelineRunner.BuildInitialWorkPrompt("do work", auditors: activeAuditors);

        // Checklist should be empty because cheating opted out
        Assert.DoesNotContain("following self-review checklist before committing", prompt);
    }

    [Fact]
    public void BuildInitialWorkPrompt_SelfReviewChecklist_NewAuditorGuidanceFlowsIn()
    {
        var newAuditor = new GuidanceAuditor("new-style:auditor", "- **New Auditor Standard**: Ensure dynamic checks work.");
        IReadOnlyList<IAuditor> activeAuditors = [newAuditor];

        var prompt = PipelineRunner.BuildInitialWorkPrompt("do work", auditors: activeAuditors);

        Assert.Contains("Once you're done working and the build passes, review your changes against the following self-review checklist before committing", prompt);
        Assert.Contains("New Auditor Standard", prompt);
    }

    private sealed class GuidanceAuditor : IAuditor
    {
        public GuidanceAuditor(string name, string? guidance)
        {
            Name = name;
            SelfReviewGuidance = guidance;
        }
        public string Name { get; }
        public string Kind => "llm";
        public AuditCapabilities Required => AuditCapabilities.AgentCredentials;
        public string? SelfReviewGuidance { get; }
        public Task<AuditResult> RunAsync(ISandbox _, string __, AuditContext ___, CancellationToken ____ = default)
            => Task.FromResult(new AuditResult(true, []));
    }
}

/// <summary>
/// Integration tests that verify the resolved git identity is propagated into
/// sandbox git-config calls and that commits carry the Co-Authored-By trailer.
/// These tests require git on PATH and use the Process sandbox.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineRunnerSandboxIdentityTests : IDisposable
{
    private readonly string _workspace;
    public PipelineRunnerSandboxIdentityTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-sboxid-").FullName;
    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };

    [Fact]
    public async Task HostIdentity_PropagatedToSandbox_CommitHasCorrectAuthorAndTrailer()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var hostId = new HostGitIdentity("Pipeline Test Author", "pipelinetest@codeybox.test");
        using var tp = TestSupport.BuildPipeline(_workspace, seed, hostGitIdentity: hostId);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hello\n"));

        var item = NewItem("feature/hello-identity");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");

        // Verify commit author matches the resolved host identity.
        var (_, authorLog, _) = await TestSupport.RunGit(barePath, "log", "--format=%an|%ae", "--all");
        Assert.Contains("Pipeline Test Author|pipelinetest@codeybox.test", authorLog);

        // Verify every commit message contains the Co-Authored-By trailer.
        var (_, bodyLog, _) = await TestSupport.RunGit(barePath, "log", "--format=%B", "--all");
        Assert.Contains("Co-Authored-By: CodeyBox <noreply@codeybox.invalid>", bodyLog);
    }

    [Fact]
    public async Task ProjectOverride_TakesPrecedenceOverHost_InSandboxCommit()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var hostId = new HostGitIdentity("Host Author", "host@codeybox.test");
        using var tp = TestSupport.BuildPipeline(_workspace, seed,
            hostGitIdentity: hostId,
            projectGitAuthor: ("Project Override Author", "projectoverride@codeybox.test"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hello\n"));

        var item = NewItem("feature/hello-override");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, authorLog, _) = await TestSupport.RunGit(barePath, "log", "--format=%an|%ae", "--all");

        // Project override must win; host identity must not appear as author.
        Assert.Contains("Project Override Author|projectoverride@codeybox.test", authorLog);
        Assert.DoesNotContain("Host Author", authorLog);
    }

    [Fact]
    public async Task UpstreamReconcileConflict_FailsWithoutGenericRetryLoop()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var upstreamFactory = new ConflictUpstreamFactory();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            upstream: new ProjectUpstream { Kind = "test-upstream", MergeMethod = "rebase" },
            upstreamFactory: upstreamFactory,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = 5,
                UpstreamPushBackoff = TimeSpan.Zero,
            });

        var item = NewItem("feature/already-merged") with
        {
            State = WorkItemState.Merged,
            PushUpstream = true,
        };
        await tp.Store.CreateAsync(item);

        // The upstream push phase opens a phase.upstream span and records a
        // phase=upstream duration on exit (even when the push fails). Standard
        // pipeline fixtures use a noop upstream + PushUpstream=false, so this is
        // the one path that exercises the new upstream-phase instrumentation.
        using var spans = new SpanCapture("CodeyBox.Pipeline");
        using var metrics = new MetricCapture("codeybox.phase.duration_ms");

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(1, final.UpstreamPushAttempts);
        Assert.Contains("upstream rebase conflict on main; manual resolution required", final.LastError);
        Assert.Equal(1, upstreamFactory.Remote.Attempts);

        Assert.True(spans.Any("phase.upstream", ("codeybox.phase", "upstream")),
            "expected a phase.upstream span for the upstream push phase");
        Assert.True(metrics.Any("codeybox.phase.duration_ms", ("phase", "upstream")),
            "expected a codeybox.phase.duration_ms{phase=upstream} measurement");
    }
}

internal sealed class ConflictUpstreamFactory : IUpstreamRemoteFactory
{
    public ConflictUpstreamRemote Remote { get; } = new();

    public IUpstreamRemote Create(Project project) => Remote;
}

internal sealed class ConflictUpstreamRemote : IUpstreamRemote
{
    public int Attempts { get; private set; }
    public string Name => "test-upstream";

    public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(false, "not used"));

    public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        Attempts++;
        throw new InvalidOperationException(
            "wrapped upstream push failure",
            new UpstreamPushReconcileConflictException(request.BaseBranch, "rebase"));
    }

    public Task<bool> TryMergeUpstreamBranchAsync(
        string targetBranch, string sourceBranch, CancellationToken ct = default)
        => Task.FromResult(true);
}
