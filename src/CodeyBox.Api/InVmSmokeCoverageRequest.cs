using CodeyBox.Core;

namespace CodeyBox.Api;

/// <summary>
/// Builds the <see cref="InVmSmokeClassCoverage"/> list handed to
/// <see cref="IInVmSmokeCoveragePolicy.EnforceMissingProbeCoverage"/> from the
/// configured agent-class catalog. Shared by the startup coverage validator and
/// the hot-reload bridge so both enforce coverage over an identical set —
/// previously this projection was copy-pasted at both call sites and could
/// drift.
/// </summary>
internal static class InVmSmokeCoverageRequest
{
    public static List<InVmSmokeClassCoverage> FromAgentClasses(IEnumerable<AgentClassOptions> classes) =>
        classes
            .Select(c => new InVmSmokeClassCoverage(
                c.Id,
                c.Members.Select(m => m.Agent).Where(a => !string.IsNullOrWhiteSpace(a)).ToList()))
            .ToList();
}
