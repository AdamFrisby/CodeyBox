using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the reload-vs-restart work item: every configuration key
/// records whether an edit takes effect on reload or requires a restart, the
/// answer is exposed without reading source, reloads report applied vs
/// ignored keys, and unbound keys surface at reload time — not only at the
/// next restart.
/// </summary>
public sealed class ConfigReloadClassificationTests : IDisposable
{
    private readonly List<CoordinatorFixture> _fixtures = new();
    private readonly int _originalMaxRetries = Agents.AgentSuspendResilience.MaxRetries;
    private readonly int _originalMaxResumeAttempts = Agents.SessionResumeOptions.MaxResumeAttempts;

    public void Dispose()
    {
        foreach (var fixture in _fixtures)
            fixture.Dispose();
        Agents.AgentSuspendResilience.SetMaxRetries(_originalMaxRetries);
        Agents.SessionResumeOptions.SetMaxResumeAttempts(_originalMaxResumeAttempts);
    }

    // ── 1. Live-reloading keys report HotReload ─────────────────────────────

    [Theory]
    [InlineData("CodeyBox:PipelineTuning:AuditorAbsoluteTimeout")]
    [InlineData("CodeyBox:PipelineTuning:AuditorIdleTimeout")]
    [InlineData("CodeyBox:WorkerPool:MaxConcurrentWorkers")]
    [InlineData("CodeyBox:WorkerPool:MaxConcurrentSandboxes")]
    [InlineData("CodeyBox:WorkerPool:MinSpawnInterval")]
    [InlineData("CodeyBox:Concurrency")]
    public void LiveReloadedKey_IsReportedAsReloadEffective(string key)
    {
        Assert.True(ConfigReloadClassification.TryGetEffect(key, out var effect));
        Assert.Equal(ConfigReloadEffect.HotReload, effect);
    }

    [Fact]
    public void Classification_IsCaseInsensitive()
    {
        Assert.True(ConfigReloadClassification.TryGetEffect(
            "codeybox:pipelinetuning:auditorabsolutetimeout", out var effect));
        Assert.Equal(ConfigReloadEffect.HotReload, effect);
    }

    // ── 2. Startup-captured keys report RestartRequired ─────────────────────

    [Theory]
    [InlineData("CodeyBox:WorkerPool:NoProgressBackoffBase")]
    [InlineData("CodeyBox:WorkerPool:NoProgressBackoffMax")]
    [InlineData("CodeyBox:WorkerPool:MaxNoProgressRedispatches")]
    [InlineData("CodeyBox:WorkerPool:DispatchGateAcquisitionBackoff")]
    [InlineData("CodeyBox:WorkerPool:MaxConsecutiveDispatchGateTimeoutsBeforeEscalation")]
    [InlineData("CodeyBox:StateDatabasePath")]
    [InlineData("CodeyBox:GitRootDirectory")]
    [InlineData("CodeyBox:AgentStreams:Path")]
    [InlineData("CodeyBox:SandboxProvider")]
    public void StartupCapturedKey_IsReportedAsRestartRequired(string key)
    {
        Assert.True(ConfigReloadClassification.TryGetEffect(key, out var effect));
        Assert.Equal(ConfigReloadEffect.RestartRequired, effect);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CodeyBox:DoesNotExist")]
    [InlineData("CodeyBox:WorkerPool:NoSuchField")]
    public void UnknownKey_IsNotClassified(string? key)
    {
        Assert.False(ConfigReloadClassification.TryGetEffect(key, out _));
    }

    // ── 3. Restart-required change emits a record naming the key ────────────

    [Fact]
    public async Task ChangingRestartRequiredKey_EmitsRecordNamingKeyAndRestart()
    {
        var initial = new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions
            {
                MaxConcurrentWorkers = 2,
                MaxConcurrentSandboxes = 4,
            },
        };
        var fixture = await StartCoordinatorAsync(initial);

        fixture.Monitor.Fire(new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions
            {
                MaxConcurrentWorkers = 2,
                MaxConcurrentSandboxes = 4,
                NoProgressBackoffBase = TimeSpan.FromSeconds(5),
            },
        });

        var entry = Assert.Single(
            fixture.Log.Entries,
            e => e.Level == LogLevel.Warning
                && e.Message.Contains("CodeyBox:WorkerPool:NoProgressBackoffBase", StringComparison.Ordinal));
        Assert.Contains("restart", entry.Message, StringComparison.OrdinalIgnoreCase);

        // The ignored edit must not resize any live gate.
        Assert.Equal(2, fixture.Orchestrator.GetConcurrencyState().GlobalMaxConcurrent);
    }

    // ── 4. Reload-effective change emits an applied record ──────────────────

    [Fact]
    public async Task ChangingReloadEffectiveKey_EmitsAppliedRecord()
    {
        // Regression test for the AuditorAbsoluteTimeout incident: raising the
        // cap from 30 to 60 minutes produced no error and no effect because
        // the reload fingerprint did not observe the field.
        var initial = new CodeyBoxOptions
        {
            PipelineTuning = new PipelineTuningOptions
            {
                AuditorAbsoluteTimeout = TimeSpan.FromMinutes(30),
            },
        };
        var snapshot = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            AuditorAbsoluteTimeout = TimeSpan.FromMinutes(30),
        });
        var fixture = await StartCoordinatorAsync(initial, snapshot);
        Assert.Equal(TimeSpan.FromMinutes(30), snapshot.Current.AuditorAbsoluteTimeout);

        fixture.Monitor.Fire(new CodeyBoxOptions
        {
            PipelineTuning = new PipelineTuningOptions
            {
                AuditorAbsoluteTimeout = TimeSpan.FromMinutes(60),
            },
        });

        Assert.Equal(TimeSpan.FromMinutes(60), snapshot.Current.AuditorAbsoluteTimeout);
        Assert.Contains(
            fixture.Log.Entries,
            e => e.Level == LogLevel.Information
                && e.Message.Contains("Hot-reloaded PipelineTuning", StringComparison.Ordinal));
    }

    // ── 5. Unbound key reported at reload, not only at startup ──────────────

    [Fact]
    public async Task UnboundKey_ReportedAtReload()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:WorkerPool:MaxConcurrentWorkers"] = "2",
            })
            .Build();
        var initial = new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions { MaxConcurrentWorkers = 2 },
        };
        var fixture = await StartCoordinatorAsync(initial, configuration: config);

        // Operator adds a block that binds to nothing (the ReleaseConfig shape
        // from the incident: silently ignored before, startup crash after).
        config["CodeyBox:ReleaseConfig:ApprovalPolicy"] = "manual";
        fixture.Monitor.Fire(new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions { MaxConcurrentWorkers = 2 },
        });

        var entry = Assert.Single(
            fixture.Log.Entries,
            e => e.Level == LogLevel.Warning
                && e.Message.Contains("ReleaseConfig", StringComparison.Ordinal));
        Assert.Contains("no option", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BoundKeysAlone_ProduceNoUnboundReport()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:WorkerPool:MaxConcurrentWorkers"] = "2",
            })
            .Build();
        var initial = new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions { MaxConcurrentWorkers = 2 },
        };
        var fixture = await StartCoordinatorAsync(initial, configuration: config);

        fixture.Monitor.Fire(new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions { MaxConcurrentWorkers = 3 },
        });

        Assert.DoesNotContain(
            fixture.Log.Entries,
            e => e.Message.Contains("bind to no option", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, fixture.Orchestrator.GetConcurrencyState().GlobalMaxConcurrent);
    }

    // ── 6. Classification asserted against the consuming code ───────────────

    [Fact]
    public void PipelineTuningPolicy_PartitionsAllPipelineTuningOptions()
    {
        var props = typeof(PipelineTuningOptions)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(static p => p.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var union = PipelineTuningHotReloadPolicy.HotReloadableFields
            .Concat(PipelineTuningHotReloadPolicy.RestartRequiredFields)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(props, union);
        Assert.Empty(PipelineTuningHotReloadPolicy.HotReloadableFields
            .Intersect(PipelineTuningHotReloadPolicy.RestartRequiredFields));
        Assert.Contains("AuditorAbsoluteTimeout", PipelineTuningHotReloadPolicy.HotReloadableFields);
    }

    [Fact]
    public void WorkerPoolClassification_MatchesWorkerPoolPolicy()
    {
        // The exposed classification must agree with the policy sets the
        // reload path itself uses — a field that moves sets changes its
        // answer here automatically.
        foreach (var field in WorkerPoolHotReloadPolicy.HotReloadableFields)
        {
            Assert.True(ConfigReloadClassification.TryGetEffect($"CodeyBox:WorkerPool:{field}", out var effect));
            Assert.Equal(ConfigReloadEffect.HotReload, effect);
        }

        foreach (var field in WorkerPoolHotReloadPolicy.RestartRequiredFields)
        {
            Assert.True(ConfigReloadClassification.TryGetEffect($"CodeyBox:WorkerPool:{field}", out var effect));
            Assert.Equal(ConfigReloadEffect.RestartRequired, effect);
        }
    }

    [Fact]
    public void PipelineTuningClassification_MatchesPipelineTuningPolicy()
    {
        foreach (var field in PipelineTuningHotReloadPolicy.HotReloadableFields)
        {
            Assert.True(ConfigReloadClassification.TryGetEffect($"CodeyBox:PipelineTuning:{field}", out var effect));
            Assert.Equal(ConfigReloadEffect.HotReload, effect);
        }

        foreach (var field in PipelineTuningHotReloadPolicy.RestartRequiredFields)
        {
            Assert.True(ConfigReloadClassification.TryGetEffect($"CodeyBox:PipelineTuning:{field}", out var effect));
            Assert.Equal(ConfigReloadEffect.RestartRequired, effect);
        }
    }

    public static TheoryData<string> PipelineTuningHotFields
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in PipelineTuningHotReloadPolicy.HotReloadableFields)
                data.Add(name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(PipelineTuningHotFields))]
    public void PipelineTuningFingerprint_ObservesEveryHotReloadableField(string field)
    {
        // A hot-classified field missing from the reload fingerprint would
        // silently behave as restart-required however it is classified — the
        // AuditorAbsoluteTimeout failure mode. Mutating the field solo must
        // move the fingerprint.
        var baseline = AgentConfigHotReload.SerializePipelineTuning(new PipelineTuningOptions());
        var mutated = new PipelineTuningOptions();
        MutatePipelineTuningField(mutated, field);

        Assert.NotEqual(
            baseline,
            AgentConfigHotReload.SerializePipelineTuning(mutated));
    }

    public static TheoryData<string> WorkerPoolHotFields
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in WorkerPoolHotReloadPolicy.HotReloadableFields)
                data.Add(name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(WorkerPoolHotFields))]
    public void WorkerPoolFingerprint_ObservesEveryHotReloadableField(string field)
    {
        var baseline = AgentConfigHotReload.SerializeWorkerPool(new WorkerPoolOptions(), legacyConcurrency: null);
        var mutated = new WorkerPoolOptions();
        MutateWorkerPoolField(mutated, field);

        Assert.NotEqual(
            baseline,
            AgentConfigHotReload.SerializeWorkerPool(mutated, legacyConcurrency: null));
    }

    [Theory]
    [InlineData("DispatchGateAcquisitionBackoff")]
    [InlineData("MaxConsecutiveDispatchGateTimeoutsBeforeEscalation")]
    [InlineData("NoProgressBackoffBase")]
    [InlineData("NoProgressBackoffMax")]
    [InlineData("MaxNoProgressRedispatches")]
    public void WorkerPoolFingerprint_IgnoresRestartRequiredFields(string field)
    {
        var baseline = AgentConfigHotReload.SerializeWorkerPool(new WorkerPoolOptions(), legacyConcurrency: null);
        var mutated = new WorkerPoolOptions();
        MutateWorkerPoolField(mutated, field);

        Assert.Equal(
            baseline,
            AgentConfigHotReload.SerializeWorkerPool(mutated, legacyConcurrency: null));
    }

    public static TheoryData<string> RouterFingerprintFields
    {
        get
        {
            // Driven by reflection over the config POCOs so a newly added
            // settable property becomes a case automatically — and
            // MutateRouterField throws for names it does not know, failing the
            // new case until the fingerprint covers the field.
            var data = new TheoryData<string>();
            foreach (var p in typeof(AgentMembershipOptions).GetProperties())
                data.Add("Member." + p.Name);
            foreach (var p in typeof(AgentInstanceOptions).GetProperties())
                data.Add("Instance." + p.Name);
            foreach (var p in typeof(AgentClassOptions).GetProperties())
                data.Add("Class." + p.Name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RouterFingerprintFields))]
    public void RouterFingerprint_ObservesEveryConfigurableField(string field)
    {
        // A configured router field missing from SerializeRouterInputs silently
        // behaves as restart-required: the edit is accepted, no reload fires,
        // and the router keeps the old value (the Pool failure mode). Mutating
        // the field solo must move the fingerprint.
        var baseline = BaselineRouterInputs();
        var mutated = BaselineRouterInputs();
        MutateRouterField(mutated, field);

        Assert.NotEqual(
            AgentConfigHotReload.SerializeRouterInputs(baseline.Classes, baseline.Instances, baseline.Modifiers),
            AgentConfigHotReload.SerializeRouterInputs(mutated.Classes, mutated.Instances, mutated.Modifiers));
    }

    public static TheoryData<string> GuardedKeyPaths
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var key in ConfigReloadClassification.ValidatorGuardedKeyPaths)
                data.Add(key);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(GuardedKeyPaths))]
    public void GuardedKey_IsStillRejectedByStartupValidator(string keyPath)
    {
        // Proves the restart-required answer is still true against the
        // consuming code: a reload carrying this change is rejected, so the
        // running process keeps the startup value.
        var (startup, candidate) = BuildGuardedKeyPair(keyPath);

        var result = new ImmutableCodeyBoxOptionsValidator(startup).Validate(name: null, candidate);

        Assert.True(result.Failed, $"Expected {keyPath} to be restart-required, but the validator accepted it.");
    }

    [Fact]
    public void DiffRestartRequiredKeys_NamesChangedKeys()
    {
        var before = new CodeyBoxOptions();
        var after = new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions
            {
                NoProgressBackoffBase = TimeSpan.FromSeconds(5),
                MaxNoProgressRedispatches = 20,
            },
            StateDatabasePath = "/tmp/other-state.db",
        };

        var changed = ConfigReloadClassification.DiffRestartRequiredKeys(before, after);

        Assert.Contains("CodeyBox:WorkerPool:NoProgressBackoffBase", changed);
        Assert.Contains("CodeyBox:WorkerPool:MaxNoProgressRedispatches", changed);
        Assert.Contains("CodeyBox:StateDatabasePath", changed);
        Assert.DoesNotContain("CodeyBox:WorkerPool:MaxConcurrentWorkers", changed);
        Assert.DoesNotContain("CodeyBox:PipelineTuning:AuditorAbsoluteTimeout", changed);
    }

    [Fact]
    public void DiffRestartRequiredKeys_HotOnlyChange_IsSilent()
    {
        var options = new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions { MaxConcurrentWorkers = 2 },
        };

        Assert.Empty(ConfigReloadClassification.DiffRestartRequiredKeys(options, new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions { MaxConcurrentWorkers = 3 },
        }));
    }

    [Fact]
    public async Task ReloadEffectsEndpoint_AnswersSingleKey()
    {
        using var factory = new WorkItemApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/config/reload-effects?key=CodeyBox:PipelineTuning:AuditorAbsoluteTimeout");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(doc.RootElement.GetProperty("found").GetBoolean());
        Assert.Equal("HotReload", doc.RootElement.GetProperty("effect").GetString());
    }

    [Fact]
    public async Task ReloadEffectsEndpoint_ListsClassifiedKeys()
    {
        using var factory = new WorkItemApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/config/reload-effects");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var found = doc.RootElement.EnumerateArray()
            .Where(static e => e.GetProperty("key").GetString() == "CodeyBox:WorkerPool:NoProgressBackoffBase")
            .Select(static e => e.GetProperty("effect").GetString())
            .SingleOrDefault();
        Assert.Equal("RestartRequired", found);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static void MutatePipelineTuningField(PipelineTuningOptions opts, string field)
    {
        switch (field)
        {
            case nameof(PipelineTuningOptions.MaxPlanReviewIterations):
                opts.MaxPlanReviewIterations += 100;
                break;
            case nameof(PipelineTuningOptions.PlanTaskBindingCoverageRatio):
                opts.PlanTaskBindingCoverageRatio = 0.5;
                break;
            case nameof(PipelineTuningOptions.DefaultQuotaFailurePause):
                opts.DefaultQuotaFailurePause += TimeSpan.FromHours(1);
                break;
            case nameof(PipelineTuningOptions.DefaultRateLimitPause):
                opts.DefaultRateLimitPause += TimeSpan.FromHours(1);
                break;
            case nameof(PipelineTuningOptions.QuotaExhaustionFallbackTtl):
                opts.QuotaExhaustionFallbackTtl += TimeSpan.FromHours(1);
                break;
            case nameof(PipelineTuningOptions.MaxParsedQuotaResetWindow):
                opts.MaxParsedQuotaResetWindow += TimeSpan.FromHours(1);
                break;
            case nameof(PipelineTuningOptions.MergeSandboxStagingRestoreAttempts):
                opts.MergeSandboxStagingRestoreAttempts += 100;
                break;
            case nameof(PipelineTuningOptions.MaxQuestionsPerWorkItem):
                opts.MaxQuestionsPerWorkItem += 100;
                break;
            case nameof(PipelineTuningOptions.AgentSuspendMaxRetries):
                opts.AgentSuspendMaxRetries += 100;
                break;
            case nameof(PipelineTuningOptions.AgentSessionResumeMaxAttempts):
                opts.AgentSessionResumeMaxAttempts += 100;
                break;
            case nameof(PipelineTuningOptions.MaxRetainedAgentTurnSandboxes):
                opts.MaxRetainedAgentTurnSandboxes += 1;
                break;
            case nameof(PipelineTuningOptions.AutoMergeRaceRecoveryMaxAttempts):
                opts.AutoMergeRaceRecoveryMaxAttempts += 100;
                break;
            case nameof(PipelineTuningOptions.EnableSandboxReuse):
                opts.EnableSandboxReuse = !opts.EnableSandboxReuse;
                break;
            case nameof(PipelineTuningOptions.MaxSandboxReuses):
                opts.MaxSandboxReuses += 100;
                break;
            case nameof(PipelineTuningOptions.MaxSandboxLifetime):
                opts.MaxSandboxLifetime += TimeSpan.FromHours(1);
                break;
            case nameof(PipelineTuningOptions.SandboxPressureThreshold):
                opts.SandboxPressureThreshold = 0.5;
                break;
            case nameof(PipelineTuningOptions.SandboxPermitWaitWarningThreshold):
                opts.SandboxPermitWaitWarningThreshold += TimeSpan.FromHours(1);
                break;
            case nameof(PipelineTuningOptions.AuditShortCircuitEnabled):
                opts.AuditShortCircuitEnabled = !opts.AuditShortCircuitEnabled;
                break;
            case nameof(PipelineTuningOptions.EmptyReworkEscalationRetries):
                opts.EmptyReworkEscalationRetries += 100;
                break;
            case nameof(PipelineTuningOptions.AuditorIdleTimeout):
                opts.AuditorIdleTimeout += TimeSpan.FromHours(1);
                break;
            case nameof(PipelineTuningOptions.AuditorAbsoluteTimeout):
                opts.AuditorAbsoluteTimeout += TimeSpan.FromHours(1);
                break;
            case nameof(PipelineTuningOptions.BlockRedundantDotnetBuildTestInAuditSandbox):
                opts.BlockRedundantDotnetBuildTestInAuditSandbox = !opts.BlockRedundantDotnetBuildTestInAuditSandbox;
                break;
            case nameof(PipelineTuningOptions.CSharpTestPassAuditorIdleTimeout):
                opts.CSharpTestPassAuditorIdleTimeout = TimeSpan.FromMinutes(1);
                break;
            case nameof(PipelineTuningOptions.CSharpTestPassBlameHangTimeout):
                opts.CSharpTestPassBlameHangTimeout = TimeSpan.FromMinutes(1);
                break;
            case nameof(PipelineTuningOptions.EnableHandoffSeeding):
                opts.EnableHandoffSeeding = !opts.EnableHandoffSeeding;
                break;
            case nameof(PipelineTuningOptions.SelfReviewChecklistEnabled):
                opts.SelfReviewChecklistEnabled = !opts.SelfReviewChecklistEnabled;
                break;
            case nameof(PipelineTuningOptions.PlannedItemAuditRebalanceEnabled):
                opts.PlannedItemAuditRebalanceEnabled = !opts.PlannedItemAuditRebalanceEnabled;
                break;
            case nameof(PipelineTuningOptions.PlannedItemAdvisoryAuditors):
                opts.PlannedItemAdvisoryAuditors = new List<string> { "other-auditor" };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, "No mutator for this PipelineTuning field.");
        }
    }

    private static void MutateWorkerPoolField(WorkerPoolOptions opts, string field)
    {
        switch (field)
        {
            case nameof(WorkerPoolOptions.MaxConcurrentWorkers):
                opts.MaxConcurrentWorkers = 99;
                break;
            case nameof(WorkerPoolOptions.MaxConcurrentSandboxes):
                opts.MaxConcurrentSandboxes = 99;
                break;
            case nameof(WorkerPoolOptions.MinSpawnInterval):
                opts.MinSpawnInterval = TimeSpan.FromSeconds(99);
                break;
            case nameof(WorkerPoolOptions.DispatchGateAcquisitionBackoff):
                opts.DispatchGateAcquisitionBackoff = TimeSpan.FromSeconds(99);
                break;
            case nameof(WorkerPoolOptions.MaxConsecutiveDispatchGateTimeoutsBeforeEscalation):
                opts.MaxConsecutiveDispatchGateTimeoutsBeforeEscalation = 99;
                break;
            case nameof(WorkerPoolOptions.NoProgressBackoffBase):
                opts.NoProgressBackoffBase = TimeSpan.FromSeconds(99);
                break;
            case nameof(WorkerPoolOptions.NoProgressBackoffMax):
                opts.NoProgressBackoffMax = TimeSpan.FromSeconds(99);
                break;
            case nameof(WorkerPoolOptions.MaxNoProgressRedispatches):
                opts.MaxNoProgressRedispatches = 99;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, "No mutator for this WorkerPool field.");
        }
    }

    private static (List<AgentClassOptions> Classes, List<AgentInstanceOptions> Instances, AgentScoreModifiersOptions Modifiers)
        BaselineRouterInputs() =>
        (
            [
                new AgentClassOptions
                {
                    Id = "frontier",
                    DisplayName = "Frontier",
                    ClaudeSession = new AgentClassClaudeSessionOptions { Enabled = true },
                    Members = [BaselineRouterMember()],
                },
            ],
            [
                new AgentInstanceOptions
                {
                    Id = "acct-a",
                    Agent = "claude",
                    CredentialFilePath = "/cred-a",
                    TokenEnvironmentVariable = "TOKEN_A",
                    AuthJsonEnvironmentVariable = "AUTH_A",
                    SettingsFilePath = "/settings-a",
                    DestinationPath = "/dest-a",
                    SandboxEnvironmentVariable = "SANDBOX_A",
                    Provider = "provider-a",
                },
            ],
            new AgentScoreModifiersOptions()
        );

    private static AgentMembershipOptions BaselineRouterMember() => new()
    {
        Agent = "claude",
        InstanceId = "acct-a",
        Pool = "pool-a",
        Billing = "Subscription",
        ModelId = "model-a",
        CredentialFilePath = "/cred-a",
        TokenEnvironmentVariable = "TOKEN_A",
        AuthJsonEnvironmentVariable = "AUTH_A",
        SettingsFilePath = "/settings-a",
        DestinationPath = "/dest-a",
        SandboxEnvironmentVariable = "SANDBOX_A",
        Provider = "provider-a",
        QualityScore = 100,
        ReasoningMode = "high",
        Capabilities = ["tag-a"],
        ClaudeSession = new AgentClassClaudeSessionOptions { Enabled = true },
    };

    private static void MutateRouterField(
        (List<AgentClassOptions> Classes, List<AgentInstanceOptions> Instances, AgentScoreModifiersOptions Modifiers) inputs,
        string field)
    {
        // Every case flips exactly one configured value. A property added to
        // any of these POCOs without a case here (and without fingerprint
        // coverage) fails loudly via the default arm — that is the point.
        var member = inputs.Classes[0].Members[0];
        var instance = inputs.Instances[0];
        var cls = inputs.Classes[0];
        switch (field)
        {
            case "Member.Agent":
                member.Agent = "codex";
                break;
            case "Member.InstanceId":
                member.InstanceId = "acct-b";
                break;
            case "Member.Pool":
                member.Pool = "pool-b";
                break;
            case "Member.Billing":
                member.Billing = "PayPerApi";
                break;
            case "Member.ModelId":
                member.ModelId = "model-b";
                break;
            case "Member.CredentialFilePath":
                member.CredentialFilePath = "/cred-b";
                break;
            case "Member.TokenEnvironmentVariable":
                member.TokenEnvironmentVariable = "TOKEN_B";
                break;
            case "Member.AuthJsonEnvironmentVariable":
                member.AuthJsonEnvironmentVariable = "AUTH_B";
                break;
            case "Member.SettingsFilePath":
                member.SettingsFilePath = "/settings-b";
                break;
            case "Member.DestinationPath":
                member.DestinationPath = "/dest-b";
                break;
            case "Member.SandboxEnvironmentVariable":
                member.SandboxEnvironmentVariable = "SANDBOX_B";
                break;
            case "Member.Provider":
                member.Provider = "provider-b";
                break;
            case "Member.QualityScore":
                member.QualityScore = 99;
                break;
            case "Member.ReasoningMode":
                member.ReasoningMode = "low";
                break;
            case "Member.Capabilities":
                member.Capabilities = ["tag-b"];
                break;
            case "Member.ClaudeSession":
                member.ClaudeSession = new AgentClassClaudeSessionOptions { Enabled = false };
                break;
            case "Instance.Id":
                instance.Id = "acct-b";
                break;
            case "Instance.Agent":
                instance.Agent = "codex";
                break;
            case "Instance.CredentialFilePath":
                instance.CredentialFilePath = "/cred-b";
                break;
            case "Instance.TokenEnvironmentVariable":
                instance.TokenEnvironmentVariable = "TOKEN_B";
                break;
            case "Instance.AuthJsonEnvironmentVariable":
                instance.AuthJsonEnvironmentVariable = "AUTH_B";
                break;
            case "Instance.SettingsFilePath":
                instance.SettingsFilePath = "/settings-b";
                break;
            case "Instance.DestinationPath":
                instance.DestinationPath = "/dest-b";
                break;
            case "Instance.SandboxEnvironmentVariable":
                instance.SandboxEnvironmentVariable = "SANDBOX_B";
                break;
            case "Instance.Provider":
                instance.Provider = "provider-b";
                break;
            case "Class.Id":
                cls.Id = "other";
                break;
            case "Class.DisplayName":
                cls.DisplayName = "Other";
                break;
            case "Class.ClaudeSession":
                cls.ClaudeSession = new AgentClassClaudeSessionOptions { Enabled = false };
                break;
            case "Class.Members":
                cls.Members.Add(new AgentMembershipOptions
                {
                    Agent = "codex",
                    Billing = "Subscription",
                    QualityScore = 50,
                });
                break;
            default:
                throw new InvalidOperationException(
                    $"MutateRouterField has no mutation for '{field}'. " +
                    "Cover the new config property in SerializeRouterInputs and add its mutation here.");
        }
    }

    private static (CodeyBoxOptions Startup, CodeyBoxOptions Candidate) BuildGuardedKeyPair(string keyPath)
    {
        var startup = new CodeyBoxOptions();
        var candidate = new CodeyBoxOptions();
        switch (keyPath)
        {
            case "CodeyBox:SandboxProvider":
                startup.SandboxProvider = "process";
                candidate.SandboxProvider = "sprites";
                break;
            case "CodeyBox:StateDatabasePath":
                startup.StateDatabasePath = "/tmp/startup-state.db";
                candidate.StateDatabasePath = "/tmp/other-state.db";
                break;
            case "CodeyBox:GitRootDirectory":
                startup.GitRootDirectory = "/tmp/startup-repos";
                candidate.GitRootDirectory = "/tmp/other-repos";
                break;
            case "CodeyBox:GitCommandMaxOutputBytes":
                candidate.GitCommandMaxOutputBytes = startup.GitCommandMaxOutputBytes + 1;
                break;
            case "CodeyBox:AgentStreams:Path":
                startup.AgentStreams.Path = "logs/startup";
                candidate.AgentStreams.Path = "logs/other";
                break;
            case "CodeyBox:EnableSharedUpstreamMirror":
                candidate.EnableSharedUpstreamMirror = !startup.EnableSharedUpstreamMirror;
                break;
            case "CodeyBox:SharedUpstreamMirrorDirectory":
                startup.SharedUpstreamMirrorDirectory = "mirror-startup";
                candidate.SharedUpstreamMirrorDirectory = "mirror-other";
                break;
            case "CodeyBox:Incus:ProjectName":
                startup.SandboxProvider = "multipass";
                candidate.SandboxProvider = "multipass";
                startup.Incus.ProjectName = "startup-project";
                candidate.Incus.ProjectName = "other-project";
                break;
            case "CodeyBox:Incus:StagingDirectory":
                startup.SandboxProvider = "incus";
                candidate.SandboxProvider = "incus";
                startup.Incus.StagingDirectory = "/tmp/startup-staging";
                candidate.Incus.StagingDirectory = "/tmp/other-staging";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(keyPath), keyPath, "No pair builder for this guarded key.");
        }

        return (startup, candidate);
    }

    private async Task<CoordinatorFixture> StartCoordinatorAsync(
        CodeyBoxOptions initial,
        PipelineTuningSnapshot? tuning = null,
        IConfiguration? configuration = null)
    {
        var fixture = await CoordinatorFixture.StartAsync(initial, tuning, configuration);
        _fixtures.Add(fixture);
        return fixture;
    }

    private sealed class CoordinatorFixture : IDisposable
    {
        public OrchestratorService Orchestrator { get; }
        public ManualOptionsMonitor<CodeyBoxOptions> Monitor { get; }
        public CapturingLogger<AgentConfigHotReload> Log { get; }

        private readonly AgentConfigHotReload _coordinator;
        private readonly SqliteWorkItemStore _store;
        private readonly string _dbPath;

        private CoordinatorFixture(
            AgentConfigHotReload coordinator,
            ManualOptionsMonitor<CodeyBoxOptions> monitor,
            CapturingLogger<AgentConfigHotReload> log,
            OrchestratorService orchestrator,
            SqliteWorkItemStore store,
            string dbPath)
        {
            _coordinator = coordinator;
            Monitor = monitor;
            Log = log;
            Orchestrator = orchestrator;
            _store = store;
            _dbPath = dbPath;
        }

        public static async Task<CoordinatorFixture> StartAsync(
            CodeyBoxOptions initial,
            PipelineTuningSnapshot? tuning,
            IConfiguration? configuration)
        {
            var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
            var log = new CapturingLogger<AgentConfigHotReload>();
            var router = new AgentClassRouter(
                Array.Empty<AgentClass>(),
                Array.Empty<IAgentQuotaProbe>(),
                new QuotaRouterOptions { MinQuotaPct = 5.0 },
                NullLogger<AgentClassRouter>.Instance);
            var dbPath = Path.Combine(Path.GetTempPath(), $"cb-reloadfx-{Guid.NewGuid():N}.db");
            var store = new SqliteWorkItemStore(dbPath);
            var orch = new OrchestratorService(
                new InMemoryTaskQueue(),
                store,
                new NoopPipelineRunner(),
                new CancellationRegistry(CancellationToken.None),
                new OrchestratorOptions
                {
                    MaxConcurrentWorkers = initial.WorkerPool.MaxConcurrentWorkers ?? initial.Concurrency ?? 1,
                },
                NullLogger<OrchestratorService>.Instance,
                agentConcurrency: initial.AgentConcurrency);
            var burn = new AgentBurnEstimator(
                () => NullCostStore.Instance,
                initial.AgentBurnEstimator,
                NullLogger<AgentBurnEstimator>.Instance);
            var coordinator = new AgentConfigHotReload(
                monitor, orch, router, burn, log,
                pipelineTuning: tuning,
                configuration: configuration);
            await coordinator.StartAsync(CancellationToken.None);
            return new CoordinatorFixture(coordinator, monitor, log, orch, store, dbPath);
        }

        public void Dispose()
        {
            _coordinator.Dispose();
            _store.Dispose();
            try
            {
                File.Delete(_dbPath);
            }
            catch
            {
                // Best effort: temp-file cleanup must not fail the test run.
            }
        }
    }

    private sealed class ManualOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _value;
        private readonly List<Action<T, string?>> _listeners = new();
        private readonly Lock _gate = new();

        public ManualOptionsMonitor(T initial)
        {
            _value = initial;
        }

        public T CurrentValue => _value;
        public T Get(string? name) => _value;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            lock (_gate)
                _listeners.Add(listener);
            return new Subscription(() =>
            {
                lock (_gate)
                    _listeners.Remove(listener);
            });
        }

        public void Fire(T next)
        {
            _value = next;
            Action<T, string?>[] snapshot;
            lock (_gate)
                snapshot = _listeners.ToArray();
            foreach (var listener in snapshot)
                listener(next, null);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Action _onDispose;

            public Subscription(Action onDispose)
            {
                _onDispose = onDispose;
            }

            public void Dispose() => _onDispose();
        }
    }

    private sealed class NoopPipelineRunner : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NullCostStore : IWorkItemCostStore
    {
        public static readonly NullCostStore Instance = new();

        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(
            string workItemId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemCost>>(Array.Empty<WorkItemCost>());

        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(
            string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemCost>>(Array.Empty<WorkItemCost>());

        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string, double)>>(Array.Empty<(string, double)>());

        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<decimal> SumEstimatedUsdAsync(
            string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult(0m);
    }
}
