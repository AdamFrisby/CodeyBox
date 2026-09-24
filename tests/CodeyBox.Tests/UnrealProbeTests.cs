using System.Text.Json;
using CodeyBox.Agents.Unreal;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for Unreal probes:
/// - UnrealSmokeProbe (credential check + Codex rejection)
/// - UnrealInVmSmokeProbe (help step + stdin prompt dispatch step)
/// - UnrealModelListProbe (known model catalog)
/// </summary>
public sealed class UnrealProbeTests
{
    [Theory]
    [InlineData("OPENROUTER_API_KEY")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("FIREWORKS_API_KEY")]
    [InlineData("UNREAL_HARNESS_LLM_API_KEY")]
    public async Task SmokeProbe_ValidApiKey_ReturnsOk(string keyName)
    {
        var probe = new UnrealSmokeProbe();
        var cred = new AgentCredential(AgentKind.Unreal,
            new Dictionary<string, string> { [keyName] = "test-key-val" },
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.FailureReason);
        Assert.Equal(SmokeFailureCategory.None, result.Category);
    }

    [Fact]
    public async Task SmokeProbe_MissingApiKey_ReturnsFail()
    {
        var probe = new UnrealSmokeProbe();
        var cred = new AgentCredential(AgentKind.Unreal,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(UnrealAgentRunner.MissingCredentialMarker, result.FailureReason);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    [Fact]
    public async Task SmokeProbe_CodexAccessToken_RejectsAsPersistent()
    {
        var probe = new UnrealSmokeProbe();
        var cred = new AgentCredential(AgentKind.Unreal,
            new Dictionary<string, string>
            {
                ["OPENROUTER_API_KEY"] = "key",
                ["OPENAI_CODEX_ACCESS_TOKEN"] = "token"
            },
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(UnrealAgentRunner.ProhibitedCodexCredentialMarker, result.FailureReason);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    [Fact]
    public async Task SmokeProbe_CodexAuthFile_RejectsAsPersistent()
    {
        var probe = new UnrealSmokeProbe();
        var cred = new AgentCredential(AgentKind.Unreal,
            new Dictionary<string, string>
            {
                ["OPENROUTER_API_KEY"] = "key",
                ["OPENAI_CODEX_AUTH_FILE"] = "/path/to/auth.json"
            },
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(UnrealAgentRunner.ProhibitedCodexCredentialMarker, result.FailureReason);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    [Fact]
    public async Task SmokeProbe_CodexProviderEnv_RejectsAsPersistent()
    {
        var probe = new UnrealSmokeProbe();
        var cred = new AgentCredential(AgentKind.Unreal,
            new Dictionary<string, string>
            {
                ["OPENROUTER_API_KEY"] = "key",
                ["UNREAL_HARNESS_LLM_PROVIDER"] = "openai-codex"
            },
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(UnrealAgentRunner.ProhibitedCodexCredentialMarker, result.FailureReason);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    [Fact]
    public void InVmSmokeProbe_NullCredential_ReturnsSingleHelpStep()
    {
        var probe = new UnrealInVmSmokeProbe();
        var steps = probe.BuildSteps(credential: null);

        Assert.Single(steps);
        Assert.Equal([UnrealAgentRunner.DefaultBinary, "-h"], steps[0].Argv);
        Assert.Null(steps[0].Stdin);
    }

    [Fact]
    public void InVmSmokeProbe_WithCredential_ReturnsHelpStepAndStdinPromptStep()
    {
        var probe = new UnrealInVmSmokeProbe();
        var cred = new AgentCredential(AgentKind.Unreal,
            new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = "key" },
            new Dictionary<string, string>());

        var steps = probe.BuildSteps(cred);

        Assert.Equal(2, steps.Count);
        // Step 1: -h
        Assert.Equal([UnrealAgentRunner.DefaultBinary, "-h"], steps[0].Argv);

        // Step 2: stdin prompt dispatch path
        var step2 = steps[1];
        Assert.Equal(UnrealAgentRunner.DefaultBinary, step2.Argv[0]);
        Assert.Contains("-workspace", step2.Argv);
        Assert.Contains("-log-directory", step2.Argv);
        Assert.Contains("-session-directory", step2.Argv);
        Assert.NotNull(step2.Stdin);

        using var doc = JsonDocument.Parse(step2.Stdin);
        Assert.Equal("ping", doc.RootElement.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task ModelListProbe_ReturnsKnownModels()
    {
        var probe = new UnrealModelListProbe();
        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.NotNull(result.ModelIds);
        Assert.Contains("gpt-6-astra", result.ModelIds);
        Assert.Contains("openrouter/nvidia/nemotron-3.5-lightning:free", result.ModelIds);
    }
}
