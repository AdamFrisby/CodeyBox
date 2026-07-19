using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Outcome of verifying that a test case's committed replay re-runs green on
/// the E2E execution pool.
/// </summary>
/// <param name="Passed">True only when the run reached <see cref="E2eRunStatus.Passed"/>.</param>
/// <param name="Status">The terminal status observed (or the last-seen status on timeout).</param>
/// <param name="Detail">Human-readable summary / failure detail for logging and gate messages.</param>
public sealed record E2eReplayVerificationOutcome(bool Passed, E2eRunStatus Status, string? Detail);

/// <summary>
/// Verifies a committed replay artifact by executing it once on the cheap-CPU
/// E2E pool and reporting whether it passed. The replay gate uses this to prove
/// an authored (or re-authored) replay is actually green before letting the
/// work item through — a red or unverifiable replay blocks.
/// </summary>
public interface IE2eReplayVerifier
{
    /// <summary>
    /// Enqueues one replay run for the given test case and waits for it to
    /// reach a terminal status (bounded by the configured verification
    /// timeout). Never throws for a normal red/timeout outcome — it reports it.
    /// </summary>
    Task<E2eReplayVerificationOutcome> VerifyAsync(string testCaseId, CancellationToken ct = default);
}
