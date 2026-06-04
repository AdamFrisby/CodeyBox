using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AgentClassesOverrideResolver"/>: enforces REPLACE
/// semantics for <c>CodeyBox:AgentClasses</c> when a higher-precedence
/// configuration layer supplies the section. Regression coverage for the
/// 2026-06-04 incident in which a 3-member operator override silently
/// re-surfaced the base array's 4th member (gemini) because the .NET
/// configuration binder merges arrays by index.
/// </summary>
public sealed class AgentClassesOverrideResolverTests
{
    [Fact]
    public void OverrideWithFewerMembers_DoesNotResurrectBaseMember()
    {
        // Base: 4-member class (claude, codex, cursor, gemini).
        var baseLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier-coding",
            ["CodeyBox:AgentClasses:0:DisplayName"] = "Frontier coding",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "codex",
            ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:2:Agent"] = "cursor",
            ["CodeyBox:AgentClasses:0:Members:2:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:2:QualityScore"] = "98",
            ["CodeyBox:AgentClasses:0:Members:3:Agent"] = "gemini",
            ["CodeyBox:AgentClasses:0:Members:3:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:3:QualityScore"] = "95",
            ["CodeyBox:AgentClasses:0:Members:3:ReasoningMode"] = "high",
        };

        // Operator override: 3 members (cursor removed, gemini intentionally
        // not listed). Under positional merge this would re-expose
        // gemini from baseLayer[3]; the resolver must prevent that.
        var overrideLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "codex-xhigh",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "codex",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:2:Agent"] = "opencode",
            ["CodeyBox:AgentClasses:0:Members:2:Billing"] = "PayPerApi",
            ["CodeyBox:AgentClasses:0:Members:2:QualityScore"] = "70",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .AddInMemoryCollection(overrideLayer)
            .Build();

        // Confirm the framework's default bind exhibits the broken behaviour
        // we're guarding against — this is the regression that motivated the
        // fix. If the framework ever changes this, the test still passes
        // because the resolver's REPLACE semantics is what we actually check.
        var defaultBound = new CodeyBoxOptions();
        config.GetSection("CodeyBox").Bind(defaultBound);
        Assert.Contains(defaultBound.AgentClasses[0].Members, m => m.Agent == "gemini");

        // Apply the resolver: the operator's 3-member view fully replaces
        // the base 4-member array. gemini is gone, no positional bleed.
        AgentClassesOverrideResolver.ApplyTo(defaultBound, config);

        var resolved = Assert.Single(defaultBound.AgentClasses);
        Assert.Equal("codex-xhigh", resolved.Id);
        Assert.Equal(new[] { "codex", "claude", "opencode" },
            resolved.Members.Select(m => m.Agent));
        Assert.DoesNotContain(resolved.Members, m => m.Agent == "gemini");
        Assert.DoesNotContain(resolved.Members, m => m.Agent == "cursor");
    }

    [Fact]
    public void NoOverride_LeavesBaseClassesIntact()
    {
        var baseLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "codex",
            ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "100",
        };
        // Override layer touches an unrelated section: must NOT trigger
        // AgentClasses replacement.
        var overrideLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:GitRootDirectory"] = "/tmp/other",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .AddInMemoryCollection(overrideLayer)
            .Build();

        var options = new CodeyBoxOptions();
        config.GetSection("CodeyBox").Bind(options);
        AgentClassesOverrideResolver.ApplyTo(options, config);

        var resolved = Assert.Single(options.AgentClasses);
        Assert.Equal("frontier", resolved.Id);
        Assert.Equal(new[] { "claude", "codex" }, resolved.Members.Select(m => m.Agent));
    }

    [Fact]
    public void OverrideReplacesEntireClassArray_NotJustMembers()
    {
        // Base provides two classes; override provides only one. Under
        // positional merge, base[1] would bleed through. Under REPLACE
        // semantics, the override defines the complete class set.
        var baseLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:1:Id"] = "bulk",
            ["CodeyBox:AgentClasses:1:Members:0:Agent"] = "haiku",
            ["CodeyBox:AgentClasses:1:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:1:Members:0:QualityScore"] = "50",
        };
        var overrideLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "codex-only",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "codex",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .AddInMemoryCollection(overrideLayer)
            .Build();

        var options = new CodeyBoxOptions();
        config.GetSection("CodeyBox").Bind(options);
        AgentClassesOverrideResolver.ApplyTo(options, config);

        var resolved = Assert.Single(options.AgentClasses);
        Assert.Equal("codex-only", resolved.Id);
        Assert.DoesNotContain(options.AgentClasses, c => c.Id == "bulk");
    }

    [Fact]
    public void OverridePreservesNestedCapabilitiesArrayWithoutPositionalMerge()
    {
        var baseLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:0:Capabilities:0"] = "sensitive",
            ["CodeyBox:AgentClasses:0:Members:0:Capabilities:1"] = "architectural",
            ["CodeyBox:AgentClasses:0:Members:0:Capabilities:2"] = "audit",
        };
        // Override drops the 'audit' capability by listing only two entries.
        // Without REPLACE the 'audit' tag would bleed through from base[2].
        var overrideLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:0:Capabilities:0"] = "sensitive",
            ["CodeyBox:AgentClasses:0:Members:0:Capabilities:1"] = "architectural",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .AddInMemoryCollection(overrideLayer)
            .Build();

        var options = new CodeyBoxOptions();
        config.GetSection("CodeyBox").Bind(options);
        AgentClassesOverrideResolver.ApplyTo(options, config);

        var member = options.AgentClasses[0].Members[0];
        Assert.Equal(new[] { "sensitive", "architectural" }, member.Capabilities);
        Assert.DoesNotContain("audit", member.Capabilities);
    }

    [Fact]
    public void IOptionsPipeline_RunsPostConfigureSoOverrideRunsEndToEnd()
    {
        // Mirrors the Program.cs wiring: AddOptions().Bind(...).PostConfigure(...).
        // Confirms the resolver is invoked through the standard
        // IOptions<CodeyBoxOptions> resolution path — i.e. anyone reading
        // .Value gets the REPLACE-applied list, not the positionally-merged one.
        var baseLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "codex",
            ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:2:Agent"] = "gemini",
            ["CodeyBox:AgentClasses:0:Members:2:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:2:QualityScore"] = "95",
            ["CodeyBox:AgentClasses:0:Members:2:ReasoningMode"] = "high",
        };
        var overrideLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "codex",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "100",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .AddInMemoryCollection(overrideLayer)
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<CodeyBoxOptions>()
            .Bind(config.GetSection("CodeyBox"))
            .PostConfigure(opts => AgentClassesOverrideResolver.ApplyTo(opts, config));
        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
        var cls = Assert.Single(resolved.AgentClasses);
        Assert.Equal(new[] { "codex", "claude" }, cls.Members.Select(m => m.Agent));
        Assert.DoesNotContain(cls.Members, m => m.Agent == "gemini");
    }

    /// <summary>
    /// End-to-end guard against the audit finding that the production startup
    /// path (the <see cref="CodeyBoxOptionsStartupSnapshot"/> +
    /// <see cref="RetainingOptionsMonitorCache{TOptions}"/> seed) bypassed
    /// <see cref="AgentClassesOverrideResolver"/>. Stock
    /// <see cref="IOptionsMonitor{TOptions}"/> returns the pre-seeded cache
    /// value without invoking the options factory, so without applying the
    /// resolver to the snapshot, <c>CurrentValue</c> would expose the raw
    /// positional-merge AgentClasses until the first reload. This test boots
    /// Program.cs with a 3-member override on top of the committed
    /// 4-member <c>frontier-coding</c> base and asserts the resolved gemini
    /// member does NOT resurface in the monitor's startup value.
    /// </summary>
    [Fact]
    public void StartupOptionsMonitor_AppliesResolverBeforeFirstRead()
    {
        // Override layer drops gemini (and cursor). Under the unresolved
        // positional merge the snapshot would emit either base[3]=gemini
        // (if override had 3 members) or a positionally blended array.
        using var factory = new AgentClassesWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier-coding",
            ["CodeyBox:AgentClasses:0:DisplayName"] = "Frontier coding agents",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "codex",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:2:Agent"] = "opencode",
            ["CodeyBox:AgentClasses:0:Members:2:Billing"] = "PayPerApi",
            ["CodeyBox:AgentClasses:0:Members:2:QualityScore"] = "70",
        });

        // Stock IOptionsMonitor returns the cache-seeded snapshot value
        // without running the options factory. This is the exact path the
        // audit finding called out — read CurrentValue (no Configure/Get
        // detour) and assert the resolver-applied list is what comes back.
        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
        var classes = monitor.CurrentValue.AgentClasses;

        var cls = Assert.Single(classes);
        Assert.Equal("frontier-coding", cls.Id);
        Assert.Equal(new[] { "codex", "claude", "opencode" }, cls.Members.Select(m => m.Agent));
        Assert.DoesNotContain(cls.Members, m => m.Agent == "gemini");
        Assert.DoesNotContain(cls.Members, m => m.Agent == "cursor");

        // The snapshot singleton feeds the validator and the cache seed too;
        // confirm it also went through the resolver so a future code path
        // reading the snapshot directly can't observe the positional merge.
        var snapshot = factory.Services.GetRequiredService<CodeyBoxOptionsStartupSnapshot>().Value;
        Assert.DoesNotContain(snapshot.AgentClasses[0].Members, m => m.Agent == "gemini");
    }

    /// <summary>
    /// The acceptance criteria require the resolved member list to be logged
    /// at startup AND on hot-reload. <see cref="AgentClassesConfigBuilder.Build"/>
    /// is the single funnel for both paths, so assert it emits the named
    /// information log entry with the agent list, the billing tag, and the
    /// model id (when set). Without this assertion, dropping or weakening the
    /// log line (e.g. logging the pre-replacement list) would not fail tests.
    /// </summary>
    [Fact]
    public void Build_LogsResolvedMembersAtInformation()
    {
        var sink = new ListLogger();
        var classOptions = new List<AgentClassOptions>
        {
            new()
            {
                Id = "logged-class",
                DisplayName = "Logged",
                Members =
                {
                    new AgentMembershipOptions
                    {
                        Agent = "claude",
                        Billing = "Subscription",
                        ModelId = "claude-opus-4-7",
                        QualityScore = 100,
                    },
                    new AgentMembershipOptions
                    {
                        Agent = "codex",
                        Billing = "PayPerApi",
                        ModelId = "gpt-5",
                        QualityScore = 100,
                    },
                },
            },
        };

        AgentClassesConfigBuilder.Build(classOptions, sink);

        var entry = Assert.Single(
            sink.Entries,
            e => e.Level == LogLevel.Information &&
                e.Message.Contains("resolved members", StringComparison.Ordinal));
        Assert.Contains("logged-class", entry.Message, StringComparison.Ordinal);
        Assert.Contains("claude/claude-opus-4-7(Subscription)", entry.Message, StringComparison.Ordinal);
        Assert.Contains("codex/gpt-5(PayPerApi)", entry.Message, StringComparison.Ordinal);
    }

    private sealed class AgentClassesWiringFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> _override;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-agentclasses-wiring-{Guid.NewGuid():N}.db");

        public AgentClassesWiringFactory(Dictionary<string, string?> @override)
        {
            _override = @override;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                // Keep the committed appsettings.json layer (the 4-member
                // frontier-coding base) — that is the base shape the audit
                // finding was about. The override sits on top as the highest-
                // precedence layer, mirroring how CODEYBOX_EXTRA_CONFIG works.
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                });
                cfg.AddInMemoryCollection(_override);
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
