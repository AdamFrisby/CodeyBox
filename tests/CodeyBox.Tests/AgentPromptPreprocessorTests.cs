using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class AgentPromptPreprocessorTests
{
    [Fact]
    public async Task Chain_AppliesBuiltInPluginBuiltInLastOrder()
    {
        var log = new List<string>();
        var chain = new AgentPromptPreprocessorChain(
        [
            new AppendingPreprocessor("built-in-last", log, AgentPromptPreprocessorOrder.BuiltInLast),
            new TestPluginPreprocessor("plugin", log, order: -100),
            new AppendingPreprocessor("built-in-first", log, AgentPromptPreprocessorOrder.BuiltInFirst + 50),
        ]);

        var result = await chain.ProcessAsync(NewContext(), "prompt");

        Assert.Equal(["built-in-first", "plugin", "built-in-last"], log);
        Assert.Equal("prompt|built-in-first|plugin|built-in-last", result);
    }

    [Fact]
    public async Task PluginLoader_RegistersPromptPreprocessorPlugins()
    {
        var plugin = new LoadedPlugin(
            PluginId: "test.prompt-preprocessor",
            DisplayName: "Prompt Preprocessor",
            AssemblyPath: "/fake.dll",
            RegisteredTypes: [typeof(TestPluginPreprocessor)]);

        var loader = new PluginLoader(
            new PluginOptions { Allowlist = ["*"] },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);
        var services = new ServiceCollection();

        loader.RegisterPlugins(services, [plugin]);

        await using var sp = services.BuildServiceProvider();
        var resolved = sp.GetServices<IAgentPromptPreprocessor>().ToList();

        var preprocessor = Assert.Single(resolved);
        Assert.IsType<TestPluginPreprocessor>(preprocessor);
    }

    [Fact]
    public async Task ProjectRulesPreprocessor_InjectsRulesAndHotReloadsPath()
    {
        var monitor = new MutableOptionsMonitor<AgentPromptPreprocessingOptions>(
            new() { ProjectRulesPath = "AGENTS.md" });
        var sandbox = new FileBackedSandbox(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENTS.md"] = "rule one\n",
            ["docs/agents.md"] = "rule two\n",
        });
        var preprocessor = new ProjectRulesPromptPreprocessor(
            monitor,
            NullLogger<ProjectRulesPromptPreprocessor>.Instance);

        var first = await preprocessor.ProcessAsync(NewContext(sandbox), "original prompt");

        Assert.Contains("## Project rules (must follow)", first);
        Assert.Contains("Loaded from `AGENTS.md`.", first);
        Assert.Contains("rule one", first);
        Assert.Contains("original prompt", first);

        monitor.CurrentValue = new AgentPromptPreprocessingOptions { ProjectRulesPath = "docs/agents.md" };

        var second = await preprocessor.ProcessAsync(NewContext(sandbox), "next prompt");

        Assert.Contains("Loaded from `docs/agents.md`.", second);
        Assert.Contains("rule two", second);
        Assert.Contains("next prompt", second);
    }

    [Fact]
    public async Task ProjectRulesPreprocessor_MissingOrInvalidPathLeavesPromptUnchanged()
    {
        var monitor = new MutableOptionsMonitor<AgentPromptPreprocessingOptions>(
            new() { ProjectRulesPath = "missing.md" });
        var sandbox = new FileBackedSandbox(new Dictionary<string, string>());
        var preprocessor = new ProjectRulesPromptPreprocessor(
            monitor,
            NullLogger<ProjectRulesPromptPreprocessor>.Instance);

        Assert.Equal(
            "original",
            await preprocessor.ProcessAsync(NewContext(sandbox), "original"));

        monitor.CurrentValue = new AgentPromptPreprocessingOptions { ProjectRulesPath = "../AGENTS.md" };

        Assert.Equal(
            "original",
            await preprocessor.ProcessAsync(NewContext(sandbox), "original"));
    }

    [Fact]
    public async Task PromptPreprocessingAgentRunner_ProcessesEveryDefinedPhase()
    {
        var recorder = new RecordingPreprocessor();
        var chain = new AgentPromptPreprocessorChain([recorder]);
        var inner = new RecordingTextOnlyRunner();
        var project = NewProject();
        var sandbox = new FileBackedSandbox(new Dictionary<string, string>());

        var phases = new[]
        {
            AgentPromptPhase.Work,
            AgentPromptPhase.Rework,
            AgentPromptPhase.Audit,
            AgentPromptPhase.Merge,
            AgentPromptPhase.CheckAndAct,
        };

        for (var i = 0; i < phases.Length; i++)
        {
            var wrapper = new PromptPreprocessingAgentRunner(
                inner,
                chain,
                WorkItemId.New(),
                phases[i],
                i + 1,
                project);

            await wrapper.RunAsync(sandbox, "/work", $"prompt-{i}", credential: null);
        }

        Assert.Equal(phases, recorder.Contexts.Select(ctx => ctx.Phase).ToArray());
        Assert.Equal(["prompt-0|processed", "prompt-1|processed", "prompt-2|processed", "prompt-3|processed", "prompt-4|processed"], inner.RunPrompts);
    }

    [Fact]
    public async Task PromptPreprocessingAgentRunner_ProcessesTextOnlyPromptWhenSandboxIsAvailable()
    {
        var recorder = new RecordingPreprocessor();
        var chain = new AgentPromptPreprocessorChain([recorder]);
        var inner = new RecordingTextOnlyRunner();
        var sandbox = new FileBackedSandbox(new Dictionary<string, string>());
        var wrapper = new PromptPreprocessingAgentRunner(
            inner,
            chain,
            WorkItemId.New(),
            AgentPromptPhase.Merge,
            7,
            NewProject());

        await wrapper.RunTextOnlyAsync("review prompt", credential: null, sandbox: sandbox, workingDirectory: "/work");

        var ctx = Assert.Single(recorder.Contexts);
        Assert.Equal(AgentPromptPhase.Merge, ctx.Phase);
        Assert.Equal(7, ctx.Iteration);
        Assert.Equal("review prompt|processed", Assert.Single(inner.TextOnlyPrompts));
    }

    private static PromptContext NewContext(ISandbox? sandbox = null) =>
        new(
            WorkItemId.New(),
            AgentKind.Codex,
            AgentPromptPhase.Work,
            1,
            NewProject(),
            sandbox ?? new FileBackedSandbox(new Dictionary<string, string>()));

    private static Project NewProject() => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test Project",
        RepositoryUrl = "https://example.invalid/repo.git",
    };

    private sealed class AppendingPreprocessor : IAgentPromptPreprocessor
    {
        private readonly string _name;
        private readonly List<string> _log;

        public AppendingPreprocessor()
            : this("plugin", [], 0) { }

        public AppendingPreprocessor(string name, List<string> log, int order)
        {
            _name = name;
            _log = log;
            Order = order;
        }

        public int Order { get; }

        public Task<string> ProcessAsync(PromptContext ctx, string prompt, CancellationToken ct = default)
        {
            _ = ctx;
            _ = ct;
            _log.Add(_name);
            return Task.FromResult($"{prompt}|{_name}");
        }
    }

    [CodeyBoxPlugin("test.prompt-preprocessor", "Prompt Preprocessor")]
    public sealed class TestPluginPreprocessor : IAgentPromptPreprocessor
    {
        private readonly string _name;
        private readonly List<string> _log;

        public TestPluginPreprocessor()
            : this("plugin", [], 0) { }

        public TestPluginPreprocessor(string name, List<string> log, int order)
        {
            _name = name;
            _log = log;
            Order = order;
        }

        public int Order { get; }

        public Task<string> ProcessAsync(PromptContext ctx, string prompt, CancellationToken ct = default)
        {
            _ = ctx;
            _ = ct;
            _log.Add(_name);
            return Task.FromResult($"{prompt}|{_name}");
        }
    }

    private sealed class RecordingPreprocessor : IAgentPromptPreprocessor
    {
        public List<PromptContext> Contexts { get; } = [];

        public int Order => 0;

        public Task<string> ProcessAsync(PromptContext ctx, string prompt, CancellationToken ct = default)
        {
            _ = ct;
            Contexts.Add(ctx);
            return Task.FromResult($"{prompt}|processed");
        }
    }

    private sealed class RecordingTextOnlyRunner : ITextOnlyAgentRunner
    {
        public List<string> RunPrompts { get; } = [];
        public List<string> TextOnlyPrompts { get; } = [];

        public AgentKind Kind => AgentKind.Codex;

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = credential;
            _ = modelId;
            _ = reasoningMode;
            _ = ct;
            _ = stdoutChunkCallback;
            _ = captureStructuredStream;
            RunPrompts.Add(prompt);
            return Task.FromResult(new AgentResult(true, "ok", null, null));
        }

        public Task<TextOnlyAgentResult> RunTextOnlyAsync(
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            ISandbox? sandbox = null,
            string? workingDirectory = null)
        {
            _ = credential;
            _ = modelId;
            _ = reasoningMode;
            _ = ct;
            _ = sandbox;
            _ = workingDirectory;
            TextOnlyPrompts.Add(prompt);
            return Task.FromResult(new TextOnlyAgentResult(true, "ok", "{}", null));
        }
    }

    private sealed class FileBackedSandbox : ISandbox
    {
        private readonly IReadOnlyDictionary<string, string> _files;

        public FileBackedSandbox(IReadOnlyDictionary<string, string> files)
        {
            _files = files;
        }

        public string Id => "sandbox-test";

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            _ = ct;
            if (exec.Argv is ["cat", "--", var path] && _files.TryGetValue(path, out var content))
                return Task.FromResult(new SandboxExecResult(0, content, ""));

            return Task.FromResult(new SandboxExecResult(1, "", "not found"));
        }
    }

    private sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public MutableOptionsMonitor(T currentValue)
        {
            CurrentValue = currentValue;
        }

        public T CurrentValue { get; set; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
