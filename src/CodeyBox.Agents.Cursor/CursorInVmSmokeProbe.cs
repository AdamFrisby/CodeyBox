using CodeyBox.Core;

namespace CodeyBox.Agents.Cursor;

/// <summary>
/// In-VM smoke check for the Cursor CLI. Catches the three-stage failure
/// cascade that the host-only <see cref="CursorSmokeProbe"/> could not see:
/// <list type="number">
///   <item><c>agent --version</c> — binary present on PATH (exit 127 otherwise).</item>
///   <item>auth materialised to <c>~/.config/cursor/auth.json</c> via the exact
///   same script the runner uses (<see cref="CursorAgentRunner.AuthMaterialiseScript"/>),
///   so a path drift like PR #138 is exercised here, not at first dispatch.</item>
///   <item><c>agent status</c> returns 0 — proves the materialised credential
///   is actually found and accepted by the CLI (it exits non-zero when the
///   credential is missing or unreadable, which is the #138 signal).</item>
///   <item>a real <c>agent --print --trust --force</c> turn against the
///   sandbox workspace returns 0 — stage 3 of the cascade ("Workspace Trust
///   Required"). The argv prefix is built from
///   <see cref="CursorAgentRunner.WorkspaceTrustInvocationPrefix"/>, the same
///   builder real dispatch uses, so if <c>--trust</c> were dropped this step
///   would hit the trust gate and exit non-zero at smoke time rather than
///   letting it cascade on first dispatch (AC#5).</item>
/// </list>
///
/// <para>When the auth credential is absent — either no credential bundle at
/// all, or one without <c>CODEYBOX_CURSOR_AUTH_JSON</c> — the probe returns only
/// the binary-presence step. The prober still execs it (see
/// <see cref="IInVmSmokeProbe.BuildSteps"/>), so a binary missing from PATH is
/// caught even before auth is configured, while a missing credential never
/// produces a false auth-failure exclusion (the host-side gate covers that).</para>
///
/// <para>The stage-3 trust turn engages workspace trust, which the
/// version / status commands cannot, so the full cascade is now caught at smoke
/// time. It is only emitted when auth is present (it makes a real, short
/// invocation that needs a materialised credential); a step timeout is treated
/// as transient (never benches), and a failure self-heals on the next sweep.
/// <c>CursorAgentRunnerTrustRegressionTests</c> remains as a fast argv-level
/// regression guard on the runner's own invocation.</para>
/// </summary>
public sealed class CursorInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Cursor;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        var steps = new List<InVmSmokeStep>
        {
            new(
                [CursorAgentRunner.DefaultBinary, "--version"],
                FailureHint: "agent binary not runnable on sandbox PATH"),
        };

        var hasAuth = credential is not null
            && credential.EnvironmentVariables.ContainsKey("CODEYBOX_CURSOR_AUTH_JSON");
        if (hasAuth)
        {
            steps.Add(new(
                ["bash", "-c", CursorAgentRunner.AuthMaterialiseScript],
                FailureHint: "failed to materialise cursor auth.json"));
            steps.Add(new(
                [CursorAgentRunner.DefaultBinary, "status"],
                FailureHint: "agent status failed (auth path drift or invalid token)"));
            // Stage 3 — a real workspace turn through the same trust-bearing
            // prefix dispatch uses. Catches "Workspace Trust Required" (exit 1)
            // at smoke time: if --trust regressed out of the shared prefix the
            // trust gate trips here and benches cursor before any work item is
            // dispatched (AC#5). Prompt is trivial — only the exit code matters.
            steps.Add(new(
                CursorAgentRunner.WorkspaceTrustInvocationPrefix(CursorAgentRunner.DefaultBinary),
                Stdin: "Reply with the single word: OK",
                FailureHint: "agent workspace turn failed (Workspace Trust Required — --trust regressed)"));
        }

        return steps;
    }
}
