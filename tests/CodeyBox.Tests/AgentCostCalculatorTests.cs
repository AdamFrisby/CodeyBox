using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class AgentCostCalculatorTests
{
    private static AgentPricingOptions MakeOpts() => new()
    {
        Rates = new()
        {
            ["claude"] = new()
            {
                ["claude-opus-4-7"] = new() { InputPerMillion = 15.0, CachedInputPerMillion = 1.50, OutputPerMillion = 75.0 }
            }
        },
        DefaultRates = new()
        {
            ["codex"] = new() { InputPerMillion = 5.0, CachedInputPerMillion = 0.5, OutputPerMillion = 25.0 }
        }
    };

    [Fact]
    public void KnownModel_CalculatesCorrectUsd()
    {
        // Billable input = 12345 - 5000 = 7345 → 7345 * 15.0 / 1_000_000 = 0.110175
        // Cached = 5000 * 1.50 / 1_000_000 = 0.0075
        // Output = 678 * 75.0 / 1_000_000 = 0.050850
        // Total = 0.168525
        var calculator = new AgentCostCalculator(MakeOpts());
        var snapshot = new AgentCostSnapshot(InputTokens: 12345, CachedInputTokens: 5000, OutputTokens: 678, ModelId: "claude-opus-4-7");

        var result = calculator.Calculate(snapshot, AgentKind.Claude);

        Assert.Equal(0.168525m, result);
    }

    [Fact]
    public void UnknownModel_FallsBackToDefaultRate()
    {
        var calculator = new AgentCostCalculator(MakeOpts());
        var snapshot = new AgentCostSnapshot(InputTokens: 1000, CachedInputTokens: 0, OutputTokens: 500, ModelId: "codex-unknown");

        var result = calculator.Calculate(snapshot, AgentKind.Codex);

        // input: 1000 * 5.0 / 1_000_000 = 0.005
        // output: 500 * 25.0 / 1_000_000 = 0.0125
        // total = 0.0175
        Assert.Equal(0.0175m, result);
    }

    [Fact]
    public void UnknownModelAndNoDefaultRate_FallsBackToBuiltIn()
    {
        var opts = new AgentPricingOptions();
        var calculator = new AgentCostCalculator(opts);
        var snapshot = new AgentCostSnapshot(InputTokens: 1000, CachedInputTokens: 0, OutputTokens: 500, ModelId: null);

        var result = calculator.Calculate(snapshot, AgentKind.Claude);

        // Built-in claude: input 15.0, output 75.0
        // 1000 * 15.0 / 1_000_000 + 500 * 75.0 / 1_000_000 = 0.015 + 0.0375 = 0.0525
        Assert.Equal(0.0525m, result);
    }

    [Fact]
    public void ZeroTokens_ReturnsZero()
    {
        var calculator = new AgentCostCalculator(MakeOpts());
        var snapshot = new AgentCostSnapshot(InputTokens: 0, CachedInputTokens: 0, OutputTokens: 0, ModelId: "claude-opus-4-7");

        var result = calculator.Calculate(snapshot, AgentKind.Claude);

        Assert.Equal(0m, result);
    }

    [Fact]
    public void NegativeRatesInConfig_ValidateAtStartupThrows()
    {
        var opts = new AgentPricingOptions
        {
            Rates = new()
            {
                ["claude"] = new()
                {
                    ["bad-model"] = new() { InputPerMillion = -1.0, CachedInputPerMillion = 0, OutputPerMillion = 0 }
                }
            }
        };

        Assert.Throws<InvalidOperationException>(() =>
            AgentCostCalculator.ValidateAtStartup(opts, [AgentKind.Claude], NullLogger.Instance));
    }

    [Fact]
    public void NegativeDefaultRatesInConfig_ValidateAtStartupThrows()
    {
        var opts = new AgentPricingOptions
        {
            DefaultRates = new()
            {
                ["gemini"] = new() { InputPerMillion = 0, CachedInputPerMillion = 0, OutputPerMillion = -1.0 }
            }
        };

        Assert.Throws<InvalidOperationException>(() =>
            AgentCostCalculator.ValidateAtStartup(opts, [AgentKind.Gemini], NullLogger.Instance));
    }

    [Fact]
    public void MissingPricing_ValidateAtStartupDoesNotThrow()
    {
        // Gemini has no registered pricing; built-in fallback exists so only a Warning is emitted.
        // Should not throw.
        var opts = new AgentPricingOptions();

        AgentCostCalculator.ValidateAtStartup(opts, [AgentKind.Gemini], NullLogger.Instance);
    }
}
