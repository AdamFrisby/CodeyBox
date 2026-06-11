using System.Reflection;
using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Api;
using CodeyBox.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class AgentNetworkToleranceProgramWiringTests
{
    [Fact]
    public async Task ProgramBindsAgentNetworkToleranceAndInjectsSameSnapshotIntoCodexRunner()
    {
        using var factory = new AgentNetworkToleranceWiringFactory();

        var snapshot = factory.Services.GetRequiredService<AgentNetworkToleranceSnapshot>();
        var codex = factory.Services.GetServices<IAgentRunner>().OfType<CodexAgentRunner>().Single();

        Assert.Same(snapshot, Field<AgentNetworkToleranceSnapshot>(codex, "_networkTolerance"));

        var tolerance = snapshot.GetTolerance("codex");
        Assert.NotNull(tolerance);
        Assert.Equal(21, tolerance!.RequestMaxRetries);
        Assert.Equal(22, tolerance.StreamMaxRetries);
        Assert.Equal(230000, tolerance.StreamIdleTimeoutMs);
        Assert.Equal("azure", tolerance.Provider);

        var sandbox = new CapturingSandbox();
        await codex.RunAsync(sandbox, "/work", "prompt", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Contains("model_providers.azure.request_max_retries=21", argv);
        Assert.Contains("model_providers.azure.stream_max_retries=22", argv);
        Assert.Contains("model_providers.azure.stream_idle_timeout_ms=230000", argv);
    }

    [Fact]
    public async Task ProgramInjectsConfiguredClaudeApiTimeoutIntoOneShotRunner()
    {
        using var factory = new AgentNetworkToleranceWiringFactory();

        var snapshot = factory.Services.GetRequiredService<AgentNetworkToleranceSnapshot>();
        var claude = factory.Services.GetServices<IAgentRunner>().OfType<ClaudeAgentRunner>().Single();

        Assert.Same(snapshot, Field<AgentNetworkToleranceSnapshot>(claude, "_networkTolerance"));

        var sandbox = new CapturingSandbox();
        await claude.RunAsync(sandbox, "/work", "prompt", credential: null);

        var extraEnv = sandbox.CapturedExec!.ExtraEnvironment;
        Assert.NotNull(extraEnv);
        Assert.Equal("64000", extraEnv!["API_TIMEOUT_MS"]);
    }

    [Fact]
    public async Task ProgramInjectsConfiguredClaudeApiTimeoutIntoAcpTransport()
    {
        using var factory = new AgentNetworkToleranceWiringFactory();

        var snapshot = factory.Services.GetRequiredService<AgentNetworkToleranceSnapshot>();
        var transport = factory.Services.GetRequiredService<AcpClaudeTransport>();

        Assert.Same(snapshot, Field<AgentNetworkToleranceSnapshot>(transport, "_networkTolerance"));

        var sandbox = new AcpProgramWiringSandbox();
        var openRequest = new ClaudeTransportOpenRequest(
            Sandbox: sandbox,
            WorkingDirectory: "/work",
            Credential: new AgentCredential(
                AgentKind.Claude,
                new Dictionary<string, string>
                {
                    ["ANTHROPIC_API_KEY"] = "sk-test",
                    ["CLAUDE_CODE_OAUTH_TOKEN"] = "oauth-test",
                },
                new Dictionary<string, string>()),
            ModelId: null,
            ReasoningMode: null,
            LocalSessionId: "program-wiring");

        await using var session = await transport.OpenAsync(openRequest, CancellationToken.None);
        var turn = await session.SendTurnAsync(
            new ClaudeTransportTurnRequest("prompt", CliResumeSessionId: null, StdoutChunkCallback: null),
            CancellationToken.None);

        Assert.True(turn.Result.Success);

        var bridgeExec = Assert.Single(sandbox.BridgeExecs);
        var extraEnv = bridgeExec.ExtraEnvironment;
        Assert.NotNull(extraEnv);
        Assert.Equal("64000", extraEnv!["API_TIMEOUT_MS"]);

        using var hello = ReadFirstStdinEnvelope(bridgeExec);
        var claudeEnv = hello.RootElement.GetProperty("claudeEnv");
        Assert.Equal("64000", claudeEnv.GetProperty("API_TIMEOUT_MS").GetString());
        Assert.Equal("sk-test", claudeEnv.GetProperty("ANTHROPIC_API_KEY").GetString());
        Assert.Equal("oauth-test", claudeEnv.GetProperty("CLAUDE_CODE_OAUTH_TOKEN").GetString());
    }

    private static T Field<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static JsonDocument ReadFirstStdinEnvelope(SandboxExec exec)
    {
        var stdin = exec.Stdin;
        Assert.NotNull(stdin);
        var firstLineEnd = stdin!.IndexOf('\n', StringComparison.Ordinal);
        var firstLine = firstLineEnd < 0 ? stdin : stdin![..firstLineEnd];
        return JsonDocument.Parse(firstLine);
    }

    private sealed class AgentNetworkToleranceWiringFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-network-tolerance-wiring-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:AgentNetworkTolerance:codex:RequestMaxRetries"] = "21",
                    ["CodeyBox:AgentNetworkTolerance:codex:StreamMaxRetries"] = "22",
                    ["CodeyBox:AgentNetworkTolerance:codex:StreamIdleTimeoutMs"] = "230000",
                    ["CodeyBox:AgentNetworkTolerance:codex:Provider"] = "azure",
                    ["CodeyBox:AgentNetworkTolerance:claude:ApiTimeoutMs"] = "64000",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }

    private sealed class AcpProgramWiringSandbox : ISandbox
    {
        public string Id { get; } = "acp-program-wiring";
        public List<SandboxExec> AllExecs { get; } = [];
        public List<SandboxExec> BridgeExecs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            AllExecs.Add(exec);
            if (exec.Argv.Count >= 3
                && exec.Argv[0] == "bash"
                && exec.Argv[1] == "-lc"
                && exec.Argv[2].Contains("claude-acp-bridge.cjs", StringComparison.Ordinal))
            {
                BridgeExecs.Add(exec);
                var stdout = string.Join('\n', new[]
                {
                    "{\"type\":\"peer_connected\"}",
                    "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"sessionId\":\"acp-program-1\"}}}",
                    "{\"type\":\"acp_recv\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"stopReason\":\"end_turn\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}}",
                    "{\"type\":\"turn_complete\",\"stopReason\":\"end_turn\"}",
                }) + "\n";
                exec.StdoutChunkCallback?.Invoke(stdout);
                return Task.FromResult(new SandboxExecResult(0, stdout, ""));
            }

            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
