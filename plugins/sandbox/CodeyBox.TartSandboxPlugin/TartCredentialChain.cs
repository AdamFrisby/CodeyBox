namespace CodeyBox.TartSandboxPlugin;

/// <summary>
/// Resolves Tart guest SSH material from operator configuration plus the host
/// credential chain (process environment). The password comes <em>only</em>
/// from the configured environment variable — never from configuration files.
/// The username and key path are non-secret and live in configuration.
/// </summary>
public static class TartCredentialChain
{
    /// <summary>
    /// Resolves the guest SSH password for one operation. A test-injected
    /// <see cref="TartSandboxOptions.SshPassword"/> wins (tests only);
    /// production reads the configured environment variable at call time so
    /// host-side rotation propagates without a restart. The value is never
    /// logged and never copied into exception messages — keep it short-lived.
    /// </summary>
    /// <exception cref="InvalidOperationException">No password is available.</exception>
    public static string? ResolveSshPassword(TartSandboxOptions options, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (!string.IsNullOrWhiteSpace(options.SshPassword))
            return options.SshPassword;

        var variable = string.IsNullOrWhiteSpace(options.SshPasswordEnvVar)
            ? "TART_SSH_PASSWORD"
            : options.SshPasswordEnvVar.Trim();
        var value = (environment(variable) ?? string.Empty).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// Requires a resolved password, failing closed naming the variable the
    /// operator must provision. Key-based setups still call this only when
    /// they need password auth — callers that accept either path resolve the
    /// key first and require the password only as the fallback.
    /// </summary>
    /// <exception cref="InvalidOperationException">No password is available.</exception>
    public static string RequireSshPassword(TartSandboxOptions options, Func<string, string?> environment)
    {
        var password = ResolveSshPassword(options, environment);
        if (password is not null)
            return password;

        var variable = string.IsNullOrWhiteSpace(options.SshPasswordEnvVar)
            ? "TART_SSH_PASSWORD"
            : options.SshPasswordEnvVar.Trim();
        throw new InvalidOperationException(
            $"Tart guest SSH password environment variable '{variable}' is not set. " +
            "Provision it via the host credential chain — never in configuration files. " +
            "Password auth also needs sshpass on the host PATH; otherwise set " +
            $"CodeyBox:Plugins:{TartSandboxOptions.PluginId}:SshPrivateKeyPath to use key auth instead.");
    }

    /// <summary>
    /// True when key-based guest auth is configured (a non-blank key path).
    /// The path itself is non-secret; the key material stays in the host file.
    /// </summary>
    public static bool UsesKeyAuth(TartSandboxOptions options) =>
        options is not null && !string.IsNullOrWhiteSpace(options.SshPrivateKeyPath);
}
