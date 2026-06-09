using CodeyBox.Agents.Antigravity;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="AntigravityAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> fake from the Gemini suite to inspect the argv,
/// stdin, and resume shape — same pattern as the Claude/Gemini runner tests.
/// </summary>
public sealed class AntigravityAgentRunnerTests
{
    [Fact]
    public void Kind_IsAntigravity()
    {
        var runner = new AntigravityAgentRunner();
        Assert.Equal(AgentKind.Antigravity, runner.Kind);
    }

    [Fact]
    public async Task RunAsync_Argv_StartsWithAgyBinary()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        Assert.Equal("agy", sandbox.CapturedExec!.Argv[0]);
    }

    [Fact]
    public async Task RunAsync_Argv_ContainsPrintAndSkipPermissions()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", "go", credential: null);

        Assert.Contains("--print", sandbox.CapturedExec!.Argv);
        Assert.Contains("--dangerously-skip-permissions", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_Argv_PassesModelWhenSet()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", "go", credential: null, modelId: "gemini-3.5-flash-high");

        var argv = sandbox.CapturedExec!.Argv;
        var modelIdx = IndexOf(argv, "--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("gemini-3.5-flash-high", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_PromptIsPassedViaStdin()
    {
        // Big rework prompts (audit findings, multi-file diffs) can exceed
        // Linux's 128 KiB MAX_ARG_STRLEN per single argv element. Verify the
        // prompt flows through stdin, not argv.
        const string prompt = "rebuild the build pipeline";
        var sandbox = new CapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
        Assert.DoesNotContain(prompt, sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public void TryParseConversationId_ExtractsIdFromCheckpointRef()
    {
        Assert.Equal("abc123", AntigravityAgentRunner.TryParseConversationId("agy-conversation:abc123"));
        Assert.Null(AntigravityAgentRunner.TryParseConversationId("agy-conversation:"));
        Assert.Null(AntigravityAgentRunner.TryParseConversationId("other-prefix:x"));
        Assert.Null(AntigravityAgentRunner.TryParseConversationId(null));
    }

    [Fact]
    public async Task RunResumedAsync_WithCheckpointId_PassesConversationFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AntigravityAgentRunner();

        var resume = new AgentResumeContext(
            CheckpointRef: "agy-conversation:conv-7",
            ScratchpadArchivePath: "/nonexistent/.codeybox/preempt-scratchpad.tgz");

        await runner.RunResumedAsync(sandbox, "/work", "next turn", credential: null, resume);

        var argv = sandbox.CapturedExec!.Argv;
        var convIdx = IndexOf(argv, "--conversation");
        Assert.True(convIdx >= 0);
        Assert.Equal("conv-7", argv[convIdx + 1]);
        Assert.DoesNotContain("--continue", argv);
    }

    private static int IndexOf(IReadOnlyList<string> argv, string needle)
    {
        for (var i = 0; i < argv.Count; i++)
            if (argv[i] == needle) return i;
        return -1;
    }

    [Fact]
    public async Task RunResumedAsync_WithoutCheckpointId_FallsBackToContinue()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AntigravityAgentRunner();

        var resume = new AgentResumeContext(
            CheckpointRef: "some-other-ref",
            ScratchpadArchivePath: "/nonexistent/.codeybox/preempt-scratchpad.tgz");

        await runner.RunResumedAsync(sandbox, "/work", "next turn", credential: null, resume);

        Assert.Contains("--continue", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("--conversation", sandbox.CapturedExec!.Argv);
    }
}
