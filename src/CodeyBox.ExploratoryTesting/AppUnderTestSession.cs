using CodeyBox.Core;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// A ready-to-drive target application. The harness returns this from
/// <see cref="IAppUnderTestHarness.LaunchAsync"/> once the recipe's build /
/// seed / run steps have succeeded and the entry URL has rendered. Disposing
/// the session tears down the underlying sandbox.
/// </summary>
public sealed class AppUnderTestSession : IAsyncDisposable
{
    private readonly ISandbox _sandbox;

    internal AppUnderTestSession(
        ISandbox sandbox,
        ComputerUseBridge computerUse,
        string entryUrl,
        byte[] readinessScreenshotPng)
    {
        _sandbox = sandbox;
        Sandbox = sandbox;
        ComputerUse = computerUse;
        EntryUrl = entryUrl;
        ReadinessScreenshotPng = readinessScreenshotPng;
    }

    /// <summary>
    /// Live sandbox the target is running inside. Most callers should drive
    /// the app through <see cref="ComputerUse"/>; this is exposed so callers
    /// can also <c>ExecAsync</c> diagnostic commands (curl, ps, journalctl)
    /// when an interaction goes wrong.
    /// </summary>
    public ISandbox Sandbox { get; }

    /// <summary>
    /// Computer-use bridge bound to <see cref="Sandbox"/>. The intended
    /// driver surface: real keyboard / mouse input plus screenshots, the
    /// same primitives a remote computer-use agent would call.
    /// </summary>
    public ComputerUseBridge ComputerUse { get; }

    /// <summary>
    /// In-VM URL the harness opened the in-VM browser at. Useful for
    /// diagnostics or for callers that want to re-navigate within the
    /// session (a second tab, an OAuth callback, etc.).
    /// </summary>
    public string EntryUrl { get; }

    /// <summary>
    /// The screenshot the harness captured when it decided the app had
    /// rendered. Exposed so callers can diff it against later screenshots
    /// (regression detection) or attach it to a test report.
    /// </summary>
    public byte[] ReadinessScreenshotPng { get; }

    public ValueTask DisposeAsync() => _sandbox.DisposeAsync();
}
