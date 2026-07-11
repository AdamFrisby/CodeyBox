using System.Reflection;
using System.Runtime.CompilerServices;
using CodeyBox.Agents.Copilot;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Incus;
using CodeyBox.Sandbox.Multipass;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the real Program.cs wiring of provider-owned baseline verification commands
/// to live <c>CodeyBoxOptions</c> + <c>ProjectsOptions</c> + the registered
/// <see cref="IInVmSmokeProbe"/> services. Unit tests cover the builder and
/// provider directly, but neither catches a regression where Program.cs
/// silently drops the assignment, resolves the wrong options snapshot, or
/// fails to register a probe — exactly the bake-skipping regression this
/// hardening exists to prevent.
/// </summary>
public sealed class BaselineVerificationProgramWiringTests
{
    [Fact]
    public void Program_PopulatesBaselineVerificationCommands_FromConfiguredAgents_WhenBaselineImagesEnabled()
    {
        using var factory = new BaselineVerificationFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "multipass",
            ["CodeyBox:MultipassUseBaselineImages"] = "true",
            ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:DisplayName"] = "Frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "antigravity",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "70",
            ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "PayPerApi",
            ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "100",
        });

        var opts = ResolveLiveMultipassOptions(factory);
        var labels = opts.BaselineVerificationCommands.Select(c => c.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("antigravity", labels);
        Assert.Contains("claude", labels);

        // Argv must come from the registered probe, not a hand-rolled default —
        // this is what proves the IInVmSmokeProbe services are actually reached.
        var antigravity = opts.BaselineVerificationCommands.Single(c =>
            string.Equals(c.Label, "antigravity", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("agy", antigravity.Argv[0]);
        Assert.Equal("--version", antigravity.Argv[^1]);
    }

    [Fact]
    public void Program_CopilotBaselineVerificationUsesRegisteredProbe_DespiteDefaultExemption()
    {
        using var factory = new BaselineVerificationFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "multipass",
            ["CodeyBox:MultipassUseBaselineImages"] = "true",
            ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
            ["CodeyBox:AgentClasses:0:Id"] = "copilot-class",
            ["CodeyBox:AgentClasses:0:DisplayName"] = "Copilot Class",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "copilot",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "75",
        });

        var opts = ResolveLiveMultipassOptions(factory);

        var copilot = opts.BaselineVerificationCommands.Single(c =>
            string.Equals(c.Label, "copilot", StringComparison.OrdinalIgnoreCase));
        Assert.Equal([CopilotAgentRunner.DefaultBinary, "--version"], copilot.Argv);
    }

    [Fact]
    public void Program_PopulatesExecutableProvisions_FromConfiguration()
    {
        using var factory = new BaselineVerificationFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "multipass",
            ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
            ["CodeyBox:MultipassExecutableProvisions:0:HostSourcePath"] = "/home/operator/agy seed/agy",
            ["CodeyBox:MultipassExecutableProvisions:0:VmDestPath"] = "/home/ubuntu/.local/bin/agy",
            ["CodeyBox:MultipassExecutableProvisions:0:VmSymlinks:0"] = "/usr/local/bin/agy",
            ["CodeyBox:MultipassExecutableProvisions:0:VmSymlinks:1"] = "/opt/codeybox/bin/agy",
            ["CodeyBox:MultipassExecutableProvisions:0:Label"] = "antigravity",
        });

        var opts = ResolveLiveMultipassOptions(factory);

        var provision = Assert.Single(opts.ExecutableProvisions);
        Assert.Equal("/home/operator/agy seed/agy", provision.HostSourcePath);
        Assert.Equal("/home/ubuntu/.local/bin/agy", provision.VmDestPath);
        Assert.Equal(["/usr/local/bin/agy", "/opt/codeybox/bin/agy"], provision.VmSymlinks);
        Assert.Equal("antigravity", provision.Label);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Program_RejectsOversizedMultipassProvisioningCollections(bool executableProvisions)
    {
        var config = new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "multipass",
            ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
            ["CodeyBox:MultipassUseBaselineImages"] = "false",
        };
        var count = executableProvisions
            ? BaselineProvisioningLimits.MaximumExecutableProvisions + 1
            : BaselineProvisioningLimits.MaximumPackageCacheSeeds + 1;
        var section = executableProvisions
            ? "MultipassExecutableProvisions"
            : "MultipassPackageCacheSeeds";
        for (var index = 0; index < count; index++)
        {
            config[$"CodeyBox:{section}:{index}:HostSourcePath"] = $"/srv/source-{index}";
            config[$"CodeyBox:{section}:{index}:VmDestPath"] = $"/var/lib/codeybox/dest-{index}";
        }
        using var factory = new BaselineVerificationFactory(config);

        var error = Assert.Throws<InvalidOperationException>(() => ResolveLiveMultipassOptions(factory));

        Assert.Contains($"CodeyBox:{section}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_PopulatesResourceMetricsOptions_FromMultipassSandboxConfig()
    {
        using var factory = new BaselineVerificationFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "multipass",
            ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
            ["CodeyBox:MultipassSandbox:CaptureResourceMetrics"] = "true",
            ["CodeyBox:MultipassSandbox:ResourceMetricsCaptureTimeoutSeconds"] = "9",
        });

        var opts = ResolveLiveMultipassOptions(factory);

        Assert.True(opts.CaptureResourceMetrics);
        Assert.Equal(TimeSpan.FromSeconds(9), opts.ResourceMetricsCaptureTimeout);
    }

    [Fact]
    public void Program_ResourceMetricsOptions_DefaultOffAndNonPositiveTimeoutFallsBack()
    {
        using var factory = new BaselineVerificationFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "multipass",
            ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
            ["CodeyBox:MultipassSandbox:ResourceMetricsCaptureTimeoutSeconds"] = "0",
        });

        var opts = ResolveLiveMultipassOptions(factory);

        Assert.False(opts.CaptureResourceMetrics);
        Assert.Equal(MultipassSandboxOptions.DefaultResourceMetricsCaptureTimeout, opts.ResourceMetricsCaptureTimeout);
    }

    [Fact]
    public void Program_PassesSandboxResourceUsageStoreIntoMultipassProvider()
    {
        using var factory = new BaselineVerificationFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "multipass",
            ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
        });

        var multipass = UnwrapToMultipass(factory.Services.GetRequiredService<ISandboxProvider>());
        var field = typeof(MultipassSandboxProvider).GetField(
            "_resourceUsageStore", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Same(factory.Services.GetRequiredService<ISandboxResourceUsageStore>(), field!.GetValue(multipass));
    }

    [Fact]
    public void Program_LeavesBaselineVerificationCommandsEmpty_WhenBaselineImagesDisabled()
    {
        // When UseBaselineImages=false there is no baseline to verify, so the
        // composition layer must not populate verification commands at all —
        // the bake-only verification path stays out of cloud-init-only launches.
        using var factory = new BaselineVerificationFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "multipass",
            ["CodeyBox:MultipassUseBaselineImages"] = "false",
            ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:DisplayName"] = "Frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "PayPerApi",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
        });

        var opts = ResolveLiveMultipassOptions(factory);

        Assert.Empty(opts.BaselineVerificationCommands);
    }

    [Fact]
    public void Program_PopulatesIncusBaselineVerification_FromRegisteredProbes()
    {
        using var factory = new BaselineVerificationFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "incus",
            ["CodeyBox:Incus:UseBaselineImages"] = "true",
            ["CodeyBox:AgentClasses:0:Id"] = "incus-frontier",
            ["CodeyBox:AgentClasses:0:DisplayName"] = "Incus Frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "antigravity",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "70",
            ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "PayPerApi",
            ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "100",
        });

        var options = ResolveLiveIncusOptions(factory);
        var commands = options.BaselineVerificationCommands
            .ToDictionary(command => command.Label, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(["agy", "--version"], commands["antigravity"].Argv);
        Assert.Equal(["claude", "--version"], commands["claude"].Argv);
    }

    [Fact]
    public void Program_DoesNotUseMultipassBaselineFlagForIncusVerification()
    {
        using var factory = new BaselineVerificationFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "incus",
            ["CodeyBox:Incus:UseBaselineImages"] = "false",
            ["CodeyBox:MultipassUseBaselineImages"] = "true",
            ["CodeyBox:Incus:ExecutableProvisions:0:HostSourcePath"] = "/srv/tools/agy",
            ["CodeyBox:Incus:ExecutableProvisions:0:VmDestPath"] = "/home/ubuntu/.local/bin/agy",
            ["CodeyBox:Incus:ExecutableProvisions:0:VmSymlinks:0"] = "/usr/local/bin/agy",
            ["CodeyBox:AgentClasses:0:Id"] = "incus-frontier",
            ["CodeyBox:AgentClasses:0:DisplayName"] = "Incus Frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "PayPerApi",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
        });

        var options = ResolveLiveIncusOptions(factory);

        Assert.Empty(options.BaselineVerificationCommands);
        Assert.Single(options.ExecutableProvisions);
    }

    [Fact]
    public void Program_IncusAccessorHotReloadsProvisioningAndVerificationIntoFreshSnapshot()
    {
        using var factory = new BaselineVerificationFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "incus",
            ["CodeyBox:Incus:UseBaselineImages"] = "true",
            ["CodeyBox:Incus:PackageCacheSeeds:0:HostSourcePath"] = "/srv/cache/v1",
            ["CodeyBox:Incus:PackageCacheSeeds:0:VmDestPath"] = "/var/cache/codeybox/v1",
            ["CodeyBox:Incus:ExecutableProvisions:0:HostSourcePath"] = "/srv/tools/v1",
            ["CodeyBox:Incus:ExecutableProvisions:0:VmDestPath"] = "/usr/local/lib/codeybox/tool-v1",
            ["CodeyBox:Incus:ExecutableProvisions:0:VmSymlinks:0"] = "/usr/local/bin/tool-v1",
            ["CodeyBox:Incus:ExecutableProvisions:0:Label"] = "tool-v1",
            ["CodeyBox:Defaults:Agent"] = "claude",
        });
        var accessor = ResolveLiveIncusOptionsAccessor(factory);

        var before = accessor();
        var configuration = Assert.IsAssignableFrom<IConfigurationRoot>(
            factory.Services.GetRequiredService<IConfiguration>());
        configuration["CodeyBox:Incus:PackageCacheSeeds:0:HostSourcePath"] = "/srv/cache/v2";
        configuration["CodeyBox:Incus:PackageCacheSeeds:0:VmDestPath"] = "/var/cache/codeybox/v2";
        configuration["CodeyBox:Incus:ExecutableProvisions:0:HostSourcePath"] = "/srv/tools/v2";
        configuration["CodeyBox:Incus:ExecutableProvisions:0:VmDestPath"] = "/usr/local/lib/codeybox/tool-v2";
        configuration["CodeyBox:Incus:ExecutableProvisions:0:VmSymlinks:0"] = "/usr/local/bin/tool-v2";
        configuration["CodeyBox:Incus:ExecutableProvisions:0:Label"] = "tool-v2";
        configuration["CodeyBox:Defaults:Agent"] = "antigravity";
        configuration.Reload();

        var after = accessor();

        var beforeSeed = Assert.Single(before.PackageCacheSeeds);
        var beforeProvision = Assert.Single(before.ExecutableProvisions);
        Assert.Equal("/srv/cache/v1", beforeSeed.HostSourcePath);
        Assert.Equal("/var/cache/codeybox/v1", beforeSeed.VmDestPath);
        Assert.Equal("/srv/tools/v1", beforeProvision.HostSourcePath);
        Assert.Equal(["/usr/local/bin/tool-v1"], beforeProvision.VmSymlinks);
        Assert.Contains(before.BaselineVerificationCommands, command =>
            string.Equals(command.Label, "claude", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(before.BaselineVerificationCommands, command =>
            string.Equals(command.Label, "antigravity", StringComparison.OrdinalIgnoreCase));

        var afterSeed = Assert.Single(after.PackageCacheSeeds);
        var afterProvision = Assert.Single(after.ExecutableProvisions);
        Assert.Equal("/srv/cache/v2", afterSeed.HostSourcePath);
        Assert.Equal("/var/cache/codeybox/v2", afterSeed.VmDestPath);
        Assert.Equal("/srv/tools/v2", afterProvision.HostSourcePath);
        Assert.Equal("/usr/local/lib/codeybox/tool-v2", afterProvision.VmDestPath);
        Assert.Equal(["/usr/local/bin/tool-v2"], afterProvision.VmSymlinks);
        Assert.Equal("tool-v2", afterProvision.Label);
        var antigravity = Assert.Single(after.BaselineVerificationCommands, command =>
            string.Equals(command.Label, "antigravity", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["agy", "--version"], antigravity.Argv);
        Assert.DoesNotContain(after.BaselineVerificationCommands, command =>
            string.Equals(command.Label, "claude", StringComparison.OrdinalIgnoreCase));

        Assert.NotSame(before, after);
        Assert.NotSame(before.PackageCacheSeeds, after.PackageCacheSeeds);
        Assert.NotSame(before.ExecutableProvisions, after.ExecutableProvisions);
        Assert.NotSame(before.BaselineVerificationCommands, after.BaselineVerificationCommands);
    }

    /// <summary>
    /// Resolves the registered <see cref="ISandboxProvider"/>, unwraps every
    /// composition wrapper around it (admission-control, disk-guard, …), and
    /// invokes the <see cref="MultipassSandboxProvider"/>'s options accessor to
    /// read the live snapshot — exactly what every real VM launch reads.
    /// </summary>
    private static MultipassSandboxOptions ResolveLiveMultipassOptions(BaselineVerificationFactory factory)
    {
        var registered = factory.Services.GetRequiredService<ISandboxProvider>();
        var multipass = UnwrapToMultipass(registered);

        var accessorField = typeof(MultipassSandboxProvider).GetField(
            "_optsAccessor", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(accessorField);
        var accessor = Assert.IsType<Func<MultipassSandboxOptions>>(accessorField!.GetValue(multipass));
        return accessor();
    }

    private static IncusSandboxOptions ResolveLiveIncusOptions(BaselineVerificationFactory factory)
        => ResolveLiveIncusOptionsAccessor(factory)();

    private static Func<IncusSandboxOptions> ResolveLiveIncusOptionsAccessor(
        BaselineVerificationFactory factory)
    {
        var registered = factory.Services.GetRequiredService<ISandboxProvider>();
        var incus = UnwrapToIncus(registered);

        var accessorField = typeof(IncusSandboxProvider).GetField(
            "_optionsAccessor", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(accessorField);
        return Assert.IsType<Func<IncusSandboxOptions>>(accessorField!.GetValue(incus));
    }

    private static MultipassSandboxProvider UnwrapToMultipass(ISandboxProvider provider)
    {
        // Walk every "_inner" field — Program.cs composes the multipass provider
        // behind admission control + disk-guard + suspending baseline wrappers,
        // each of which holds the next layer as a private _inner field.
        var current = provider;
        var visited = new HashSet<object>();
        while (current is not MultipassSandboxProvider && visited.Add(current))
        {
            if (current is ReloadableSandboxProvider reloadable)
            {
                current = reloadable.GetProvider(SandboxProviderKinds.Multipass);
                continue;
            }
            var innerField = FindInnerField(current.GetType());
            if (innerField is null) break;
            if (innerField.GetValue(current) is not ISandboxProvider next) break;
            current = next;
        }
        return Assert.IsType<MultipassSandboxProvider>(current);
    }

    private static IncusSandboxProvider UnwrapToIncus(ISandboxProvider provider)
    {
        var current = provider;
        var visited = new HashSet<object>();
        while (current is not IncusSandboxProvider && visited.Add(current))
        {
            if (current is ReloadableSandboxProvider reloadable)
            {
                current = reloadable.GetProvider(SandboxProviderKinds.Incus);
                continue;
            }
            var innerField = FindInnerField(current.GetType());
            if (innerField is null) break;
            if (innerField.GetValue(current) is not ISandboxProvider next) break;
            current = next;
        }
        return Assert.IsType<IncusSandboxProvider>(current);
    }

    private static FieldInfo? FindInnerField(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var field = t.GetField("_inner", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field is not null && typeof(ISandboxProvider).IsAssignableFrom(field.FieldType))
                return field;
        }
        return null;
    }

    private sealed class BaselineVerificationFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
    {
        private readonly Dictionary<string, string?> _extraConfig;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-baseline-verify-wiring-{Guid.NewGuid():N}.db");

        public BaselineVerificationFactory(Dictionary<string, string?> extraConfig)
        {
            _extraConfig = extraConfig;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Temp.Root;
                var baseConfig = new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                };
                foreach (var kvp in _extraConfig)
                    baseConfig[kvp.Key] = kvp.Value;
                cfg.AddInMemoryCollection(baseConfig);
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
                services.RemoveAll<ITimingStore>();
                services.AddSingleton<ITimingStore, NoopTimingStore>();
            });
        }

        protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(disposing, _dbPath);
    }

    private sealed class NoopTimingStore : ITimingStore
    {
        public Task BeginAsync(TimingRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task EndAsync(string id, DateTimeOffset endedAt, long durationMs, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<TimingRecord>> GetByWorkItemAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TimingRecord>>([]);

        public Task DeleteByWorkItemAsync(WorkItemId id, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<TimingRecord> StreamCompletedAsync(
            int workItemLimit,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
