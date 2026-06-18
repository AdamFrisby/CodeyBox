using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Projects;

/// <summary>
/// Resolves a project's configured deterministic mechanical fixers by name.
/// The pipeline consumes the composed list; adding another fixer is a DI
/// registration plus project config entry.
/// </summary>
public sealed class ProjectMechanicalFixerComposer
{
    private readonly IReadOnlyDictionary<string, IMechanicalFixer> _fixersByName;
    private readonly ILogger<ProjectMechanicalFixerComposer> _logger;

    public ProjectMechanicalFixerComposer(
        IMechanicalFixerRegistry registry,
        ILogger<ProjectMechanicalFixerComposer> logger)
    {
        _fixersByName = registry.All.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    public static ProjectMechanicalFixerComposer FromFixers(IEnumerable<IMechanicalFixer> fixers)
        => new(new InlineMechanicalFixerRegistry(fixers), NullLogger<ProjectMechanicalFixerComposer>.Instance);

    public IReadOnlyList<IMechanicalFixer> Compose(Project project, string? profile = null)
    {
        var audit = project.Audit.ResolveProfile(profile);
        var fixers = new List<IMechanicalFixer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var configuredName in audit.MechanicalFixers)
        {
            if (string.IsNullOrWhiteSpace(configuredName))
                continue;
            var name = configuredName.Trim();
            if (!seen.Add(name))
                continue;

            if (_fixersByName.TryGetValue(name, out var fixer))
            {
                fixers.Add(fixer);
            }
            else
            {
                _logger.LogWarning(
                    "Mechanical fixer '{FixerName}' was requested by project {ProjectId} but is not registered; skipping",
                    name,
                    project.Id.Value);
            }
        }

        return fixers;
    }

    private sealed class InlineMechanicalFixerRegistry : IMechanicalFixerRegistry
    {
        public InlineMechanicalFixerRegistry(IEnumerable<IMechanicalFixer> fixers)
        {
            All = fixers.ToList();
        }

        public IReadOnlyList<IMechanicalFixer> All { get; }
    }
}
