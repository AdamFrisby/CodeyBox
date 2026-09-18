using CodeyBox.Agents.Crush;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CrushSmokeProbe"/>,
/// <see cref="CrushInVmSmokeProbe"/>, <see cref="CrushModelListProbe"/>,
/// and <see cref="CrushKnownModels"/>: the host-side probe is a
/// credential-presence check only (no network call — Crush fronts dozens
/// of providers and any provider call would spend real quota), the in-VM
/// probe pins the runner's binary plus its <c>run -m</c> dispatch flags
/// (deliberately NOT <c>--yolo</c>, which is root-only and rejected by
/// <c>run</c>), and the model-list probe serves the curated seed with
/// warn-only validation. No quota-meter probe exists — <c>crush stats</c>
/// renders HTML with no machine-readable balance — so members fall through
/// to the <c>NullQuotaProbe</c> unknown path.
/// </summary>
public sealed class CrushProbeTests
{
    private static AgentCredential Cred(string? key) =>
        new(AgentKind.Crush,
            key is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = key },
            new Dictionary<string, string>());

    [Fact]
    public async Task SmokeProbe_WithKey_PassesWithoutNetwork()
    {
        var result = await new CrushSmokeProbe(NullLogger<CrushSmokeProbe>.Instance)
            .SmokeTestAsync(Cred("test-key"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(SmokeFailureCategory.None, result.Category);
    }

    [Fact]
    public async Task SmokeProbe_WithoutKey_FailsNamingHostVariable()
    {
        var result = await new CrushSmokeProbe(NullLogger<CrushSmokeProbe>.Instance)
            .SmokeTestAsync(Cred(null), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        Assert.Contains("CODEYBOX_CRUSH_API_KEY", result.FailureReason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void InVmProbe_EmitsVersionPlusModelAndQuietAssertions_PinnedToRunnerBinary()
    {
        // Crush's probe has two steps: the --version binary check plus a
        // `run --help` assertion for the runner's exact dispatch flags (-m
        // and -q). Both pin to the runner's binary constant so
        // probe/runner drift fails loudly. --yolo is deliberately NOT
        // asserted: it is a root-only flag that `run` rejects.
        var probe = new CrushInVmSmokeProbe();
        Assert.Equal(AgentKind.Crush, probe.Kind);

        foreach (var credential in new AgentCredential?[] { null, Cred("k") })
        {
            var steps = probe.BuildSteps(credential);
            Assert.Equal(2, steps.Count);
            Assert.Equal([CrushAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
            Assert.Equal("crush", CrushAgentRunner.DefaultBinary);
            var assertion = string.Join(" ", steps[1].Argv);
            Assert.Contains(CrushAgentRunner.DefaultBinary, assertion, StringComparison.Ordinal);
            Assert.Contains("run", assertion, StringComparison.Ordinal);
            Assert.Contains("--model", assertion, StringComparison.Ordinal);
            Assert.Contains("--quiet", assertion, StringComparison.Ordinal);
            Assert.DoesNotContain("--yolo", assertion, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InVmProbe_FirstStepNamesBinaryForExit127()
    {
        // The infrastructure-vs-no-change contract: when the CLI is absent
        // inside the guest the shell reports argv[0], so the first step
        // must BE the bare binary invocation.
        var steps = new CrushInVmSmokeProbe().BuildSteps(null);

        Assert.Equal("crush", steps[0].Argv[0]);
        Assert.NotNull(steps[0].FailureHint);
        Assert.Contains("crush", steps[0].FailureHint!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelListProbe_ReturnsKnownSeed()
    {
        var result = await new CrushModelListProbe().GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains(CrushKnownModels.All[0], result.ModelIds);
    }

    [Fact]
    public void KnownModels_SeedContainsShippedDefault()
    {
        // The shipped CodeyBox:AgentDefaults[crush] id must equal the id the
        // runner passes to -m; the seed and the default must agree or
        // dispatches route an unvetted id.
        Assert.Contains(
            "openrouter/nvidia/nemotron-3.5-lightning:free",
            CrushKnownModels.All);
        Assert.True(CrushKnownModels.IsKnown("openrouter/nvidia/nemotron-3.5-lightning:free"));
        Assert.False(CrushKnownModels.IsKnown("some-paid-model"));
    }

    [Fact]
    public void ValidateModelId_UnknownId_WarnsButDoesNotReject()
    {
        var log = NullLogger.Instance;

        var message = CrushKnownModels.ValidateModelIdAgainstProviderList(
            "frontier-coding", "typo-model:free", log);

        Assert.NotNull(message);
        Assert.Contains("typo-model", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateModelId_KnownId_Silent()
    {
        var log = NullLogger.Instance;

        var message = CrushKnownModels.ValidateModelIdAgainstProviderList(
            "frontier-coding", "openrouter/nvidia/nemotron-3.5-lightning:free", log);

        Assert.Null(message);
    }

    [Fact]
    public void ValidateModelId_BlankId_ReturnsNull()
    {
        var log = NullLogger.Instance;

        Assert.Null(CrushKnownModels.ValidateModelIdAgainstProviderList("frontier-coding", null, log));
        Assert.Null(CrushKnownModels.ValidateModelIdAgainstProviderList("frontier-coding", "  ", log));
    }

    [Fact]
    public void IsKnown_MatchesCaseInsensitively()
    {
        Assert.True(CrushKnownModels.IsKnown("openrouter/nvidia/nemotron-3.5-lightning:free"));
        Assert.True(CrushKnownModels.IsKnown("OPENROUTER/NVIDIA/NEMOTRON-3.5-LIGHTNING:FREE"));
        Assert.False(CrushKnownModels.IsKnown(null));
        Assert.False(CrushKnownModels.IsKnown("  "));
    }
}
