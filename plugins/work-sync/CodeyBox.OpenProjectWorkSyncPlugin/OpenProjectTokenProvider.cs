namespace CodeyBox.OpenProjectWorkSyncPlugin;

/// <summary>
/// Resolves the OpenProject API token for REST calls. The token is read from
/// the env var named by <c>TokenEnvVar</c> on every call (no caching, so
/// host-side rotation propagates without a restart) and sent as
/// <c>Authorization: Bearer</c>, per the official API introduction (API
/// tokens as bearer tokens).
/// <para>Values come only from the injected environment reader (production:
/// process environment populated from the host credential chain). Raw values
/// are never logged and never appear in exception messages.</para>
/// </summary>
public sealed class OpenProjectTokenProvider
{
    private readonly Func<string, string?> _env;

    /// <param name="env">Environment reader; defaults to process environment.</param>
    public OpenProjectTokenProvider(Func<string, string?>? env = null)
    {
        _env = env ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>
    /// Returns the credential for OpenProject API calls as
    /// <c>Authorization: Bearer</c>. Throws <see
    /// cref="InvalidOperationException"/> when no credential is configured
    /// (operator misconfiguration — the message names the env var, never its
    /// value).
    /// </summary>
    public Task<OpenProjectCredential> GetCredentialAsync(
        OpenProjectWorkSyncOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var token = string.IsNullOrWhiteSpace(options.TokenEnvVar)
            ? null : _env(options.TokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"OpenProject work sync is not authenticated: environment variable " +
                $"'{options.TokenEnvVar}' must hold an API token. " +
                "Provision it from the host credential chain (vault agent, container secret).");
        return Task.FromResult(new OpenProjectCredential("Bearer", token));
    }
}

/// <summary>
/// Credential plus the header scheme OpenProject expects for it. A plain type,
/// not a record: the generated record <c>ToString</c> would print <see
/// cref="Value"/> — a live secret — into any log or exception text.
/// </summary>
public sealed class OpenProjectCredential
{
    public OpenProjectCredential(string scheme, string value)
    {
        Scheme = scheme;
        Value = value;
    }

    /// <summary>The Authorization header scheme (always <c>Bearer</c>).</summary>
    public string Scheme { get; }

    /// <summary>The token value — a secret; never log or interpolate it.</summary>
    public string Value { get; }
}
