using CodeyBox.Core;

namespace CodeyBox.Agents.CavemanCode;

/// <summary>
/// In-VM smoke check for the caveman-code CLI:
/// <list type="number">
///   <item><c>caveman-code --version</c> — binary present on PATH (exit 127 otherwise).</item>
///   <item>When the credential bundle carries a provider API key,
///   <c>caveman-code --list-models</c> must exit 0 with a parseable model
///   table — proving the key reaches the CLI's environment and the model
///   registry resolves. Verified against 0.65.2: the registry answers
///   offline from env keys alone, but prints
///   <c>No models available. Set API keys in environment variables.</c>
///   (exit 0) when no key is visible, so the step asserts on parsed ids,
///   not just the exit code. See <see cref="CavemanCodeModelListProbe"/> for
///   the shared table parser.</item>
/// </list>
///
/// <para>When no provider key is configured the probe returns only the
/// binary-presence step (still exec'd by the prober), so a binary missing
/// from PATH is caught without a false auth-failure exclusion. See
/// <see cref="IInVmSmokeProbe"/>.</para>
/// </summary>
public sealed class CavemanCodeInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.CavemanCode;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        var steps = new List<InVmSmokeStep>
        {
            new(
                [CavemanCodeAgentRunner.DefaultBinary, "--version"],
                FailureHint: "caveman-code binary not runnable on sandbox PATH"),
        };

        if (HasProviderApiKey(credential))
        {
            steps.Add(new(
                [CavemanCodeAgentRunner.DefaultBinary, "--list-models"],
                FailureHint: "caveman-code --list-models failed (no provider API key visible in-VM or registry drift)"));
        }

        return steps;
    }

    private static bool HasProviderApiKey(AgentCredential? credential)
    {
        if (credential is null) return false;
        foreach (var variable in CavemanCodeAgentRunner.CredentialEnvironmentVariables)
        {
            if (credential.EnvironmentVariables.TryGetValue(variable, out var value)
                && !string.IsNullOrEmpty(value))
            {
                return true;
            }
        }

        return false;
    }
}
