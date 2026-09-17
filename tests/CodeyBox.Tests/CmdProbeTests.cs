using CodeyBox.Agents.Cmd;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CmdSmokeProbe"/>, <see cref="CmdInVmSmokeProbe"/>,
/// <see cref="CmdModelListProbe"/>, and <see cref="CmdKnownModels"/>: the
/// host-side probe is a credential-presence check only (no network call —
/// cmd fronts 150+ providers and any provider call would spend real quota),
/// the in-VM probe pins the runner's binary plus its <c>--output-format
/// json</c> transport and the mandatory <c>--yolo</c> autonomy flag, and the
/// model-list probe serves the curated seed with warn-only validation.
/// </summary>
public sealed class CmdProbeTests
{
    private static AgentCredential Cred(string? key) =>
        new(AgentKind.Cmd,
            key is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = key },
            new Dictionary<string, string>());

    [Fact]
    public async Task SmokeProbe_WithKey_PassesWithoutNetwork()
    {
        var result = await new CmdSmokeProbe(NullLogger<CmdSmokeProbe>.Instance)
            .SmokeTestAsync(Cred("test-key"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(SmokeFailureCategory.None, result.Category);
    }

    [Fact]
    public async Task SmokeProbe_WithoutKey_FailsNamingHostVariable()
    {
        var result = await new CmdSmokeProbe(NullLogger<CmdSmokeProbe>.Instance)
            .SmokeTestAsync(Cred(null), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        Assert.Contains("CODEYBOX_CMD_API_KEY", result.FailureReason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void InVmProbe_EmitsVersionPlusTransportAndYoloAssertions_PinnedToRunnerBinary()
    {
        // Cmd's probe has two steps: the --version binary check plus a -p
        // --help assertion for the runner's only transport (--output-format
        // json) and its mandatory autonomy flag (--yolo — without it a
        // headless run silently changes nothing). Both pin to the runner's
        // binary constant so probe/runner drift fails loudly.
        var probe = new CmdInVmSmokeProbe();
        Assert.Equal(AgentKind.Cmd, probe.Kind);

        foreach (var credential in new AgentCredential?[] { null, Cred("k") })
        {
            var steps = probe.BuildSteps(credential);
            Assert.Equal(2, steps.Count);
            Assert.Equal([CmdAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
            var assertion = string.Join(" ", steps[1].Argv);
            Assert.Contains(CmdAgentRunner.DefaultBinary, assertion, StringComparison.Ordinal);
            Assert.Contains("--output-format", assertion, StringComparison.Ordinal);
            Assert.Contains("json", assertion, StringComparison.Ordinal);
            Assert.Contains("--yolo", assertion, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ModelListProbe_ReturnsKnownSeed()
    {
        var result = await new CmdModelListProbe().GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains(CmdKnownModels.All[0], result.ModelIds);
    }

    [Fact]
    public void KnownModels_SeedContainsShippedDefault()
    {
        Assert.Contains("openrouter/nvidia/nemotron-3.5-lightning:free", CmdKnownModels.All);
        Assert.True(CmdKnownModels.IsKnown("openrouter/nvidia/nemotron-3.5-lightning:free"));
        Assert.False(CmdKnownModels.IsKnown("anthropic/paid-model"));
        Assert.False(CmdKnownModels.IsKnown(null));
    }

    [Fact]
    public void KnownModels_UnknownId_WarnsButIsNotRejected()
    {
        var log = NullLogger.Instance;

        var message = CmdKnownModels.ValidateModelIdAgainstProviderList("cls", "some/future-model", log);

        Assert.NotNull(message);
        Assert.Contains("some/future-model", message, StringComparison.Ordinal);
        Assert.Null(CmdKnownModels.ValidateModelIdAgainstProviderList("cls", "openrouter/nvidia/nemotron-3.5-lightning:free", log));
        Assert.Null(CmdKnownModels.ValidateModelIdAgainstProviderList("cls", null, log));
    }
}
