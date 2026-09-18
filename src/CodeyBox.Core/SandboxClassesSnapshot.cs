namespace CodeyBox.Core;

/// <summary>
/// Shared, swappable holder for the validated sandbox class catalog.
/// Registered as a DI singleton so the placement path reads through one
/// reference. The hot-reload coordinator updates this holder via
/// <see cref="Replace"/>, and subsequent placements pick up the new catalog
/// without a process restart; a rejected edit keeps the prior catalog.
/// </summary>
/// <remarks>
/// Mirrors the <c>AgentDefaultsSnapshot</c> pattern: Volatile read/write so a
/// concurrent <see cref="Replace"/> cannot tear the reference; callers should
/// bind once into a local for any compound read. The work-phase sandbox
/// acquisition routes through this catalog; per-member admission gates are a
/// separate item and still live in the global admission wrapper.
/// </remarks>
public sealed class SandboxClassesSnapshot
{
    private IReadOnlyList<SandboxClass> _current;

    public SandboxClassesSnapshot(IReadOnlyList<SandboxClass> initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    public IReadOnlyList<SandboxClass> Current => Volatile.Read(ref _current);

    public void Replace(IReadOnlyList<SandboxClass> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }
}
