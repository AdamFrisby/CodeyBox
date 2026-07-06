namespace CodeyBox.Orchestrator;

public sealed record MergeScopeHint(string Value, bool HighlightInResolverLog);

public interface IMergeScopeResolver
{
    MergeScopeHint Resolve(
        IReadOnlyDictionary<string, string>? itemKnobs,
        IReadOnlyDictionary<string, string>? projectKnobs);
}

internal sealed class NullMergeScopeResolver : IMergeScopeResolver
{
    public static NullMergeScopeResolver Instance { get; } = new();

    private NullMergeScopeResolver() { }

    public MergeScopeHint Resolve(
        IReadOnlyDictionary<string, string>? itemKnobs,
        IReadOnlyDictionary<string, string>? projectKnobs)
    {
        _ = itemKnobs;
        _ = projectKnobs;
        return new MergeScopeHint("moderate", HighlightInResolverLog: false);
    }
}
