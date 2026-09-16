using CodeyBox.Agents.Kilo;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="KiloSmokeProbe"/>, <see cref="KiloInVmSmokeProbe"/>,
/// <see cref="KiloModelListProbe"/>, and <see cref="KiloKnownModels"/>: the
/// host-side probe is a credential-presence check only (no network call —
/// kilo fronts hundreds of models and any provider call would spend real
/// quota), the in-VM probe pins the runner's binary plus its <c>--format
/// json</c> transport and mandatory <c>--auto</c> flag, and the model-list
/// probe serves the curated seed with warn-only validation.
/// </summary>
public sealed class KiloProbeTests
{
    private static AgentCredential Cred(string? key) =>
        new(AgentKind.Kilo,
            key is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["KILO_API_KEY"] = key },
            new Dictionary<string, string>());

    [Fact]
    public async Task SmokeProbe_WithKey_PassesWithoutNetwork()
    {
        var result = await new KiloSmokeProbe(NullLogger<KiloSmokeProbe>.Instance)
            .SmokeTestAsync(Cred("test-key"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(SmokeFailureCategory.None, result.Category);
    }

    [Fact]
    public async Task SmokeProbe_WithoutKey_FailsNamingHostVariable()
    {
        var result = await new KiloSmokeProbe(NullLogger<KiloSmokeProbe>.Instance)
            .SmokeTestAsync(Cred(null), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        Assert.Contains("CODEYBOX_KILO_API_KEY", result.FailureReason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void InVmProbe_EmitsVersionPlusFormatAndAutoAssertions_PinnedToRunnerBinary()
    {
        // Kilo's probe has two steps: the --version binary check plus a
        // `run --help` assertion for the runner's only transport (--format
        // json) and its mandatory autonomy flag (--auto). Both pin to the
        // runner's binary constant so probe/runner drift fails loudly.
        var probe = new KiloInVmSmokeProbe();
        Assert.Equal(AgentKind.Kilo, probe.Kind);

        foreach (var credential in new AgentCredential?[] { null, Cred("k") })
        {
            var steps = probe.BuildSteps(credential);
            Assert.Equal(2, steps.Count);
            Assert.Equal([KiloAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
            var assertion = string.Join(" ", steps[1].Argv);
            Assert.Contains(KiloAgentRunner.DefaultBinary, assertion, StringComparison.Ordinal);
            Assert.Contains("--format", assertion, StringComparison.Ordinal);
            Assert.Contains("--auto", assertion, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ModelListProbe_ReturnsKnownSeed()
    {
        var result = await new KiloModelListProbe().GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains(KiloKnownModels.All[0], result.ModelIds);
    }

    [Fact]
    public void KnownModels_SeedContainsShippedDefault()
    {
        // The shipped CodeyBox:AgentDefaults[kilo] id must resolve against
        // the seeded guest-config models map; the seed and the default must
        // agree or dispatch fails closed with "Model not found".
        Assert.Contains(
            "openai-compatible/nvidia/nemotron-3.5-lightning:free",
            KiloKnownModels.All);
        Assert.True(KiloKnownModels.IsKnown("openai-compatible/nvidia/nemotron-3.5-lightning:free"));
        Assert.False(KiloKnownModels.IsKnown("openai-compatible/some-paid-model"));
    }

    [Fact]
    public void ValidateModelId_UnknownId_WarnsButDoesNotReject()
    {
        var log = NullLogger.Instance;

        var message = KiloKnownModels.ValidateModelIdAgainstProviderList(
            "frontier-coding", "openai-compatible/typo-model:free", log);

        Assert.NotNull(message);
        Assert.Contains("typo-model", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateModelId_KnownId_Silent()
    {
        var log = NullLogger.Instance;

        var message = KiloKnownModels.ValidateModelIdAgainstProviderList(
            "frontier-coding", "openai-compatible/nvidia/nemotron-3.5-lightning:free", log);

        Assert.Null(message);
    }

    [Fact]
    public void ValidateModelId_BlankId_ReturnsNull()
    {
        var log = NullLogger.Instance;

        Assert.Null(KiloKnownModels.ValidateModelIdAgainstProviderList("frontier-coding", null, log));
        Assert.Null(KiloKnownModels.ValidateModelIdAgainstProviderList("frontier-coding", "  ", log));
    }

    [Fact]
    public void IsKnown_MatchesCaseInsensitively()
    {
        Assert.True(KiloKnownModels.IsKnown("openai-compatible/nvidia/nemotron-3.5-lightning:free"));
        Assert.True(KiloKnownModels.IsKnown("OPENAI-COMPATIBLE/NVIDIA/NEMOTRON-3.5-LIGHTNING:FREE"));
        Assert.False(KiloKnownModels.IsKnown("nvidia/nemotron-3.5-lightning:free"));
        Assert.False(KiloKnownModels.IsKnown(null));
        Assert.False(KiloKnownModels.IsKnown("  "));
    }
}
