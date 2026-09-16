using CodeyBox.Agents.Aider;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Copilot;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Gemini;
using CodeyBox.Agents.Goose;
using CodeyBox.Agents.Opencode;
using CodeyBox.Agents.Pi;
using CodeyBox.Agents.Prime;
using CodeyBox.Agents.Vibe;
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
    [InlineData("copilot")]
    [InlineData("codex")]
    [InlineData("gemini")]
    [InlineData("antigravity")]
    public void VersionOnlyProbes_EmitSingleVersionStep_PinnedToRunnerBinary(string agent)
    {
        // Cross-check the probe's binary against the RUNNER's binary constant
        // (not a literal): if the probe and a literal-based test both encoded the
        // same wrong name, dispatch would still hit exit 127 — the exact failure
        // this feature targets. Pinning to the runner constant catches that drift.
        (IInVmSmokeProbe Probe, string RunnerBinary, AgentKind ExpectedKind) cases = agent switch
        {
            "claude" => (new ClaudeInVmSmokeProbe(), ClaudeAgentRunner.DefaultBinary, AgentKind.Claude),
            "copilot" => (new CopilotInVmSmokeProbe(), CopilotAgentRunner.DefaultBinary, AgentKind.Copilot),
            "codex" => (new CodexInVmSmokeProbe(), CodexAgentRunner.DefaultBinary, AgentKind.Codex),
            "gemini" => (new GeminiInVmSmokeProbe(), GeminiAgentRunner.DefaultBinary, AgentKind.Gemini),
            _ => (new AntigravityInVmSmokeProbe(), AntigravityAgentRunner.DefaultBinary, AgentKind.Antigravity),
        };

        Assert.Equal(cases.ExpectedKind, cases.Probe.Kind);

        foreach (var credential in new AgentCredential?[] { null, Cred(cases.Probe.Kind) })
        {
            var steps = cases.Probe.BuildSteps(credential);
            var step = Assert.Single(steps);
            Assert.Equal([cases.RunnerBinary, "--version"], step.Argv);
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
    public void Cursor_WithAuthEnv_EmitsVersionMaterialiseStatusTrust()
    {
        var steps = new CursorInVmSmokeProbe().BuildSteps(Cred(AgentKind.Cursor, "CODEYBOX_CURSOR_AUTH_JSON"));

        Assert.Equal(4, steps.Count);
        Assert.Equal([CursorAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
        // Probe must reuse the runner's exact materialisation script (PR #138).
        Assert.Equal(["bash", "-c", CursorAgentRunner.AuthMaterialiseScript], steps[1].Argv);
        Assert.Equal([CursorAgentRunner.DefaultBinary, "status"], steps[2].Argv);
        // Stage 3 — the trust-bearing prefix must be the SAME builder real
        // dispatch uses, so a dropped --trust regresses both paths together.
        Assert.Equal(
            CursorAgentRunner.WorkspaceTrustInvocationPrefix(CursorAgentRunner.DefaultBinary),
            steps[3].Argv);
        Assert.Contains(CursorAgentRunner.WorkspaceTrustFlag, steps[3].Argv);
        Assert.NotNull(steps[3].Stdin);
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

    [Fact]
    public void Pi_EmitsVersionPlusModeJsonAssertion_PinnedToRunnerBinary()
    {
        // Pi's probe has two steps: the --version binary check plus a
        // --mode assertion (the runner's only transport). Both pin to the
        // runner's binary constant so probe/runner drift fails loudly.
        var probe = new PiInVmSmokeProbe();
        Assert.Equal(AgentKind.Pi, probe.Kind);

        foreach (var credential in new AgentCredential?[] { null, Cred(AgentKind.Pi) })
        {
            var steps = probe.BuildSteps(credential);
            Assert.Equal(2, steps.Count);
            Assert.Equal([PiAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
            Assert.Contains(PiAgentRunner.DefaultBinary, string.Join(" ", steps[1].Argv));
            Assert.Contains("--mode", string.Join(" ", steps[1].Argv));
        }
    }

    [Fact]
    public void Aider_EmitsVersionPlusMessageAssertion_PinnedToRunnerBinary()
    {
        // Aider's probe has two steps: the --version binary check plus a
        // --message assertion (the runner's only transport). Both pin to the
        // runner's binary constant so probe/runner drift fails loudly.
        var probe = new AiderInVmSmokeProbe();
        Assert.Equal(AgentKind.Aider, probe.Kind);

        foreach (var credential in new AgentCredential?[] { null, Cred(AgentKind.Aider) })
        {
            var steps = probe.BuildSteps(credential);
            Assert.Equal(2, steps.Count);
            Assert.Equal([AiderAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
            Assert.Contains(AiderAgentRunner.DefaultBinary, string.Join(" ", steps[1].Argv));
            Assert.Contains("--message", string.Join(" ", steps[1].Argv));
        }
    }

    [Fact]
    public void Goose_EmitsVersionPlusOutputFormatAssertion_PinnedToRunnerBinary()
    {
        // Goose's probe has two steps: the --version binary check plus an
        // --output-format assertion (the runner's only transport). Both pin
        // to the runner's binary constant so probe/runner drift fails loudly.
        var probe = new GooseInVmSmokeProbe();
        Assert.Equal(AgentKind.Goose, probe.Kind);

        foreach (var credential in new AgentCredential?[] { null, Cred(AgentKind.Goose) })
        {
            var steps = probe.BuildSteps(credential);
            Assert.Equal(2, steps.Count);
            Assert.Equal([GooseAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
            Assert.Contains(GooseAgentRunner.DefaultBinary, string.Join(" ", steps[1].Argv));
            Assert.Contains("--output-format", string.Join(" ", steps[1].Argv));
        }
    }

    [Fact]
    public void Prime_EmitsVersionPlusPrintModeAssertion_PinnedToRunnerBinary()
    {
        // Prime's probe has two steps: the --version binary check plus a
        // -p/--print + --mode assertion (the runner's only transport). All
        // pin to the runner's binary constant so probe/runner drift fails
        // loudly.
        var probe = new PrimeInVmSmokeProbe();
        Assert.Equal(AgentKind.Prime, probe.Kind);

        foreach (var credential in new AgentCredential?[] { null, Cred(AgentKind.Prime) })
        {
            var steps = probe.BuildSteps(credential);
            Assert.Equal(2, steps.Count);
            Assert.Equal([PrimeAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
            var transport = string.Join(" ", steps[1].Argv);
            Assert.Contains(PrimeAgentRunner.DefaultBinary, transport);
            Assert.Contains("--print", transport);
            Assert.Contains("--mode", transport);
        }
    }

    [Fact]
    public void Vibe_EmitsVersionPlusOutputStreamingAssertion_PinnedToRunnerBinary()
    {
        // Vibe's probe has two steps: the --version binary check plus an
        // --output/streaming assertion (the runner's only transport). Both
        // pin to the runner's binary constant so probe/runner drift fails
        // loudly.
        var probe = new VibeInVmSmokeProbe();
        Assert.Equal(AgentKind.Vibe, probe.Kind);

        foreach (var credential in new AgentCredential?[] { null, Cred(AgentKind.Vibe) })
        {
            var steps = probe.BuildSteps(credential);
            Assert.Equal(2, steps.Count);
            Assert.Equal([VibeAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
            var transport = string.Join(" ", steps[1].Argv);
            Assert.Contains(VibeAgentRunner.DefaultBinary, transport);
            Assert.Contains("--output", transport);
            Assert.Contains("streaming", transport);
        }
    }
}
