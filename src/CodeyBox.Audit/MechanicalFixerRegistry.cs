using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Default mechanical fixer registry backed by DI.
/// </summary>
public sealed class MechanicalFixerRegistry : IMechanicalFixerRegistry
{
    public MechanicalFixerRegistry(IEnumerable<IMechanicalFixer> fixers)
    {
        All = fixers.ToList();
    }

    public IReadOnlyList<IMechanicalFixer> All { get; }
}
