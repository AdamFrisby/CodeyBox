using CodeyBox.Core;
using CodeyBox.Upstream;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Projects;

/// <summary>
/// Default <see cref="IUpstreamRemoteFactory"/>: reads each project's
/// upstream config and constructs the matching IUpstreamRemote with the
/// per-project token resolved from the env var named in
/// <see cref="ProjectUpstream.TokenEnvVar"/>.
///
/// Tokens are read here, never persisted. A given orchestrator process
/// reads each project's token once per work item; rotating the env var
/// before the next work item is the rotation path.
///
/// Built-in kinds (noop, github, git-generic) always take precedence over
/// any plugin remote with the same Name. Collisions are logged as warnings
/// and the colliding plugin is silently unreachable for that kind.
/// </summary>
public sealed class UpstreamRemoteFactory : IUpstreamRemoteFactory
{
    private static readonly HashSet<string> _builtInKinds = new(StringComparer.OrdinalIgnoreCase)
        { "noop", "github", "git-generic" };

    private readonly IGitHost _gitHost;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubUpstreamRemote> _githubLog;
    private readonly ITimingStore? _timings;
    private readonly ISandboxProvider _sandboxes;
    private readonly IAgentRegistry _agents;
    private readonly ICredentialProvider _credentials;
    private readonly ILogger<LlmPullRequestDescriptionGenerator> _generatorLog;
    private readonly IReadOnlyList<IUpstreamRemote> _pluginRemotes;
    private readonly ILogger<UpstreamRemoteFactory>? _factoryLog;
    private readonly GitHubAppStore? _githubApps;
    private readonly ICompletionClient? _completionClient;
    private readonly ILogger<CompletionPullRequestDescriptionGenerator>? _completionLog;

    public UpstreamRemoteFactory(
        IGitHost gitHost,
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubUpstreamRemote> githubLog,
        ISandboxProvider sandboxes,
        IAgentRegistry agents,
        ICredentialProvider credentials,
        ILogger<LlmPullRequestDescriptionGenerator> generatorLog,
        IEnumerable<IUpstreamRemote>? pluginRemotes = null,
        ILogger<UpstreamRemoteFactory>? factoryLog = null,
        ITimingStore? timings = null,
        GitHubAppStore? githubApps = null,
        ICompletionClient? completionClient = null,
        ILogger<CompletionPullRequestDescriptionGenerator>? completionLog = null)
    {
        _gitHost = gitHost;
        _httpClientFactory = httpClientFactory;
        _githubLog = githubLog;
        _sandboxes = sandboxes;
        _agents = agents;
        _credentials = credentials;
        _generatorLog = generatorLog;
        _timings = timings;
        _factoryLog = factoryLog;
        _githubApps = githubApps;
        _completionClient = completionClient;
        _completionLog = completionLog;

        var remotes = new List<IUpstreamRemote>();
        foreach (var remote in pluginRemotes ?? [])
        {
            if (_builtInKinds.Contains(remote.Name))
            {
                factoryLog?.LogWarning(
                    "Plugin upstream remote '{Name}' ({Type}) conflicts with a built-in kind " +
                    "and will not be reachable; built-in remotes always take precedence over plugins",
                    remote.Name, remote.GetType().Name);
            }
            else
            {
                remotes.Add(remote);
            }
        }
        _pluginRemotes = remotes;
    }

    public IUpstreamRemote Create(Project project)
    {
        var u = project.Upstream;
        var kind = u.Kind?.ToLowerInvariant() ?? "noop";

        // Built-in remotes always win — checked before the plugin registry.
        switch (kind)
        {
            case "noop":
                return new NoopUpstreamRemote();

            case "github":
                return new GitHubUpstreamRemote(_gitHost, _httpClientFactory, _githubLog, new GitHubUpstreamOptions
                {
                    Owner = u.GitHubOwner ?? throw new InvalidOperationException(
                        $"Project {project.Id}: Upstream.Kind=github requires GitHubOwner"),
                    Repository = u.GitHubRepository ?? throw new InvalidOperationException(
                        $"Project {project.Id}: Upstream.Kind=github requires GitHubRepository"),
                    Token = HasGitHubAppConfiguration(u) || !string.IsNullOrWhiteSpace(u.GitHubAppSlug)
                        ? null
                        : ReadToken(project, u),
                    TokenProvider = BuildGitHubAppTokenProvider(project, u),
                    MergeMethod = ValidateMergeMethod(project.Id, u.MergeMethod),
                    AutoMerge = u.AutoMerge,
                    PullRequestTitleTemplate = u.PullRequestTitleTemplate,
                    PrDescription = MapPrDescriptionOptions(u.PrDescription),
                }, _timings, BuildDescriptionGenerator(u.PrDescription));

            case "git-generic":
                return new GitGenericUpstreamRemote(_gitHost, new GitGenericUpstreamOptions
                {
                    UpstreamUrl = u.GenericUrl ?? throw new InvalidOperationException(
                        $"Project {project.Id}: Upstream.Kind=git-generic requires GenericUrl"),
                });
        }

        // Plugin registry — look up by Name after the built-in cases.
        foreach (var remote in _pluginRemotes)
        {
            if (string.Equals(remote.Name, kind, StringComparison.OrdinalIgnoreCase))
                return remote;
        }

        // Nothing matched — throw a helpful error listing all known kinds.
        var allKinds = _builtInKinds.Concat(_pluginRemotes.Select(r => r.Name)).Distinct();
        throw new InvalidOperationException(
            $"Project {project.Id}: unknown upstream kind '{u.Kind}'. " +
            $"Available kinds: {string.Join(", ", allKinds)}");
    }

    private IGitHubTokenProvider? BuildGitHubAppTokenProvider(Project project, ProjectUpstream upstream)
    {
        if (!string.IsNullOrWhiteSpace(upstream.GitHubAppSlug))
        {
            if (HasGitHubAppConfiguration(upstream) || !string.IsNullOrWhiteSpace(upstream.TokenEnvVar))
                throw new InvalidOperationException(
                    $"Project {project.Id}: GitHubAppSlug cannot be combined with other credential settings.");
            var app = _githubApps?.Get(upstream.GitHubAppSlug)
                ?? throw new InvalidOperationException(
                    $"Project {project.Id}: linked GitHub App '{upstream.GitHubAppSlug}' was not found.");
            if (app.InstallationId <= 0)
                throw new InvalidOperationException(
                    $"Project {project.Id}: linked GitHub App '{upstream.GitHubAppSlug}' is not installed.");
            return new GitHubAppTokenProvider(
                _httpClientFactory,
                new GitHubAppTokenOptions(app.AppId, app.InstallationId, app.PrivateKeyPath));
        }
        if (!HasGitHubAppConfiguration(upstream))
            return null;
        if (!string.IsNullOrWhiteSpace(upstream.TokenEnvVar))
            throw new InvalidOperationException(
                $"Project {project.Id}: configure either TokenEnvVar or GitHub App credentials, not both.");
        if (string.IsNullOrWhiteSpace(upstream.GitHubAppIdEnvVar)
            || string.IsNullOrWhiteSpace(upstream.GitHubAppInstallationIdEnvVar)
            || string.IsNullOrWhiteSpace(upstream.GitHubAppPrivateKeyPathEnvVar))
            throw new InvalidOperationException(
                $"Project {project.Id}: GitHub App delivery requires all three GitHubApp*EnvVar settings.");

        var appId = ReadPositiveInt64EnvironmentVariable(project, upstream.GitHubAppIdEnvVar);
        var installationId = ReadPositiveInt64EnvironmentVariable(
            project, upstream.GitHubAppInstallationIdEnvVar);
        var keyPath = Environment.GetEnvironmentVariable(upstream.GitHubAppPrivateKeyPathEnvVar);
        if (string.IsNullOrWhiteSpace(keyPath))
            throw new InvalidOperationException(
                $"Project {project.Id}: env var '{upstream.GitHubAppPrivateKeyPathEnvVar}' is empty.");
        return new GitHubAppTokenProvider(
            _httpClientFactory,
            new GitHubAppTokenOptions(appId, installationId, keyPath));
    }

    private static bool HasGitHubAppConfiguration(ProjectUpstream upstream) =>
        !string.IsNullOrWhiteSpace(upstream.GitHubAppIdEnvVar)
        || !string.IsNullOrWhiteSpace(upstream.GitHubAppInstallationIdEnvVar)
        || !string.IsNullOrWhiteSpace(upstream.GitHubAppPrivateKeyPathEnvVar);

    private static long ReadPositiveInt64EnvironmentVariable(Project project, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!long.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            || parsed <= 0)
            throw new InvalidOperationException(
                $"Project {project.Id}: env var '{name}' must contain a positive integer.");
        return parsed;
    }

    private static string ValidateMergeMethod(ProjectId projectId, string mergeMethod)
    {
        if (mergeMethod is "merge" or "squash" or "rebase")
            return mergeMethod;
        throw new InvalidOperationException(
            $"Project {projectId}: Upstream.MergeMethod '{mergeMethod}' is invalid; " +
            "valid values: merge, squash, rebase");
    }

    private static string ReadToken(Project project, ProjectUpstream u)
    {
        if (string.IsNullOrWhiteSpace(u.TokenEnvVar))
            throw new InvalidOperationException(
                $"Project {project.Id}: Upstream.Kind=github requires TokenEnvVar to name an env var holding the token");
        var token = Environment.GetEnvironmentVariable(u.TokenEnvVar);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException(
                $"Project {project.Id}: env var '{u.TokenEnvVar}' is empty (set it to the GitHub PAT)");
        // Log the env var NAME only — never the token value.
        AuditLog.TokenRead(u.TokenEnvVar, project.Id);
        return token;
    }

    private static PrDescriptionOptions MapPrDescriptionOptions(ProjectPrDescription pd) =>
        new()
        {
            Enabled = pd.Enabled,
            Strategy = pd.Strategy,
            GeneratorAgent = pd.GeneratorAgent,
            GeneratorModelId = pd.GeneratorModelId,
            MaxDiffBytes = pd.MaxDiffBytes,
            Timeout = pd.Timeout,
            SandboxImageReference = pd.SandboxImageReference,
            AgentAllowedHosts = pd.AgentAllowedHosts,
            CompletionEndpoint = pd.CompletionEndpoint,
            CompletionModel = pd.CompletionModel,
            CompletionWireApi = pd.CompletionWireApi,
            CompletionApiKey = pd.CompletionApiKey,
            CompletionApiKeyEnvVars = pd.CompletionApiKeyEnvVars,
            CompletionMaxOutputTokens = pd.CompletionMaxOutputTokens,
            CompletionAnthropicVersion = pd.CompletionAnthropicVersion,
        };

    /// <summary>
    /// Builds the description generator for the selected strategy. Availability
    /// is evaluated against the selected strategy's own requirement — a
    /// configured completion endpoint for the completion strategy, a
    /// <c>SandboxImageReference</c> for the agentic strategy — so selecting one
    /// strategy is never disabled by the other's missing configuration. The
    /// completion strategy additionally requires a completion client.
    /// </summary>
    private IPullRequestDescriptionGenerator? BuildDescriptionGenerator(ProjectPrDescription pd)
    {
        if (!pd.Enabled)
            return null;

        if (pd.Strategy == PrDescriptionStrategy.Agentic)
        {
            if (string.IsNullOrEmpty(pd.SandboxImageReference))
            {
                _factoryLog?.LogDebug(
                    "PR description generator disabled: agentic strategy selected but SandboxImageReference is empty");
                return null;
            }
            return new LlmPullRequestDescriptionGenerator(
                _sandboxes, _agents, _credentials, MapPrDescriptionOptions(pd), _generatorLog);
        }

        if (string.IsNullOrEmpty(pd.CompletionEndpoint))
        {
            _factoryLog?.LogDebug(
                "PR description generator disabled: completion strategy selected but CompletionEndpoint is empty");
            return null;
        }
        if (_completionClient is null)
        {
            _factoryLog?.LogDebug(
                "PR description generator disabled: completion strategy selected but no completion client is registered");
            return null;
        }
        return new CompletionPullRequestDescriptionGenerator(
            _completionClient,
            MapPrDescriptionOptions(pd),
            _completionLog ?? NullLogger<CompletionPullRequestDescriptionGenerator>.Instance);
    }
}
