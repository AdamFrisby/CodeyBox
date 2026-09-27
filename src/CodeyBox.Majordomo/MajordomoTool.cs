using System.Diagnostics.CodeAnalysis;

namespace CodeyBox.Majordomo;

/// <summary>
/// One entry in the closed majordomo tool vocabulary: a canonical
/// snake_case name, the typed argument contract, the typed result contract,
/// and the READ/MUTATE classification the authorization layer gates on.
///
/// The constructor is internal, so the catalog in
/// <see cref="MajordomoTools"/> is intended as the single source of
/// descriptors — off-catalog descriptors can only be minted from inside
/// this assembly, never by a transport or caller.
/// </summary>
public sealed record MajordomoTool
{
    internal MajordomoTool(
        string name,
        MajordomoToolClass classification,
        Type argumentsType,
        Type resultType,
        string description,
        bool idempotent = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
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
        Idempotent = idempotent;
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

    /// <summary>
    /// Whether replaying the call is a no-op (published as the MCP
    /// <c>IdempotentHint</c> annotation). Cancel qualifies: repeating it on an
    /// already-cancelled item succeeds without further mutation.
    /// </summary>
    public bool Idempotent { get; }

    /// <summary>
    /// Whether <paramref name="arguments"/> is exactly this tool's declared
    /// contract type — an exact runtime-type match, not assignability, so a
    /// derived payload cannot smuggle extra fields past the vocabulary's
    /// contract. The single check every caller pairing a name with a
    /// payload must pass.
    /// </summary>
    public bool AcceptsArguments([NotNullWhen(true)] MajordomoToolArgs? arguments) =>
        arguments is not null && arguments.GetType() == ArgumentsType;
}
