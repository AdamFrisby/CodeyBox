namespace CodeyBox.Core;

/// <summary>
/// Immutable identity of the authenticated person or service that initiated work.
/// The issuer and subject form the stable identity; display names and provider
/// logins are informational snapshots.
/// </summary>
public sealed record WorkInitiator
{
    public required string Issuer { get; init; }
    public required string Subject { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<WorkInitiatorProviderIdentity> ProviderIdentities { get; init; }
        = Array.Empty<WorkInitiatorProviderIdentity>();

    public WorkInitiatorProviderIdentity? FindProvider(string provider) =>
        ProviderIdentities.FirstOrDefault(identity =>
            string.Equals(identity.Provider, provider, StringComparison.OrdinalIgnoreCase));
}

public sealed record WorkInitiatorProviderIdentity
{
    public required string Provider { get; init; }
    public required string AccountId { get; init; }
    public required string Login { get; init; }
}

public static class GitHubIdentity
{
    public static bool IsValidLogin(string login) =>
        login.Length is > 0 and <= 39
        && login.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
        && login[0] != '-'
        && login[^1] != '-';

    public static bool TryNoreplyEmail(
        WorkInitiatorProviderIdentity identity,
        out string email)
    {
        email = string.Empty;
        if (!long.TryParse(
                identity.AccountId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var accountId)
            || accountId <= 0
            || !IsValidLogin(identity.Login))
            return false;
        email = $"{identity.AccountId}+{identity.Login}@users.noreply.github.com";
        return true;
    }
}
