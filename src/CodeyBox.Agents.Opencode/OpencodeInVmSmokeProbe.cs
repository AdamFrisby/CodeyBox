using CodeyBox.Core;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// In-VM smoke check for the opencode CLI:
/// <list type="number">
///   <item><c>opencode --version</c> — binary present on PATH (exit 127 otherwise).</item>
///   <item>auth materialised via the exact script the runner uses
///   (<see cref="OpencodeAgentRunner.AuthMaterialiseScript"/>), then
///   <c>opencode providers</c> must return 0 — proving the credential file is
///   in the right place and the CLI can enumerate its configured providers.</item>
/// </list>
///
/// <para>When the auth credential is absent — no credential bundle, or one
/// without <c>OPENCODE_AUTH_JSON</c> — the probe returns only the
/// binary-presence step (still exec'd by the prober), so a binary missing from
/// PATH is caught without a false auth-failure exclusion. See
/// <see cref="IInVmSmokeProbe"/>.</para>
/// </summary>
public sealed class OpencodeInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Opencode;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        var steps = new List<InVmSmokeStep>
        {
            new(
                [OpencodeAgentRunner.DefaultBinary, "--version"],
                FailureHint: "opencode binary not runnable on sandbox PATH"),
        };

        var hasAuth = credential is not null
            && credential.EnvironmentVariables.ContainsKey("OPENCODE_AUTH_JSON");
        if (hasAuth)
        {
            steps.Add(new(
                ["bash", "-c", OpencodeAgentRunner.AuthMaterialiseScript],
                FailureHint: "failed to materialise opencode auth.json"));
            steps.Add(new(
                [OpencodeAgentRunner.DefaultBinary, "providers"],
                FailureHint: "opencode providers failed (auth path drift or invalid token)"));
        }

        return steps;
    }
}
