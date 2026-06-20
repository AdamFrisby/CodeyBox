using CodeyBox.Core;

namespace CodeyBox.Projects;

/// <summary>
/// Resolves a project's configured deterministic mechanical fixers by name.
/// The pipeline consumes the composed list; adding another fixer is a DI
/// registration plus project config entry.
/// </summary>
public sealed class ProjectMechanicalFixerComposer
{
    private readonly IReadOnlyDictionary<string, IMechanicalFixer> _fixersByName;

    public ProjectMechanicalFixerComposer(IMechanicalFixerRegistry registry)
    {
        _fixersByName = registry.All.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
    }

    public static ProjectMechanicalFixerComposer FromFixers(IEnumerable<IMechanicalFixer> fixers)
        => new(new InlineMechanicalFixerRegistry(fixers));

    public void Validate(Project project, string? profile = null)
        => _ = Compose(project, profile);

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
                throw new ProjectMechanicalFixerConfigurationException(
                    $"Project '{project.Id.Value}' requested mechanical fixer '{name}', but that fixer is not registered.");
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

public sealed class ProjectMechanicalFixerConfigurationException : InvalidOperationException
{
    public ProjectMechanicalFixerConfigurationException(string message)
        : base(message)
    {
    }
}
