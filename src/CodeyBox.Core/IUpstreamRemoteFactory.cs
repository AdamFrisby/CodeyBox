namespace CodeyBox.Core;

/// <summary>
/// Builds an <see cref="IUpstreamRemote"/> for a given project. Each project
/// gets its own instance with its own credentials — tokens never cross
/// project boundaries even within one orchestrator process.
/// </summary>
public interface IUpstreamRemoteFactory
{
    IUpstreamRemote Create(Project project);
}
