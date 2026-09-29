namespace CodeyBox.Core;

/// <summary>
/// Derives the build/test-gate evidence a command line consumes. A
/// standalone <c>--no-build</c> token means the invocation reuses a prior
/// build's outputs (the <c>dotnet test</c>/<c>dotnet run</c> contract)
/// instead of producing its own, so the command depends on an earlier
/// build-evidence gate having passed in the same audit iteration.
/// </summary>
public static class GateEvidenceConsumption
{
    /// <summary>
    /// Returns <see cref="BuildTestGateEvidence.Build"/> when
    /// <paramref name="argv"/> carries a standalone <c>--no-build</c> token;
    /// otherwise <see cref="BuildTestGateEvidence.None"/>.
    /// </summary>
    public static BuildTestGateEvidence ForArgv(IReadOnlyList<string> argv)
        => argv.Any(static a => string.Equals(a, "--no-build", StringComparison.OrdinalIgnoreCase))
            ? BuildTestGateEvidence.Build
            : BuildTestGateEvidence.None;
}
