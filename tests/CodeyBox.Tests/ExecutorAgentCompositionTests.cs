using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Crush;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the shared agent-execution composition: the executor host
/// composes every agent runner plus the credential chain through the same unit
/// the orchestrator calls (no API-host involvement), registration declares
/// only agents whose credentials actually resolve, and an agent added at the
/// single composition site is reachable from the executor path with no
/// executor-side change.
/// </summary>
public sealed class ExecutorAgentCompositionTests
{
    // The executor composes a real runner and resolves its credential through
    // the real chained provider, using only the shared composition — no API
    // host types involved.
    [Fact]
    public async Task Executor_ComposesRunnerAndResolvesCredential_WithoutApiHost()
    {
        using var provider = BuildAgentServices();
        var registry = provider.GetRequiredService<IAgentRegistry>();
        Assert.True(registry.TryGet(AgentKind.Crush, out var runner));
        Assert.IsType<CrushAgentRunner>(runner);
        Assert.True(registry.TryGet(AgentKind.Claude, out var claude));
        Assert.IsType<ClaudeAgentRunner>(claude);

        const string hostVar = "CODEYBOX_CRUSH_API_KEY";
        var saved = Environment.GetEnvironmentVariable(hostVar);
        try
        {
            Environment.SetEnvironmentVariable(hostVar, "test-crush-key");
            var credentials = provider.GetRequiredService<ICredentialProvider>();
            var credential = await credentials.GetAsync(AgentKind.Crush);
            Assert.NotNull(credential);
            Assert.Equal("test-crush-key", credential.EnvironmentVariables["OPENROUTER_API_KEY"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(hostVar, saved);
        }
    }

    // An agent whose credential is absent is not declared at registration:
    // placement can only match on honestly runnable agents.
    [Fact]
    public async Task ExecutorRegistration_OmitsAgentWithoutCredential()
    {
        using var provider = BuildAgentServices();
        var registry = provider.GetRequiredService<IAgentRegistry>();
        var advertiser = new ExecutorAgentAdvertiser(
            registry, new SingleAgentCredentialProvider(AgentKind.Crush));

        string? body = null;
        var client = MakeClient(ValidOptions(), new PlacementFakeSandboxProvider("process"),
            async (req, ct) =>
            {
                body = await req.Content!.ReadAsStringAsync(ct);
                return JsonResponse(new { workerId = "executor:exec-1" });
            },
            advertiser);

        Assert.Equal("executor:exec-1", await client.RegisterAsync());

        using var doc = JsonDocument.Parse(body!);
        var declared = doc.RootElement
            .GetProperty("declaredCredentials")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Equal(["crush"], declared);
    }

    // Allow-list narrows but never widens: an asserted name with no credential
    // stays undeclared even when the operator lists it.
    [Fact]
    public void CredentialAllowList_NarrowsButNeverWidens()
    {
        var runnable = new[] { "crush", "codex" };
        Assert.Equal(
            new[] { "codex" },
            ExecutorAgentAdvertiser.ApplyCredentialAllowList(runnable, ["codex", "claude"]));
        Assert.Equal(
            runnable,
            ExecutorAgentAdvertiser.ApplyCredentialAllowList(runnable, []));
        Assert.Equal(
            runnable,
            ExecutorAgentAdvertiser.ApplyCredentialAllowList(runnable, ["*"]));
    }

    // Adding an agent requires no executor-side edit: a runner added through
    // the shared catalog (the single composition site) resolves from an
    // executor-wired provider and is advertised once its credential exists.
    [Fact]
    public async Task ExecutorPath_ResolvesNewlyRegisteredAgent_Unmodified()
    {
        var added = new AgentKind("test-fake-agent");
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:CredentialFileWatchers"] = "false",
            })
            .Build();
        services.AddAgentExecution(
            configuration,
            options => options.AdditionalRunners.Add(_ => new FakeAgentRunner(added)));
        // Same sandbox wiring the executor host process applies; unchanged by
        // the agent addition above.
        services.AddExecutorSandboxProviders();
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IAgentRegistry>();
        Assert.True(registry.TryGet(added, out var runner));
        Assert.IsType<FakeAgentRunner>(runner);
        // The pre-existing catalog is untouched by the addition.
        Assert.True(registry.TryGet(AgentKind.Opencode, out _));

        var advertiser = new ExecutorAgentAdvertiser(
            registry, new SingleAgentCredentialProvider(added));
        using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var runnable = await advertiser.GetRunnableAgentNamesAsync(
            TimeSpan.FromSeconds(5), probeCts.Token);
        Assert.Contains(added.Value, runnable);
        Assert.DoesNotContain(AgentKind.Claude.Value, runnable);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static ServiceProvider BuildAgentServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:CredentialFileWatchers"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentExecution(configuration);
        return services.BuildServiceProvider();
    }

    private static ExecutorOptions ValidOptions() => new()
    {
        HostId = "exec-1",
        OrchestratorBaseUrl = "http://127.0.0.1:1/",
        ApiKeyEnvVar = TestApiKey.EnvVar,
    };

    private static ExecutorClient MakeClient(
        ExecutorOptions options,
        ISandboxProvider provider,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        ExecutorAgentAdvertiser advertiser)
    {
        var http = new HttpClient(new RecordingHandler(handler))
        {
            BaseAddress = new Uri("http://127.0.0.1:9/"),
        };
        return new ExecutorClient(
            http,
            () => options,
            provider,
            tracker: null,
            phaseRunner: null,
            clock: null,
            log: NullLogger<ExecutorClient>.Instance,
            providerRegistry: null,
            agentAdvertiser: advertiser);
    }

    private static HttpResponseMessage JsonResponse(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(payload),
        };

    private static class TestApiKey
    {
        public static readonly string EnvVar = "CODEYBOX_TEST_EXECUTOR_AGENT_COMPOSITION_API_KEY";
        static TestApiKey() => Environment.SetEnvironmentVariable(EnvVar, new string('k', 40));
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            handler(request, ct);
    }

    private sealed class SingleAgentCredentialProvider(AgentKind runnable) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default) =>
            Task.FromResult<AgentCredential?>(
                agent.Equals(runnable)
                    ? new AgentCredential(
                        agent,
                        new Dictionary<string, string> { ["TEST_FAKE_TOKEN"] = "present" },
                        new Dictionary<string, string>())
                    : null);
    }

    private sealed class FakeAgentRunner(AgentKind kind) : IAgentRunner
    {
        public AgentKind Kind { get; } = kind;

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false) =>
            Task.FromResult(new AgentResult(Success: true, Summary: "fake", Stdout: null, Stderr: null));
    }
}
