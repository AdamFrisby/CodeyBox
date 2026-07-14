using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the <see cref="ShellCommandAuditorOptions.ExecPreamble"/> wrapping:
/// a configured preamble runs in the same shell immediately before the command,
/// which is then <c>exec</c>'d with its arguments intact, while the configured
/// argv (not the wrapped form) remains what findings report. This is the seam
/// that gives the dotnet tool auditors (csharp:build-WaE / csharp:test-pass) the
/// same NuGet-home relocation the required-build gate carries.
/// </summary>
public sealed class ShellCommandAuditorExecPreambleTests
{
    private static AuditContext CodeContext() => new(
        WorkItemId.New(),
        WorkBranch: "codeybox/test",
        BaseBranch: "main",
        Iteration: 1,
        OriginalPrompt: "irrelevant");

    [Fact]
    public async Task NoPreamble_DispatchesArgvVerbatim()
    {
        var sandbox = new RecordingSandbox(new SandboxExecResult(0, "ok", ""));
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "csharp:build-WaE",
            Argv = ["dotnet", "build", "--no-incremental", "/warnaserror"],
        });

        var result = await auditor.RunAsync(sandbox, "/work", CodeContext());

        Assert.True(result.Passed);
        // The command that ran is exactly the configured argv -- no wrapper.
        Assert.Equal(
            ["dotnet", "build", "--no-incremental", "/warnaserror"],
            sandbox.LastCommandArgv);
    }

    [Fact]
    public async Task WithPreamble_WrapsInShellAndExecsCommandWithArgumentsPreserved()
    {
        var sandbox = new RecordingSandbox(new SandboxExecResult(0, "ok", ""));
        var argv = new[] { "dotnet", "build", "--no-incremental", "/warnaserror" };
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "csharp:build-WaE",
            Argv = argv,
            ExecPreamble = NuGetHomeGuard.RelocationPreamble,
        });

        await auditor.RunAsync(sandbox, "/work", CodeContext());

        var dispatched = sandbox.LastCommandArgv;
        // sh -c "<preamble>\nexec \"$@\"" sh dotnet build --no-incremental /warnaserror
        Assert.Equal("sh", dispatched[0]);
        Assert.Equal("-c", dispatched[1]);
        Assert.Contains("relocating HOME", dispatched[2]);
        Assert.EndsWith("exec \"$@\"", dispatched[2]);
        Assert.Equal("sh", dispatched[3]); // $0 placeholder
        // The real command and every argument follow $0 so `exec "$@"` runs them.
        Assert.Equal(argv, dispatched.Skip(4).ToArray());
    }

    [Fact]
    public async Task WithPreamble_FailingCommand_FindingReportsCleanArgvNotWrapper()
    {
        var sandbox = new RecordingSandbox(new SandboxExecResult(1, "", "build broke"));
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "csharp:build-WaE",
            Argv = ["dotnet", "build", "--no-incremental", "/warnaserror"],
            ExecPreamble = NuGetHomeGuard.RelocationPreamble,
        });

        var result = await auditor.RunAsync(sandbox, "/work", CodeContext());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        // The operator-facing title shows the command they can run, not the
        // multi-hundred-line sh -c wrapper.
        Assert.Contains("dotnet build --no-incremental /warnaserror", finding.Title);
        Assert.DoesNotContain("exec \"$@\"", finding.Title);
    }

    /// <summary>
    /// Minimal ISandbox that records the argv of the last non-probe command and
    /// returns a canned result. The tool-presence probe (<c>command -v</c>) is
    /// answered as "present" so the auditor proceeds to the real command.
    /// </summary>
    private sealed class RecordingSandbox(SandboxExecResult commandResult) : ISandbox
    {
        public string Id => "recording";
        public IReadOnlyList<string> LastCommandArgv { get; private set; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            // The direct-tool-missing probe: `sh -c "command -v \"$1\"..." sh dotnet`.
            var isToolProbe = exec.Argv.Count >= 3
                && exec.Argv[0] == "sh"
                && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);
            if (isToolProbe)
                return Task.FromResult(new SandboxExecResult(0, "", ""));

            LastCommandArgv = exec.Argv;
            return Task.FromResult(commandResult);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
