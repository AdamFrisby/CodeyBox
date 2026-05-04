namespace CodeyBox.Core;

/// <summary>
/// Extends <see cref="ICredentialProvider"/> with per-project plugin ordering.
/// <para>
/// <see cref="ChainedCredentialProvider"/> implements this interface. Injecting
/// it alongside <see cref="ICredentialProvider"/> allows components that hold
/// project context (e.g. <c>PipelineRunner</c>) to honour
/// <see cref="Project.CredentialProviderPriority"/> at agent pickup time without
/// exposing the concrete chain implementation to call sites.
/// </para>
/// </summary>
public interface IProjectAwareCredentialProvider : ICredentialProvider
{
    /// <summary>
    /// Returns the credential from providers filtered and ordered by
    /// <paramref name="credentialProviderPriority"/>.
    /// When the list is empty, falls back to global discovery order (same as
    /// <see cref="ICredentialProvider.GetAsync"/>).
    /// </summary>
    Task<AgentCredential?> GetAsync(
        AgentKind agent,
        IReadOnlyList<string> credentialProviderPriority,
        CancellationToken ct = default);
}
