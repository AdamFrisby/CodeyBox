using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Default <see cref="IVisualWait"/>: polls
/// <see cref="ISandbox.GetScreenshotAsync"/> at
/// <see cref="ReplayOptions.VisualWaitPollInterval"/>, comparing decoded PNG
/// pixels for stability when possible. The screen is "settled" when
/// <see cref="ReplayOptions.StableFrameCount"/> consecutive screenshots are
/// pixel-identical, mirroring how a human says "ok the spinner stopped, now
/// I can read it."
///
/// <para>When a <c>predicate</c> is supplied it is the expected-state gate:
/// a matching frame returns immediately, while stable non-matching frames keep
/// polling until the expected state appears or the deadline expires. Without a
/// predicate, stability alone is sufficient.</para>
///
/// <para>Pixel-equality is intentionally conservative: a single pixel of
/// noise restarts the stable count. PNGs that cannot be decoded fall back to
/// byte equality so non-PNG fakes used by tests still get deterministic wait
/// behavior.</para>
///
/// <para>On deadline expiry the wait returns <c>null</c>: a screen that
/// never settles (a continuous animation / never-ending load) is a real
/// failure class the engine reports as <see cref="ReplayFailureKind.WaitTimeout"/>,
/// not a soft pass.</para>
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
        Func<byte[], CancellationToken, Task<bool>>? predicate,
        ReplayOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(options);

        var deadline = _timeProvider.GetUtcNow() + options.VisualWaitTimeout;
        byte[]? previous = null;
        var stable = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var current = await sandbox.GetScreenshotAsync(ct).ConfigureAwait(false);

            if (current is not null)
            {
                if (predicate is not null && await predicate(current, ct).ConfigureAwait(false))
                    return current;

                if (previous is not null && ScreenshotsRepresentSamePixels(previous, current))
                {
                    // Account for both the current frame and the prior frame —
                    // a "2 consecutive identical screenshots" requirement is
                    // satisfied the first time we observe one match, not after
                    // two matches. When a predicate is supplied, stability
                    // alone is not enough; keep waiting for the expected state.
                    stable++;
                    if (predicate is null && stable + 1 >= options.StableFrameCount)
                        return current;
                }
                else
                {
                    stable = 0;
                }
                previous = current;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
                return null;

            await Task.Delay(options.VisualWaitPollInterval, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    private static bool ScreenshotsRepresentSamePixels(byte[] a, byte[] b)
    {
        if (VisualSignatureElementLocator.PngBitmap.TryDecode(a, out var left)
            && VisualSignatureElementLocator.PngBitmap.TryDecode(b, out var right))
        {
            return left.HasSamePixelsAs(right);
        }

        if (a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
    }
}
