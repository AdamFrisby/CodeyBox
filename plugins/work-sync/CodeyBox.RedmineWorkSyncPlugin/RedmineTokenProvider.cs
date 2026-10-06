namespace CodeyBox.RedmineWorkSyncPlugin;

/// <summary>
/// Resolves the Redmine API key for REST calls from the host credential
/// chain. The key is read from the environment variable named by
/// <see cref="RedmineWorkSyncOptions.ApiKeyEnvVar"/> on every call (no
/// caching, so host-side rotation propagates without a restart) and sent as
/// the <c>X-Redmine-API-Key</c> header — never as a <c>?key=</c> query
/// string, which would leak it into access logs.
/// <para>Values come only from the injected environment reader (production:
/// process environment populated from the host credential chain). Raw values
/// are never logged and never appear in exception messages.</para>
/// </summary>
public sealed class RedmineTokenProvider
{
    private readonly Func<string, string?> _env;

    /// <param name="env">Environment reader; defaults to process environment.</param>
    public RedmineTokenProvider(Func<string, string?>? env = null)
    {
        _env = env ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>
    /// Returns the configured API key. Throws <see
    /// cref="InvalidOperationException"/> when no key is configured
    /// (operator misconfiguration — the message names the env var, never its
    /// value).
    /// </summary>
    public string GetApiKey(RedmineWorkSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var name = options.ApiKeyEnvVar;
        var key = string.IsNullOrWhiteSpace(name) ? null : _env(name);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"Redmine work sync is not authenticated: environment variable " +
                $"'{name}' must hold a Redmine API key. " +
                "Provision it from the host credential chain (vault agent, container secret).");
        return key;
    }
}
