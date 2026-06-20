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
/// <para>When a <c>predicate</c> is supplied it is an <b>early-stop hint</b>,
/// not a hard gate: a matching frame returns immediately (the engine can
/// stop waiting once the expected state appears), but the stability fallback
/// still applies, so a frame that never matches the predicate still returns
/// once the screen settles — that lets a downstream assertion verifier run
/// against the stable frame and produce a precise
/// <see cref="ReplayFailureKind.AssertionMismatch"/> diagnostic instead of a
/// misleading <see cref="ReplayFailureKind.WaitTimeout"/>. Treating the
/// predicate as a hard gate would also defeat any verifier wired with a
/// non-byte-equality <see cref="IScreenshotComparer"/>, since the engine's
/// byte-equality predicate would never agree with a perceptual comparator.</para>
///
/// <para>Byte-equality is intentionally conservative: a single pixel of
/// noise restarts the stable count. That's what we want — the cursor blink
/// is the canonical false positive a softer "near equal" metric would
/// accept, and accepting it is how you build a flaky test suite.</para>
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
        Func<byte[], bool>? predicate,
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

            byte[]? current;
            try
            {
                current = await sandbox.GetScreenshotAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Screenshot acquisition can hiccup on graphical sandboxes (window
                // not yet mapped, transient framebuffer error). The wait absorbs
                // those by counting them as "no frame this poll" — the deadline
                // path below will still surface a sustained failure as null.
                current = null;
            }

            if (current is not null)
            {
                if (predicate is not null && predicate(current))
                    return current;

                if (previous is not null && ByteSpansEqual(previous, current))
                {
                    // Account for both the current frame and the prior frame —
                    // a "2 consecutive identical screenshots" requirement is
                    // satisfied the first time we observe one match, not after
                    // two matches. Stability returns the frame regardless of
                    // whether a predicate was supplied: the predicate is an
                    // early-stop hint, not a hard gate (see type docs).
                    stable++;
                    if (stable + 1 >= options.StableFrameCount)
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

    private static bool ByteSpansEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
    }
}
