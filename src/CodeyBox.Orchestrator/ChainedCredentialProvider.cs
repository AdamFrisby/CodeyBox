using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Tries each wrapped <see cref="ICredentialProvider"/> in order and returns
/// the first non-null credential. Lets operators stack a fresh-from-file
/// provider in front of the env-var provider so a file refresh on the host
/// is picked up without restarting the orchestrator.
/// </summary>
public sealed class ChainedCredentialProvider : ICredentialProvider
{
    private readonly IReadOnlyList<ICredentialProvider> _providers;

    public ChainedCredentialProvider(IEnumerable<ICredentialProvider> providers)
    {
        _providers = providers.ToList();
    }

    public async Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        foreach (var p in _providers)
        {
            var cred = await p.GetAsync(agent, ct);
            if (cred is not null)
                return cred;
        }
        return null;
    }
}
