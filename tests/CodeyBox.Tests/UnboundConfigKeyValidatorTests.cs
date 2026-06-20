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
    public void Inspect_ExemptsDirectConfigOAuthLeafKeys()
    {
        // CodeyBox:ClaudeOAuthFile / CodexOAuthFile / GeminiOAuthFile /
        // GeminiSettingsFile / CursorAuthFile / OpencodeAuthFile /
        // OpencodeAuthDestPath / GeminiOauthClientId / GeminiOauthClientSecret
        // are read directly via builder.Configuration["CodeyBox:…"] in
        // Program.cs; they have no matching property on CodeyBoxOptions or
        // ProjectsOptions. Strict-mode startup must NOT fail for operators
        // who set these documented credential-file pointers in appsettings.
        var config = BuildConfig(new()
        {
            ["CodeyBox:ClaudeOAuthFile"] = "/home/op/.claude/.credentials.json",
            ["CodeyBox:CodexOAuthFile"] = "/home/op/.codex/auth.json",
            ["CodeyBox:GeminiOAuthFile"] = "/home/op/.gemini/oauth_creds.json",
            ["CodeyBox:GeminiSettingsFile"] = "/home/op/.gemini/settings.json",
            ["CodeyBox:CursorAuthFile"] = "/home/op/.config/cursor/auth.json",
            ["CodeyBox:OpencodeAuthFile"] = "/home/op/.local/share/opencode/auth.json",
            ["CodeyBox:OpencodeAuthDestPath"] = "/home/ubuntu/.local/share/opencode/auth.json",
            ["CodeyBox:GeminiOauthClientId"] = "client-id",
            ["CodeyBox:GeminiOauthClientSecret"] = "client-secret",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        Assert.Empty(reports);
    }

    [Fact]
    public void Inspect_ExemptsLanguagesOverridesMapShape()
    {
        // ProjectsOptionsBinder.ApplyLanguageMap reads Audit:Languages:Overrides
        // as a dict<lang-id, ProjectLanguagePresetOverrideConfig> even though
        // ProjectAuditConfig.Languages is typed List<string>?. Documented in
        // docs/languages.md:65-90. Default-strict startup must NOT flag the
        // documented map shape.
        var config = BuildConfig(new()
        {
            ["CodeyBox:Defaults:Audit:Languages:0"] = "csharp",
            ["CodeyBox:Defaults:Audit:Languages:Overrides:csharp:Replace"] = "true",
            ["CodeyBox:Defaults:Audit:Languages:Overrides:csharp:Auditors:0:Name"] = "csharp:test-pass",
            ["CodeyBox:Defaults:Audit:Languages:Overrides:csharp:Auditors:0:Argv:0"] = "dotnet",
            ["CodeyBox:Defaults:Audit:Languages:Overrides:csharp:Auditors:0:Argv:1"] = "test",
            ["CodeyBox:Projects:0:Id"] = "alpha",
            ["CodeyBox:Projects:0:RepositoryUrl"] = "https://example/repo.git",
            ["CodeyBox:Projects:0:Audit:Languages:Overrides:python:Replace"] = "false",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        Assert.Empty(reports);
    }

    [Fact]
    public void Inspect_FlagsTypoInsideLanguagesOverridesOverrideValue()
    {
        // Map shape is exempt at the key level (lang ids are operator-defined),
        // but the override VALUE must still be validated — a typo like
        // "Replce" or an unknown sub-field has to surface.
        var config = BuildConfig(new()
        {
            ["CodeyBox:Defaults:Audit:Languages:Overrides:csharp:NotARealField"] = "x",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal(
            "CodeyBox:Defaults:Audit:Languages:Overrides:csharp:NotARealField",
            report.Path);
    }

    [Fact]
    public void Inspect_ExemptsAuditTypesMapShape()
    {
        // ProjectsOptionsBinder.ApplyAuditTypeMap accepts Audit:AuditTypes as
        // a string-keyed dict of audit-type id to ProjectAuditTypeOverrideConfig
        // even though the typed property is List<string>?. Documented in
        // docs/audit-types.md:43-65 and docs/projects.md:118-133.
        var config = BuildConfig(new()
        {
            ["CodeyBox:Defaults:Audit:AuditTypes:security:ReviewFocus"] = "- Project-specific auth checks",
            ["CodeyBox:Defaults:Audit:AuditTypes:security:Auditors:0:Name"] = "security:custom-scanner",
            ["CodeyBox:Defaults:Audit:AuditTypes:security:Auditors:0:Argv:0"] = "custom-scan",
            ["CodeyBox:Defaults:Audit:AuditTypes:completeness:ReviewFocus"] = "- Acceptance criteria",
            ["CodeyBox:Projects:0:Id"] = "alpha",
            ["CodeyBox:Projects:0:RepositoryUrl"] = "https://example/repo.git",
            ["CodeyBox:Projects:0:Audit:AuditTypes:security:Replace"] = "true",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        Assert.Empty(reports);
    }

    [Fact]
    public void Inspect_ExemptsAuditTypesListShape()
    {
        // The legacy list form (all-numeric keys) must keep working alongside
        // the map shape exemption.
        var config = BuildConfig(new()
        {
            ["CodeyBox:Defaults:Audit:AuditTypes:0"] = "security",
            ["CodeyBox:Defaults:Audit:AuditTypes:1"] = "completeness",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        Assert.Empty(reports);
    }

    [Fact]
    public void Inspect_FlagsTypoInsideAuditTypesMapOverrideValue()
    {
        // Map shape is exempt at the key level, but the override VALUE must
        // still be validated. A typo inside the per-audit-type override must
        // still surface.
        var config = BuildConfig(new()
        {
            ["CodeyBox:Defaults:Audit:AuditTypes:security:NotARealField"] = "x",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal(
            "CodeyBox:Defaults:Audit:AuditTypes:security:NotARealField",
            report.Path);
    }

    [Fact]
    public void Inspect_ExemptsMapShapesInsideProfilesSubsection()
    {
        // Profiles<id> is Dictionary<string, ProjectAuditConfig>, so the
        // custom-binder map handling has to cascade — i.e. the same exemption
        // must apply inside named profiles.
        var config = BuildConfig(new()
        {
            ["CodeyBox:Defaults:Audit:Profiles:default:Languages:Overrides:csharp:Replace"] = "true",
            ["CodeyBox:Defaults:Audit:Profiles:default:AuditTypes:security:ReviewFocus"] = "x",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        Assert.Empty(reports);
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
