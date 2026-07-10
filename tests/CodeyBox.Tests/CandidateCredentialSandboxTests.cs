using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class CandidateCredentialSandboxTests
{
    [Fact]
    public async Task SameKindCandidates_ReceiveOnlyTheirOwnDirectCredential()
    {
        var inner = new EnvironmentRecordingSandbox();
        var runner = new EnvironmentPolicyRunner(
            AgentKind.Codex,
            direct: new HashSet<string>(StringComparer.Ordinal) { "OPENAI_API_KEY" },
            fileBacked: new HashSet<string>(StringComparer.Ordinal) { "CODEX_AUTH_JSON" });
        var first = CandidateCredentialSandbox.Create(
            inner,
            Candidate(runner, "first-account", "first-file-payload"));
        var second = CandidateCredentialSandbox.Create(
            inner,
            Candidate(runner, "second-account", "second-file-payload"));

        await first.ExecAsync(new SandboxExec { Argv = ["agent"] });
        await second.ExecAsync(new SandboxExec { Argv = ["agent"] });

        var firstEnvironment = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            inner.Execs[0].ExtraEnvironment);
        var secondEnvironment = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            inner.Execs[1].ExtraEnvironment);
        Assert.Equal("first-account", firstEnvironment["OPENAI_API_KEY"]);
        Assert.Equal("second-account", secondEnvironment["OPENAI_API_KEY"]);
        Assert.DoesNotContain("CODEX_AUTH_JSON", firstEnvironment.Keys);
        Assert.DoesNotContain("CODEX_AUTH_JSON", secondEnvironment.Keys);
        Assert.All(inner.Execs, exec => Assert.True(exec.EnvironmentContainsSecrets));
    }

    [Theory]
    [InlineData("X; touch /work/pwn #")]
    [InlineData("1INVALID")]
    [InlineData("LD_PRELOAD")]
    public void UntrustedOrReservedCredentialEnvironmentName_IsRejected(string name)
    {
        var runner = new EnvironmentPolicyRunner(
            AgentKind.Codex,
            direct: new HashSet<string>(StringComparer.Ordinal) { name },
            fileBacked: new HashSet<string>(StringComparer.Ordinal));
        var candidate = new AgenticConflictResolverCandidate(
            runner,
            new AgentCredential(
                AgentKind.Codex,
                new Dictionary<string, string> { [name] = "secret" },
                new Dictionary<string, string>()));

        Assert.Throws<ArgumentException>(() =>
            CandidateCredentialSandbox.Create(new EnvironmentRecordingSandbox(), candidate));
    }

    private static AgenticConflictResolverCandidate Candidate(
        IAgentRunner runner,
        string apiKey,
        string filePayload) =>
        new(
            runner,
            new AgentCredential(
                AgentKind.Codex,
                new Dictionary<string, string>
                {
                    ["OPENAI_API_KEY"] = apiKey,
                    ["CODEX_AUTH_JSON"] = filePayload,
                },
                new Dictionary<string, string>()));

    private sealed class EnvironmentPolicyRunner(
        AgentKind kind,
        IReadOnlySet<string> direct,
        IReadOnlySet<string> fileBacked) : IAgentRunner, IAgentCredentialEnvironmentPolicy
    {
        public AgentKind Kind { get; } = kind;
        public IReadOnlySet<string> DirectCredentialEnvironmentVariables { get; } = direct;
        public IReadOnlySet<string> FileBackedCredentialEnvironmentVariables { get; } = fileBacked;
        public IReadOnlyList<AgentCredentialFileDestination> CredentialFileDestinations => [];

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
            Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    private sealed class EnvironmentRecordingSandbox : ISandbox
    {
        public string Id => "environment-recording";
        public List<SandboxExec> Execs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
