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
