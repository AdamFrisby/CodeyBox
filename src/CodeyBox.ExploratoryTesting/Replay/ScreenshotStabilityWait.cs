using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Default <see cref="IVisualWait"/>: polls
/// <see cref="ISandbox.GetScreenshotAsync"/> at
/// <see cref="ReplayOptions.VisualWaitPollInterval"/>, comparing PNG bytes
/// for pixel stability. The screen is "settled" when
/// <see cref="ReplayOptions.StableFrameCount"/> consecutive screenshots are
/// byte-identical, mirroring how a human says "ok the spinner stopped, now
/// I can read it."
///
/// <para>When <paramref name="predicate"/> is supplied, success is the
/// predicate returning true on any captured frame — the predicate short-
/// circuits stability so the engine can wait for a specific expected state
/// without paying the full settle window.</para>
///
/// <para>Byte-equality is intentionally conservative: a single pixel of
/// noise restarts the stable count. That's what we want — the cursor blink
/// is the canonical false positive a softer "near equal" metric would
/// accept, and accepting it is how you build a flaky test suite.</para>
/// </summary>
public sealed class ScreenshotStabilityWait : IVisualWait
{
    private readonly TimeProvider _timeProvider;

    public ScreenshotStabilityWait(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<byte[]?> WaitAsync(
        ISandbox sandbox,
        Func<byte[], bool>? predicate,
        ReplayOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(options);

        var deadline = _timeProvider.GetUtcNow() + options.VisualWaitTimeout;
        byte[]? previous = null;
        var stable = 0;
        byte[]? last = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            byte[]? current;
            try
            {
                current = await sandbox.GetScreenshotAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                current = null;
            }

            if (current is not null)
            {
                last = current;
                if (predicate is not null && predicate(current))
                    return current;

                if (previous is not null && ByteSpansEqual(previous, current))
                {
                    stable++;
                    if (predicate is null && stable >= Math.Max(1, options.StableFrameCount - 1))
                        return current;
                }
                else
                {
                    stable = 0;
                }
                previous = current;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
                return predicate is null ? last : null;

            try
            {
                await Task.Delay(options.VisualWaitPollInterval, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // delay aborted because the cancellation token tied to a sub-component fired;
                // surface the next iteration so the deadline check terminates cleanly.
            }
        }
    }

    private static bool ByteSpansEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
    }
}
