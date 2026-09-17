using CodeyBox.Agents.Continue;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ContinueSmokeProbe"/>,
/// <see cref="ContinueInVmSmokeProbe"/>, <see cref="ContinueModelListProbe"/>,
/// and <see cref="ContinueKnownModels"/>: the host-side probe is a
/// credential-presence check only (no network call — Continue fronts
/// hundreds of models and any provider call would spend real quota), the
/// in-VM probe pins the runner's binary plus its <c>--print</c> transport
/// and <c>--auto</c> autonomy flag, and the model-list probe serves the
/// curated seed with warn-only validation.
/// </summary>
public sealed class ContinueProbeTests
{
    private static AgentCredential Cred(string? key) =>
        new(AgentKind.Continue,
            key is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = key },
            new Dictionary<string, string>());

    [Fact]
    public async Task SmokeProbe_WithKey_PassesWithoutNetwork()
    {
        var result = await new ContinueSmokeProbe(NullLogger<ContinueSmokeProbe>.Instance)
            .SmokeTestAsync(Cred("test-key"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(SmokeFailureCategory.None, result.Category);
    }

    [Fact]
    public async Task SmokeProbe_WithoutKey_FailsNamingHostVariable()
    {
        var result = await new ContinueSmokeProbe(NullLogger<ContinueSmokeProbe>.Instance)
            .SmokeTestAsync(Cred(null), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        Assert.Contains("CODEYBOX_CONTINUE_API_KEY", result.FailureReason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void InVmProbe_EmitsVersionPlusPrintAndAutoAssertions_PinnedToRunnerBinary()
    {
        // Continue's probe has two steps: the --version binary check plus a
        // --help assertion for the runner's only transport (--print) and its
        // autonomy flag (--auto). Both pin to the runner's binary constant so
        // probe/runner drift fails loudly.
        var probe = new ContinueInVmSmokeProbe();
        Assert.Equal(AgentKind.Continue, probe.Kind);

        foreach (var credential in new AgentCredential?[] { null, Cred("k") })
        {
            var steps = probe.BuildSteps(credential);
            Assert.Equal(2, steps.Count);
            Assert.Equal([ContinueAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
            Assert.Equal("cn", ContinueAgentRunner.DefaultBinary);
            var assertion = string.Join(" ", steps[1].Argv);
            Assert.Contains(ContinueAgentRunner.DefaultBinary, assertion, StringComparison.Ordinal);
            Assert.Contains("--print", assertion, StringComparison.Ordinal);
            Assert.Contains("--auto", assertion, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ModelListProbe_ReturnsKnownSeed()
    {
        var result = await new ContinueModelListProbe().GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains(ContinueKnownModels.All[0], result.ModelIds);
    }

    [Fact]
    public void KnownModels_SeedContainsShippedDefault()
    {
        // The shipped CodeyBox:AgentDefaults[continue] id must equal the
        // entry the runner seeds into the guest config; the seed and the
        // default must agree or dispatches route an unvetted id.
        Assert.Contains(
            "nvidia/nemotron-3.5-lightning:free",
            ContinueKnownModels.All);
        Assert.True(ContinueKnownModels.IsKnown("nvidia/nemotron-3.5-lightning:free"));
        Assert.False(ContinueKnownModels.IsKnown("some-paid-model"));
    }

    [Fact]
    public void ValidateModelId_UnknownId_WarnsButDoesNotReject()
    {
        var log = NullLogger.Instance;

        var message = ContinueKnownModels.ValidateModelIdAgainstProviderList(
            "frontier-coding", "typo-model:free", log);

        Assert.NotNull(message);
        Assert.Contains("typo-model", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateModelId_KnownId_Silent()
    {
        var log = NullLogger.Instance;

        var message = ContinueKnownModels.ValidateModelIdAgainstProviderList(
            "frontier-coding", "nvidia/nemotron-3.5-lightning:free", log);

        Assert.Null(message);
    }

    [Fact]
    public void ValidateModelId_BlankId_ReturnsNull()
    {
        var log = NullLogger.Instance;

        Assert.Null(ContinueKnownModels.ValidateModelIdAgainstProviderList("frontier-coding", null, log));
        Assert.Null(ContinueKnownModels.ValidateModelIdAgainstProviderList("frontier-coding", "  ", log));
    }

    [Fact]
    public void IsKnown_MatchesCaseInsensitively()
    {
        Assert.True(ContinueKnownModels.IsKnown("nvidia/nemotron-3.5-lightning:free"));
        Assert.True(ContinueKnownModels.IsKnown("NVIDIA/NEMOTRON-3.5-LIGHTNING:FREE"));
        Assert.False(ContinueKnownModels.IsKnown(null));
        Assert.False(ContinueKnownModels.IsKnown("  "));
    }
}
