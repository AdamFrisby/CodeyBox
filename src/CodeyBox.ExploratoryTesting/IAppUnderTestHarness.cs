namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Brings a target application up inside a sandbox in a deterministic, known
/// state, ready to be driven by the
/// <see cref="CodeyBox.Sandbox.Graphical.ComputerUseBridge"/> with real
/// keyboard/mouse input. The harness owns the lifecycle: build → seed → run →
/// open → readiness, and teardown via <see cref="AppUnderTestSession.DisposeAsync"/>.
///
/// <para><b>Seam scope (read first):</b> this interface currently models one
/// modality (web apps driven via a graphical desktop sandbox) and one
/// execution environment (Multipass-backed graphical VMs on the same host).
/// It is deliberately minimal — both the recipe (<see cref="AppUnderTestRecipe"/>)
/// and the session (<see cref="AppUnderTestSession"/>) carry only what a web
/// app on a graphical VM needs to launch and be driven. When the second
/// modality lands (CLI, native, 3D, API) or the second execution environment
/// lands (container, cloud), expect to split <see cref="AppUnderTestRecipe"/>
/// into per-modality records (<c>WebAppRecipe</c>, <c>CliRecipe</c>, ...) and
/// keep <see cref="IAppUnderTestHarness"/> generic over the recipe type. The
/// session abstraction should grow capability flags (graphical vs not) so a
/// non-graphical session can omit
/// <see cref="AppUnderTestSession.ComputerUse"/>. Do not pre-abstract these
/// now: the cost of guessing wrong is higher than the cost of the eventual
/// refactor.</para>
/// </summary>
public interface IAppUnderTestHarness
{
    /// <summary>
    /// Provisions the sandbox, executes the recipe's build / seed / run
    /// steps, opens the recipe's entry URL in the in-VM browser, and
    /// blocks until the app responds AND a screenshot confirms the UI
    /// has actually rendered. The returned session is the caller's
    /// handle to drive the app and to tear it down.
    /// </summary>
    /// <param name="recipe">Per-target build / seed / run / open instructions.</param>
    /// <param name="ct">Cancellation token; cancellation cleans up any in-flight sandbox.</param>
    Task<AppUnderTestSession> LaunchAsync(AppUnderTestRecipe recipe, CancellationToken ct = default);
}
