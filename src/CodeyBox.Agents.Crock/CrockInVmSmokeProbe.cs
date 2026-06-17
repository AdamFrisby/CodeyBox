using CodeyBox.Core;

namespace CodeyBox.Agents.Crock;

/// <summary>
/// In-VM smoke check for the crock CLI:
/// <list type="number">
///   <item><c>crock --version</c> — binary present on PATH.</item>
///   <item>auth materialised via the exact script the runner uses
///   (<see cref="CrockAgentRunner.ConfigMaterialiseScript"/>), then
///   <c>crock doctor</c> must return 0 — proving the config file is in the
///   right place and the daemon-backed CLI can reach Anthropic + the
///   tunnel.</item>
/// </list>
///
/// <para>When the credential is absent the probe runs only the
/// binary-presence step (still exec'd by the prober) so a binary missing
/// from PATH is caught without a false auth-failure exclusion. Mirrors
/// <c>OpencodeInVmSmokeProbe</c>'s shape.</para>
/// </summary>
public sealed class CrockInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Crock;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        var steps = new List<InVmSmokeStep>
        {
            new(
                [CrockAgentRunner.DefaultBinary, "--version"],
                FailureHint: "crock binary not runnable on sandbox PATH"),
        };

        var hasAuth = credential is not null
            && credential.EnvironmentVariables.ContainsKey(CrockAgentRunner.ConfigEnvVar);
        if (hasAuth)
        {
            steps.Add(new(
                ["bash", "-c", CrockAgentRunner.ConfigMaterialiseScript],
                FailureHint: "failed to materialise crock config.json"));
            steps.Add(new(
                [CrockAgentRunner.DefaultBinary, "doctor"],
                FailureHint: "crock doctor failed (auth path drift, daemon down, or tunnel unreachable)"));
        }

        return steps;
    }
}
