using System.Text;
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

    [Fact]
    public async Task PromptPreprocessingAgentRunner_SkipsChainWhenSandboxIsNull()
    {
        var recorder = new RecordingPreprocessor();
        var chain = new AgentPromptPreprocessorChain([recorder]);
        var inner = new RecordingTextOnlyRunner();
        var wrapper = new PromptPreprocessingAgentRunner(
            inner,
            chain,
            WorkItemId.New(),
            AgentPromptPhase.Audit,
            3,
            NewProject());

        var result = await wrapper.RunTextOnlyAsync("untouched prompt", credential: null, sandbox: null);

        Assert.True(result.Success);
        Assert.Empty(recorder.Contexts);
        Assert.Equal("untouched prompt", Assert.Single(inner.TextOnlyPrompts));
    }

    [Fact]
    public async Task PromptPreprocessingAgentRunner_ReturnsUnavailabilityWhenInnerIsNotTextOnly()
    {
        var recorder = new RecordingPreprocessor();
        var chain = new AgentPromptPreprocessorChain([recorder]);
        var inner = new RecordingPlainRunner();
        var sandbox = new FileBackedSandbox(new Dictionary<string, string>());
        var wrapper = new PromptPreprocessingAgentRunner(
            inner,
            chain,
            WorkItemId.New(),
            AgentPromptPhase.Merge,
            1,
            NewProject());

        Assert.False(wrapper.SupportsTextOnly);

        var result = await wrapper.RunTextOnlyAsync("prompt", credential: null, sandbox: sandbox);

        Assert.False(result.Success);
        Assert.Contains("not text-only capable", result.Summary);
        Assert.Empty(recorder.Contexts);
        var reason = wrapper.GetTextOnlyUnavailabilityReason(credential: null);
        Assert.NotNull(reason);
        Assert.Contains("not text-only capable", reason);
    }

    [Fact]
    public async Task ProjectRulesPreprocessor_TruncatesContentLargerThanCap()
    {
        var oversized = new string('a', (256 * 1024) + 10);
        var monitor = new MutableOptionsMonitor<AgentPromptPreprocessingOptions>(
            new() { ProjectRulesPath = "AGENTS.md" });
        var sandbox = new FileBackedSandbox(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENTS.md"] = oversized,
        });
        var preprocessor = new ProjectRulesPromptPreprocessor(
            monitor,
            NullLogger<ProjectRulesPromptPreprocessor>.Instance);

        var result = await preprocessor.ProcessAsync(NewContext(sandbox), "prompt");

        Assert.Contains("[Project rules truncated by CodeyBox at 256 KiB.]", result);
        // The injected rules section ends at the truncation marker; everything
        // between BEGIN and the marker is the cap-sized prefix of the input.
        var begin = result.IndexOf("--- BEGIN PROJECT RULES ---", StringComparison.Ordinal);
        Assert.True(begin >= 0);
        var bodyStart = begin + "--- BEGIN PROJECT RULES ---\n".Length;
        var markerStart = result.IndexOf("\n\n[Project rules truncated", bodyStart, StringComparison.Ordinal);
        Assert.True(markerStart > bodyStart);
        var rulesPrefix = result[bodyStart..markerStart];
        Assert.Equal(256 * 1024, Encoding.UTF8.GetByteCount(rulesPrefix));
    }

    [Fact]
    public async Task ProjectRulesPreprocessor_DoesNotTruncateContentAtOrBelowCap()
    {
        // Pick a size that puts every byte right under the cap and includes a
        // multi-byte UTF-8 character so we exercise the byte-vs-char distinction.
        var prefix = new string('a', (256 * 1024) - 4);
        var content = prefix + "€"; // euro sign = 3 UTF-8 bytes
        Assert.Equal((256 * 1024) - 1, Encoding.UTF8.GetByteCount(content));
        var monitor = new MutableOptionsMonitor<AgentPromptPreprocessingOptions>(
            new() { ProjectRulesPath = "AGENTS.md" });
        var sandbox = new FileBackedSandbox(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENTS.md"] = content,
        });
        var preprocessor = new ProjectRulesPromptPreprocessor(
            monitor,
            NullLogger<ProjectRulesPromptPreprocessor>.Instance);

        var result = await preprocessor.ProcessAsync(NewContext(sandbox), "prompt");

        Assert.DoesNotContain("[Project rules truncated", result);
        Assert.Contains("€", result);
    }

    [Fact]
    public async Task ProjectRulesPreprocessor_TruncatesContentSafelyAroundMultiByteChars()
    {
        // Fill content with euro signs (3 UTF-8 bytes each). The cap is not a
        // multiple of 3, so a naive char-index truncation would land inside a
        // surrogate-free multi-byte sequence; verify the result is still valid.
        var sb = new StringBuilder();
        while (Encoding.UTF8.GetByteCount(sb.ToString()) < (256 * 1024) + 1024)
            sb.Append('€');
        var monitor = new MutableOptionsMonitor<AgentPromptPreprocessingOptions>(
            new() { ProjectRulesPath = "AGENTS.md" });
        var sandbox = new FileBackedSandbox(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENTS.md"] = sb.ToString(),
        });
        var preprocessor = new ProjectRulesPromptPreprocessor(
            monitor,
            NullLogger<ProjectRulesPromptPreprocessor>.Instance);

        var result = await preprocessor.ProcessAsync(NewContext(sandbox), "prompt");

        Assert.Contains("[Project rules truncated by CodeyBox at 256 KiB.]", result);
        // Resulting string round-trips through UTF-8 without invalid sequences.
        var roundTripped = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(result));
        Assert.Equal(result, roundTripped);
    }

    [Fact]
    public async Task ProjectRulesPreprocessor_WhitespaceOnlyContentLeavesPromptUnchanged()
    {
        var monitor = new MutableOptionsMonitor<AgentPromptPreprocessingOptions>(
            new() { ProjectRulesPath = "AGENTS.md" });
        var sandbox = new FileBackedSandbox(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENTS.md"] = "   \n\t\n",
        });
        var preprocessor = new ProjectRulesPromptPreprocessor(
            monitor,
            NullLogger<ProjectRulesPromptPreprocessor>.Instance);

        Assert.Equal(
            "original",
            await preprocessor.ProcessAsync(NewContext(sandbox), "original"));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("foo\0bar.md")]
    [InlineData("   ")]
    [InlineData("nested/../escape.md")]
    public async Task ProjectRulesPreprocessor_RejectsUnsafePaths(string configuredPath)
    {
        var monitor = new MutableOptionsMonitor<AgentPromptPreprocessingOptions>(
            new() { ProjectRulesPath = configuredPath });
        // Populate the sandbox with content the preprocessor would otherwise inject,
        // so a regression that lets the path through would show up as injected rules.
        var sandbox = new FileBackedSandbox(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [configuredPath] = "should never be injected",
            ["etc/passwd"] = "should never be injected",
            ["nested/../escape.md"] = "should never be injected",
        });
        var preprocessor = new ProjectRulesPromptPreprocessor(
            monitor,
            NullLogger<ProjectRulesPromptPreprocessor>.Instance);

        Assert.Equal(
            "original",
            await preprocessor.ProcessAsync(NewContext(sandbox), "original"));
    }

    [Fact]
    public async Task ProjectRulesPreprocessor_NormalizesBackslashesToForwardSlashes()
    {
        var monitor = new MutableOptionsMonitor<AgentPromptPreprocessingOptions>(
            new() { ProjectRulesPath = "docs\\agents.md" });
        var sandbox = new FileBackedSandbox(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["docs/agents.md"] = "win-style path resolves\n",
        });
        var preprocessor = new ProjectRulesPromptPreprocessor(
            monitor,
            NullLogger<ProjectRulesPromptPreprocessor>.Instance);

        var result = await preprocessor.ProcessAsync(NewContext(sandbox), "prompt");

        Assert.Contains("Loaded from `docs/agents.md`.", result);
        Assert.Contains("win-style path resolves", result);
    }

    [Fact]
    public async Task Chain_ThrowsWhenPreprocessorReturnsNull()
    {
        var chain = new AgentPromptPreprocessorChain([new NullReturningPreprocessor()]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => chain.ProcessAsync(NewContext(), "prompt"));

        Assert.Contains(nameof(NullReturningPreprocessor), ex.Message);
    }

    [Fact]
    public async Task Chain_OrdersMultiplePluginsByOrderInPluginBand()
    {
        var log = new List<string>();
        var chain = new AgentPromptPreprocessorChain(
        [
            new TestPluginPreprocessor("plugin-late", log, order: 50),
            new TestPluginPreprocessor("plugin-early", log, order: -50),
            new AppendingPreprocessor("built-in-first", log, AgentPromptPreprocessorOrder.BuiltInFirst),
            new AppendingPreprocessor("built-in-last", log, AgentPromptPreprocessorOrder.BuiltInLast),
        ]);

        var result = await chain.ProcessAsync(NewContext(), "prompt");

        Assert.Equal(["built-in-first", "plugin-early", "plugin-late", "built-in-last"], log);
        Assert.Equal("prompt|built-in-first|plugin-early|plugin-late|built-in-last", result);
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

    private sealed class RecordingPlainRunner : IAgentRunner
    {
        public List<string> RunPrompts { get; } = [];

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
    }

    private sealed class NullReturningPreprocessor : IAgentPromptPreprocessor
    {
        public int Order => 0;

        public Task<string> ProcessAsync(PromptContext ctx, string prompt, CancellationToken ct = default)
        {
            _ = ctx;
            _ = prompt;
            _ = ct;
            return Task.FromResult<string>(null!);
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
