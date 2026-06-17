using CodeyBox.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class UnboundConfigKeyValidatorTests
{
    [Fact]
    public void Inspect_FlagsAgentStreamsRootDirectoryAsUnbound()
    {
        // The brief's canonical example: operator wrote RootDirectory but the
        // bound property is Path; the binder silently dropped the value.
        var config = BuildConfig(new()
        {
            ["CodeyBox:AgentStreams:RootDirectory"] = "/var/log/codeybox",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal("CodeyBox:AgentStreams:RootDirectory", report.Path);
    }

    [Fact]
    public void Inspect_OffersNearestPropertySuggestionForTypoedKey()
    {
        // Cheap Levenshtein hint — only fires when the operator's typo is
        // close to a real property name (cutoff 3). "EnableSharedUpstreamMirorr"
        // is a one-edit typo of "EnableSharedUpstreamMirror".
        var config = BuildConfig(new()
        {
            ["CodeyBox:EnableSharedUpstreamMirorr"] = "true",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal("EnableSharedUpstreamMirror", report.NearestProperty);
    }

    [Fact]
    public void Inspect_AllowsKnownLeafProperty()
    {
        var config = BuildConfig(new()
        {
            ["CodeyBox:AgentStreams:Path"] = "/var/log/codeybox",
            ["CodeyBox:MaxTemplateChecks"] = "50",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        Assert.Empty(reports);
    }

    [Fact]
    public void Inspect_DescendsIntoNestedObjects_AndReportsPathInFull()
    {
        var config = BuildConfig(new()
        {
            ["CodeyBox:Shutdown:GraceSeconds"] = "30",
            ["CodeyBox:Shutdown:Bogus"] = "x",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal("CodeyBox:Shutdown:Bogus", report.Path);
    }

    [Fact]
    public void Inspect_ExemptsDictionarySectionKeys()
    {
        // AgentNetworkTolerance is Dictionary<string, AgentNetworkToleranceOptions?>.
        // The dictionary keys (codex, claude, etc.) are operator-defined; only
        // unknown keys NESTED INSIDE the value type should still flag.
        var config = BuildConfig(new()
        {
            ["CodeyBox:AgentNetworkTolerance:codex:RequestMaxRetries"] = "8",
            ["CodeyBox:AgentNetworkTolerance:any-arbitrary-key-the-operator-wants:RequestMaxRetries"] = "8",
            ["CodeyBox:QuotaFailurePatterns:cursor:0:Pattern"] = "rate-limited",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        Assert.Empty(reports);
    }

    [Fact]
    public void Inspect_FlagsUnboundPropertyInsideDictionaryValue()
    {
        var config = BuildConfig(new()
        {
            ["CodeyBox:AgentNetworkTolerance:codex:NotARealField"] = "true",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal("CodeyBox:AgentNetworkTolerance:codex:NotARealField", report.Path);
    }

    [Fact]
    public void Inspect_DescendsIntoListElementsViaNumericIndex()
    {
        var config = BuildConfig(new()
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:DisplayName"] = "x",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:MisspelledModelId"] = "claude-opus-4-7",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal("CodeyBox:AgentClasses:0:Members:0:MisspelledModelId", report.Path);
    }

    [Fact]
    public void Inspect_RecognizesProjectsOptionsAtTheCodeyBoxRoot()
    {
        // ProjectsOptions binds at "CodeyBox" too, so Defaults / Projects must
        // not be flagged as unbound.
        var config = BuildConfig(new()
        {
            ["CodeyBox:Projects:0:Id"] = "p1",
            ["CodeyBox:Projects:0:RepositoryUrl"] = "https://example/repo.git",
            ["CodeyBox:Defaults:Audit:MaxIterations"] = "3",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        Assert.Empty(reports);
    }

    [Fact]
    public void Inspect_ExemptsBuiltInExtensionSections()
    {
        var config = BuildConfig(new()
        {
            ["CodeyBox:BuildScriptAudit:Enabled"] = "true",
            ["CodeyBox:Plugins:Foo"] = "bar",
            ["CodeyBox:Mutation:Threshold"] = "60",
            ["CodeyBox:CheckAndActCompletion:OnlySomeKey"] = "true",
            ["CodeyBox:Presets:Languages:0:Id"] = "cs",
            ["CodeyBox:PromptPreprocessing:Whatever"] = "x",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        Assert.Empty(reports);
    }

    [Fact]
    public void Inspect_HonoursAdditionalExemptPathPrefixes()
    {
        var config = BuildConfig(new()
        {
            ["CodeyBox:MyOperatorExtension:Whatever"] = "x",
        });

        // Without exemption: flagged.
        var unexempted = UnboundConfigKeyHostedValidator.Inspect(config);
        Assert.Single(unexempted);

        // With exemption: skipped.
        var exempted = UnboundConfigKeyHostedValidator.Inspect(
            config,
            additionalExemptPaths: new[] { "CodeyBox:MyOperatorExtension" });
        Assert.Empty(exempted);
    }

    [Fact]
    public void Inspect_RespectsConfigurationKeyNameAttribute()
    {
        var config = BuildConfig(new()
        {
            ["Root:alias-key"] = "x",
            ["Root:OriginalPropertyName"] = "y",
        });

        var reports = UnboundConfigKeyInspector.Inspect(
            config.GetSection("Root"),
            new[] { typeof(AliasedOptions) });

        var report = Assert.Single(reports);
        Assert.Equal("Root:OriginalPropertyName", report.Path);
    }

    [Fact]
    public void Inspect_LeafSectionsWithChildKeysAreFlagged()
    {
        // MaxTemplateChecks is int; any subkey is junk.
        var config = BuildConfig(new()
        {
            ["CodeyBox:MaxTemplateChecks"] = "100",
            ["CodeyBox:MaxTemplateChecks:UnexpectedSubkey"] = "true",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal("CodeyBox:MaxTemplateChecks:UnexpectedSubkey", report.Path);
    }

    [Fact]
    public async Task HostedValidator_StrictMode_ThrowsAtStartAsync()
    {
        var validator = BuildHostedValidator(
            mode: "strict",
            kvs: new()
            {
                ["CodeyBox:AgentStreams:RootDirectory"] = "/var/log/codeybox",
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("AgentStreams:RootDirectory", ex.Message);
    }

    [Fact]
    public async Task HostedValidator_WarnMode_LogsWarningAndStarts()
    {
        var sink = new ListLogger<UnboundConfigKeyHostedValidator>();
        var validator = BuildHostedValidator(
            mode: "warn",
            kvs: new()
            {
                ["CodeyBox:AgentStreams:RootDirectory"] = "/var/log/codeybox",
            },
            logger: sink);

        await validator.StartAsync(CancellationToken.None);

        Assert.Contains(sink.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("RootDirectory"));
    }

    [Fact]
    public async Task HostedValidator_DisabledFlag_SkipsValidation()
    {
        var validator = BuildHostedValidator(
            mode: "strict",
            kvs: new()
            {
                ["CodeyBox:AgentStreams:RootDirectory"] = "/var/log/codeybox",
            },
            enabled: false);

        // Must not throw even though strict + unbound key present.
        await validator.StartAsync(CancellationToken.None);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> kvs)
        => new ConfigurationBuilder().AddInMemoryCollection(kvs).Build();

    private static UnboundConfigKeyHostedValidator BuildHostedValidator(
        string mode,
        Dictionary<string, string?> kvs,
        bool enabled = true,
        ILogger<UnboundConfigKeyHostedValidator>? logger = null)
    {
        var configKvs = new Dictionary<string, string?>(kvs)
        {
            ["CodeyBox:ConfigValidation:UnboundKeys:Enabled"] = enabled.ToString(),
            ["CodeyBox:ConfigValidation:UnboundKeys:Mode"] = mode,
        };
        var config = BuildConfig(configKvs);
        var services = new ServiceCollection();
        services.AddOptions<CodeyBoxOptions>().Bind(config.GetSection("CodeyBox"));
        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>();
        return new UnboundConfigKeyHostedValidator(
            config,
            opts,
            logger ?? NullLogger<UnboundConfigKeyHostedValidator>.Instance);
    }

    private sealed class AliasedOptions
    {
        [Microsoft.Extensions.Configuration.ConfigurationKeyName("alias-key")]
        public string? PropertyWithAlias { get; set; }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add((logLevel, formatter(state, exception)));
        }
    }
}
