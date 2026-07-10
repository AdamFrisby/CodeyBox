using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Copilot;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests.Uat.AgentRunnersAndCredentials;

/// <summary>
/// UAT coverage for Claude, Codex, Gemini, and Copilot runner rows in the Agent Runners And Credentials plan section.
/// Plan anchors:
/// docs/uat/00-plan.md#claude-agent-runner---drives-claude-code-cli-and-claude-text-only-calls
/// docs/uat/00-plan.md#codex-agent-runner---drives-codex-cli-and-openai-text-only-calls
/// docs/uat/00-plan.md#gemini-agent-runner---drives-gemini-cli-with-model-encoded-thinking-and-oauth-files
/// docs/uat/00-plan.md#copilot-agent-runner---drives-github-copilot-cli-as-a-direct-agent-option
/// </summary>
public sealed class ConcreteAgentRunnerUatTests
{
    [Fact]
    public async Task ClaudeRunner_UsesNonInteractiveFlagsEffortStructuredStreamAndOAuthFileMaterialization()
    {
        var sandbox = new RecordingSandbox(helpOutput: "--output-format stream-json --verbose");
        var credential = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string>
            {
                [ClaudeOAuthFileCredentialProvider.OAuthJsonEnvVar] =
                    """{"claudeAiOauth":{"accessToken":"uat-access","refreshToken":"uat-refresh"}}""",
            },
            new Dictionary<string, string>());

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox,
            "/work",
            "ship it",
            credential,
            modelId: "claude-sonnet-4-6",
            reasoningMode: "high",
            captureStructuredStream: true);

        Assert.True(result.Success);
        Assert.Equal(["claude", "--help"], sandbox.Execs[0].Argv);
        Assert.Equal(".claude/.credentials.json", sandbox.Execs[1].Argv[4]);
        Assert.Equal(credential.EnvironmentVariables[ClaudeOAuthFileCredentialProvider.OAuthJsonEnvVar], sandbox.Execs[1].Stdin);
        Assert.DoesNotContain(ClaudeOAuthFileCredentialProvider.OAuthJsonEnvVar, sandbox.Execs[1].Argv[2]);
        Assert.Contains("0o600", sandbox.Execs[1].Argv[2]);

        var argv = sandbox.Execs[2].Argv.ToList();
        Assert.Equal("claude", argv[0]);
        Assert.Contains("--print", argv);
        Assert.Contains("--dangerously-skip-permissions", argv);
        AssertFlagValue(argv, "--model", "claude-sonnet-4-6");
        AssertFlagValue(argv, "--effort", "high");
        AssertFlagValue(argv, "--output-format", "stream-json");
        Assert.Contains("--verbose", argv);
        // Prompt passed via stdin (claude --print reads stdin when no positional)
        // to dodge Linux's 128 KiB per-argv MAX_ARG_STRLEN ceiling.
        Assert.DoesNotContain("ship it", argv);
        Assert.Equal("ship it", sandbox.Execs[2].Stdin);
    }

    [Fact]
    public async Task CodexRunner_MaterializesAuthUsesReasoningConfigAndPreferredStructuredFlag()
    {
        var sandbox = new RecordingSandbox(helpOutput: "Usage: codex exec --json --json-stream");

        var result = await new CodexAgentRunner().RunAsync(
            sandbox,
            "/work",
            "implement",
            credential: null,
            modelId: "gpt-5.5",
            reasoningMode: "medium",
            captureStructuredStream: true);

        Assert.True(result.Success);
        Assert.Equal(["codex", "exec", "--help"], sandbox.Execs[0].Argv);
        Assert.Contains("CODEX_AUTH_JSON", sandbox.Execs[1].Argv[2]);
        Assert.Contains(".codex/auth.json", sandbox.Execs[1].Argv[2]);

        var argv = sandbox.Execs[2].Argv.ToList();
        Assert.Equal(["codex", "exec"], argv[..2]);
        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", argv);
        Assert.Contains("--json-stream", argv);
        AssertFlagValue(argv, "--model", "gpt-5.5");
        AssertFlagValue(argv, "-c", "model_reasoning_effort=medium");
        // Prompt passed via stdin (codex exec reads stdin when no positional)
        // to dodge Linux's 128 KiB per-argv MAX_ARG_STRLEN ceiling.
        Assert.DoesNotContain("implement", argv);
        Assert.Equal("implement", sandbox.Execs[2].Stdin);
    }

    [Fact]
    public async Task GeminiRunner_UsesYoloTrustPromptStructuredStreamAndOAuthFileMaterialization()
    {
        var sandbox = new RecordingSandbox(helpOutput: "--output-format stream-json");
        var credential = new AgentCredential(
            AgentKind.Gemini,
            new Dictionary<string, string>
            {
                [GeminiOAuthFileCredentialProvider.OAuthCredsEnvVar] = """{"type":"authorized_user"}""",
                [GeminiOAuthFileCredentialProvider.SettingsEnvVar] = """{"selectedAuthType":"oauth-personal"}""",
            },
            new Dictionary<string, string>());

        var result = await new GeminiAgentRunner().RunAsync(
            sandbox,
            "/work",
            "build",
            credential,
            modelId: "gemini-3-pro-preview",
            reasoningMode: "high",
            captureStructuredStream: true);

        Assert.True(result.Success);
        Assert.Equal(["gemini", "--help"], sandbox.Execs[0].Argv);
        Assert.Equal(".gemini/oauth_creds.json", sandbox.Execs[1].Argv[4]);
        Assert.Equal(credential.EnvironmentVariables[GeminiOAuthFileCredentialProvider.OAuthCredsEnvVar], sandbox.Execs[1].Stdin);
        Assert.Equal(".gemini/settings.json", sandbox.Execs[2].Argv[4]);
        Assert.Equal(credential.EnvironmentVariables[GeminiOAuthFileCredentialProvider.SettingsEnvVar], sandbox.Execs[2].Stdin);

        var argv = sandbox.Execs[3].Argv.ToList();
        Assert.Equal("gemini", argv[0]);
        Assert.Contains("--yolo", argv);
        Assert.Contains("--skip-trust", argv);
        AssertFlagValue(argv, "--output-format", "stream-json");
        AssertFlagValue(argv, "--model", "gemini-3-pro-preview");
        // Prompt passed via stdin (gemini-cli appends -p's value onto stdin per
        // its own docs; we skip -p entirely so stdin IS the prompt) to dodge
        // Linux's 128 KiB per-argv MAX_ARG_STRLEN ceiling.
        Assert.DoesNotContain("-p", argv);
        Assert.DoesNotContain("build", argv);
        Assert.Equal("build", sandbox.Execs[3].Stdin);
        Assert.DoesNotContain("--thinking", argv);
        Assert.DoesNotContain("--reasoning", argv);
        Assert.DoesNotContain("--effort", argv);
    }

    [Fact]
    public async Task GeminiRunner_StripsAnsiForLiveOutputWhenStructuredCaptureIsDisabled()
    {
        var chunks = new List<string>();
        var sandbox = new RecordingSandbox(stdout: "\x1b[32mfinal\x1b[0m", stderr: "\x1b[31merr\x1b[0m", stdoutChunk: "\x1b[32mlive\x1b[0m");

        var result = await new GeminiAgentRunner().RunAsync(
            sandbox,
            "/work",
            "prompt",
            credential: null,
            stdoutChunkCallback: chunks.Add);

        Assert.Equal("final", result.Stdout);
        Assert.Equal("err", result.Stderr);
        Assert.Equal(["live"], chunks);
    }

    [Fact]
    public async Task CopilotRunner_UsesPromptShapeAndIgnoresUnsupportedModelAndReasoningKnobs()
    {
        var sandbox = new RecordingSandbox();

        await new CopilotAgentRunner().RunAsync(
            sandbox,
            "/work",
            "answer",
            credential: null,
            modelId: "ignored-model",
            reasoningMode: "high");

        var argv = Assert.Single(sandbox.Execs).Argv;
        Assert.Equal(["copilot", "-p", "answer"], argv);
        Assert.DoesNotContain("--model", argv);
        Assert.DoesNotContain("--reasoning", argv);
        Assert.DoesNotContain("--effort", argv);
    }

    private static void AssertFlagValue(IReadOnlyList<string> argv, string flag, string value)
    {
        var index = argv.ToList().IndexOf(flag);
        Assert.True(index >= 0, $"argv must contain {flag}");
        Assert.True(index + 1 < argv.Count, $"{flag} must have a value");
        Assert.Equal(value, argv[index + 1]);
    }
}
