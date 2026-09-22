namespace CodeyBox.Majordomo;

/// <summary>
/// One entry in the closed majordomo tool vocabulary: a canonical
/// snake_case name, the typed argument contract, the typed result contract,
/// and the READ/MUTATE classification the authorization layer gates on.
///
/// The constructor is internal — descriptors exist only inside
/// <see cref="MajordomoTools"/>, so a tool outside the vocabulary cannot be
/// minted downstream.
/// </summary>
public sealed record MajordomoTool
{
    internal MajordomoTool(
        string name,
        MajordomoToolClass classification,
        Type argumentsType,
        Type resultType,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!typeof(MajordomoToolArgs).IsAssignableFrom(argumentsType))
            throw new ArgumentException(
                $"argumentsType {argumentsType?.Name ?? "<null>"} must derive from MajordomoToolArgs",
                nameof(argumentsType));
        if (!typeof(MajordomoToolResult).IsAssignableFrom(resultType))
            throw new ArgumentException(
                $"resultType {resultType?.Name ?? "<null>"} must derive from MajordomoToolResult",
                nameof(resultType));
        // The gate-relevant fact "is this tool a mutation" has one source of
        // truth: the argument contract. A Read label on mutate args (or vice
        // versa) would bypass or break the authorization layer, so a
        // disagreeing pair cannot be constructed.
        var carriesMutateContract = typeof(MajordomoMutateArgs).IsAssignableFrom(argumentsType);
        if (carriesMutateContract != (classification == MajordomoToolClass.Mutate))
            throw new ArgumentException(
                $"classification {classification} disagrees with arguments type {argumentsType.Name}: " +
                "a tool is a mutation iff its arguments derive from MajordomoMutateArgs",
                nameof(classification));

        Name = name;
        Class = classification;
        ArgumentsType = argumentsType;
        ResultType = resultType;
        Description = description;
    }

    /// <summary>Canonical wire name, matched ordinally (exact, case-sensitive).</summary>
    public string Name { get; }

    /// <summary>Whether the tool observes the queue or changes it.</summary>
    public MajordomoToolClass Class { get; }

    /// <summary>The one argument record type this tool accepts.</summary>
    public Type ArgumentsType { get; }

    /// <summary>The result type executions of this tool produce.</summary>
    public Type ResultType { get; }

    /// <summary>One-line summary suitable for operator docs and prompt rendering.</summary>
    public string Description { get; }
}
