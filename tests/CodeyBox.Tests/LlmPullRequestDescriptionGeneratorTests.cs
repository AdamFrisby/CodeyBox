using System.Text;
using CodeyBox.Core;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="LlmPullRequestDescriptionGenerator"/>.
/// Uses fakes for <see cref="IAgentRunner"/> and <see cref="ISandboxProvider"/>
/// so no real sandbox or LLM API is invoked.
/// </summary>
public sealed class LlmPullRequestDescriptionGeneratorTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static readonly PrDescriptionOptions DefaultOpts = new()
    {
        Enabled = true,
        GeneratorAgent = "claude",
        MaxDiffBytes = 32_768,
        Timeout = TimeSpan.FromSeconds(30),
        SandboxImageReference = "test-image",
    };

    private static readonly PullRequestDescriptionRequest SampleRequest = new()
    {
        DiffSummary = "src/Foo.cs | 10 ++++",
        FullDiff = "diff --git a/src/Foo.cs b/src/Foo.cs\n+added line",
        Title = "Add feature X",
        Prompt = "Implement feature X as described.",
        AddressedFindings = ["Missing null check", "Unused variable"],
        AgentReasoningTail = "I made the change because...",
    };

    private static LlmPullRequestDescriptionGenerator BuildGenerator(
        string agentResponse,
        PrDescriptionOptions? opts = null)
    {
        opts ??= DefaultOpts;
        var runner = new FixedOutputAgentRunner(agentResponse);
        var registry = new FakeSingleAgentRegistry(runner);
        var credentials = new NullCredentialProvider();
        var sandboxes = new InProcessFakeSandboxProvider();
        return new LlmPullRequestDescriptionGenerator(
            sandboxes, registry, credentials, opts,
            NullLogger<LlmPullRequestDescriptionGenerator>.Instance);
    }

    // -------------------------------------------------------------------------
    // Tests — basic generation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GenerateAsync_AgentReturnsText_ReturnsRedactedText()
    {
        var generator = BuildGenerator("This PR adds feature X.\n- Changed Foo.cs\n\n## Test plan\n- [ ] Verify feature X");
        var result = await generator.GenerateAsync(SampleRequest, CancellationToken.None);

        Assert.Contains("feature X", result);
        Assert.Contains("Test plan", result);
    }

    [Fact]
    public async Task GenerateAsync_AgentReturnsSecretToken_RedactsItFromOutput()
    {
        const string secret = "ghp_AAABBBCCC12345678";
        var generator = BuildGenerator($"Summary mentions token {secret} accidentally.");
        var result = await generator.GenerateAsync(SampleRequest, CancellationToken.None);

        Assert.DoesNotContain(secret, result);
        Assert.Contains("***", result);
    }

    [Fact]
    public async Task GenerateAsync_RequestShapePassedToAgent_ContainsTitleAndDiff()
    {
        var runner = new CapturingAgentRunner("Summary text");
        var registry = new FakeSingleAgentRegistry(runner);
        var generator = new LlmPullRequestDescriptionGenerator(
            new InProcessFakeSandboxProvider(), registry, new NullCredentialProvider(),
            DefaultOpts, NullLogger<LlmPullRequestDescriptionGenerator>.Instance);

        await generator.GenerateAsync(SampleRequest, CancellationToken.None);

        Assert.NotNull(runner.LastPrompt);
        Assert.Contains("Add feature X", runner.LastPrompt);
        Assert.Contains("src/Foo.cs", runner.LastPrompt);
        Assert.Contains("Missing null check", runner.LastPrompt);
    }

    // -------------------------------------------------------------------------
    // Tests — diff truncation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GenerateAsync_DiffExceedsMaxDiffBytes_TruncatesFromMiddle()
    {
        // Build a diff that is larger than MaxDiffBytes (128 bytes for this test).
        var opts = new PrDescriptionOptions
        {
            Enabled = true,
            GeneratorAgent = "claude",
            MaxDiffBytes = 128,
            Timeout = TimeSpan.FromSeconds(30),
            SandboxImageReference = "test-image",
        };
        var largeDiff = new string('A', 50) + "\n" + new string('B', 100) + "\n" + new string('C', 50);
        var request = SampleRequest with { FullDiff = largeDiff };

        var runner = new CapturingAgentRunner("ok");
        var registry = new FakeSingleAgentRegistry(runner);
        var generator = new LlmPullRequestDescriptionGenerator(
            new InProcessFakeSandboxProvider(), registry, new NullCredentialProvider(),
            opts, NullLogger<LlmPullRequestDescriptionGenerator>.Instance);

        await generator.GenerateAsync(request, CancellationToken.None);

        Assert.NotNull(runner.LastPrompt);
        // Truncation marker must appear in the prompt.
        Assert.Contains("truncated", runner.LastPrompt);
        // Start (A's) and end (C's) should still be visible.
        Assert.Contains("AAAA", runner.LastPrompt);
        Assert.Contains("CCCC", runner.LastPrompt);
    }

    [Fact]
    public void TruncateMiddle_SmallInput_ReturnsUnchanged()
    {
        const string input = "hello world";
        var result = LlmPullRequestDescriptionGenerator.TruncateMiddle(input, 1000);
        Assert.Equal(input, result);
    }

    [Fact]
    public void TruncateMiddle_LargeInput_PreservesStartAndEnd()
    {
        var start = new string('S', 100);
        var middle = new string('M', 200);
        var end = new string('E', 100);
        var input = start + middle + end;

        // Cap at 150 bytes — forces truncation of the middle.
        var result = LlmPullRequestDescriptionGenerator.TruncateMiddle(input, 150);

        Assert.True(result.Length < input.Length, "Result should be shorter than input");
        Assert.Contains("truncated", result);
        // Both extremes should be preserved.
        Assert.StartsWith("SS", result);
        Assert.EndsWith("EE", result);
    }

    [Fact]
    public void TruncateMiddle_ByteCountRespectsMaxBytes()
    {
        // Use a mix of ASCII and multi-byte UTF-8 characters.
        var input = string.Concat(Enumerable.Repeat("α", 200)); // each 'α' = 2 UTF-8 bytes → 400 bytes
        const int maxBytes = 100;
        var result = LlmPullRequestDescriptionGenerator.TruncateMiddle(input, maxBytes);

        var resultBytes = Encoding.UTF8.GetByteCount(result);
        // Result may be slightly over due to the marker, but the text portions must fit.
        Assert.True(resultBytes <= maxBytes + 50, $"Result ({resultBytes} bytes) far exceeds cap ({maxBytes} bytes)");
    }

    [Fact]
    public async Task GenerateAsync_AgentReturnsEmpty_Throws()
    {
        var generator = BuildGenerator(string.Empty);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            generator.GenerateAsync(SampleRequest, CancellationToken.None));
    }

    [Fact]
    public async Task GenerateAsync_UnknownAgentKind_Throws()
    {
        var opts = new PrDescriptionOptions
        {
            Enabled = true,
            GeneratorAgent = "unknown-agent",
            MaxDiffBytes = 32_768,
            Timeout = TimeSpan.FromSeconds(30),
            SandboxImageReference = "test-image",
        };
        var runner = new FixedOutputAgentRunner("text");
        // Registry does NOT have "unknown-agent"
        var registry = new FakeSingleAgentRegistry(runner); // runner is "claude"
        var generator = new LlmPullRequestDescriptionGenerator(
            new InProcessFakeSandboxProvider(), registry, new NullCredentialProvider(),
            opts, NullLogger<LlmPullRequestDescriptionGenerator>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            generator.GenerateAsync(SampleRequest, CancellationToken.None));
    }
}

// -------------------------------------------------------------------------
// Test fakes
// -------------------------------------------------------------------------

internal sealed class FixedOutputAgentRunner : IAgentRunner
{
    private readonly string _output;
    public FixedOutputAgentRunner(string output) => _output = output;
    public AgentKind Kind => AgentKind.Claude;
    public Task<AgentResult> RunAsync(ISandbox sandbox, string workingDirectory, string prompt,
        AgentCredential? credential, string? modelId = null, CancellationToken ct = default)
        => Task.FromResult(new AgentResult(!string.IsNullOrEmpty(_output), "ok", _output, null));
}

internal sealed class CapturingAgentRunner : IAgentRunner
{
    private readonly string _output;
    public string? LastPrompt { get; private set; }
    public CapturingAgentRunner(string output) => _output = output;
    public AgentKind Kind => AgentKind.Claude;
    public Task<AgentResult> RunAsync(ISandbox sandbox, string workingDirectory, string prompt,
        AgentCredential? credential, string? modelId = null, CancellationToken ct = default)
    {
        LastPrompt = prompt;
        return Task.FromResult(new AgentResult(true, "ok", _output, null));
    }
}

internal sealed class FakeSingleAgentRegistry : IAgentRegistry
{
    private readonly IAgentRunner _runner;
    public FakeSingleAgentRegistry(IAgentRunner runner) => _runner = runner;
    public IReadOnlyCollection<AgentKind> Available => [_runner.Kind];
    public bool TryGet(AgentKind kind, out IAgentRunner runner)
    {
        if (kind == _runner.Kind) { runner = _runner; return true; }
        runner = null!;
        return false;
    }
}

internal sealed class NullCredentialProvider : ICredentialProvider
{
    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        => Task.FromResult<AgentCredential?>(null);
}

/// <summary>Sandbox provider that returns a no-op sandbox without spawning any process.</summary>
internal sealed class InProcessFakeSandboxProvider : ISandboxProvider
{
    public string Name => "fake";
    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        => Task.FromResult<ISandbox>(new NoOpFakeSandbox());
}

internal sealed class NoOpFakeSandbox : ISandbox
{
    public string Id => "fake-sandbox";
    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        => Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
