using System.Reflection;
using System.Runtime.CompilerServices;
using CodeyBox.Agents.Copilot;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
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
/// Pins the real Program.cs wiring of <see cref="MultipassSandboxOptions.BaselineVerificationCommands"/>
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

    private static MultipassSandboxProvider UnwrapToMultipass(ISandboxProvider provider)
    {
        // Walk every "_inner" field — Program.cs composes the multipass provider
        // behind admission control + disk-guard + suspending baseline wrappers,
        // each of which holds the next layer as a private _inner field.
        var current = provider;
        var visited = new HashSet<object>();
        while (current is not MultipassSandboxProvider && visited.Add(current))
        {
            var innerField = FindInnerField(current.GetType());
            if (innerField is null) break;
            if (innerField.GetValue(current) is not ISandboxProvider next) break;
            current = next;
        }
        return Assert.IsType<MultipassSandboxProvider>(current);
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

    private sealed class BaselineVerificationFactory : WebApplicationFactory<Program>
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
                var tmp = Path.GetTempPath();
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
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
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
