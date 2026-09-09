using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// How to launch and drive the app under test for one declared e2e-replay
/// <see cref="TestCase"/>: the web-app recipe that brings the app up in a
/// sandbox, and the exploration plan (entry URL, assertions, emit options) the
/// cheap-model author drives against it.
/// </summary>
public sealed record E2eAuthoringTarget(WebAppRecipe Recipe, E2eExplorationPlan Plan);

/// <summary>
/// Resolves the app-launch recipe and exploration plan for a declared
/// e2e-replay test case. This is the per-project seam: how the app is built,
/// seeded, run and driven is project-specific and lives outside the generic
/// authoring driver.
///
/// <para>Returning <c>null</c> is a first-class outcome meaning "no target is
/// configured for this case." The authoring driver reports that as an
/// unresolved (blocking) case rather than fabricating a replay — honest fail-
/// closed behaviour, never a fake pass.</para>
/// </summary>
public interface IE2eAuthoringTargetResolver
{
    Task<E2eAuthoringTarget?> ResolveAsync(TestCase testCase, CancellationToken ct = default);
}
