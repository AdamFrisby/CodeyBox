using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.PluginSdk.Credentials;

/// <summary>
/// Shared configuration readers and operator-facing policy for
/// credential-provider plugins. One implementation so boolean/integer
/// parsing, loopback detection, the endpoint-URL acceptance rule, the
/// common operational-knob bounds, the enabled+validation gate, and
/// environment-variable-name validation cannot drift per backend.
/// </summary>
public static class CredentialOptions
{
    /// <summary>Smallest accepted <c>StaticLeaseTtlMinutes</c>.</summary>
    public const int MinStaticLeaseTtlMinutes = 1;

    /// <summary>Largest accepted <c>StaticLeaseTtlMinutes</c> (one day).</summary>
    public const int MaxStaticLeaseTtlMinutes = 1440;

    /// <summary>Smallest accepted per-request timeout, in seconds.</summary>
    public const int MinTimeoutSeconds = 1;

    /// <summary>Largest accepted per-request timeout, in seconds.</summary>
    public const int MaxTimeoutSeconds = 300;

    /// <summary>Smallest accepted <c>TokenRefreshSkewSeconds</c>.</summary>
    public const int MinTokenRefreshSkewSeconds = 0;

    /// <summary>Largest accepted <c>TokenRefreshSkewSeconds</c>.</summary>
    public const int MaxTokenRefreshSkewSeconds = 3600;

    /// <summary>Smallest accepted response/request body cap, in bytes.</summary>
    public const int MinBodyBytes = 1024;

    /// <summary>
    /// Maximum sandbox-variable length accepted in a mapping. Lease handles
    /// embed the variable name, and handles are capped at
    /// <see cref="LeaseHandles.MaxHandleLength"/>.
    /// </summary>
    public const int MaxSandboxEnvVarChars = 64;

    /// <summary>
    /// Reads an optional boolean; absent values fall back, unparseable
    /// values fall back with a warning surfaced to the operator.
    /// </summary>
    public static bool ReadBool(
        IConfigurationSection section, string key, bool fallback, List<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(key);
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!bool.TryParse(raw.Trim(), out var parsed))
        {
            warnings?.Add($"'{key}' value '{raw.Trim()}' is not a boolean; using {fallback}.");
            return fallback;
        }
        return parsed;
    }

    /// <summary>
    /// Reads an optional integer; absent values fall back, unparseable
    /// values fall back with a warning surfaced to the operator.
    /// </summary>
    public static int ReadInt(
        IConfigurationSection section, string key, int fallback, List<string>? warnings)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(key);
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            warnings?.Add($"'{key}' value '{raw.Trim()}' is not an integer; using {fallback}.");
            return fallback;
        }
        return parsed;
    }

    /// <summary>Reads an optional non-empty string; absent values fall back.</summary>
    public static string ReadNonEmpty(IConfigurationSection section, string key, string fallback)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fallback);
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    /// <summary>
    /// Reads and clamps the shared <c>StaticLeaseTtlMinutes</c> knob
    /// (<see cref="MinStaticLeaseTtlMinutes"/>–<see cref="MaxStaticLeaseTtlMinutes"/>).
    /// </summary>
    public static int ReadStaticLeaseTtlMinutes(
        IConfigurationSection section, int fallback, List<string>? warnings)
        => Math.Clamp(
            ReadInt(section, "StaticLeaseTtlMinutes", fallback, warnings),
            MinStaticLeaseTtlMinutes, MaxStaticLeaseTtlMinutes);

    /// <summary>
    /// Reads and clamps a per-request timeout knob, in seconds
    /// (<see cref="MinTimeoutSeconds"/>–<see cref="MaxTimeoutSeconds"/>).
    /// </summary>
    public static int ReadTimeoutSeconds(
        IConfigurationSection section, string key, int fallback, List<string>? warnings)
        => Math.Clamp(ReadInt(section, key, fallback, warnings), MinTimeoutSeconds, MaxTimeoutSeconds);

    /// <summary>
    /// Reads and clamps the shared <c>TokenRefreshSkewSeconds</c> knob
    /// (<see cref="MinTokenRefreshSkewSeconds"/>–<see cref="MaxTokenRefreshSkewSeconds"/>).
    /// </summary>
    public static int ReadTokenRefreshSkewSeconds(
        IConfigurationSection section, int fallback, List<string>? warnings)
        => Math.Clamp(
            ReadInt(section, "TokenRefreshSkewSeconds", fallback, warnings),
            MinTokenRefreshSkewSeconds, MaxTokenRefreshSkewSeconds);

    /// <summary>
    /// Reads a response/request body cap, in bytes, floored at
    /// <see cref="MinBodyBytes"/>.
    /// </summary>
    public static int ReadByteCap(
        IConfigurationSection section, string key, int fallback, List<string>? warnings)
        => Math.Max(ReadInt(section, key, fallback, warnings), MinBodyBytes);

    /// <summary>
    /// Clamps a configured per-request timeout (seconds) into a
    /// <see cref="TimeSpan"/> — the same bounds a bound option went through.
    /// </summary>
    public static TimeSpan TimeoutSpan(int seconds)
        => TimeSpan.FromSeconds(Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds));

    /// <summary>
    /// Clamps a configured token-refresh skew (seconds) into a
    /// <see cref="TimeSpan"/> — the same bounds a bound option went through.
    /// </summary>
    public static TimeSpan TokenSkewSpan(int seconds)
        => TimeSpan.FromSeconds(Math.Clamp(seconds, MinTokenRefreshSkewSeconds, MaxTokenRefreshSkewSeconds));

    /// <summary>
    /// Resolves a static-lease window: the configured TTL clamped to the
    /// shared range, tightened by a shorter requested TTL.
    /// </summary>
    public static TimeSpan StaticLeaseWindow(int configuredMinutes, TimeSpan requestedTtl)
    {
        var configured = TimeSpan.FromMinutes(
            Math.Clamp(configuredMinutes, MinStaticLeaseTtlMinutes, MaxStaticLeaseTtlMinutes));
        return requestedTtl > TimeSpan.Zero && requestedTtl < configured ? requestedTtl : configured;
    }

    /// <summary>True for loopback hosts allowed to serve plain HTTP (tests and local backends).</summary>
    public static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "::1", StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="uri"/> is an absolute https endpoint, or
    /// plain http on a loopback host — the only places credential-bearing
    /// traffic may go.
    /// </summary>
    public static bool IsCredentialEndpoint(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri
            && (uri.Scheme == Uri.UriSchemeHttps
                || (uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri.Host)));
    }

    /// <summary>
    /// The endpoint acceptance rule every configured credential URL shares —
    /// API origins, identity origins, broker upstreams — so the policy cannot
    /// drift per field or per backend: absolute http(s), and plain http only
    /// for loopback hosts (local development and tests). Appends
    /// operator-facing errors to <paramref name="errors"/>, prefixed by
    /// <paramref name="context"/> when one is given.
    /// </summary>
    public static void ValidateEndpointUrl(
        string field, string url, List<string> errors, string? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(errors);
        var prefix = string.IsNullOrEmpty(context) ? string.Empty : $"{context}: ";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            errors.Add($"{prefix}{field} '{url}' must be an absolute http(s) URL.");
            return;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(uri.Host))
            errors.Add($"{prefix}{field} '{url}' uses plain http against a non-loopback host; use https.");
    }

    /// <summary>True for valid environment-variable names.</summary>
    public static bool IsEnvVarName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        if (!(value[0] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_'))
            return false;
        foreach (var c in value.AsSpan(1))
        {
            if (!(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Validates a mapping's <c>SandboxEnvVar</c> — presence, length,
    /// POSIX-identifier shape, and uniqueness within <paramref name="seen"/> —
    /// appending operator-facing errors. Returns false when the variable is
    /// absent; the caller then skips the rest of that mapping's checks.
    /// <paramref name="label"/> always receives the operator-facing mapping
    /// label for follow-on messages.
    /// </summary>
    public static bool ValidateSandboxEnvVar(
        string? sandboxEnvVar, ISet<string> seen, List<string> errors, out string label)
    {
        ArgumentNullException.ThrowIfNull(seen);
        ArgumentNullException.ThrowIfNull(errors);
        label = string.IsNullOrWhiteSpace(sandboxEnvVar)
            ? "mapping with an empty SandboxEnvVar"
            : $"mapping for '{sandboxEnvVar}'";
        if (string.IsNullOrWhiteSpace(sandboxEnvVar))
        {
            errors.Add("A mapping has an empty SandboxEnvVar; every mapping must name its sandbox variable.");
            return false;
        }
        if (sandboxEnvVar.Length > MaxSandboxEnvVarChars)
            errors.Add($"{label}: SandboxEnvVar exceeds {MaxSandboxEnvVarChars} characters (lease handles embed it).");
        if (!IsEnvVarName(sandboxEnvVar))
            errors.Add($"{label}: SandboxEnvVar must be a POSIX identifier ([A-Za-z_][A-Za-z0-9_]*).");
        if (!seen.Add(sandboxEnvVar))
            errors.Add($"{label}: duplicate SandboxEnvVar; each sandbox variable maps once.");
        return true;
    }

    /// <summary>
    /// The enabled-and-valid gate every credential provider applies before
    /// issuing or renewing: a disabled plugin or an invalid configuration
    /// is a typed <see cref="CredentialFailureKind.Misconfigured"/>
    /// failure carrying the backend's own exception type, identical across
    /// backends. Returns the options for the caller to keep using.
    /// Revocation uses <see cref="RequireValid{TOptions}"/> instead —
    /// teardown must not be gated on the plugin still being enabled.
    /// </summary>
    public static TOptions RequireUsable<TOptions>(
        TOptions options, string backend, CredentialExceptionFactory exceptionFactory)
        where TOptions : ICredentialOptions
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        ArgumentNullException.ThrowIfNull(exceptionFactory);
        if (!options.Enabled)
        {
            throw exceptionFactory(
                CredentialFailureKind.Misconfigured,
                $"{backend} provider is disabled (Enabled=false); enable it to issue.",
                null, null, null);
        }
        return RequireValid(options, backend, exceptionFactory);
    }

    /// <summary>
    /// The teardown-side counterpart to <see cref="RequireUsable{TOptions}"/>:
    /// configuration must still be valid (a revocation cannot run against an
    /// unusable address either), but <see cref="ICredentialOptions.Enabled"/>
    /// does not apply — disabling a plugin is a natural operator response to
    /// a suspect backend and must not strand already-issued leases until
    /// their server-side expiry.
    /// </summary>
    public static TOptions RequireValid<TOptions>(
        TOptions options, string backend, CredentialExceptionFactory exceptionFactory)
        where TOptions : ICredentialOptions
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        ArgumentNullException.ThrowIfNull(exceptionFactory);
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw exceptionFactory(
                CredentialFailureKind.Misconfigured,
                $"{backend} configuration is invalid: {errors[0]}",
                null, null, null);
        }
        return options;
    }
}

/// <summary>
/// The contract every credential-plugin options record carries — the master
/// switch plus pure validation — so <see cref="CredentialOptions.RequireUsable"/>
/// can gate issuance identically across backends.
/// </summary>
public interface ICredentialOptions
{
    /// <summary>Master switch. Off unless an operator enables the plugin.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Pure validation: every operator-facing problem, or empty when usable.
    /// Never touches the network and never reads secret values — only names.
    /// </summary>
    IReadOnlyList<string> Validate();
}
