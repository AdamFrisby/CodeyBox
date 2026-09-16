using CodeyBox.Agents.Cline;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ClineSmokeProbe"/>, <see cref="ClineModelListProbe"/>,
/// <see cref="ClineKnownModels"/>, and <see cref="ClineInVmSmokeProbe"/>.
/// The smoke probe is a credential-presence check only (any provider call
/// would spend real quota); the in-VM probe pins the binary against the
/// runner constant plus a <c>--json</c> transport assertion.
/// </summary>
public sealed class ClineSmokeProbeTests
{
    private static AgentCredential Cred(string? key) =>
        new(AgentKind.Cline,
            key is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = key },
            new Dictionary<string, string>());

    [Fact]
    public void Kind_IsCline()
    {
        Assert.Equal(AgentKind.Cline, new ClineSmokeProbe().Kind);
    }

    [Fact]
    public async Task SmokeTestAsync_ApiKeyPresent_ReturnsOk()
    {
        var result = await new ClineSmokeProbe(NullLogger<ClineSmokeProbe>.Instance)
            .SmokeTestAsync(Cred("test-key"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.FailureReason);
        Assert.Equal(SmokeFailureCategory.None, result.Category);
    }

    [Fact]
    public async Task SmokeTestAsync_ApiKeyMissing_ReturnsPersistentFailure()
    {
        foreach (var credential in new[] { Cred(null), Cred(string.Empty) })
        {
            var result = await new ClineSmokeProbe(NullLogger<ClineSmokeProbe>.Instance)
                .SmokeTestAsync(credential, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.NotNull(result.FailureReason);
            Assert.Contains("CODEYBOX_CLINE_API_KEY", result.FailureReason, StringComparison.Ordinal);
            // Missing credential never self-heals: operator action required.
            Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        }
    }
}

public sealed class ClineModelListProbeTests
{
    [Fact]
    public void Kind_IsCline()
    {
        Assert.Equal(AgentKind.Cline, new ClineModelListProbe().Kind);
    }

    [Fact]
    public async Task GetModelListAsync_ReturnsKnownSeed()
    {
        var result = await new ClineModelListProbe().GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains("nvidia/nemotron-3.5-lightning:free", result.ModelIds);
    }
}

public sealed class ClineKnownModelsTests
{
    [Fact]
    public void IsKnown_SeedModel_ReturnsTrue()
    {
        Assert.True(ClineKnownModels.IsKnown("nvidia/nemotron-3.5-lightning:free"));
        Assert.False(ClineKnownModels.IsKnown("anthropic/claude-sonnet-4"));
        Assert.False(ClineKnownModels.IsKnown(null));
        Assert.False(ClineKnownModels.IsKnown("  "));
    }

    [Fact]
    public void ValidateModelIdAgainstProviderList_UnknownId_WarnsButAllows()
    {
        using var factory = new TestLoggerFactory();
        var log = factory.CreateLogger("test");

        // Unknown ids are not rejected (the CLI accepts any provider-native
        // id beyond the seed) — the warning prompts a typo check.
        var message = ClineKnownModels.ValidateModelIdAgainstProviderList(
            "frontier-coding", "anthropic/claude-sonnet-4", log);

        Assert.NotNull(message);
        Assert.Contains("anthropic/claude-sonnet-4", message, StringComparison.Ordinal);
        Assert.Single(factory.Entries);
    }

    [Fact]
    public void ValidateModelIdAgainstProviderList_KnownId_Silent()
    {
        using var factory = new TestLoggerFactory();
        var log = factory.CreateLogger("test");

        var message = ClineKnownModels.ValidateModelIdAgainstProviderList(
            "frontier-coding", "nvidia/nemotron-3.5-lightning:free", log);

        Assert.Null(message);
        Assert.Empty(factory.Entries);
    }

    private sealed class TestLoggerFactory : ILoggerFactory
    {
        public List<string> Entries { get; } = [];

        public void AddProvider(ILoggerProvider provider) { }

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(Entries);

        public void Dispose() { }

        private sealed class CapturingLogger(List<string> entries) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                entries.Add(formatter(state, exception));
            }
        }
    }
}

public sealed class ClineInVmSmokeProbeTests
{
    [Fact]
    public void Kind_IsCline()
    {
        Assert.Equal(AgentKind.Cline, new ClineInVmSmokeProbe().Kind);
    }

    [Fact]
    public void BuildSteps_EmitsVersionPlusJsonAssertion_PinnedToRunnerBinary()
    {
        // The probe has two steps: the --version binary check plus a --help
        // assertion for --json (the runner's only transport). Both pin to
        // the runner's binary constant so probe/runner drift fails loudly.
        var probe = new ClineInVmSmokeProbe();

        foreach (var credential in new AgentCredential?[] { null, Cred("test-key") })
        {
            var steps = probe.BuildSteps(credential);
            Assert.Equal(2, steps.Count);
            Assert.Equal([ClineAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
            Assert.Contains(ClineAgentRunner.DefaultBinary, string.Join(" ", steps[1].Argv));
            Assert.Contains("--json", string.Join(" ", steps[1].Argv));
        }
    }

    private static AgentCredential Cred(string key) =>
        new(AgentKind.Cline,
            new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = key },
            new Dictionary<string, string>());
}
