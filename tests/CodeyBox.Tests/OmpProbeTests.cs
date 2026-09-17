using CodeyBox.Agents.Omp;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="OmpSmokeProbe"/>, <see cref="OmpInVmSmokeProbe"/>,
/// <see cref="OmpModelListProbe"/>, and <see cref="OmpKnownModels"/>: the
/// host-side probe is a credential-presence check only (no network call —
/// omp fronts ~60 providers and any provider call would spend real quota),
/// the in-VM probe pins the runner's binary plus its <c>--mode json</c>
/// transport and <c>-p/--print</c> one-shot flag, and the model-list probe
/// serves the curated seed with warn-only validation.
/// </summary>
public sealed class OmpProbeTests
{
    private static AgentCredential Cred(string? key) =>
        new(AgentKind.Omp,
            key is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = key },
            new Dictionary<string, string>());

    [Fact]
    public async Task SmokeProbe_WithKey_PassesWithoutNetwork()
    {
        var result = await new OmpSmokeProbe(NullLogger<OmpSmokeProbe>.Instance)
            .SmokeTestAsync(Cred("test-key"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(SmokeFailureCategory.None, result.Category);
    }

    [Fact]
    public async Task SmokeProbe_WithoutKey_FailsNamingHostVariable()
    {
        var result = await new OmpSmokeProbe(NullLogger<OmpSmokeProbe>.Instance)
            .SmokeTestAsync(Cred(null), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        Assert.Contains("CODEYBOX_OMP_API_KEY", result.FailureReason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void InVmProbe_EmitsVersionPlusModeAndPrintAssertions_PinnedToRunnerBinary()
    {
        // Omp's probe has two steps: the --version binary check plus a
        // --help assertion for the runner's only transport (--mode json)
        // and its one-shot contract (-p/--print — a bare omp "prompt" is
        // interactive). Both pin to the runner's binary constant so
        // probe/runner drift fails loudly.
        var probe = new OmpInVmSmokeProbe();
        Assert.Equal(AgentKind.Omp, probe.Kind);

        foreach (var credential in new AgentCredential?[] { null, Cred("k") })
        {
            var steps = probe.BuildSteps(credential);
            Assert.Equal(2, steps.Count);
            Assert.Equal([OmpAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
            var assertion = string.Join(" ", steps[1].Argv);
            Assert.Contains(OmpAgentRunner.DefaultBinary, assertion, StringComparison.Ordinal);
            Assert.Contains("--mode", assertion, StringComparison.Ordinal);
            Assert.Contains("json", assertion, StringComparison.Ordinal);
            Assert.Contains("--print", assertion, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ModelListProbe_ReturnsKnownSeed()
    {
        var result = await new OmpModelListProbe().GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains(OmpKnownModels.All[0], result.ModelIds);
    }

    [Fact]
    public void KnownModels_SeedContainsShippedDefault()
    {
        Assert.Contains("nvidia/nemotron-3.5-lightning:free", OmpKnownModels.All);
        Assert.True(OmpKnownModels.IsKnown("nvidia/nemotron-3.5-lightning:free"));
        Assert.False(OmpKnownModels.IsKnown("anthropic/paid-model"));
        Assert.False(OmpKnownModels.IsKnown(null));
    }

    [Fact]
    public void KnownModels_UnknownId_WarnsButIsNotRejected()
    {
        var log = NullLogger.Instance;

        var message = OmpKnownModels.ValidateModelIdAgainstProviderList("cls", "some/future-model", log);

        Assert.NotNull(message);
        Assert.Contains("some/future-model", message, StringComparison.Ordinal);
        Assert.Null(OmpKnownModels.ValidateModelIdAgainstProviderList("cls", "nvidia/nemotron-3.5-lightning:free", log));
        Assert.Null(OmpKnownModels.ValidateModelIdAgainstProviderList("cls", null, log));
    }
}
