using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests.Uat.AgentRunnersAndCredentials;

internal sealed class RecordingSandbox : ISandbox
{
    private readonly int _bashExitCode;
    private readonly string _stdout;
    private readonly string _stderr;
    private readonly string? _stdoutChunk;

    public RecordingSandbox(
        string stdout = "stdout",
        string stderr = "stderr",
        string? stdoutChunk = null,
        string? helpOutput = null,
        int bashExitCode = 0)
    {
        _stdout = stdout;
        _stderr = stderr;
        _stdoutChunk = stdoutChunk;
        HelpOutput = helpOutput;
        _bashExitCode = bashExitCode;
    }

    public string Id => "uat-recording";
    public string? HelpOutput { get; }
    public List<SandboxExec> Execs { get; } = [];

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        Execs.Add(exec);
        if (HelpOutput is not null && exec.Argv.Contains("--help"))
            return Task.FromResult(new SandboxExecResult(0, HelpOutput, string.Empty));

        if (exec.Argv.Count > 0 && exec.Argv[0] == "bash")
            return Task.FromResult(new SandboxExecResult(_bashExitCode, string.Empty, _bashExitCode == 0 ? string.Empty : "bash failed"));

        if (_stdoutChunk is not null)
            exec.StdoutChunkCallback?.Invoke(_stdoutChunk);

        return Task.FromResult(new SandboxExecResult(0, _stdout, _stderr));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class UatCliRunner(IReadOnlyList<string>? scratchpadHomeDirectories = null) : CliAgentRunnerBase
{
    public override AgentKind Kind => new("uat-cli");

    protected override IReadOnlyList<string> ScratchpadHomeDirectories { get; } =
        scratchpadHomeDirectories ?? [".uat-agent/scratch"];

    protected override string PreemptProcessPattern => "uat-agent";

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        _ = credential;
        _ = modelId;
        _ = reasoningMode;
        _ = captureStructuredStream;
        return new AgentInvocation(
            ["sh", "-c", prompt],
            new Dictionary<string, string> { ["UAT_RUNNER_ENV"] = "present" });
    }

    protected override AgentInvocation BuildResumeInvocation(
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null)
    {
        _ = resume;
        return BuildInvocation(prompt, credential, modelId, reasoningMode);
    }
}

internal sealed class OrderedCredentialProvider(
    string id,
    List<string> calls,
    AgentCredential? credential = null) : ICredentialProvider
{
    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        _ = agent;
        _ = ct;
        calls.Add(id);
        return Task.FromResult(credential);
    }
}

internal sealed class CountingSmokeProbe(AgentKind kind) : IAgentSmokeProbe
{
    private readonly Queue<AgentSmokeResult> _results = new();

    public AgentKind Kind { get; } = kind;
    public int CallCount { get; private set; }

    public void Enqueue(AgentSmokeResult result) => _results.Enqueue(result);

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        _ = credential;
        _ = ct;
        CallCount++;
        return Task.FromResult(_results.Count == 0
            ? new AgentSmokeResult(true, null, TimeSpan.Zero)
            : _results.Dequeue());
    }
}
