using CodeyBox.Agents.Devin;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DevinInVmSmokeProbe"/>: the step list must track the
/// runner's real dispatch contract (binary check always; auth materialisation
/// + authenticated <c>models list</c> + a real print-mode turn only when the
/// credential bundle carries the auth TOML).
/// </summary>
public sealed class DevinInVmSmokeProbeTests
{
    private static readonly DevinInVmSmokeProbe Probe = new();

    private static AgentCredential CredWithToml() =>
        new(AgentKind.Devin,
            new Dictionary<string, string>
            {
                [DevinAgentRunner.AuthTomlEnvironmentVariable] = "api_key = \"sk\"",
            },
            new Dictionary<string, string>());

    [Fact]
    public void Kind_IsDevin()
        => Assert.Equal(AgentKind.Devin, Probe.Kind);

    [Fact]
    public void BuildSteps_NoCredential_BinaryCheckOnly()
    {
        var steps = Probe.BuildSteps(credential: null);

        Assert.Single(steps);
        Assert.Equal(["devin", "--version"], steps[0].Argv.ToArray());
    }

    [Fact]
    public void BuildSteps_CredentialWithoutToml_BinaryCheckOnly()
    {
        var cred = new AgentCredential(AgentKind.Devin,
            new Dictionary<string, string> { ["UNRELATED"] = "x" },
            new Dictionary<string, string>());

        var steps = Probe.BuildSteps(cred);

        Assert.Single(steps);
    }

    [Fact]
    public void BuildSteps_WithToml_RunsAuthChainAndRealTurn()
    {
        var steps = Probe.BuildSteps(CredWithToml());

        Assert.Equal(4, steps.Count);
        Assert.Equal(["devin", "--version"], steps[0].Argv.ToArray());

        // Auth materialisation reuses the runner's exact script so the smoke
        // check exercises the same destination path as a real dispatch.
        Assert.Equal("bash", steps[1].Argv[0]);
        Assert.Equal(DevinAgentRunner.AuthMaterialiseScript, steps[1].Argv[2]);

        // `models list --format json` exits 1 unauthenticated —
        // `auth status` would pass even logged out, so it is not the check.
        Assert.Equal(["devin", "models", "list", "--format", "json"], steps[2].Argv.ToArray());

        // The turn exercises the same permission/trust contract as dispatch.
        Assert.Equal(
            ["devin", "-p", "--permission-mode", "dangerous",
             "--respect-workspace-trust", "false", "--prompt-file", "/dev/stdin"],
            steps[3].Argv.ToArray());
        Assert.NotNull(steps[3].Stdin);
    }
}
