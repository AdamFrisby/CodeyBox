using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodeyBox.Core;

/// <summary>
/// The cheap CPU-only VM pool that runs committed e2e-replay artifacts. The pool
/// is intentionally separate from the coding-worker fleet. The pool decides
/// its own concurrency cap, leases (sandbox, working directory) slots
/// clone-per-test from a pre-baked image, and tears them down after each run.
/// Implementations can run locally for development or fan out to remote
/// cheap-CPU hosts; the dispatcher only depends on this provider-agnostic
/// contract.
/// </summary>
public interface IE2eExecutionPool
{
    /// <summary>
    /// Stable identifier for diagnostics ("local-multipass", "remote-ssh", "fake-test").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Maximum number of concurrent leases. Sourced from configuration, hot-reloaded.
    /// </summary>
    int MaxConcurrent { get; }

    /// <summary>
    /// Current number of in-flight leases. Used by the dispatcher to gate
    /// queue draining and by metrics.
    /// </summary>
    int InFlight { get; }

    /// <summary>
    /// Acquire a slot. Blocks until the pool has capacity or
    /// <paramref name="ct"/> fires. The returned slot exposes a fresh sandbox
    /// (cloned from the pre-baked baseline image when the provider supports it)
    /// and releases capacity on dispose.
    /// </summary>
    Task<IE2eExecutionSlot> LeaseAsync(CancellationToken ct = default);
}

/// <summary>
/// A leased pool slot. Disposing releases the capacity gate AND tears down the
/// sandbox so the next lease starts from a clean clone.
/// </summary>
public interface IE2eExecutionSlot : IAsyncDisposable
{
    /// <summary>The cloned sandbox the replay runtime executes against.</summary>
    ISandbox Sandbox { get; }

    /// <summary>Identifier of the underlying compute node (for diagnostics / run record).</summary>
    string SandboxId { get; }
}
