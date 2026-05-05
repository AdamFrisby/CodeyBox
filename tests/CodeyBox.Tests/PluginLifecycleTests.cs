using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class PluginLifecycleTests
{
    private static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().Build();

    // ── Initialization ────────────────────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_CalledAtStartup()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var opts = new PluginOptions { AssemblyPaths = [samplePath], Allowlist = ["sample.auditor"] };
        var config = EmptyConfig();

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);

        var loader = new PluginLoader(opts, config, NullLogger<PluginLoader>.Instance);
        var plugins = loader.DiscoverPlugins();
        loader.RegisterPlugins(services, plugins);
        services.AddSingleton<IPluginLoader>(new PluginLoader(opts, config, NullLogger<PluginLoader>.Instance, plugins));
        services.AddHostedService<PluginInitializationService>();

        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        var initService = hostedServices.OfType<PluginInitializationService>().Single();

        // Should complete without throwing
        await initService.StartAsync(CancellationToken.None);

        // Verify the auditor is resolvable (i.e., initialization did not corrupt DI)
        var auditors = provider.GetServices<IAuditor>().ToList();
        Assert.Single(auditors);
        Assert.Equal("sample-auditor", auditors[0].Name);
    }

    [Fact]
    public async Task InitializationException_Propagates()
    {
        var config = EmptyConfig();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IAuditor, ThrowingAuditor>();

        var pluginType = typeof(ThrowingAuditor);
        var fakePlugin = new LoadedPlugin("throwing.plugin", "Throwing Plugin", "/fake.dll", [pluginType]);
        var preloaded = (IReadOnlyList<LoadedPlugin>)[fakePlugin];

        services.AddSingleton<IPluginLoader>(new PluginLoader(
            new PluginOptions(), config, NullLogger<PluginLoader>.Instance, preloaded));
        services.AddHostedService<PluginInitializationService>();

        using var provider = services.BuildServiceProvider();
        var initService = provider.GetServices<IHostedService>()
            .OfType<PluginInitializationService>()
            .Single();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => initService.StartAsync(CancellationToken.None));

        Assert.Equal("Simulated init failure", ex.Message);
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task IAsyncDisposable_DisposedByDiContainer()
    {
        var config = EmptyConfig();
        var services = new ServiceCollection();
        services.AddSingleton<IAuditor, DisposableAuditor>();

        using var provider = services.BuildServiceProvider();
        var auditor = (DisposableAuditor)provider.GetRequiredService<IAuditor>();

        Assert.False(auditor.IsDisposed);
        await provider.DisposeAsync();
        Assert.True(auditor.IsDisposed);
    }

    // ── Test stubs ────────────────────────────────────────────────────────────

    [CodeyBoxPlugin("throwing.plugin", "Throwing Plugin")]
    private sealed class ThrowingAuditor : IAuditor, IPluginInitializer
    {
        public string Name => "throwing-auditor";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory,
            AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));

        public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated init failure");
    }

    [CodeyBoxPlugin("disposable.plugin", "Disposable Plugin")]
    private sealed class DisposableAuditor : IAuditor, IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public string Name => "disposable-auditor";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory,
            AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
