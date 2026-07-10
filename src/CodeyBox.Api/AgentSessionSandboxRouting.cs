using CodeyBox.Core;

namespace CodeyBox.Api;

/// <summary>
/// Builds provider-scoped durable session references and keeps Multipass resume
/// calls behind an exact provider guard. A null provider is accepted only as
/// the backward-compatible shape written before provider identity was persisted,
/// when Multipass was the sole resumable VM backend.
/// </summary>
internal static class AgentSessionSandboxRouting
{
    internal static AgentSessionSandboxRef CreateReference(ISandbox sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);

        var current = sandbox;
        var visited = new HashSet<ISandbox>(ReferenceEqualityComparer.Instance);
        while (visited.Add(current))
        {
            if (current is IProviderOwnedSandbox owned)
            {
                ValidateProviderId(owned.ProviderId);
                return new AgentSessionSandboxRef(sandbox.Id, owned.ProviderId);
            }

            if (current is not ISandboxDecorator decorator)
                return new AgentSessionSandboxRef(sandbox.Id);
            current = decorator.InnerSandbox
                ?? throw new InvalidOperationException("A sandbox decorator returned a null inner sandbox.");
        }

        throw new InvalidOperationException("A sandbox decorator cycle prevents provider identity resolution.");
    }

    internal static string? GetMultipassResumeUnsupportedReason(AgentSessionSandboxRef sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        if (sandbox.Provider is null
            || string.Equals(
                sandbox.Provider,
                HotSwappableSandboxProvider.MultipassProviderId,
                StringComparison.Ordinal))
        {
            return null;
        }
        if (string.Equals(
                sandbox.Provider,
                HotSwappableSandboxProvider.IncusProviderId,
                StringComparison.Ordinal))
        {
            return "Incus sandboxes do not support stopped Claude-session resume; refusing to route the sandbox through Multipass.";
        }
        return "The persisted sandbox provider is unsupported for stopped Claude-session resume; refusing to route the sandbox through Multipass.";
    }

    internal static Task ResumeWithMultipassAsync(
        ISuspendingSandboxProvider multipass,
        AgentSessionSandboxRef sandbox,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(multipass);
        ArgumentNullException.ThrowIfNull(sandbox);
        var unsupportedReason = GetMultipassResumeUnsupportedReason(sandbox);
        if (unsupportedReason is not null)
            throw new NotSupportedException(unsupportedReason);
        ValidateMultipassSandboxId(sandbox.Id);
        return multipass.ResumeSandboxAsync(sandbox.Id, ct);
    }

    private static void ValidateProviderId(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)
            || providerId.Length > 128
            || providerId.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "A provider-owned sandbox returned an invalid durable provider identifier.");
        }
    }

    private static void ValidateMultipassSandboxId(string sandboxId)
    {
        if (string.IsNullOrWhiteSpace(sandboxId)
            || sandboxId.Length > 63
            || !char.IsAsciiLetterOrDigit(sandboxId[0])
            || !char.IsAsciiLetterOrDigit(sandboxId[^1])
            || sandboxId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            throw new ArgumentException("The persisted Multipass sandbox identifier is invalid.", nameof(sandboxId));
        }
    }
}
