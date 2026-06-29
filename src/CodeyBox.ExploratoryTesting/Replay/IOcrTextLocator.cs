using CodeyBox.Core;

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

internal sealed class TesseractOcrTextLocator : IOcrTextLocator
{
    public static TesseractOcrTextLocator Instance { get; } = new();

    private const int MaxOcrScreenshotBytes = 8 * 1024 * 1024;
    private const int MaxOcrStdoutBytes = 1024 * 1024;

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

        return TryLocateFromTsv(result.Stdout, expected);
    }

    internal static LocatedTarget? TryLocateFromTsv(string tsv, string expectedText)
    {
        var rows = ParseRows(tsv);
        if (rows.Count == 0) return null;

        for (var i = 0; i < rows.Count; i++)
        {
            if (TextMatches(rows[i].Text, expectedText))
                return FromRow(rows[i]);

            var union = rows[i].Bounds;
            var combined = rows[i].Text;
            for (var j = i + 1; j < rows.Count && j < i + 8; j++)
            {
                combined += " " + rows[j].Text;
                union = Union(union, rows[j].Bounds);
                if (TextMatches(combined, expectedText))
                    return FromBounds(union);
            }
        }

        return null;
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
        var textIndex = Array.IndexOf(columns, "text");
        if (leftIndex < 0 || topIndex < 0 || widthIndex < 0 || heightIndex < 0 || textIndex < 0)
            return rows;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = line.Split('\t');
            if (parts.Length <= Math.Max(textIndex, Math.Max(heightIndex, Math.Max(widthIndex, Math.Max(leftIndex, topIndex)))))
                continue;

            var text = parts[textIndex].Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
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
        => actual.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static LocatedTarget FromRow(OcrRow row) => FromBounds(row.Bounds);

    private static LocatedTarget FromBounds(TraceBoundingRegion region) => new()
    {
        CenterX = region.X + region.Width / 2,
        CenterY = region.Y + region.Height / 2,
        Region = region,
        Source = "visual-ocr",
        Confidence = 0.72,
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

    private sealed record OcrRow(string Text, TraceBoundingRegion Bounds);
}
