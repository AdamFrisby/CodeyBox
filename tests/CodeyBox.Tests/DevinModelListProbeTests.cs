using CodeyBox.Agents.Devin;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DevinModelListProbe"/> over a scripted
/// <see cref="IDevinCliRunner"/>: the real process surface (exit codes,
/// stderr) is what a failing CLI produces, so the probe is exercised through
/// it rather than around it.
/// </summary>
public sealed class DevinModelListProbeTests
{
    private sealed class ScriptedDevinCli : IDevinCliRunner
    {
        public int ExitCode { get; set; }
        public string Stdout { get; set; } = "[]";
        public string Stderr { get; set; } = "";
        public Exception? Failure { get; set; }
        public string? LastBinary { get; private set; }

        public Task<DevinCliRunResult> RunModelsListAsync(string binary, CancellationToken ct)
        {
            LastBinary = binary;
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(new DevinCliRunResult(ExitCode, Stdout, Stderr));
        }
    }

    [Fact]
    public void Kind_IsDevin()
        => Assert.Equal(AgentKind.Devin, new DevinModelListProbe(new ScriptedDevinCli()).Kind);

    [Fact]
    public async Task Probe_ClientModelConfigArray_YieldsIds()
    {
        // Shape from the devin 3000.11.1 binary: ClientModelConfig records
        // carry model_or_alias (protojson may emit modelOrAlias).
        var cli = new ScriptedDevinCli
        {
            Stdout = """
            [
              {"model_or_alias": "claude-sonnet-4", "is_recommended": true},
              {"modelOrAlias": "claude-opus-4.6"},
              {"model_id": "codex"}
            ]
            """,
        };
        var probe = new DevinModelListProbe(cli);

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Equal(["claude-sonnet-4", "claude-opus-4.6", "codex"], result.ModelIds);
        Assert.Equal("devin", cli.LastBinary);
    }

    [Fact]
    public async Task Probe_BareStringArray_Tolerated()
    {
        var cli = new ScriptedDevinCli { Stdout = """["claude-sonnet-4", "opus"]""" };
        var probe = new DevinModelListProbe(cli);

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Equal(["claude-sonnet-4", "opus"], result.ModelIds);
    }

    [Fact]
    public async Task Probe_WrapperObject_Tolerated()
    {
        var cli = new ScriptedDevinCli { Stdout = """{"models": [{"model_or_alias": "opus"}]}""" };
        var probe = new DevinModelListProbe(cli);

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Equal(["opus"], result.ModelIds);
    }

    [Fact]
    public async Task Probe_NonZeroExit_Fails()
    {
        // Unauthenticated: `devin models list` exits 1 with an Error line.
        var cli = new ScriptedDevinCli { ExitCode = 1, Stderr = "Error: Not logged in" };
        var probe = new DevinModelListProbe(cli);

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.ModelIds);
    }

    [Fact]
    public async Task Probe_BinaryNotFound_Fails()
    {
        var cli = new ScriptedDevinCli { ExitCode = 127, Stderr = "devin: command not found" };
        var probe = new DevinModelListProbe(cli);

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("devin CLI not found", result.FailureReason);
    }

    [Fact]
    public async Task Probe_LauncherThrowsENOENT_FailsAsNotFound()
    {
        var cli = new ScriptedDevinCli { Failure = new FileNotFoundException("devin") };
        var probe = new DevinModelListProbe(cli);

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("devin CLI not found", result.FailureReason);
    }

    [Fact]
    public async Task Probe_UnparseableOutput_Fails()
    {
        var cli = new ScriptedDevinCli { Stdout = "not json" };
        var probe = new DevinModelListProbe(cli);

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.ModelIds);
    }

    [Fact]
    public async Task Probe_BinaryOverride_Used()
    {
        var cli = new ScriptedDevinCli { Stdout = """["opus"]""" };
        var probe = new DevinModelListProbe(cli, binary: "/opt/devin/bin/devin");

        await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("/opt/devin/bin/devin", cli.LastBinary);
    }
}
