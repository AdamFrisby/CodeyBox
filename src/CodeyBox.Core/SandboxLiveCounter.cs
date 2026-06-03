namespace CodeyBox.Core;

/// <summary>
/// Process-wide count of sandboxes that have been created and not yet disposed.
/// Ephemeral providers (process, bubblewrap) increment on a successful
/// <see cref="ISandboxProvider.CreateAsync"/> and decrement when the returned
/// <see cref="ISandbox"/> is disposed, so the OTel <c>codeybox.sandbox.active</c>
/// gauge reflects in-flight sandboxes on the default local paths and not just on
/// VM backends that expose a richer native snapshot via
/// <see cref="IActiveSandboxProvider.SnapshotActiveSandboxes"/>.
///
/// <para>Static to match the existing static telemetry instruments
/// (<c>CodeyBoxMeters</c> / <c>CodeyBoxActivities</c>) and to avoid threading a
/// counter through every provider constructor and sandbox handle.</para>
/// </summary>
public static class SandboxLiveCounter
{
    private static long _active;

    /// <summary>Current number of created-but-not-disposed sandboxes.</summary>
    public static long Active => Interlocked.Read(ref _active);

    /// <summary>Record a sandbox as live. Call once, after a successful create.</summary>
    public static void Increment() => Interlocked.Increment(ref _active);

    /// <summary>Record a sandbox as gone. Call once, on first dispose.</summary>
    public static void Decrement() => Interlocked.Decrement(ref _active);
}
