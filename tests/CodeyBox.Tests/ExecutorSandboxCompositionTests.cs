using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the executor provider composition: the executor host
/// resolves every registered provider kind through the shared composition
/// (no executor-side switch), provider options bind from the executor's own
/// configuration section, one host can serve several kinds, operator
/// capability claims cannot exceed what the serving providers implement, and
/// a registry populated with a brand-new kind resolves through the executor
/// path with no executor-code change.
/// </summary>
public sealed class ExecutorSandboxCompositionTests
{
    // Executor configured for a registered kind the old hardcoded switch
    // could not construct resolves it through the shared composition.
    [Fact]
    public void Executor_ResolvesRegisteredKind_BeyondOldSwitch()
    {
        using var services = BuildExecutorServices(new Dictionary<string, string?>
        {
            ["CodeyBox:Executor:HostId"] = "exec-comp",
            ["CodeyBox:Executor:OrchestratorBaseUrl"] = "http://127.0.0.1:1/",
            ["CodeyBox:Executor:LocalSandboxProvider"] = "incus",
        });

        var resolved = services.GetRequiredService<ISandboxProvider>();
        var registry = services.GetRequiredService<ISandboxProviderRegistry>();

        Assert.Equal("incus", resolved.Name);
        Assert.Same(resolved, registry.EnsureKind("incus"));
        Assert.Same(resolved, registry.Resolve(SandboxPlacementTestMembers.Member("m", "INCUS")));
    }

    // An unknown kind fails at startup with a message naming the offending
    // value and the registered kinds — never a hardcoded literal list.
    [Fact]
    public void ExecutorStartup_UnknownKind_FailsFastNamingValueAndKinds()
    {
        using var services = BuildExecutorServices(new Dictionary<string, string?>
        {
            ["CodeyBox:Executor:HostId"] = "exec-comp",
            ["CodeyBox:Executor:OrchestratorBaseUrl"] = "http://127.0.0.1:1/",
            ["CodeyBox:Executor:LocalSandboxProvider"] = "bogus-kind",
        });

        var options = services.GetRequiredService<IOptionsMonitor<ExecutorOptions>>().CurrentValue;
        var registry = services.GetRequiredService<ISandboxProviderRegistry>();
        var ex = Assert.Throws<InvalidOperationException>(
            () => ExecutorSandboxStartup.Validate(options, registry));
        Assert.Contains("bogus-kind", ex.Message, StringComparison.Ordinal);
        Assert.Contains("incus", ex.Message, StringComparison.Ordinal);
        Assert.Contains("process", ex.Message, StringComparison.Ordinal);
    }

    // Provider options bind from the executor's own section rather than
    // falling back to defaults.
    [Fact]
    public void Executor_ProviderOptions_BindFromExecutorSection()
    {
        using var services = BuildExecutorServices(new Dictionary<string, string?>
        {
            ["CodeyBox:Executor:HostId"] = "exec-comp",
            ["CodeyBox:Executor:OrchestratorBaseUrl"] = "http://127.0.0.1:1/",
            ["CodeyBox:Executor:LocalSandboxProvider"] = "incus",
            ["CodeyBox:Executor:Bubblewrap:BwrapBinary"] = "/custom/bwrap",
            ["CodeyBox:Executor:Incus:ProjectName"] = "custom-project",
            ["CodeyBox:Executor:Incus:BinaryPath"] = "/custom/incus",
        });

        var options = services.GetRequiredService<IOptionsMonitor<ExecutorOptions>>().CurrentValue;
        Assert.Equal("/custom/bwrap", options.Bubblewrap.BwrapBinary);
        Assert.Equal("custom-project", options.Incus.ProjectName);
        Assert.Equal("/custom/incus", options.Incus.BinaryPath);
    }

    // The bound options actually reach provider construction: an invalid
    // configured value fails the build, while defaults build fine.
    [Fact]
    public void Executor_BoundOptions_FlowIntoProviderConstruction()
    {
        using var valid = BuildExecutorServices(ConfigFor("incus", new Dictionary<string, string?>
        {
            ["CodeyBox:Executor:Incus:ProjectName"] = "custom-project",
        }));
        var validRegistry = valid.GetRequiredService<ISandboxProviderRegistry>();
        Assert.Equal("incus", validRegistry.EnsureKind("incus").Name);

        using var broken = BuildExecutorServices(ConfigFor("incus", new Dictionary<string, string?>
        {
            ["CodeyBox:Executor:Incus:ProjectName"] = "",
        }));
        var brokenRegistry = broken.GetRequiredService<ISandboxProviderRegistry>();
        Assert.Throws<InvalidOperationException>(() => brokenRegistry.EnsureKind("incus"));
    }

    // One executor declaring two kinds serves both, and each kind resolves
    // to the same shared instance a second member naming that kind gets.
    [Fact]
    public void Executor_TwoKinds_ServeBothAsSharedInstances()
    {
        using var services = BuildExecutorServices(new Dictionary<string, string?>
        {
            ["CodeyBox:Executor:HostId"] = "exec-comp",
            ["CodeyBox:Executor:OrchestratorBaseUrl"] = "http://127.0.0.1:1/",
            ["CodeyBox:Executor:SandboxProviders:0"] = "incus",
            ["CodeyBox:Executor:SandboxProviders:1"] = "process",
        });

        var options = services.GetRequiredService<IOptionsMonitor<ExecutorOptions>>().CurrentValue;
        Assert.Equal(["incus", "process"], options.GetDeclaredKinds());

        var registry = services.GetRequiredService<ISandboxProviderRegistry>();
        var providers = ExecutorSandboxStartup.Validate(options, registry);
        Assert.Equal(2, providers.Count);

        var incusDirect = registry.EnsureKind("incus");
        var processDirect = registry.EnsureKind("process");
        Assert.NotSame(incusDirect, processDirect);
        Assert.Same(incusDirect, registry.Resolve(SandboxPlacementTestMembers.Member("second-incus", "incus")));
        Assert.Same(processDirect, registry.Resolve(SandboxPlacementTestMembers.Member("second-process", "process")));

        // The singleton stays the primary (first-declared) kind.
        Assert.Same(incusDirect, services.GetRequiredService<ISandboxProvider>());
    }

    // A capability no serving provider implements cannot be declared: startup
    // fails fast naming it.
    [Fact]
    public void ExecutorStartup_UnimplementedCapability_FailsFast()
    {
        var options = ValidOptions();
        options.LocalSandboxProvider = "process";
        options.DeclaredCapabilities = ["suspend-resume"];
        var registry = new SandboxProviderRegistry(
            kind => new PlacementFakeSandboxProvider(kind),
            NullLogger<SandboxProviderRegistry>.Instance);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ExecutorSandboxStartup.Validate(options, registry));
        Assert.Contains("suspend-resume", ex.Message, StringComparison.Ordinal);
    }

    // ...while a capability a serving provider implements passes, and
    // operator clearance tags always pass through.
    [Fact]
    public void ExecutorStartup_ImplementedCapability_Passes()
    {
        var options = ValidOptions();
        options.LocalSandboxProvider = "multipass";
        options.DeclaredCapabilities = ["suspend-resume", "sensitive"];
        var registry = new SandboxProviderRegistry(
            static kind => new PlacementFakeSandboxProvider(
                kind,
                kind == "multipass" ? [SandboxCapabilities.SuspendResume] : []),
            NullLogger<SandboxProviderRegistry>.Instance);

        var providers = ExecutorSandboxStartup.Validate(options, registry);
        Assert.Equal("multipass", Assert.Single(providers).Name);
    }

    // Registration carries provider truth: well-known tags the provider
    // lacks are dropped, clearance tags are kept.
    [Fact]
    public async Task ExecutorRegistration_DropsCapabilitiesProviderLacks()
    {
        var options = ValidOptions();
        options.DeclaredCapabilities = ["sensitive", "suspend-resume"];
        string? body = null;
        var client = MakeClient(
            options,
            new PlacementFakeSandboxProvider("process"),
            async (req, ct) =>
            {
                body = await req.Content!.ReadAsStringAsync(ct);
                return JsonResponse(new { workerId = "executor:exec-1" });
            });

        await client.RegisterAsync();

        using var doc = JsonDocument.Parse(body!);
        var capabilities = doc.RootElement
            .GetProperty("declaredCapabilities")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Equal(["sensitive"], capabilities);
    }

    // Adding a provider requires no edit to executor code: a registry
    // populated in the test with a brand-new kind resolves through the
    // executor startup path and the client path unmodified.
    [Fact]
    public async Task ExecutorPath_ResolvesTestRegisteredKind_Unmodified()
    {
        var testRegistry = new SandboxProviderRegistry(
            static kind => kind == "test-fake"
                ? new PlacementFakeSandboxProvider("test-fake", [SandboxCapabilities.SuspendResume])
                : throw new InvalidOperationException($"Unexpected kind '{kind}'."),
            NullLogger<SandboxProviderRegistry>.Instance);

        var options = ValidOptions();
        options.LocalSandboxProvider = "test-fake";
        options.DeclaredCapabilities = ["suspend-resume", "sensitive"];

        var providers = ExecutorSandboxStartup.Validate(options, testRegistry);
        Assert.Equal("test-fake", Assert.Single(providers).Name);

        string? body = null;
        var client = MakeClient(
            options,
            testRegistry.EnsureKind("test-fake"),
            async (req, ct) =>
            {
                body = await req.Content!.ReadAsStringAsync(ct);
                return JsonResponse(new { workerId = "executor:exec-1" });
            },
            testRegistry);

        Assert.Equal("executor:exec-1", await client.RegisterAsync());
        using var doc = JsonDocument.Parse(body!);
        var capabilities = doc.RootElement
            .GetProperty("declaredCapabilities")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Equal(["suspend-resume", "sensitive"], capabilities);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static ServiceProvider BuildExecutorServices(Dictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ExecutorOptions>(configuration.GetSection("CodeyBox:Executor"));
        services.AddSingleton<Func<ExecutorOptions>>(
            sp => () => sp.GetRequiredService<IOptionsMonitor<ExecutorOptions>>().CurrentValue);
        services.AddExecutorSandboxProviders();
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> ConfigFor(string kind, Dictionary<string, string?> extra)
    {
        var config = new Dictionary<string, string?>
        {
            ["CodeyBox:Executor:HostId"] = "exec-comp",
            ["CodeyBox:Executor:OrchestratorBaseUrl"] = "http://127.0.0.1:1/",
            ["CodeyBox:Executor:LocalSandboxProvider"] = kind,
        };
        foreach (var (key, value) in extra)
            config[key] = value;
        return config;
    }

    private static ExecutorOptions ValidOptions() => new()
    {
        HostId = "exec-1",
        OrchestratorBaseUrl = "http://127.0.0.1:1/",
        ApiKeyEnvVar = TestKey.EnvVar,
    };

    private static ExecutorClient MakeClient(
        ExecutorOptions options,
        ISandboxProvider provider,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        ISandboxProviderRegistry? registry = null)
    {
        var http = new HttpClient(new RecordingHandler(handler))
        {
            BaseAddress = new Uri("http://127.0.0.1:9/"),
        };
        return new ExecutorClient(
            http,
            () => options,
            provider,
            null,
            null,
            null,
            NullLogger<ExecutorClient>.Instance,
            registry);
    }

    private static HttpResponseMessage JsonResponse(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(payload),
        };

    private static class TestKey
    {
        public static readonly string EnvVar = "CODEYBOX_TEST_EXECUTOR_COMPOSITION_API_KEY";
        static TestKey() => Environment.SetEnvironmentVariable(EnvVar, new string('k', 40));
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            handler(request, ct);
    }
}
