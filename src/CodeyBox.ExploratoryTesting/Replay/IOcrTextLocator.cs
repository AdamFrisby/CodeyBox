using CodeyBox.Core;
using System.Globalization;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Locates recorded OCR text on the current screenshot. Implementations must
/// return null when they cannot prove a text match with current-screen bounds.
/// </summary>
public interface IOcrTextLocator
{
    Task<LocatedTarget?> LocateTextAsync(
        ISandbox sandbox,
        byte[] currentScreenshotPng,
        TraceVisualDescriptor visual,
        CancellationToken ct);
}

internal sealed class NullOcrTextLocator : IOcrTextLocator
{
    public static NullOcrTextLocator Instance { get; } = new();

    public Task<LocatedTarget?> LocateTextAsync(
        ISandbox sandbox,
        byte[] currentScreenshotPng,
        TraceVisualDescriptor visual,
        CancellationToken ct)
        => Task.FromResult<LocatedTarget?>(null);
}

public sealed class TesseractOcrTextLocator : IOcrTextLocator
{
    public static TesseractOcrTextLocator Instance { get; } = new();

    private const int MaxOcrScreenshotBytes = 8 * 1024 * 1024;
    private const int MaxOcrStdoutBytes = 1024 * 1024;
    private const double MinOcrConfidence = 50.0;

    public async Task<LocatedTarget?> LocateTextAsync(
        ISandbox sandbox,
        byte[] currentScreenshotPng,
        TraceVisualDescriptor visual,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(currentScreenshotPng);
        ArgumentNullException.ThrowIfNull(visual);

        var expected = visual.OcrText?.Trim();
        if (string.IsNullOrWhiteSpace(expected)) return null;
        if (currentScreenshotPng.Length == 0 || currentScreenshotPng.Length > MaxOcrScreenshotBytes)
            return null;

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash",
                "-lc",
                """
                set -e
                if ! command -v tesseract >/dev/null 2>&1; then exit 127; fi
                tmp="$(mktemp --suffix=.png)"
                cleanup() { rm -f "$tmp"; }
                trap cleanup EXIT
                base64 -d > "$tmp"
                tesseract "$tmp" stdout --psm 6 tsv 2>/dev/null
                """,
            ],
            Stdin = Convert.ToBase64String(currentScreenshotPng),
            MaxStdoutBytes = MaxOcrStdoutBytes,
            MaxStderrBytes = 4096,
        }, ct).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Stdout))
            return null;

        return TryLocateFromTsv(result.Stdout, expected, visual);
    }

    public static LocatedTarget? TryLocateFromTsv(string tsv, string expectedText)
        => TryLocateFromTsv(tsv, expectedText, visual: null);

    public static LocatedTarget? TryLocateFromTsv(string tsv, string expectedText, TraceVisualDescriptor? visual)
    {
        var rows = ParseRows(tsv);
        if (rows.Count == 0) return null;

        var matches = new List<TraceBoundingRegion>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (TextMatches(rows[i].Text, expectedText))
            {
                matches.Add(rows[i].Bounds);
                continue;
            }

            var union = rows[i].Bounds;
            var combined = rows[i].Text;
            for (var j = i + 1; j < rows.Count && j < i + 8; j++)
            {
                combined += " " + rows[j].Text;
                union = Union(union, rows[j].Bounds);
                if (TextMatches(combined, expectedText))
                    matches.Add(union);
            }
        }

        if (matches.Count == 0) return null;
        if (matches.Count == 1) return FromBounds(matches[0]);

        var disambiguated = DisambiguateByRecordedRegion(matches, visual);
        return disambiguated is null ? null : FromBounds(disambiguated);
    }

    private static List<OcrRow> ParseRows(string tsv)
    {
        var rows = new List<OcrRow>();
        using var reader = new StringReader(tsv);
        var header = reader.ReadLine();
        if (header is null) return rows;

        var columns = header.Split('\t');
        var leftIndex = Array.IndexOf(columns, "left");
        var topIndex = Array.IndexOf(columns, "top");
        var widthIndex = Array.IndexOf(columns, "width");
        var heightIndex = Array.IndexOf(columns, "height");
        var confidenceIndex = Array.IndexOf(columns, "conf");
        var textIndex = Array.IndexOf(columns, "text");
        if (leftIndex < 0 || topIndex < 0 || widthIndex < 0 || heightIndex < 0 || confidenceIndex < 0 || textIndex < 0)
            return rows;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = line.Split('\t');
            if (parts.Length <= Math.Max(textIndex, Math.Max(confidenceIndex, Math.Max(heightIndex, Math.Max(widthIndex, Math.Max(leftIndex, topIndex))))))
                continue;

            var text = parts[textIndex].Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!double.TryParse(parts[confidenceIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence))
                continue;
            if (confidence < MinOcrConfidence) continue;
            if (!int.TryParse(parts[leftIndex], out var left)) continue;
            if (!int.TryParse(parts[topIndex], out var top)) continue;
            if (!int.TryParse(parts[widthIndex], out var width)) continue;
            if (!int.TryParse(parts[heightIndex], out var height)) continue;
            if (width <= 0 || height <= 0) continue;

            rows.Add(new OcrRow(
                text,
                new TraceBoundingRegion
                {
                    X = left,
                    Y = top,
                    Width = width,
                    Height = height,
                }));
        }

        return rows;
    }

    private static bool TextMatches(string actual, string expected)
    {
        var actualTokens = Tokenize(actual);
        var expectedTokens = Tokenize(expected);
        if (actualTokens.Length == 0 || expectedTokens.Length == 0)
            return false;
        if (actualTokens.Length != expectedTokens.Length)
            return false;

        for (var i = 0; i < actualTokens.Length; i++)
        {
            if (!string.Equals(actualTokens[i], expectedTokens[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string[] Tokenize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsLetterOrDigit(value[i]))
            {
                if (start < 0) start = i;
                continue;
            }

            if (start >= 0)
            {
                tokens.Add(value[start..i]);
                start = -1;
            }
        }

        if (start >= 0)
            tokens.Add(value[start..]);

        return tokens.ToArray();
    }

    private static LocatedTarget FromBounds(TraceBoundingRegion region) => new()
    {
        CenterX = region.X + region.Width / 2,
        CenterY = region.Y + region.Height / 2,
        Region = region,
        Source = "visual-ocr",
        Confidence = 0.72,
        Evidence = LocatedTargetEvidence.Ocr,
    };

    private static TraceBoundingRegion Union(TraceBoundingRegion left, TraceBoundingRegion right)
    {
        var x1 = Math.Min(left.X, right.X);
        var y1 = Math.Min(left.Y, right.Y);
        var x2 = Math.Max(left.X + left.Width, right.X + right.Width);
        var y2 = Math.Max(left.Y + left.Height, right.Y + right.Height);
        return new TraceBoundingRegion
        {
            X = x1,
            Y = y1,
            Width = x2 - x1,
            Height = y2 - y1,
        };
    }

    private static TraceBoundingRegion? DisambiguateByRecordedRegion(
        IReadOnlyList<TraceBoundingRegion> matches,
        TraceVisualDescriptor? visual)
    {
        var recorded = visual?.Region;
        if (recorded is not { Width: > 0, Height: > 0 }) return null;

        TraceBoundingRegion? best = null;
        long bestDistance = long.MaxValue;
        var tied = false;
        foreach (var match in matches)
        {
            if (!Intersects(Expand(recorded, recorded.Width, recorded.Height), match))
                continue;

            var distance = SquaredCenterDistance(recorded, match);
            if (distance < bestDistance)
            {
                best = match;
                bestDistance = distance;
                tied = false;
            }
            else if (distance == bestDistance)
            {
                tied = true;
            }
        }

        return tied ? null : best;
    }

    private static TraceBoundingRegion Expand(TraceBoundingRegion region, int xPadding, int yPadding) => new()
    {
        X = region.X - Math.Max(0, xPadding),
        Y = region.Y - Math.Max(0, yPadding),
        Width = region.Width + Math.Max(0, xPadding) * 2,
        Height = region.Height + Math.Max(0, yPadding) * 2,
    };

    private static bool Intersects(TraceBoundingRegion left, TraceBoundingRegion right)
        => left.X < right.X + right.Width
            && right.X < left.X + left.Width
            && left.Y < right.Y + right.Height
            && right.Y < left.Y + left.Height;

    private static long SquaredCenterDistance(TraceBoundingRegion left, TraceBoundingRegion right)
    {
        var dx = (long)(left.X + left.Width / 2) - (right.X + right.Width / 2);
        var dy = (long)(left.Y + left.Height / 2) - (right.Y + right.Height / 2);
        return dx * dx + dy * dy;
    }

    private sealed record OcrRow(string Text, TraceBoundingRegion Bounds);
}
