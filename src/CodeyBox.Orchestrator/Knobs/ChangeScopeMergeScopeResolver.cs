using CodeyBox.Core;

namespace CodeyBox.Orchestrator.Knobs;

public sealed class ChangeScopeMergeScopeResolver : IMergeScopeResolver
{
    private readonly IKnobRegistry _registry;

    public ChangeScopeMergeScopeResolver(IKnobRegistry registry)
    {
        _registry = registry;
    }

    public MergeScopeHint Resolve(
        IReadOnlyDictionary<string, string>? itemKnobs,
        IReadOnlyDictionary<string, string>? projectKnobs)
    {
        var resolved = _registry.Resolve(itemKnobs, projectKnobs);
        var value = resolved.TryGetValue(ChangeScopeKnob.KeyName, out var raw)
            && !string.IsNullOrWhiteSpace(raw)
                ? raw
                : ChangeScopeKnob.ValueModerate;

        return new MergeScopeHint(
            value,
            string.Equals(value, ChangeScopeKnob.ValueSurgical, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, ChangeScopeKnob.ValueRefactor, StringComparison.OrdinalIgnoreCase));
    }
}
