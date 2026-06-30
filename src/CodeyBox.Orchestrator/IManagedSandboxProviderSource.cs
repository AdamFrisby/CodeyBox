using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Exposes sandbox providers hidden behind higher-level pools so lifecycle
/// services such as <see cref="SandboxLeakReaper"/> can list and dispose their
/// managed sandboxes without using those providers for normal coding work.
/// </summary>
public interface IManagedSandboxProviderSource
{
    IReadOnlyList<IManagedSandboxLifecycle> ManagedSandboxProviders { get; }
}
