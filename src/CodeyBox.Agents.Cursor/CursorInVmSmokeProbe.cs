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
/// </list>
///
/// <para>When the auth credential is absent — either no credential bundle at
/// all, or one without <c>CODEYBOX_CURSOR_AUTH_JSON</c> — the probe returns only
/// the binary-presence step. The prober still execs it (see
/// <see cref="IInVmSmokeProbe.BuildSteps"/>), so a binary missing from PATH is
/// caught even before auth is configured, while a missing credential never
/// produces a false auth-failure exclusion (the host-side gate covers that).</para>
///
/// <para>The "Workspace Trust Required" stage of the cascade is handled by the
/// runner always passing <c>--trust</c> (pinned by a regression test); the
/// version / status commands used here do not engage workspace trust.</para>
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
        }

        return steps;
    }
}
