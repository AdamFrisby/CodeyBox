namespace CodeyBox.Orchestrator;

/// <summary>
/// How the majordomo sandbox authenticates its model.
/// <list type="bullet">
/// <item><see cref="CodingAgentCli"/> — drive a coding-agent CLI already supported
/// in the sandbox baseline (see docs/concepts/agents.md). Subscription OAuth
/// bundles stay inside the CLI's own credential file layout.</item>
/// <item><see cref="ApiKey"/> — call a provider API with a genuine pay-per-use
/// API key. A subscription OAuth credential must never be used here: using it
/// against a raw HTTP API risks account termination.</item>
/// </list>
/// </summary>
public enum MajordomoModelBackend
{
    CodingAgentCli = 0,
    ApiKey = 1,
}

/// <summary>
/// Operator configuration for the long-lived majordomo sandbox.
/// Bound from <c>CodeyBox:MajordomoSandbox</c> and hot-reloadable: the session
/// reads the current value on every turn so a policy change applies to the
/// next turn without a restart.
/// </summary>
public sealed class MajordomoSandboxOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "CodeyBox:MajordomoSandbox";

    /// <summary>Default restricted network profile for the majordomo sandbox.</summary>
    public const string DefaultNetworkProfile = "majordomo";

    /// <summary>
    /// Conventional host bridge for <see cref="DefaultNetworkProfile"/>, created
    /// with <c>scripts/setup-host-networks.sh</c> (see docs/concepts/sandboxes.md).
    /// </summary>
    public const string DefaultBridgeName = "cb-majordomo";

    /// <summary>Default sandbox image baked with the majordomo baseline.</summary>
    public const string DefaultImageReference = "codeybox-majordomo";

    /// <summary>Default idle lifetime before the sandbox is torn down.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Shortest accepted idle timeout.</summary>
    public static readonly TimeSpan MinIdleTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Longest accepted idle timeout.</summary>
    public static readonly TimeSpan MaxIdleTimeout = TimeSpan.FromHours(8);

    /// <summary>Maximum configured external model endpoints.</summary>
    public const int MaxAdditionalAllowedHosts = 4;

    /// <summary>Maximum repository mounts.</summary>
    public const int MaxRepositories = 64;

    /// <summary>
    /// Host-side nftables network profile the majordomo sandbox attaches to.
    /// Required and non-blank: the sandbox is never created without enforced
    /// egress, and a missing profile fails closed instead of falling back to
    /// open egress. The host must define this profile in
    /// <c>/etc/codeybox/networks.conf</c> (see docs/concepts/sandboxes.md)
    /// with an allowlist covering only the orchestrator API host (plus
    /// <see cref="AdditionalAllowedHosts"/> when the model needs it).
    /// </summary>
    public string NetworkProfile { get; set; } = DefaultNetworkProfile;

    /// <summary>
    /// Hostname of the orchestrator API the sandbox may reach (MCP tool
    /// server). This is the only egress member always present. Must be a
    /// bare hostname (no scheme, port, or path).
    /// </summary>
    public string OrchestratorHost { get; set; } = "host.codeybox.internal";

    /// <summary>
    /// Optional extra egress endpoints for an external model API (for example
    /// <c>api.openai.com</c>). Empty by default: no public internet. Each entry
    /// must be a bare hostname and is the only addition beyond
    /// <see cref="OrchestratorHost"/> — endpoints are named in configuration,
    /// never implied by the backend choice.
    /// </summary>
    public List<string> AdditionalAllowedHosts { get; set; } = [];

    /// <summary>
    /// Bounded idle lifetime after which the sandbox is torn down and
    /// transparently recreated on the next turn. Within
    /// [<see cref="MinIdleTimeout"/>, <see cref="MaxIdleTimeout"/>].
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = DefaultIdleTimeout;

    /// <summary>
    /// Which model credential the sandbox carries. Configuration choice, not a
    /// hardcoded assumption.
    /// </summary>
    public MajordomoModelBackend ModelBackend { get; set; } = MajordomoModelBackend.CodingAgentCli;

    /// <summary>
    /// Agent kind driven when <see cref="ModelBackend"/> is
    /// <see cref="MajordomoModelBackend.CodingAgentCli"/> (for example
    /// <c>codex</c>). Must name a CLI already supported in the sandbox
    /// baseline. Ignored for <see cref="MajordomoModelBackend.ApiKey"/>.
    /// </summary>
    public string AgentKind { get; set; } = "codex";

    /// <summary>
    /// When <see cref="ModelBackend"/> is <see cref="MajordomoModelBackend.ApiKey"/>,
    /// whether a genuine pay-per-use API key is configured (the key itself lives
    /// in the host environment, never in this file). True is required for the
    /// ApiKey backend; a subscription OAuth bundle must not be substituted.
    /// </summary>
    public bool HasMeteredApiKey { get; set; }

    /// <summary>
    /// Project repository host paths mounted read-only into the sandbox for
    /// investigation. The sandbox never writes there — writing code is what
    /// work items are for.
    /// </summary>
    public List<string> RepositoryPaths { get; set; } = [];

    /// <summary>
    /// Sandbox image reference for the majordomo VM. Hot-reloadable: the next
    /// turn after an idle teardown boots this image.
    /// </summary>
    public string ImageReference { get; set; } = DefaultImageReference;

    /// <summary>Hot-reload validator: returns the failure message or null.</summary>
    public static string? Validate(MajordomoSandboxOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (string.IsNullOrWhiteSpace(opts.NetworkProfile))
            return $"{SectionName}:NetworkProfile must be a non-empty network profile name";
        if (opts.NetworkProfile.Any(char.IsWhiteSpace))
            return $"{SectionName}:NetworkProfile must not contain whitespace";
        if (!IsBareHostname(opts.OrchestratorHost))
            return $"{SectionName}:OrchestratorHost must be a bare hostname (no scheme, port, or path)";
        if (opts.AdditionalAllowedHosts.Count > MaxAdditionalAllowedHosts)
            return $"{SectionName}:AdditionalAllowedHosts must list at most {MaxAdditionalAllowedHosts} hosts";
        foreach (var host in opts.AdditionalAllowedHosts)
        {
            if (!IsBareHostname(host))
                return $"{SectionName}:AdditionalAllowedHosts entry '{host}' must be a bare hostname (no scheme, port, or path)";
        }
        if (opts.IdleTimeout < MinIdleTimeout || opts.IdleTimeout > MaxIdleTimeout)
            return $"{SectionName}:IdleTimeout must be within [{MinIdleTimeout}, {MaxIdleTimeout}]";
        if (!Enum.IsDefined(opts.ModelBackend))
            return $"{SectionName}:ModelBackend must be a defined {nameof(MajordomoModelBackend)}";
        if (opts.ModelBackend == MajordomoModelBackend.CodingAgentCli)
        {
            if (string.IsNullOrWhiteSpace(opts.AgentKind))
                return $"{SectionName}:AgentKind must name a supported coding-agent CLI when ModelBackend is CodingAgentCli";
        }
        else
        {
            if (!opts.HasMeteredApiKey)
                return $"{SectionName}:HasMeteredApiKey must be true when ModelBackend is ApiKey — a subscription OAuth credential must not be used against a raw HTTP API";
        }
        if (opts.RepositoryPaths.Count > MaxRepositories)
            return $"{SectionName}:RepositoryPaths must list at most {MaxRepositories} repositories";
        foreach (var repo in opts.RepositoryPaths)
        {
            if (string.IsNullOrWhiteSpace(repo))
                return $"{SectionName}:RepositoryPaths must not contain blank entries";
        }
        if (string.IsNullOrWhiteSpace(opts.ImageReference))
            return $"{SectionName}:ImageReference must be a non-empty sandbox image reference";
        return null;
    }

    internal static bool IsBareHostname(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var v = value.Trim();
        if (v.Contains("://", StringComparison.Ordinal) || v.Contains('/', StringComparison.Ordinal))
            return false;
        if (v.Contains(':'))
            return false;
        var host = v;
        if (host.Length > 253)
            return false;
        foreach (var c in host)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '-' || c == '.')
                continue;
            return false;
        }
        return !host.StartsWith(".", StringComparison.Ordinal)
            && !host.EndsWith(".", StringComparison.Ordinal)
            && !host.Contains("..", StringComparison.Ordinal);
    }
}
