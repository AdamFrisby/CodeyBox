namespace CodeyBox.AsanaWorkSyncPlugin;

/// <summary>
/// Resolves the Asana credential for REST calls. Asana authenticates both
/// personal access tokens and OAuth access tokens as <c>Authorization:
/// Bearer</c>, so one env var covers both: the operator provisions a PAT for
/// service integrations, or an OAuth access token minted through the
/// authorization-code flow. Asana offers no client-credentials grant, so
/// there is nothing to refresh in-process — the value is read from the
/// environment on every call, and host-side rotation propagates without a
/// restart.
/// <para>Values come only from the injected environment reader (production:
/// process environment populated from the host credential chain). Raw values
/// are never logged and never appear in exception messages.</para>
/// </summary>
public sealed class AsanaTokenProvider
{
    private readonly Func<string, string?> _env;

    /// <param name="env">Environment reader; defaults to process environment.</param>
    public AsanaTokenProvider(Func<string, string?>? env = null)
    {
        _env = env ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>
    /// Returns the credential for Asana API calls as
    /// <c>Authorization: Bearer</c> — the scheme is Bearer for both personal
    /// access tokens and OAuth access tokens. Throws <see
    /// cref="InvalidOperationException"/> when no credential is configured
    /// (operator misconfiguration — the message names the env var, never its
    /// value).
    /// </summary>
    public Task<AsanaCredential> GetCredentialAsync(AsanaWorkSyncOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        var token = string.IsNullOrWhiteSpace(options.TokenEnvVar)
            ? null
            : _env(options.TokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"Asana work sync is not authenticated: environment variable " +
                $"'{options.TokenEnvVar}' must hold a personal access token (or OAuth access token). " +
                "Provision it from the host credential chain (vault agent, container secret).");
        return Task.FromResult(new AsanaCredential("Bearer", token.Trim()));
    }
}

/// <summary>
/// Credential plus the header scheme Asana expects for it. A plain type,
/// not a record: the generated record <c>ToString</c> would print <see
/// cref="Value"/> — a live secret — into any log or exception text.
/// </summary>
public sealed class AsanaCredential
{
    public AsanaCredential(string scheme, string value)
    {
        Scheme = scheme;
        Value = value;
    }

    /// <summary>The Authorization header scheme (always <c>Bearer</c>).</summary>
    public string Scheme { get; }

    /// <summary>The token value — a secret; never log or interpolate it.</summary>
    public string Value { get; }
}
