using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Gemini;
using CodeyBox.Agents.Opencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pins each <see cref="IInVmSmokeProbe.BuildSteps"/> sequence: the right binary
/// name (drift from the runner is the failure these probes catch), the
/// credential-independent <c>--version</c> step that always runs, and the
/// auth-gated branch for cursor / opencode. A wrong argv or a broken
/// credential-gate here would silently weaken the smoke contract.
/// </summary>
public sealed class InVmSmokeProbeBuildStepsTests
{
    private static AgentCredential Cred(AgentKind kind, params string[] envKeys)
    {
        var env = new Dictionary<string, string>();
        foreach (var k in envKeys) env[k] = "{\"token\":\"t\"}";
        return new AgentCredential(kind, env, new Dictionary<string, string>());
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public void VersionOnlyProbes_EmitSingleVersionStep_RegardlessOfCredential(string binary)
    {
        IInVmSmokeProbe probe = binary switch
        {
            "claude" => new ClaudeInVmSmokeProbe(),
            "codex" => new CodexInVmSmokeProbe(),
            _ => new GeminiInVmSmokeProbe(),
        };

        foreach (var credential in new AgentCredential?[] { null, Cred(probe.Kind) })
        {
            var steps = probe.BuildSteps(credential);
            var step = Assert.Single(steps);
            Assert.Equal([binary, "--version"], step.Argv);
        }
    }

    [Fact]
    public void Cursor_NoAuthEnv_EmitsOnlyVersionStep()
    {
        var probe = new CursorInVmSmokeProbe();

        // null credential, and a credential without the auth env key, both yield
        // version-only — a missing credential must not bench the binary check.
        foreach (var credential in new AgentCredential?[] { null, Cred(AgentKind.Cursor) })
        {
            var steps = probe.BuildSteps(credential);
            var step = Assert.Single(steps);
            Assert.Equal([CursorAgentRunner.DefaultBinary, "--version"], step.Argv);
        }
    }

    [Fact]
    public void Cursor_WithAuthEnv_EmitsVersionMaterialiseStatus()
    {
        var steps = new CursorInVmSmokeProbe().BuildSteps(Cred(AgentKind.Cursor, "CODEYBOX_CURSOR_AUTH_JSON"));

        Assert.Equal(3, steps.Count);
        Assert.Equal([CursorAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
        // Probe must reuse the runner's exact materialisation script (PR #138).
        Assert.Equal(["bash", "-c", CursorAgentRunner.AuthMaterialiseScript], steps[1].Argv);
        Assert.Equal([CursorAgentRunner.DefaultBinary, "status"], steps[2].Argv);
    }

    [Fact]
    public void Opencode_NoAuthEnv_EmitsOnlyVersionStep()
    {
        foreach (var credential in new AgentCredential?[] { null, Cred(AgentKind.Opencode) })
        {
            var steps = new OpencodeInVmSmokeProbe().BuildSteps(credential);
            var step = Assert.Single(steps);
            Assert.Equal([OpencodeAgentRunner.DefaultBinary, "--version"], step.Argv);
        }
    }

    [Fact]
    public void Opencode_WithAuthEnv_EmitsVersionMaterialiseProviders()
    {
        var steps = new OpencodeInVmSmokeProbe().BuildSteps(Cred(AgentKind.Opencode, "OPENCODE_AUTH_JSON"));

        Assert.Equal(3, steps.Count);
        Assert.Equal([OpencodeAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
        Assert.Equal(["bash", "-c", OpencodeAgentRunner.AuthMaterialiseScript], steps[1].Argv);
        Assert.Equal([OpencodeAgentRunner.DefaultBinary, "providers"], steps[2].Argv);
    }
}
