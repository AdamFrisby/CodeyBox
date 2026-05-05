using CodeyBox.Core;
using CodeyBox.Upstream;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging;

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
        ITimingStore? timings = null)
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
                    Token = ReadToken(project, u),
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
            GeneratorAgent = pd.GeneratorAgent,
            GeneratorModelId = pd.GeneratorModelId,
            MaxDiffBytes = pd.MaxDiffBytes,
            Timeout = pd.Timeout,
            SandboxImageReference = pd.SandboxImageReference,
            AgentAllowedHosts = pd.AgentAllowedHosts,
        };

    private LlmPullRequestDescriptionGenerator? BuildDescriptionGenerator(ProjectPrDescription pd)
    {
        if (!pd.Enabled || string.IsNullOrEmpty(pd.SandboxImageReference))
            return null;
        return new LlmPullRequestDescriptionGenerator(
            _sandboxes, _agents, _credentials, MapPrDescriptionOptions(pd), _generatorLog);
    }
}
