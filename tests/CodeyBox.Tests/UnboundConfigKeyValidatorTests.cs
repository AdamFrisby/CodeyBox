using CodeyBox.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    public void Inspect_AllowsDocumentedNamedApiClientConfiguration()
    {
        var config = BuildConfig(new()
        {
            ["CodeyBox:ApiClients:0:Name"] = "jobtrack",
            ["CodeyBox:ApiClients:0:TokenEnvVar"] = "CODEYBOX_JOBTRACK_API_KEY",
            ["CodeyBox:ApiClients:0:CanDelegateInitiator"] = "true",
            ["CodeyBox:ApiClients:0:Principal:Issuer"] = "jobtrack",
            ["CodeyBox:ApiClients:0:Principal:Subject"] = "service",
            ["CodeyBox:ApiClients:0:Principal:DisplayName"] = "JobTrack",
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
    public void Inspect_AcceptsValidKeysInSeparatelyBoundSections()
    {
        // Separately-bound POCO sections (BuildScriptAudit, PromptPreprocessing,
        // Presets, Mutation, CheckAndActCompletion) are walked against their
        // typed root. Real properties bind; this fixture pins the happy path.
        var config = BuildConfig(new()
        {
            ["CodeyBox:BuildScriptAudit:TimeoutSeconds"] = "1200",
            ["CodeyBox:PromptPreprocessing:ProjectRulesPath"] = "AGENTS.md",
            ["CodeyBox:Presets:ProjectRoot"] = "/etc/codeybox/presets",
            ["CodeyBox:Mutation:Enabled"] = "true",
            ["CodeyBox:Mutation:ChangedCodeThresholdPercent"] = "80",
            ["CodeyBox:CheckAndActCompletion:Enabled"] = "true",
            ["CodeyBox:CheckAndActCompletion:GeminiModel"] = "gemini-2.5-pro",
            // Plugins: typed properties bind…
            ["CodeyBox:Plugins:AssemblyPaths:0"] = "/etc/codeybox/plugins/My.dll",
            ["CodeyBox:Plugins:Allowlist:0"] = "codeybox.statistics",
            // …and operator-defined plugin-id sub-trees stay opaque.
            ["CodeyBox:Plugins:codeybox.statistics:SomePluginConfig"] = "value",
            ["CodeyBox:Plugins:codeybox.statistics:Deep:Nested:Thing"] = "value",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        Assert.Empty(reports);
    }

    [Fact]
    public void Inspect_FlagsTypoInsideSeparatelyBoundSection_BuildScriptAudit()
    {
        // The Error-fix case: a typo of TimeoutSeconds inside the
        // separately-bound BuildScriptAuditorOptions must surface instead of
        // being lost to a blanket subtree exemption.
        var config = BuildConfig(new()
        {
            ["CodeyBox:BuildScriptAudit:TimoutSeconds"] = "1200",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal("CodeyBox:BuildScriptAudit:TimoutSeconds", report.Path);
        Assert.Equal("TimeoutSeconds", report.NearestProperty);
    }

    [Fact]
    public void Inspect_FlagsTypoInsideSeparatelyBoundSection_PromptPreprocessing()
    {
        var config = BuildConfig(new()
        {
            ["CodeyBox:PromptPreprocessing:ProjectRulesPth"] = "AGENTS.md",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal("CodeyBox:PromptPreprocessing:ProjectRulesPth", report.Path);
        Assert.Equal("ProjectRulesPath", report.NearestProperty);
    }

    [Fact]
    public void Inspect_FlagsTypoInsideSeparatelyBoundSection_Mutation()
    {
        var config = BuildConfig(new()
        {
            ["CodeyBox:Mutation:Enabld"] = "true",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal("CodeyBox:Mutation:Enabld", report.Path);
        Assert.Equal("Enabled", report.NearestProperty);
    }

    [Fact]
    public void Inspect_FlagsTypoDeepInsideSeparatelyBoundSection_Presets()
    {
        // Nested typo inside a dictionary value type also surfaces.
        var config = BuildConfig(new()
        {
            ["CodeyBox:Presets:LanguageOverrides:csharp:Replce"] = "true",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal("CodeyBox:Presets:LanguageOverrides:csharp:Replce", report.Path);
    }

    [Fact]
    public void Inspect_ExemptsCredentialFileWatchersLeafKey()
    {
        // CodeyBox:CredentialFileWatchers is read directly via
        // CredentialFileWatcherSettings.IsEnabled and has no matching property
        // on CodeyBoxOptions / ProjectsOptions. It MUST not trip strict-mode
        // validation — the docs (configuration.md:165) tell operators to set
        // it.
        var config = BuildConfig(new()
        {
            ["CodeyBox:CredentialFileWatchers"] = "false",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        Assert.Empty(reports);
    }

    [Fact]
    public void Inspect_HonoursAdditionalExemptPaths()
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
        // docs/quality/presets.md:65-90. Default-strict startup must NOT flag the
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
        // docs/quality/presets.md:43-65 and docs/concepts/projects.md:118-133.
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
        var sink = new ListLogger<UnboundConfigKeyHostedValidator>();
        var validator = BuildHostedValidator(
            mode: "strict",
            kvs: new()
            {
                ["CodeyBox:AgentStreams:RootDirectory"] = "/var/log/codeybox",
            },
            enabled: false,
            logger: sink);

        // Disabled means the inspector never runs at all: no throw AND no
        // warning logged. A regression that ran the inspector but swallowed
        // the throw would still emit the warning and fail this assertion.
        await validator.StartAsync(CancellationToken.None);
        Assert.Empty(sink.Lines);
    }

    [Fact]
    public async Task HostedValidator_UnknownMode_LogsWarningAndStillThrows()
    {
        // The Mode parser only special-cases "warn" and "strict". A
        // typo'd value like "log-only" falls through to strict so the
        // operator's safest invariant (fail-fast) is preserved, but the
        // unrecognised value MUST also surface as a warning so the
        // operator notices the typo instead of inheriting strict by
        // accident.
        var sink = new ListLogger<UnboundConfigKeyHostedValidator>();
        var validator = BuildHostedValidator(
            mode: "log-only",
            kvs: new()
            {
                ["CodeyBox:AgentStreams:RootDirectory"] = "/var/log/codeybox",
            },
            logger: sink);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains(
            sink.Lines,
            l => l.Level == LogLevel.Warning && l.Message.Contains("log-only"));
    }

    [Fact]
    public void Inspect_FlagsReadOnlyComputedProperty_OnSeparatelyBoundOptions()
    {
        // MutationTestingAuditorOptions.Budget is a get-only TimeSpan
        // computed from BudgetMinutes. ConfigurationBinder cannot write to
        // it, so an operator who writes CodeyBox:Mutation:Budget alongside
        // BudgetMinutes has their value silently dropped — exactly the
        // shape this feature exists to catch. The walker must flag it
        // instead of treating it as a known bindable property.
        var config = BuildConfig(new()
        {
            ["CodeyBox:Mutation:BudgetMinutes"] = "20",
            ["CodeyBox:Mutation:Budget"] = "00:20:00",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal("CodeyBox:Mutation:Budget", report.Path);
    }

    [Fact]
    public void Inspect_ReportsAllUnboundKeys_NotJustTheFirst()
    {
        // Multiple unbound keys at different depths must each surface
        // independently. A regression that early-returned in WalkPoco
        // after the first hit would only report one of these.
        var config = BuildConfig(new()
        {
            ["CodeyBox:AgentStreams:RootDirectory"] = "/var/log/codeybox",
            ["CodeyBox:Shutdown:Bogus"] = "x",
            ["CodeyBox:NotARealField"] = "y",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        Assert.Equal(3, reports.Count);
        Assert.Contains(reports, r => r.Path == "CodeyBox:AgentStreams:RootDirectory");
        Assert.Contains(reports, r => r.Path == "CodeyBox:Shutdown:Bogus");
        Assert.Contains(reports, r => r.Path == "CodeyBox:NotARealField");
    }

    [Fact]
    public void Inspect_NearestPropertyHint_DoesNotFireForShortUnrelatedKeys()
    {
        // Path is 4 chars; with a fixed cutoff of 3, an unrelated short
        // key like "Foo" would mis-hint as "Path" (distance 3 ≈ "one
        // shared char"). The length-scaled cutoff prevents that.
        var config = BuildConfig(new()
        {
            ["CodeyBox:AgentStreams:Foo"] = "x",
        });

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal("CodeyBox:AgentStreams:Foo", report.Path);
        Assert.Null(report.NearestProperty);
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
        return new UnboundConfigKeyHostedValidator(
            config,
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
