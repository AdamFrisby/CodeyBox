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
/// </summary>
public sealed class UpstreamRemoteFactory : IUpstreamRemoteFactory
{
    private readonly IGitHost _gitHost;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubUpstreamRemote> _githubLog;
    private readonly ITimingStore? _timings;

    public UpstreamRemoteFactory(
        IGitHost gitHost,
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubUpstreamRemote> githubLog,
        ITimingStore? timings = null)
    {
        _gitHost = gitHost;
        _httpClientFactory = httpClientFactory;
        _githubLog = githubLog;
        _timings = timings;
    }

    public IUpstreamRemote Create(Project project)
    {
        var u = project.Upstream;
        return u.Kind?.ToLowerInvariant() switch
        {
            "github" => new GitHubUpstreamRemote(_gitHost, _httpClientFactory, _githubLog, new GitHubUpstreamOptions
            {
                Owner = u.GitHubOwner ?? throw new InvalidOperationException(
                    $"Project {project.Id}: Upstream.Kind=github requires GitHubOwner"),
                Repository = u.GitHubRepository ?? throw new InvalidOperationException(
                    $"Project {project.Id}: Upstream.Kind=github requires GitHubRepository"),
                Token = ReadToken(project, u),
                MergeMethod = ValidateMergeMethod(project.Id, u.MergeMethod),
                AutoMerge = u.AutoMerge,
                PullRequestTitleTemplate = u.PullRequestTitleTemplate,
            }, _timings),
            "git-generic" => new GitGenericUpstreamRemote(_gitHost, new GitGenericUpstreamOptions
            {
                UpstreamUrl = u.GenericUrl ?? throw new InvalidOperationException(
                    $"Project {project.Id}: Upstream.Kind=git-generic requires GenericUrl"),
            }),
            _ => new NoopUpstreamRemote(),
        };
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
}
