using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// In-VM smoke check for the devin CLI:
/// <list type="number">
///   <item><c>devin --version</c> — binary present on PATH (exit 127 otherwise).</item>
///   <item>auth materialised via the exact script the runner uses
///   (<see cref="DevinAgentRunner.AuthMaterialiseScript"/>), then
///   <c>devin models list --format json</c> must return 0 — this subcommand
///   exits 1 when unauthenticated (verified against devin 3000.11.1), proving
///   the credentials file landed where the CLI reads it AND the api_server_url
///   inside is reachable. <c>devin auth status</c> is deliberately NOT the
///   check: it prints "Not logged in." and still exits 0.</item>
///   <item>a real print-mode turn using the exact dispatch argv
///   (<see cref="DevinAgentRunner.FullAutonomyInvocationPrefix"/>) so the
///   workspace-trust and permission-mode contract is exercised end to end.</item>
/// </list>
///
/// <para>When the auth credential is absent — no credential bundle, or one
/// without <c>CODEYBOX_DEVIN_AUTH_TOML</c> — the probe returns only the
/// binary-presence step (still exec'd by the prober), so a binary missing from
/// PATH is caught without a false auth-failure exclusion. See
/// <see cref="IInVmSmokeProbe"/>.</para>
/// </summary>
public sealed class DevinInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Devin;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        var steps = new List<InVmSmokeStep>
        {
            new(
                [DevinAgentRunner.DefaultBinary, "--version"],
                FailureHint: "devin binary not runnable on sandbox PATH"),
        };

        var hasAuth = credential is not null
            && credential.EnvironmentVariables.ContainsKey(DevinAgentRunner.AuthTomlEnvironmentVariable);
        if (hasAuth)
        {
            steps.Add(new(
                ["bash", "-c", DevinAgentRunner.AuthMaterialiseScript],
                FailureHint: "failed to materialise devin credentials.toml"));
            steps.Add(new(
                [DevinAgentRunner.DefaultBinary, "models", "list", "--format", "json"],
                FailureHint: "devin models list failed (credentials path drift or invalid token)"));
            steps.Add(new(
                [.. DevinAgentRunner.FullAutonomyInvocationPrefix(DevinAgentRunner.DefaultBinary),
                    "--prompt-file", "/dev/stdin"],
                Stdin: "Reply with the single word: OK",
                FailureHint: "devin print-mode turn failed (workspace-trust or permission-mode contract drift)"));
        }

        return steps;
    }
}
