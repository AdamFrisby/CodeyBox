using CodeyBox.Core;

namespace CodeyBox.Agents.Omp;

/// <summary>
/// In-VM smoke check for the omp CLI:
/// <list type="number">
/// <item><c>omp --version</c> — binary present on PATH (exit 127 otherwise).
/// This is the upstream oh-my-pi binary; the <c>oh-omp</c> fork ships a
/// different binary name and does not satisfy this check.</item>
/// <item><c>omp --help</c> must advertise <c>--mode</c>/<c>json</c> — the
/// runner's only transport. A build that dropped the JSON event stream
/// would otherwise dispatch into an unparseable run and fail late. The
/// check additionally pins the <c>-p/--print</c> one-shot flag: a bare
/// <c>omp "prompt"</c> is interactive and must never back a headless
/// dispatch.</item>
/// </list>
///
/// <para>No auth step: omp fronts ~60 providers with no single lightweight
/// "whoami", and any provider call would spend real quota. Credential
/// viability is covered host-side by <see cref="OmpSmokeProbe"/>; the first
/// real dispatch surfaces provider auth errors through
/// <see cref="OmpTerminalDiagnoser"/>. There is deliberately no quota-meter
/// probe either: <c>omp usage</c> only reports login-account balances
/// (verified: with an env-key-only credential it prints "No credentials
/// found"), not the env-key path the runner uses — so omp members fall
/// through to the <c>NullQuotaProbe</c> unknown path like pi/prime/goose
/// and the router gates on observed failures.</para>
/// </summary>
public sealed class OmpInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Omp;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [OmpAgentRunner.DefaultBinary, "--version"],
                FailureHint: "omp binary not runnable on sandbox PATH"),
            // Exit-code-only by InVmSmokeStep contract, so assert the
            // transport flag/value and the one-shot flag through grep's exit
            // code: the runner's only transport is the JSON event stream
            // under -p, and a build that dropped either must bench here
            // rather than dispatch into an unparseable or interactive run.
            new(
                ["bash", "-c", $"{OmpAgentRunner.DefaultBinary} --help | grep -q -- --mode && {OmpAgentRunner.DefaultBinary} --help | grep -q json && {OmpAgentRunner.DefaultBinary} --help | grep -q -- --print"],
                FailureHint: "omp --help does not advertise --mode json and -p/--print; the runner's transport and one-shot contract could not be verified"),
        ];
    }
}
