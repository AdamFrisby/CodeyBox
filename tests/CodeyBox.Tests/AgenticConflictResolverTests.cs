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
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1 }));

        var first = new FakeAgentResolverRunner(_ =>
            new AgentResult(false, "rate limited", null, "429")) { Kind = new AgentKind("first") };
        var second = new FakeAgentResolverRunner(sb =>
        {
            sb.WriteFile("src/a.txt", "m + w\n");
            sb.GitAdd("src/a.txt");
            return new AgentResult(true, "ok", null, null);
        }) { Kind = new AgentKind("second") };

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
    /// the git invocations the resolver issues (<c>diff --diff-filter=U</c>,
    /// <c>grep</c>, <c>add</c>) and forwards everything else to a registered
    /// command map or a permissive default.
    /// </summary>
    internal sealed class ConflictSandbox : ISandbox
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unmerged = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SandboxExecResult> _commands = new(StringComparer.Ordinal);

        public string Id => "agentic-resolver-fake";
        public List<string> AddedFiles { get; } = new();

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
                && argv[0] == "git" && argv[1] == "-C" && argv[3] == "diff"
                && argv.Contains("--diff-filter=U"))
            {
                var listed = string.Join('\n', _unmerged.Order(StringComparer.Ordinal));
                return Task.FromResult(new SandboxExecResult(0, listed, ""));
            }

            if (argv.Count >= 4
                && argv[0] == "git" && argv[1] == "-C" && argv[3] == "grep")
            {
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
}
