using CodeyBox.Core;
using CodeyBox.Upstream;
using CodeyBox.Upstream.GitHub;

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

    public UpstreamRemoteFactory(IGitHost gitHost)
    {
        _gitHost = gitHost;
    }

    public IUpstreamRemote Create(Project project)
    {
        var u = project.Upstream;
        return u.Kind?.ToLowerInvariant() switch
        {
            "github" => new GitHubUpstreamRemote(_gitHost, new GitHubUpstreamOptions
            {
                Owner = u.GitHubOwner ?? throw new InvalidOperationException(
                    $"Project {project.Id}: Upstream.Kind=github requires GitHubOwner"),
                Repository = u.GitHubRepository ?? throw new InvalidOperationException(
                    $"Project {project.Id}: Upstream.Kind=github requires GitHubRepository"),
                Token = ReadToken(project, u),
            }),
            "git-generic" => new GitGenericUpstreamRemote(_gitHost, new GitGenericUpstreamOptions
            {
                UpstreamUrl = u.GenericUrl ?? throw new InvalidOperationException(
                    $"Project {project.Id}: Upstream.Kind=git-generic requires GenericUrl"),
            }),
            _ => new NoopUpstreamRemote(),
        };
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
        return token;
    }
}
