using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Api;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the bundled-defaults loader and the merge with operator config.
/// Each test stages its own pricing JSON in a temp directory so it can
/// assert load + merge behaviour without depending on the file shipped
/// next to the API binary.
/// </summary>
public sealed class AgentPricingDefaultsTests : IDisposable
{
    private readonly string _tempDir;

    public AgentPricingDefaultsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codeybox-pricing-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string content) =>
        File.WriteAllText(Path.Combine(_tempDir, AgentPricingDefaults.FileName), content);

    [Fact]
    public void Load_MissingFile_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentPricingDefaults.Load(_tempDir, NullLogger.Instance));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ValidFile_ParsesMetaAndRates()
    {
        Write("""
            {
              "_meta": {
                "lastUpdated": "2026-05-28",
                "sources": { "claude": "https://example.invalid/pricing" },
                "notes":   { "claude": "test fixture" }
              },
              "Rates": {
                "claude": {
                  "claude-opus-4-7": { "inputPerMillion": 5.0, "cachedInputPerMillion": 0.5, "outputPerMillion": 25.0 }
                }
              }
            }
            """);

        var bundle = AgentPricingDefaults.Load(_tempDir, NullLogger.Instance);

        Assert.Equal("2026-05-28", bundle.Meta.LastUpdated);
        Assert.Equal("https://example.invalid/pricing", bundle.Meta.Sources["claude"]);
        Assert.Equal("test fixture", bundle.Meta.Notes["claude"]);
        var rate = bundle.Rates["claude"]["claude-opus-4-7"];
        Assert.Equal(5.0, rate.InputPerMillion);
        Assert.Equal(0.5, rate.CachedInputPerMillion);
        Assert.Equal(25.0, rate.OutputPerMillion);
    }

    [Fact]
    public void Load_MalformedJson_ThrowsLoudly()
    {
        Write("{ this is not valid json");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentPricingDefaults.Load(_tempDir, NullLogger.Instance));

        Assert.Contains("malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_NullDocument_Throws()
    {
        Write("null");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentPricingDefaults.Load(_tempDir, NullLogger.Instance));

        Assert.Contains("deserialized to null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_NullRateEntry_Throws()
    {
        Write("""
            {
              "Rates": {
                "claude": {
                  "claude-bad": null
                }
              }
            }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentPricingDefaults.Load(_tempDir, NullLogger.Instance));

        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_NegativeRate_Throws()
    {
        Write("""
            {
              "Rates": {
                "claude": {
                  "claude-bad": { "inputPerMillion": -1.0, "cachedInputPerMillion": 0, "outputPerMillion": 0 }
                }
              }
            }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentPricingDefaults.Load(_tempDir, NullLogger.Instance));

        Assert.Contains("negative", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Merge_BundledOnly_AllBundledRatesPresent()
    {
        var bundled = MakeBundle(
            ("claude", "claude-opus-4-7", 5.0, 0.5, 25.0),
            ("claude", "claude-haiku-4-5", 1.0, 0.1, 5.0));

        var merged = AgentPricingOptions.Merge(bundled, new AgentPricingOptions());

        Assert.Equal(2, merged.BundledRateCount);
        Assert.Equal(0, merged.OperatorRateCount);
        Assert.Equal(0, merged.OverlapCount);
        Assert.Equal(2, merged.TotalRateCount);
        Assert.Equal(5.0, merged.Options.Rates["claude"]["claude-opus-4-7"].InputPerMillion);
        Assert.Equal(1.0, merged.Options.Rates["claude"]["claude-haiku-4-5"].InputPerMillion);
    }

    [Fact]
    public void Merge_OperatorOnly_NoBundledNoOverlap()
    {
        var bundled = new BundledAgentPricing();
        var operatorOpts = new AgentPricingOptions
        {
            Rates = new()
            {
                ["opencode"] = new()
                {
                    ["deepseek-v4-pro"] = new() { InputPerMillion = 0.27, CachedInputPerMillion = 0.07, OutputPerMillion = 1.10 }
                }
            }
        };

        var merged = AgentPricingOptions.Merge(bundled, operatorOpts);

        Assert.Equal(0, merged.BundledRateCount);
        Assert.Equal(1, merged.OperatorRateCount);
        Assert.Equal(0, merged.OverlapCount);
        Assert.Equal(1, merged.TotalRateCount);
        Assert.Equal(0.27, merged.Options.Rates["opencode"]["deepseek-v4-pro"].InputPerMillion);
    }

    [Fact]
    public void Merge_OperatorWinsOnOverlap()
    {
        var bundled = MakeBundle(
            ("claude", "claude-opus-4-7", 5.0, 0.5, 25.0));
        var operatorOpts = new AgentPricingOptions
        {
            Rates = new()
            {
                ["claude"] = new()
                {
                    ["claude-opus-4-7"] = new() { InputPerMillion = 10.0, CachedInputPerMillion = 1.0, OutputPerMillion = 50.0 }
                }
            }
        };

        var merged = AgentPricingOptions.Merge(bundled, operatorOpts);

        Assert.Equal(1, merged.BundledRateCount);
        Assert.Equal(1, merged.OperatorRateCount);
        Assert.Equal(1, merged.OverlapCount);
        Assert.Equal(1, merged.TotalRateCount);
        Assert.Equal(10.0, merged.Options.Rates["claude"]["claude-opus-4-7"].InputPerMillion);
        Assert.Equal(50.0, merged.Options.Rates["claude"]["claude-opus-4-7"].OutputPerMillion);
    }

    [Fact]
    public void Merge_NewAgentFromOperator_AddedAlongsideBundledAgents()
    {
        var bundled = MakeBundle(("claude", "claude-opus-4-7", 5.0, 0.5, 25.0));
        var operatorOpts = new AgentPricingOptions
        {
            Rates = new()
            {
                ["opencode"] = new()
                {
                    ["deepseek-v4-pro"] = new() { InputPerMillion = 0.27, CachedInputPerMillion = 0.07, OutputPerMillion = 1.10 }
                }
            }
        };

        var merged = AgentPricingOptions.Merge(bundled, operatorOpts);

        Assert.Equal(2, merged.TotalRateCount);
        Assert.True(merged.Options.Rates.ContainsKey("claude"));
        Assert.True(merged.Options.Rates.ContainsKey("opencode"));
    }

    [Fact]
    public void Merge_PreservesOperatorDefaultRates()
    {
        var bundled = MakeBundle(("claude", "claude-opus-4-7", 5.0, 0.5, 25.0));
        var operatorOpts = new AgentPricingOptions
        {
            DefaultRates = new()
            {
                ["codex"] = new() { InputPerMillion = 5.0, CachedInputPerMillion = 0.5, OutputPerMillion = 30.0 }
            }
        };

        var merged = AgentPricingOptions.Merge(bundled, operatorOpts);

        Assert.Equal(5.0, merged.Options.DefaultRates["codex"].InputPerMillion);
    }

    [Fact]
    public void Merge_ReplacingDictionarySlot_DoesNotAffectMergedSnapshot()
    {
        var bundled = MakeBundle(("claude", "claude-opus-4-7", 5.0, 0.5, 25.0));
        var operatorOpts = new AgentPricingOptions();

        var merged = AgentPricingOptions.Merge(bundled, operatorOpts);

        bundled.Rates["claude"]["claude-opus-4-7"] = new ModelRateConfig
        {
            InputPerMillion = 999,
            CachedInputPerMillion = 999,
            OutputPerMillion = 999,
        };

        Assert.Equal(5.0, merged.Options.Rates["claude"]["claude-opus-4-7"].InputPerMillion);
    }

    [Fact]
    public void Merge_InstanceMutation_DoesNotAffectMergedSnapshot()
    {
        var bundled = MakeBundle(("claude", "claude-opus-4-7", 5.0, 0.5, 25.0));
        var merged = AgentPricingOptions.Merge(bundled, new AgentPricingOptions());

        bundled.Rates["claude"]["claude-opus-4-7"].InputPerMillion = 999.0;

        Assert.Equal(5.0, merged.Options.Rates["claude"]["claude-opus-4-7"].InputPerMillion);
    }

    [Fact]
    public void Merge_NullOperatorRate_Throws()
    {
        var bundled = new BundledAgentPricing();
        var operatorOpts = new AgentPricingOptions
        {
            Rates = new()
            {
                ["claude"] = new() { ["claude-opus-4-7"] = null! }
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentPricingOptions.Merge(bundled, operatorOpts));

        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShippedDefaultsFile_LoadsCleanly()
    {
        var sourceFile = LocateShippedFile();
        Assert.True(File.Exists(sourceFile),
            $"agent-pricing-defaults.json not found at expected location: {sourceFile}");

        var dir = Path.GetDirectoryName(sourceFile)!;
        var bundle = AgentPricingDefaults.Load(dir, NullLogger.Instance);

        Assert.False(string.IsNullOrWhiteSpace(bundle.Meta.LastUpdated),
            "_meta.lastUpdated must be set on the shipped file");
        Assert.True(bundle.Rates.ContainsKey("claude"), "shipped defaults must include claude pricing");
        Assert.True(bundle.Rates.ContainsKey("codex"), "shipped defaults must include codex pricing");
        Assert.True(bundle.Rates.ContainsKey("gemini"), "shipped defaults must include gemini pricing");
        Assert.True(bundle.Rates["codex"].ContainsKey("codex-5.5"),
            "shipped codex defaults must alias codex-5.5 for CLI attribution");
        Assert.True(bundle.Rates["gemini"].ContainsKey("gemini-3-flash-preview"),
            "shipped gemini defaults must include default AgentClasses model");
        Assert.NotEmpty(bundle.Rates["claude"]);
    }

    [Fact]
    public void ShippedDefaults_IsPresentBesideApiAssembly()
    {
        var apiDir = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
        var path = Path.Combine(apiDir, AgentPricingDefaults.FileName);
        Assert.True(File.Exists(path),
            $"CopyToOutputDirectory must place {AgentPricingDefaults.FileName} next to CodeyBox.Api at {path}");

        var bundle = AgentPricingDefaults.Load(apiDir, NullLogger.Instance);
        Assert.True(bundle.Rates["claude"].ContainsKey("claude-opus-4-7"));
    }

    private static string LocateShippedFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CodeyBox.Api", AgentPricingDefaults.FileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"Could not locate src/CodeyBox.Api/{AgentPricingDefaults.FileName} walking up from {AppContext.BaseDirectory}");
    }

    private static BundledAgentPricing MakeBundle(params (string Agent, string Model, double Input, double Cached, double Output)[] entries)
    {
        var bundle = new BundledAgentPricing();
        foreach (var (agent, model, input, cached, output) in entries)
        {
            if (!bundle.Rates.TryGetValue(agent, out var bucket))
            {
                bucket = new Dictionary<string, ModelRateConfig>(StringComparer.Ordinal);
                bundle.Rates[agent] = bucket;
            }
            bucket[model] = new ModelRateConfig
            {
                InputPerMillion = input,
                CachedInputPerMillion = cached,
                OutputPerMillion = output,
            };
        }
        return bundle;
    }
}
