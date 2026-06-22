using System.Text;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class AgenticConflictResolverTests
{
    private const string MarkerStart = "<<<<<<<";
    private const string MarkerMid = "=======";
    private const string MarkerEnd = ">>>>>>>";

    [Fact]
    public async Task ResolveAsync_TwoFileConflict_AgentResolvesBoth()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("alpha-base", "alpha-main", "alpha-work"));
        sandbox.AddConflictedFile("src/nested/b.txt", BuildSimpleConflict("beta-base", "beta-main", "beta-work"));

        var resolver = new AgenticConflictResolver();
        var runner = new FakeAgentResolverRunner(sb =>
        {
            // Agent resolves both files by interleaving both sides and stages them.
            sb.WriteFile("src/a.txt", "alpha-main + alpha-work\n");
            sb.WriteFile("src/nested/b.txt", "beta-main + beta-work\n");
            sb.GitAdd("src/a.txt");
            sb.GitAdd("src/nested/b.txt");
            return new AgentResult(true, "resolved both", null, null);
        });

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(1, result.IterationsUsed);
        Assert.Equal(["src/a.txt", "src/nested/b.txt"], result.ConflictFiles.ToArray());
        Assert.Same(runner, result.ChosenRunner);
        Assert.False(sandbox.HasMarkers("src/a.txt"));
        Assert.False(sandbox.HasMarkers("src/nested/b.txt"));
        Assert.Equal(2, sandbox.AddedFiles.Count);
        Assert.Equal(1, runner.InvocationCount);
    }

    [Fact]
    public async Task ResolveAsync_ResumableRunnerNeedingStructuredSessionId_ForcesStructuredCapture()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));
        var runner = new ResumableCaptureRecordingRunner();
        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1 }));

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal([true], runner.CaptureStructuredStreamCalls);
    }

    [Fact]
    public async Task ResolveAsync_WithSupervision_ReusesSessionHandleForQueuedInjection()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));
        var notifier = new AutoQueueConflictInjectionNotifier();
        var supervision = new AgentSupervisionService(
            () => new AgentSupervisionOptions { Enabled = true, InjectionQueueCapacity = 4 },
            notifier);
        notifier.Service = supervision;
        var runner = new RecordingConflictSessionRunner();
        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1 }),
            agentSupervision: supervision);

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase)
            {
                ProjectId = new ProjectId("project"),
            },
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(runner.OpenedSessionIds);
        Assert.Equal(2, runner.SendTurnSessionIds.Count);
        Assert.All(runner.SendTurnSessionIds, id => Assert.Equal(runner.OpenedSessionIds[0], id));
        Assert.Contains(notifier.Commands, c => c.Kind == "autonomous" && c.Phase == "conflict-rebase");
        Assert.Contains(notifier.Commands, c => c.Kind == "human-injection" && c.Phase == "conflict-rebase");
        Assert.Single(notifier.CompletedInjections);
    }

    [Fact]
    public async Task ResolveAsync_WrappedResumableRunner_ForcesStructuredCapture()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));
        var inner = new ResumableCaptureRecordingRunner();
        var wrapped = PromptPreprocessingAgentRunner.Wrap(
            inner,
            new AgentPromptPreprocessorChain([new NoopPreprocessor()]),
            WorkItemId.New(),
            AgentPromptPhase.Merge,
            1,
            NewProject());
        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1 }));

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(wrapped, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal([true], inner.CaptureStructuredStreamCalls);
    }

    [Fact]
    public async Task ResolveAsync_AgentLeavesMarkers_FailsWithReason()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 2 }));

        var runner = new FakeAgentResolverRunner(sb =>
        {
            // Agent claims success but the file still contains markers (regression).
            sb.GitAdd("src/a.txt");
            return new AgentResult(true, "lied about resolution", null, null);
        });

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("conflict markers remain", result.Summary, StringComparison.Ordinal);
        Assert.Contains("src/a.txt", result.Summary, StringComparison.Ordinal);
        Assert.Equal(2, result.IterationsUsed);
        Assert.Equal(2, runner.InvocationCount);
    }

    [Fact]
    public async Task ResolveAsync_LargeFileBeyond128KB_HandledWithoutTruncation()
    {
        var sandbox = new ConflictSandbox();
        var largeContent = BuildLargeConflict(targetBytes: 200 * 1024);
        var resolved = BuildLargeResolved(largeContent);
        Assert.True(Encoding.UTF8.GetByteCount(largeContent) > 128 * 1024, "test fixture must exceed legacy 128 KB cap");

        sandbox.AddConflictedFile("src/big.txt", largeContent);

        var resolver = new AgenticConflictResolver();
        var runner = new FakeAgentResolverRunner(sb =>
        {
            sb.WriteFile("src/big.txt", resolved);
            sb.GitAdd("src/big.txt");
            return new AgentResult(true, "resolved big", null, null);
        });

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.True(result.Success, result.Summary);
        Assert.False(sandbox.HasMarkers("src/big.txt"));
        Assert.Equal(resolved, sandbox.GetFileContent("src/big.txt"));
        Assert.True(Encoding.UTF8.GetByteCount(sandbox.GetFileContent("src/big.txt")) > 128 * 1024);
    }

    [Fact]
    public async Task ResolveAsync_FirstAttemptLeavesMarkers_SecondAttemptSucceeds()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 2 }));

        var attempts = 0;
        var runner = new FakeAgentResolverRunner(sb =>
        {
            attempts++;
            if (attempts == 1)
            {
                // First attempt: stages without removing markers.
                sb.GitAdd("src/a.txt");
                return new AgentResult(true, "first try", null, null);
            }
            sb.WriteFile("src/a.txt", "m + w\n");
            sb.GitAdd("src/a.txt");
            return new AgentResult(true, "second try", null, null);
        });

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(2, result.IterationsUsed);
        Assert.Equal(2, attempts);
        // Retry prompt mentions prior verification error.
        Assert.Equal(2, runner.PromptHistory.Count);
        Assert.Contains("retry", runner.PromptHistory[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_FirstCandidateFails_SecondCandidateSucceeds()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 2, MaxAttemptsPerAgent = 1 }));

        var first = new FakeAgentResolverRunner(_ =>
            new AgentResult(false, "rate limited", null, "429"))
        { Kind = new AgentKind("first") };
        var second = new FakeAgentResolverRunner(sb =>
        {
            sb.WriteFile("src/a.txt", "m + w\n");
            sb.GitAdd("src/a.txt");
            return new AgentResult(true, "ok", null, null);
        })
        { Kind = new AgentKind("second") };

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [
                new AgenticConflictResolverCandidate(first, Credential: null),
                new AgenticConflictResolverCandidate(second, Credential: null),
            ],
            CancellationToken.None);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("second", result.ChosenRunner?.Kind.Value);
        Assert.Equal(1, first.InvocationCount);
        Assert.Equal(1, second.InvocationCount);
    }

    [Fact]
    public async Task ResolveAsync_FailureClassificationResult_UsesLastFailedCandidate_NotAggregateTrail()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 2, MaxAttemptsPerAgent = 1 }));

        var first = new FakeAgentResolverRunner(_ =>
            new AgentResult(false, "agent exited 127", null, "env: 'codex': No such file or directory"))
        { Kind = new AgentKind("codex") };
        var second = new FakeAgentResolverRunner(_ =>
            new AgentResult(false, "agent exited 2", null, null))
        { Kind = new AgentKind("claude") };

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Merge),
            [
                new AgenticConflictResolverCandidate(first, Credential: null),
                new AgenticConflictResolverCandidate(second, Credential: null),
            ],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("agent exited 127", result.Summary, StringComparison.Ordinal);
        Assert.Same(second, result.FailureRunner);
        var classificationResult = Assert.IsType<AgentResult>(result.FailureClassificationResult);
        Assert.Equal("agent exited 2", classificationResult.Summary);

        var classification = ((IAgentRunner)second).ClassifyFailure(classificationResult);
        Assert.NotEqual(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public async Task ResolveAsync_FailureClassificationResult_PreservesEarlierTransientCandidate()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 2, MaxAttemptsPerAgent = 1 }));

        var first = new FakeAgentResolverRunner(_ =>
            new AgentResult(false, "agent transport failed", null, "Transport channel closed"))
        { Kind = new AgentKind("codex") };
        var second = new FakeAgentResolverRunner(_ =>
            new AgentResult(false, "agent exited 2", null, "ordinary resolver failure"))
        { Kind = new AgentKind("claude") };

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Merge),
            [
                new AgenticConflictResolverCandidate(first, Credential: null),
                new AgenticConflictResolverCandidate(second, Credential: null),
            ],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Same(first, result.FailureRunner);
        var classificationResult = Assert.IsType<AgentResult>(result.FailureClassificationResult);
        Assert.Equal("agent transport failed", classificationResult.Summary);

        var classification = ((IAgentRunner)first).ClassifyFailure(classificationResult);
        Assert.Equal(AgentFailureKind.TransientNetwork, classification.Kind);
    }

    [Fact]
    public async Task ResolveAsync_FailureClassificationResult_RunnerThrows_AssignsMetadata()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1 }));

        var exception = new InvalidOperationException("agent CLI exploded");
        var runner = new FakeAgentResolverRunner(_ =>
            throw exception)
        { Kind = new AgentKind("throwing-agent") };
        var cred = new AgentCredential(new AgentKind("throwing-agent"), new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Merge),
            [new AgenticConflictResolverCandidate(runner, cred)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Same(runner, result.FailureRunner);
        Assert.Same(cred, result.FailureCredential);
        var classificationResult = Assert.IsType<AgentResult>(result.FailureClassificationResult);
        Assert.False(classificationResult.Success);
        Assert.Contains("threw InvalidOperationException", classificationResult.Summary);
        Assert.Contains("agent CLI exploded", classificationResult.Summary);
        Assert.Equal(exception.ToString(), classificationResult.Stderr);
    }

    [Fact]
    public async Task ResolveAsync_FailureClassificationResult_SessionResumeExhausted_PreservesLastResult()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1 }));

        var lastResult = new AgentResult(
            false,
            "agent exited 1",
            "resume stdout",
            "Transport channel closed");
        var runner = new FakeAgentResolverRunner(_ =>
            throw new AgentSessionResumeExhaustedException(
                new AgentKind("resumable-agent"),
                maxResumeAttempts: 2,
                lastResult))
        { Kind = new AgentKind("resumable-agent") };
        var cred = new AgentCredential(new AgentKind("resumable-agent"), new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, cred)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Same(runner, result.FailureRunner);
        Assert.Same(cred, result.FailureCredential);
        Assert.Same(lastResult, result.FailureClassificationResult);
        Assert.Equal("resume stdout", result.Stdout);
        Assert.Equal("Transport channel closed", result.Stderr);
        Assert.Contains("session resume exhausted", result.Summary);

        var classificationResult = Assert.IsType<AgentResult>(result.FailureClassificationResult);
        var classification = ((IAgentRunner)runner).ClassifyFailure(classificationResult);
        Assert.Equal(AgentFailureKind.TransientNetwork, classification.Kind);
    }

    [Fact]
    public async Task ResolveAsync_SessionResumeExhausted_RedactsStderrFromSummary()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1 }));

        var lastResult = new AgentResult(
            false,
            "agent exited 1",
            "resume stdout",
            "Transport channel closed after sk-ant-api03-AABBCCDDEEFFGGHHIIJJKKLLMMNNOOPPQQRRSSTT-0123456");
        var runner = new FakeAgentResolverRunner(_ =>
            throw new AgentSessionResumeExhaustedException(
                new AgentKind("resumable-agent"),
                maxResumeAttempts: 2,
                lastResult))
        { Kind = new AgentKind("resumable-agent") };

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Transport channel closed", result.Summary);
        Assert.Contains("***", result.Summary);
        Assert.DoesNotContain("sk-ant-api03", result.Summary);
    }

    [Fact]
    public async Task ResolveAsync_FailureClassificationResult_VerificationFails_AssignsMetadata()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1 }));

        var runner = new FakeAgentResolverRunner(sb =>
        {
            // Agent claims success, but leaves conflict markers in the file so verification fails.
            sb.GitAdd("src/a.txt");
            return new AgentResult(
                true,
                "agent thought it resolved",
                "stdout text",
                "Transport channel closed after a harmless reconnect");
        })
        { Kind = new AgentKind("lying-agent") };
        var cred = new AgentCredential(new AgentKind("lying-agent"), new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Merge),
            [new AgenticConflictResolverCandidate(runner, cred)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Same(runner, result.FailureRunner);
        Assert.Same(cred, result.FailureCredential);
        var classificationResult = Assert.IsType<AgentResult>(result.FailureClassificationResult);
        Assert.False(classificationResult.Success);
        Assert.Contains("conflict markers remain", classificationResult.Summary);
        Assert.Null(classificationResult.Stdout);
        Assert.Null(classificationResult.Stderr);
        Assert.Equal("stdout text", result.Stdout);
        Assert.Equal("Transport channel closed after a harmless reconnect", result.Stderr);

        var classification = ((IAgentRunner)runner).ClassifyFailure(classificationResult);
        Assert.NotEqual(AgentFailureKind.TransientNetwork, classification.Kind);
    }

    [Fact]
    public async Task ResolveAsync_NoConflicts_ReturnsTriviallySuccessful()
    {
        var sandbox = new ConflictSandbox();
        var runner = new FakeAgentResolverRunner(_ => new AgentResult(true, "should not be called", null, null));

        var result = await new AgenticConflictResolver().ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.IterationsUsed);
        Assert.Empty(result.ConflictFiles);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task ResolveAsync_BuildVerifyEnabled_FailsWhenBuildFails()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));
        sandbox.RegisterCommand(["dotnet", "build"], new SandboxExecResult(1, "build failed", "error CS0001"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions
            {
                MaxIterations = 1,
                BuildVerify = true,
                BuildVerifyArgv = ["dotnet", "build"],
            }));

        var runner = new FakeAgentResolverRunner(sb =>
        {
            sb.WriteFile("src/a.txt", "m + w\n");
            sb.GitAdd("src/a.txt");
            return new AgentResult(true, "resolved", null, null);
        });

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("build-verify", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_AgentReportsFailure_DoesNotRetrySameCandidate()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 5 }));

        var runner = new FakeAgentResolverRunner(_ =>
            new AgentResult(false, "agent error", null, "boom"));

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        // An agent that *reports* failure (vs. one that lies about success) is
        // pointless to retry on the same prompt — break out and either try the
        // next candidate or fail.
        Assert.Equal(1, runner.InvocationCount);
    }

    [Fact]
    public void PromptShape_NamesEveryConflictFileAndRebaseOperation()
    {
        var prompt = AgenticConflictResolver.BuildAgenticConflictResolverPrompt(
            new AgenticConflictResolverContext("main", "feature/x", AgenticConflictResolverOperation.Rebase),
            ["a.txt", "src/b.cs"],
            attempt: 1,
            maxAttempts: 3,
            priorVerificationError: null);

        Assert.Contains("`a.txt`", prompt);
        Assert.Contains("`src/b.cs`", prompt);
        Assert.Contains("mid-rebase", prompt, StringComparison.Ordinal);
        Assert.Contains("`feature/x`", prompt);
        Assert.Contains("`main`", prompt);
        Assert.DoesNotContain("rebase --continue", prompt[..prompt.IndexOf("DO NOT", StringComparison.Ordinal)]);
    }

    [Fact]
    public void PromptShape_RetryIncludesPriorError()
    {
        var prompt = AgenticConflictResolver.BuildAgenticConflictResolverPrompt(
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Merge),
            ["a.txt"],
            attempt: 2,
            maxAttempts: 3,
            priorVerificationError: "conflict markers remain in: a.txt");

        Assert.Contains("This is a retry", prompt, StringComparison.Ordinal);
        Assert.Contains("conflict markers remain in: a.txt", prompt, StringComparison.Ordinal);
        Assert.Contains("(2/3)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_HotReloadedOptions_AreObservedOnNextCall()
    {
        // Knob #4 is documented as hot-reloadable: a snapshot.Apply between
        // two ResolveAsync calls must reach the next call. Construct with
        // MaxIterations=1, run once (agent lies → markers remain → 1 attempt
        // total, failure). Apply MaxIterations=3, run again (same scenario →
        // 3 attempts, still failure but trail proves the new cap was honoured).
        var snapshot = new AgenticConflictResolverOptionsSnapshot(
            new AgenticConflictResolverOptions { MaxIterations = 1 });
        var resolver = new AgenticConflictResolver(snapshot);

        var sandbox1 = new ConflictSandbox();
        sandbox1.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));
        var runner1 = new FakeAgentResolverRunner(sb =>
        {
            // Agent lies about resolution — markers remain so verification fails
            // and the resolver burns one attempt per iteration.
            sb.GitAdd("src/a.txt");
            return new AgentResult(true, "lied", null, null);
        });
        var firstRun = await resolver.ResolveAsync(
            sandbox1,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner1, Credential: null)],
            CancellationToken.None);
        Assert.False(firstRun.Success);
        Assert.Equal(1, firstRun.IterationsUsed);
        Assert.Equal(1, runner1.InvocationCount);

        snapshot.Apply(new AgenticConflictResolverOptions { MaxIterations = 3, MaxAttemptsPerAgent = 3 });
        Assert.Equal(3, snapshot.Current.MaxIterations);

        var sandbox2 = new ConflictSandbox();
        sandbox2.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));
        var runner2 = new FakeAgentResolverRunner(sb =>
        {
            sb.GitAdd("src/a.txt");
            return new AgentResult(true, "still lying", null, null);
        });
        var secondRun = await resolver.ResolveAsync(
            sandbox2,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner2, Credential: null)],
            CancellationToken.None);
        Assert.False(secondRun.Success);
        Assert.Equal(3, secondRun.IterationsUsed);
        Assert.Equal(3, runner2.InvocationCount);
    }

    [Fact]
    public async Task ResolveAsync_ListUnmergedPathsAsyncFails_ThrowsMergeConflictResolutionFailed()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));
        // First (and only) unmerged-index call must fail so ListUnmergedPathsAsync throws
        // before any agent invocation happens.
        sandbox.LsFilesResponseQueue.Enqueue(new SandboxExecResult(128, "", "fatal: not a git repository"));

        var resolver = new AgenticConflictResolver();
        var runner = new FakeAgentResolverRunner(_ => throw new InvalidOperationException("agent should never run"));

        var ex = await Assert.ThrowsAsync<MergeConflictResolutionFailedException>(() =>
            resolver.ResolveAsync(
                sandbox,
                "/work",
                WorkItemId.New(),
                new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
                [new AgenticConflictResolverCandidate(runner, Credential: null)],
                CancellationToken.None));
        Assert.Contains("failed to inspect unmerged paths", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fatal: not a git repository", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task ResolveAsync_AgentThrows_AdvancesToNextCandidate()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 3 }));

        var first = new FakeAgentResolverRunner(_ =>
            throw new InvalidOperationException("agent CLI exploded"))
        { Kind = new AgentKind("first") };
        var second = new FakeAgentResolverRunner(sb =>
        {
            sb.WriteFile("src/a.txt", "m + w\n");
            sb.GitAdd("src/a.txt");
            return new AgentResult(true, "recovered", null, null);
        })
        { Kind = new AgentKind("second") };

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [
                new AgenticConflictResolverCandidate(first, Credential: null),
                new AgenticConflictResolverCandidate(second, Credential: null),
            ],
            CancellationToken.None);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("second", result.ChosenRunner?.Kind.Value);
        // First candidate threw on attempt 1 → resolver broke out of the inner
        // loop (no retry of the same candidate on a thrown exception) and moved
        // to the second candidate, which resolved on its first attempt.
        Assert.Equal(1, first.InvocationCount);
        Assert.Equal(1, second.InvocationCount);
    }

    [Fact]
    public async Task ResolveAsync_GrepReturnsUnexpectedExitCode_ReportsScanFailure()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));
        // grep exit codes: 0 = matched (markers), 1 = no match (clean),
        // anything else = grep itself failed. Force a real-grep-error exit so
        // the "scan failed" branch fires.
        sandbox.GrepResponseQueue.Enqueue(new SandboxExecResult(2, "", "grep: I/O error"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1 }));

        var runner = new FakeAgentResolverRunner(sb =>
        {
            sb.WriteFile("src/a.txt", "m + w\n");
            sb.GitAdd("src/a.txt");
            return new AgentResult(true, "resolved", null, null);
        });

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("failed to scan for conflict markers", result.Summary, StringComparison.Ordinal);
        Assert.Contains("grep: I/O error", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_VerifyUnmergedPathInspectionFails_ReportsInspectionFailure()
    {
        var sandbox = new ConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", BuildSimpleConflict("b", "m", "w"));
        // First ls-files call (in ListUnmergedPathsAsync) returns default success
        // plus the conflict file; second call (in VerifyResolutionAsync) fails.
        sandbox.LsFilesResponseQueue.Enqueue(null);
        sandbox.LsFilesResponseQueue.Enqueue(new SandboxExecResult(128, "", "fatal: index corrupted"));

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1 }));

        var runner = new FakeAgentResolverRunner(sb =>
        {
            sb.WriteFile("src/a.txt", "m + w\n");
            sb.GitAdd("src/a.txt");
            return new AgentResult(true, "resolved", null, null);
        });

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("git ls-files failed", result.Summary, StringComparison.Ordinal);
        Assert.Contains("fatal: index corrupted", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseUnmergedPathsFromLsFilesStdout_IgnoresPrefixedMultipassStartupNoise()
    {
        var oid2 = new string('a', 40);
        var oid3 = new string('b', 40);
        const string path = "src/CodeyBox.Api/CodeyBoxOptionsValidator.cs";
        var stdout =
            "\x1b[2K\x1b[0A\x1b[0EStarting codeybox-xxxx  <spinner>  " +
            $"100644 {oid2} 2\t{path}\0" +
            $"100644 {oid3} 3\t{path}\0";

        var paths = AgenticConflictResolver.ParseUnmergedPathsFromLsFilesStdout(stdout);

        Assert.Equal([path], paths);
        Assert.DoesNotContain(paths, p => p.Contains('\x1b'));
        Assert.DoesNotContain(paths, p => p.Contains("Starting codeybox", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptShape_MergeOperationUsesMergeTokens()
    {
        var prompt = AgenticConflictResolver.BuildAgenticConflictResolverPrompt(
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Merge),
            ["a.txt"],
            attempt: 1,
            maxAttempts: 3,
            priorVerificationError: null);

        // The Rebase/Merge ternary at the top of the builder picks the noun for
        // both the situation sentence ("mid-{op}") and the "git {op} --continue"
        // constraint line. Both must use "merge" when Operation = Merge.
        Assert.Contains("mid-merge", prompt, StringComparison.Ordinal);
        Assert.Contains("git merge --continue", prompt, StringComparison.Ordinal);
        Assert.Contains("git merge --abort", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("mid-rebase", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("git rebase --continue", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRelativeWorkPath_RejectsTraversal()
    {
        Assert.Throws<MergeConflictResolutionFailedException>(() =>
            AgenticConflictResolver.ValidateRelativeWorkPath("../etc/passwd"));
        Assert.Throws<MergeConflictResolutionFailedException>(() =>
            AgenticConflictResolver.ValidateRelativeWorkPath("/etc/passwd"));
        Assert.Throws<MergeConflictResolutionFailedException>(() =>
            AgenticConflictResolver.ValidateRelativeWorkPath("foo\\bar"));
        // Sane paths pass.
        AgenticConflictResolver.ValidateRelativeWorkPath("src/a.cs");
    }

    private static string BuildSimpleConflict(string baseLine, string mainLine, string workLine)
    {
        var sb = new StringBuilder();
        sb.Append("prelude\n");
        sb.Append(MarkerStart).Append(" HEAD\n");
        sb.Append(mainLine).Append('\n');
        sb.Append("|||||||| merged common ancestor\n");
        sb.Append(baseLine).Append('\n');
        sb.Append(MarkerMid).Append('\n');
        sb.Append(workLine).Append('\n');
        sb.Append(MarkerEnd).Append(" feature\n");
        sb.Append("trailer\n");
        return sb.ToString();
    }

    private static string BuildLargeConflict(int targetBytes)
    {
        // Build a single large file with one conflict hunk sandwiched between
        // big benign segments so the file as a whole exceeds the legacy 128 KB
        // byte cap. The conflict markers live inside a small window so we can
        // still observe whether the agent successfully removed them.
        var filler = new string('x', 64) + '\n'; // 65 bytes per line
        var lines = targetBytes / filler.Length;
        var sb = new StringBuilder(targetBytes + 1024);
        for (var i = 0; i < lines / 2; i++) sb.Append(filler);
        sb.Append(MarkerStart).Append(" HEAD\n");
        sb.Append("main-side\n");
        sb.Append(MarkerMid).Append('\n');
        sb.Append("work-side\n");
        sb.Append(MarkerEnd).Append(" feature\n");
        for (var i = 0; i < lines / 2; i++) sb.Append(filler);
        return sb.ToString();
    }

    private static string BuildLargeResolved(string conflictedContent)
    {
        // Replace just the conflict block with a merged line. Everything else
        // stays — confirms the resolver doesn't truncate, doesn't transcode,
        // and doesn't impose a per-file byte cap.
        var sb = new StringBuilder(conflictedContent.Length);
        var lines = conflictedContent.Split('\n');
        var skipping = false;
        var emittedResolved = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith(MarkerStart, StringComparison.Ordinal)) { skipping = true; continue; }
            if (line.StartsWith(MarkerEnd, StringComparison.Ordinal))
            {
                skipping = false;
                if (!emittedResolved) { sb.Append("main-side + work-side\n"); emittedResolved = true; }
                continue;
            }
            if (skipping) continue;
            sb.Append(line);
            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// In-memory sandbox that mocks just enough of git to drive the agentic
    /// resolver. Tracks per-path file contents and "unmerged" state; intercepts
    /// the git invocations the resolver issues (<c>ls-files -u</c>,
    /// <c>diff --diff-filter=U</c>, <c>grep</c>, <c>add</c>) and forwards
    /// everything else to a registered command map or a permissive default.
    /// </summary>
    internal sealed class ConflictSandbox : ISandbox
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unmerged = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SandboxExecResult> _commands = new(StringComparer.Ordinal);

        public string Id => "agentic-resolver-fake";
        public List<string> AddedFiles { get; } = new();

        // Per-call response overrides. Null entry = use default behaviour for
        // that call; concrete entry overrides it. Empty queue = always default.
        public Queue<SandboxExecResult?> DiffResponseQueue { get; } = new();
        public Queue<SandboxExecResult?> LsFilesResponseQueue { get; } = new();
        public Queue<SandboxExecResult?> GrepResponseQueue { get; } = new();

        public void AddConflictedFile(string relativePath, string content)
        {
            _files[relativePath] = content;
            _unmerged.Add(relativePath);
        }

        public void WriteFile(string relativePath, string content) => _files[relativePath] = content;

        public string GetFileContent(string relativePath) =>
            _files.TryGetValue(relativePath, out var content) ? content : throw new KeyNotFoundException(relativePath);

        public void GitAdd(string relativePath)
        {
            // Mirror real git: `git add` always succeeds and removes the file
            // from the unmerged set, even when the staged content still contains
            // conflict markers. The marker check is the separate gate that
            // catches a "lying" agent.
            if (!_files.TryGetValue(relativePath, out _))
                throw new InvalidOperationException($"GitAdd: file not present: {relativePath}");
            AddedFiles.Add(relativePath);
            _unmerged.Remove(relativePath);
        }

        public bool HasMarkers(string relativePath) =>
            _files.TryGetValue(relativePath, out var content) && ContainsMarkers(content);

        public void RegisterCommand(IReadOnlyList<string> argv, SandboxExecResult result)
            => _commands[string.Join('\0', argv)] = result;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var argv = exec.Argv;
            if (argv.Count >= 5
                && argv[0] == "git" && argv[1] == "-C" && argv[3] == "ls-files"
                && argv.Contains("-u"))
            {
                if (LsFilesResponseQueue.TryDequeue(out var queued) && queued is not null)
                    return Task.FromResult(queued);

                var listed = BuildLsFilesUnmergedOutput(_unmerged.Order(StringComparer.Ordinal));
                return Task.FromResult(new SandboxExecResult(0, listed, ""));
            }

            if (argv.Count >= 5
                && argv[0] == "git" && argv[1] == "-C" && argv[3] == "diff"
                && argv.Contains("--diff-filter=U"))
            {
                if (DiffResponseQueue.TryDequeue(out var queued) && queued is not null)
                    return Task.FromResult(queued);
                var listed = string.Join('\n', _unmerged.Order(StringComparer.Ordinal));
                return Task.FromResult(new SandboxExecResult(0, listed, ""));
            }

            if (argv.Count >= 4
                && argv[0] == "git" && argv[1] == "-C" && argv[3] == "grep")
            {
                if (GrepResponseQueue.TryDequeue(out var queued) && queued is not null)
                    return Task.FromResult(queued);
                var sepIdx = -1;
                for (var i = 4; i < argv.Count; i++)
                {
                    if (argv[i] == "--") { sepIdx = i; break; }
                }
                var matched = new List<string>();
                if (sepIdx >= 0)
                {
                    for (var i = sepIdx + 1; i < argv.Count; i++)
                    {
                        var path = argv[i];
                        if (_files.TryGetValue(path, out var content) && ContainsMarkers(content))
                            matched.Add(path);
                    }
                }
                return matched.Count == 0
                    ? Task.FromResult(new SandboxExecResult(1, "", ""))
                    : Task.FromResult(new SandboxExecResult(0, string.Join('\n', matched), ""));
            }

            if (argv.Count >= 5
                && argv[0] == "git" && argv[1] == "-C" && argv[3] == "add" && argv[4] == "--")
            {
                for (var i = 5; i < argv.Count; i++)
                    GitAdd(argv[i]);
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }

            var key = string.Join('\0', argv);
            if (_commands.TryGetValue(key, out var canned))
                return Task.FromResult(canned);

            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static string BuildLsFilesUnmergedOutput(IEnumerable<string> paths)
        {
            var sb = new StringBuilder();
            var oid = new string('a', 40);
            foreach (var path in paths)
                sb.Append("100644 ").Append(oid).Append(" 2\t").Append(path).Append('\0');
            return sb.ToString();
        }

        private static bool ContainsMarkers(string content)
        {
            foreach (var line in content.Split('\n'))
            {
                if (line.StartsWith("<<<<<<<", StringComparison.Ordinal)
                    || line.StartsWith("=======", StringComparison.Ordinal)
                    || line.StartsWith(">>>>>>>", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private sealed class FakeAgentResolverRunner : IAgentRunner
    {
        private readonly Func<ConflictSandbox, AgentResult> _onRun;

        public FakeAgentResolverRunner(Func<ConflictSandbox, AgentResult> onRun) { _onRun = onRun; }

        public AgentKind Kind { get; init; } = new("fake-resolver");
        public int InvocationCount { get; private set; }
        public List<string> PromptHistory { get; } = new();

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
        {
            InvocationCount++;
            PromptHistory.Add(prompt);
            return Task.FromResult(_onRun((ConflictSandbox)sandbox));
        }
    }

    private sealed class ResumableCaptureRecordingRunner : IAgentRunner, ICliSessionResumableAgentRunner
    {
        public AgentKind Kind { get; } = new("resumable-conflict-test");
        public bool RequiresStructuredStreamForSessionId => true;
        public IQuotaFailureClassifier SessionResumeQuotaClassifier { get; } = new NoQuotaFailureClassifier();
        public List<bool> CaptureStructuredStreamCalls { get; } = [];

        public string? TryExtractSessionId(string? stdout) => null;

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
        {
            CaptureStructuredStreamCalls.Add(captureStructuredStream);
            return Task.FromResult(new AgentResult(false, "agent exited 1", null, "transient crash"));
        }

        private sealed class NoQuotaFailureClassifier : IQuotaFailureClassifier
        {
            public QuotaFailureClassification Classify(AgentKind agent, string? stderr, string? stdout)
                => QuotaFailureClassification.None;

            public QuotaDetection? Detect(AgentKind agent, string? stderr, string? stdout)
                => null;
        }
    }

    private sealed class RecordingConflictSessionRunner : ISessionAgentRunner
    {
        public AgentKind Kind { get; } = new("session-conflict-test");
        public List<string> OpenedSessionIds { get; } = [];
        public List<string> SendTurnSessionIds { get; } = [];

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false) =>
            throw new InvalidOperationException("resolver should use the session path when supervision is enabled");

        public Task<AgentSessionHandle> OpenSessionAsync(
            ISandbox sandbox,
            string workingDirectory,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default)
        {
            var sessionId = "resolver-session-" + Guid.NewGuid().ToString("N");
            OpenedSessionIds.Add(sessionId);
            return Task.FromResult(new AgentSessionHandle(
                Kind,
                sessionId,
                new AgentSessionSandboxRef(sandbox.Id),
                workingDirectory,
                modelId,
                reasoningMode));
        }

        public Task<AgentResult> SendTurnAsync(
            AgentSessionHandle sessionHandle,
            string prompt,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
        {
            SendTurnSessionIds.Add(sessionHandle.SessionId);
            stdoutChunkCallback?.Invoke($"turn:{SendTurnSessionIds.Count}\n");
            return Task.FromResult(new AgentResult(true, "ok", $"turn:{SendTurnSessionIds.Count}", null));
        }

        public Task SuspendSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task ResumeSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task CloseSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class AutoQueueConflictInjectionNotifier : IAgentSupervisionNotifier
    {
        private bool _queued;
        public AgentSupervisionService? Service { get; set; }
        public List<AgentSupervisionCommandEvent> Commands { get; } = [];
        public List<AgentSupervisionInjectionCompletedEvent> CompletedInjections { get; } = [];

        public async Task SessionStartedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default)
        {
            if (_queued || Service is null)
                return;
            _queued = true;
            var receipt = await Service.EnqueueInjectionAsync(
                session.SessionId,
                new AgentSupervisionInjectionRequest("operator conflict hint", "resolver-test"),
                ct);
            Assert.True(receipt.Accepted, receipt.Error);
        }

        public Task SessionUpdatedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task SessionCompletedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task CodeyBoxCommandAsync(AgentSupervisionCommandEvent command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }
        public Task StdoutChunkAsync(AgentSupervisionStdoutEvent chunk, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task InjectionQueuedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task InjectionStartedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task InjectionCompletedAsync(AgentSupervisionInjectionCompletedEvent injection, CancellationToken ct = default)
        {
            CompletedInjections.Add(injection);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopPreprocessor : IAgentPromptPreprocessor
    {
        public int Order => 0;

        public Task<string> ProcessAsync(PromptContext ctx, string prompt, CancellationToken ct = default)
        {
            _ = ctx;
            _ = ct;
            return Task.FromResult(prompt);
        }
    }

    private static Project NewProject() => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test Project",
        RepositoryUrl = "file:///tmp/repo.git",
    };
}
