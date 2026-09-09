using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

internal static class DotnetTestFailureAttributionRunner
{
    private const int CommandNotFoundExitCode = 127;

    // Scratch location (inside the sandbox) for the detached base-branch worktree
    // used to re-run failing tests against the merge base. A unique suffix is
    // appended per attribution run so concurrent audits never collide.
    private const string BaseWorktreePathPrefix = "/tmp/codeybox-test-attribution-";

    public static async Task<IReadOnlyList<TestFailureAttributionResult>> AttributeAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        string auditorName,
        IReadOnlyList<string> originalArgv,
        IReadOnlyList<string> failedTestNames,
        bool hitFailureParseCap,
        TestFailureAttributionOptionsSnapshot? options,
        CancellationToken ct)
    {
        if (failedTestNames.Count == 0)
            return [];

        if (options?.Enabled != true)
        {
            AuditLog.TestFailureAttributionSkipped(
                context.WorkItemId,
                auditorName,
                "CodeyBox:TestFailureAttribution:Enabled is false",
                failedTestNames.Count);
            return TestFailureAttributionClassifier.FailClosed(
                failedTestNames,
                TestFailureAttributionSkipReason.Disabled);
        }

        if (!IsDotnetTestCommand(originalArgv))
        {
            AuditLog.TestFailureAttributionSkipped(
                context.WorkItemId,
                auditorName,
                "auditor command is not a supported dotnet test invocation",
                failedTestNames.Count);
            return TestFailureAttributionClassifier.FailClosed(
                failedTestNames,
                TestFailureAttributionSkipReason.UnsupportedCommand);
        }

        if (hitFailureParseCap)
        {
            AuditLog.TestFailureAttributionPartial(
                context.WorkItemId,
                auditorName,
                "dotnet test output exceeded the failed-test parser cap; attribution covers only parsed failures");
        }

        string? repoRoot = null;
        string? worktreePath = null;
        try
        {
            Validation.ValidateBranchName(context.BaseBranch, nameof(context.BaseBranch));
            repoRoot = await RequiredGitStdoutAsync(
                sandbox,
                workingDirectory,
                ct,
                "rev-parse",
                "--show-toplevel");
            var prefix = await RequiredGitStdoutAsync(
                sandbox,
                workingDirectory,
                ct,
                "rev-parse",
                "--show-prefix");
            var baseRef = $"origin/{context.BaseBranch}";
            var baseAvailable = await RunGitAsync(
                sandbox,
                repoRoot,
                ct,
                "rev-parse",
                "--verify",
                $"{baseRef}^{{commit}}");
            if (!baseAvailable.Success)
            {
                await RunGitAsync(
                    sandbox,
                    repoRoot,
                    ct,
                    "fetch",
                    "origin",
                    $"+refs/heads/{context.BaseBranch}:refs/remotes/origin/{context.BaseBranch}");
                baseAvailable = await RunGitAsync(
                    sandbox,
                    repoRoot,
                    ct,
                    "rev-parse",
                    "--verify",
                    $"{baseRef}^{{commit}}");
            }

            if (!baseAvailable.Success)
                throw new InvalidOperationException($"base ref '{baseRef}' is not available in the audit sandbox");

            var mergeBase = await RequiredGitStdoutAsync(
                sandbox,
                repoRoot,
                ct,
                "merge-base",
                "HEAD",
                baseRef);
            worktreePath = $"{BaseWorktreePathPrefix}{Guid.NewGuid():N}";
            var add = await RunGitAsync(
                sandbox,
                repoRoot,
                ct,
                "worktree",
                "add",
                "--detach",
                "--quiet",
                worktreePath,
                mergeBase);
            if (!add.Success)
                throw new InvalidOperationException($"could not create base worktree: {SingleLine(CombinedOutput(add))}");

            var baseWorkingDirectory = CombineSandboxPath(worktreePath, prefix);
            var pairs = new List<TestFailureRunPair>(failedTestNames.Count);
            foreach (var testName in failedTestNames)
            {
                var rerun = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = BuildFilteredDotnetTestArgv(originalArgv, testName),
                    WorkingDirectory = baseWorkingDirectory,
                }, ct);
                var baseOutcome = ToRunOutcome(rerun);
                var skipReason = baseOutcome == TestFailureRunOutcome.Unavailable
                    ? TestFailureAttributionSkipReason.BaseRerunUnavailable
                    : TestFailureAttributionSkipReason.None;
                if (skipReason == TestFailureAttributionSkipReason.BaseRerunUnavailable)
                {
                    AuditLog.TestFailureAttributionPartial(
                        context.WorkItemId,
                        auditorName,
                        $"base rerun unavailable for test '{testName}'");
                }

                pairs.Add(new TestFailureRunPair(
                    testName,
                    baseOutcome,
                    DiffRun: TestFailureRunOutcome.Failed,
                    skipReason));
            }

            return TestFailureAttributionClassifier.Classify(pairs);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AuditLog.TestFailureAttributionSkipped(
                context.WorkItemId,
                auditorName,
                $"base rerun could not be performed: {SingleLine(ex.Message)}",
                failedTestNames.Count);
            return TestFailureAttributionClassifier.FailClosed(
                failedTestNames,
                TestFailureAttributionSkipReason.BaseRerunUnavailable);
        }
        finally
        {
            if (repoRoot is not null && worktreePath is not null)
            {
                var remove = await RunGitAsync(
                    sandbox,
                    repoRoot,
                    CancellationToken.None,
                    "worktree",
                    "remove",
                    "--force",
                    worktreePath);
                if (!remove.Success)
                {
                    AuditLog.TestFailureAttributionPartial(
                        context.WorkItemId,
                        auditorName,
                        $"could not remove temporary base worktree: {SingleLine(CombinedOutput(remove))}");
                }
            }
        }
    }

    private static bool IsDotnetTestCommand(IReadOnlyList<string> argv)
        => argv.Count >= 2
           && string.Equals(argv[0], "dotnet", StringComparison.Ordinal)
           && string.Equals(argv[1], "test", StringComparison.Ordinal);

    private static async Task<string> RequiredGitStdoutAsync(
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct,
        params string[] args)
    {
        var result = await RunGitAsync(sandbox, workingDirectory, ct, args);
        if (!result.Success)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {SingleLine(CombinedOutput(result))}");

        return result.Stdout.Trim();
    }

    private static Task<SandboxExecResult> RunGitAsync(
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct,
        params string[] args)
    {
        var argv = new List<string>(args.Length + 1) { "git" };
        argv.AddRange(args);
        return sandbox.ExecAsync(new SandboxExec
        {
            Argv = argv,
            WorkingDirectory = workingDirectory,
        }, ct);
    }

    private static IReadOnlyList<string> BuildFilteredDotnetTestArgv(
        IReadOnlyList<string> originalArgv,
        string testName)
    {
        var argv = new List<string>(originalArgv.Count + 2);
        foreach (var arg in originalArgv)
        {
            if (string.Equals(arg, "--no-build", StringComparison.OrdinalIgnoreCase))
                continue;
            argv.Add(arg);
        }

        argv.Add("--filter");
        argv.Add($"FullyQualifiedName={EscapeFilterValue(TrimDisplayArguments(testName))}");
        return argv;
    }

    private static string TrimDisplayArguments(string testName)
    {
        var paren = testName.IndexOf('(', StringComparison.Ordinal);
        return paren > 0 ? testName[..paren].TrimEnd() : testName;
    }

    private static string EscapeFilterValue(string value)
    {
        var chars = new List<char>(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\\' or ',' or '(' or ')' or '!' or '~' or '&' or '|' or '=')
                chars.Add('\\');
            chars.Add(ch);
        }

        return new string(chars.ToArray());
    }

    private static TestFailureRunOutcome ToRunOutcome(SandboxExecResult result)
    {
        if (result.ExecutionUnavailable || result.OutputLimitExceeded || result.ExitCode == CommandNotFoundExitCode)
            return TestFailureRunOutcome.Unavailable;
        return result.Success ? TestFailureRunOutcome.Passed : TestFailureRunOutcome.Failed;
    }

    private static string CombineSandboxPath(string root, string prefix)
    {
        var trimmedPrefix = prefix.Trim().Trim('/');
        return trimmedPrefix.Length == 0 ? root : root.TrimEnd('/') + "/" + trimmedPrefix;
    }

    private static string CombinedOutput(SandboxExecResult result)
        => string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stdout
            : string.IsNullOrWhiteSpace(result.Stdout)
                ? result.Stderr
                : result.Stdout + "\n" + result.Stderr;

    private static string SingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "(no output)";

        var chars = new char[text.Length];
        var pos = 0;
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t' || char.IsControl(ch) || ch == ' ')
            {
                if (!lastWasSpace)
                {
                    chars[pos++] = ' ';
                    lastWasSpace = true;
                }
                continue;
            }

            chars[pos++] = ch;
            lastWasSpace = false;
        }

        return new string(chars, 0, pos).Trim();
    }
}
