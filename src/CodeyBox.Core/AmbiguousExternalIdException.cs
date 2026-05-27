namespace CodeyBox.Core;

/// <summary>
/// Thrown by bare external-ID lookups when the value matches a row in more
/// than one namespace within the same project. Callers should disambiguate
/// by using the namespaced lookup variant (<c>namespace:value</c>).
/// </summary>
public sealed class AmbiguousExternalIdException : Exception
{
    public string ExternalId { get; }
    public IReadOnlyList<string> Namespaces { get; }

    public AmbiguousExternalIdException(string externalId, IReadOnlyList<string> namespaces)
        : base($"external id '{externalId}' is ambiguous — matches in namespaces: {string.Join(", ", namespaces)}")
    {
        ExternalId = externalId;
        Namespaces = namespaces;
    }
}
