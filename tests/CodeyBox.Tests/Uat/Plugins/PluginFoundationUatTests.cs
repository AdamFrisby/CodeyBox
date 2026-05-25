using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.Loader;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests.Uat.Plugins;

/// <summary>
/// UAT coverage for <c>Plugin foundation - Discovers, loads, and initializes versioned plugins</c>.
/// Plan anchor: docs/uat/00-plan.md#plugins
/// </summary>
public sealed class PluginFoundationUatTests
{
    [Fact]
    public void ConfiguredPluginDirectory_LoadsAllowlistedPluginsInPluginAssemblyLoadContext()
    {
        using var pluginDirectory = new TemporaryPluginDirectory();
        var samplePath = pluginDirectory.CopyIn(PluginTestHelpers.GetSamplePluginAssemblyPath());
        var loader = PluginsUatHelpers.Loader(new PluginOptions
        {
            PackageDirectories = [pluginDirectory.Path],
            Allowlist = ["sample.auditor"],
        });

        var plugins = loader.DiscoverPlugins();

        var plugin = Assert.Single(plugins);
        Assert.Equal("sample.auditor", plugin.PluginId);
        Assert.Equal(samplePath, plugin.AssemblyPath);
        var loadedType = Assert.Single(plugin.RegisteredTypes);
        Assert.Equal("SampleAuditor", loadedType.Name);
        var loadContext = AssemblyLoadContext.GetLoadContext(loadedType.Assembly);
        Assert.NotNull(loadContext);
        Assert.StartsWith("Plugin:", loadContext!.Name, StringComparison.Ordinal);
        Assert.NotSame(AssemblyLoadContext.Default, loadContext);
    }

    [Fact]
    public async Task SupportedApiVersion_InitializerRunsWithPluginScopedContext()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Plugins:uat.initializer:Mode"] = "strict",
                ["CodeyBox:Plugins:other.plugin:Mode"] = "hidden",
            })
            .Build();
        var loader = PluginsUatHelpers.Loader(new PluginOptions { Allowlist = ["*"] }, config);
        var plugin = new LoadedPlugin(
            "uat.initializer",
            "UAT Initializer",
            "/fake/initializer.dll",
            [typeof(InitializingPluginAuditor)]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        loader.RegisterPlugins(services, [plugin]);
        services.AddSingleton<IPluginLoader>(new PluginLoader(
            new PluginOptions(),
            config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PluginLoader>.Instance,
            [plugin]));
        services.AddHostedService<PluginInitializationService>();

        await using var provider = services.BuildServiceProvider();
        var initService = provider.GetServices<IHostedService>()
            .OfType<PluginInitializationService>()
            .Single();

        await initService.StartAsync(CancellationToken.None);

        var auditor = provider.GetRequiredService<InitializingPluginAuditor>();
        Assert.True(auditor.Initialized);
        Assert.Equal(CodeyBoxApiVersion.Current, auditor.HostApiVersion);
        Assert.Equal("uat.initializer", auditor.PluginId);
        Assert.Equal("strict", auditor.Mode);
        Assert.Null(auditor.OtherPluginMode);
    }

    [Fact]
    public void UnsupportedApiVersion_RejectsOnlyIncompatiblePlugin()
    {
        var loader = PluginsUatHelpers.Loader(new PluginOptions
        {
            AssemblyPaths = [PluginTestHelpers.GetSamplePluginAssemblyPath()],
            Allowlist = ["*"],
        });

        var plugins = loader.DiscoverPlugins();

        var ids = plugins.Select(p => p.PluginId).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["sample.auditor", "sample.blocked-auditor"], ids);
        Assert.DoesNotContain("sample.future-auditor", ids);
    }

    [Fact]
    public void AssemblyLoadFailure_LogsAndContinuesScanningOtherAssemblies()
    {
        using var pluginDirectory = new TemporaryPluginDirectory();
        File.WriteAllText(System.IO.Path.Combine(pluginDirectory.Path, "not-a-plugin.dll"), "not an assembly");
        pluginDirectory.CopyIn(PluginTestHelpers.GetSamplePluginAssemblyPath());
        var logger = new CapturingLogger<PluginLoader>();
        var loader = new PluginLoader(
            new PluginOptions { PackageDirectories = [pluginDirectory.Path], Allowlist = ["sample.auditor"] },
            PluginsUatHelpers.EmptyConfig(),
            logger);

        var plugins = loader.DiscoverPlugins();

        Assert.Single(plugins);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("Failed to load plugin assembly", StringComparison.Ordinal));
    }
}

public sealed class PluginEndpointUatTests : IDisposable
{
    private readonly PluginEndpointFactory _factory = new();
    private readonly HttpClient _client;

    public PluginEndpointUatTests()
        => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task GetPlugins_ReturnsLoadedAuditorPluginMetadata()
    {
        var response = await _client.GetAsync("/plugins");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<PluginDto>>();
        Assert.NotNull(body);
        Assert.Equal(["uat.endpoint-auditor"],
            body!.Select(p => p.PluginId).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(body, p =>
            p.PluginId == "uat.endpoint-auditor" &&
            p.DisplayName == "UAT Endpoint Auditor");
        Assert.DoesNotContain(body, p => p.PluginId == "uat.endpoint-credential");
    }

    private sealed record PluginDto(string PluginId, string DisplayName);
}

internal sealed class PluginEndpointFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"codeybox-plugin-uat-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = System.IO.Path.GetTempPath();
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:SandboxProvider"] = "process",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = System.IO.Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = System.IO.Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = System.IO.Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IPluginLoader>();
            services.AddSingleton<IPluginLoader>(new StaticPluginLoader(
            [
                new LoadedPlugin(
                    "uat.endpoint-auditor",
                    "UAT Endpoint Auditor",
                    "/fake/auditor.dll",
                    [typeof(PassingPluginAuditor)]),
                new LoadedPlugin(
                    "uat.endpoint-credential",
                    "UAT Endpoint Credential",
                    "/fake/credential.dll",
                    [typeof(EndpointCredentialPlugin)]),
            ]));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { File.Delete(_dbPath); } catch { }
        }
        base.Dispose(disposing);
    }
}

internal sealed class StaticPluginLoader(IReadOnlyList<LoadedPlugin> plugins) : IPluginLoader
{
    public Task<IReadOnlyList<LoadedPlugin>> DiscoverAndLoadAsync(CancellationToken ct)
        => Task.FromResult(plugins);
}

internal sealed class EndpointCredentialPlugin : ICredentialProvider
{
    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        => Task.FromResult<AgentCredential?>(null);
}

[CodeyBoxPlugin("uat.initializer", "UAT Initializer")]
internal sealed class InitializingPluginAuditor : IAuditor, IPluginInitializer
{
    public bool Initialized { get; private set; }
    public string? HostApiVersion { get; private set; }
    public string? PluginId { get; private set; }
    public string? Mode { get; private set; }
    public string? OtherPluginMode { get; private set; }

    public string Name => "uat:initializer";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        Initialized = true;
        HostApiVersion = context.HostApiVersion;
        PluginId = context.PluginId;
        Mode = context.ScopedConfig["Mode"];
        OtherPluginMode = context.ScopedConfig["other.plugin:Mode"];
        return Task.CompletedTask;
    }

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
        => Task.FromResult(new AuditResult(true, []));
}

internal sealed class TemporaryPluginDirectory : IDisposable
{
    public TemporaryPluginDirectory()
        => Path = Directory.CreateTempSubdirectory("codeybox-plugin-uat-").FullName;

    public string Path { get; }

    public string CopyIn(string sourcePath)
    {
        var destination = System.IO.Path.Combine(Path, System.IO.Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destination);
        return destination;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
