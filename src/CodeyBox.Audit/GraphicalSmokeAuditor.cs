using System.IO.Compression;
using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Minimal graphical plumbing audit. It waits for the desktop to settle,
/// captures a screenshot through the sandbox capability surface, and verifies
/// the image has non-uniform pixels. This is intentionally a smoke test, not a
/// GUI test framework.
/// </summary>
public sealed class GraphicalSmokeAuditor : IAuditor
{
    private readonly TimeSpan _settleDelay;

    public GraphicalSmokeAuditor()
        : this(TimeSpan.FromSeconds(5))
    {
    }

    public GraphicalSmokeAuditor(TimeSpan settleDelay)
    {
        _settleDelay = settleDelay;
    }

    public string Name => "gui:smoke";
    public string Kind => "gui-smoke";
    public AuditCapabilities Required => AuditCapabilities.None;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        _ = workingDirectory;
        _ = context;

        if (_settleDelay > TimeSpan.Zero)
            await Task.Delay(_settleDelay, ct);

        byte[] png;
        try
        {
            png = await sandbox.GetScreenshotAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed($"graphical screenshot failed: {ex.Message}");
        }

        PngPixelStats stats;
        try
        {
            stats = PngPixelStats.FromPng(png);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed($"graphical screenshot was not a supported PNG: {ex.Message}");
        }

        var rawOutput = $"PNG {stats.Width}x{stats.Height}; uniqueColors={stats.UniqueColors}; lumaRange={stats.LumaRange}";
        if (stats.PixelCount <= 0 || stats.UniqueColors < 2 || stats.LumaRange < 8)
            return Failed("graphical screenshot appears blank or uniform", rawOutput);

        return new AuditResult(true, [], RawOutput: rawOutput);
    }

    private static AuditResult Failed(string description, string? rawOutput = null)
    {
        var finding = new AuditFinding(
            AuditorName: "gui:smoke",
            Severity: AuditSeverity.Error,
            Title: "graphical desktop smoke test failed",
            Description: description);
        return new AuditResult(false, [finding], RawOutput: rawOutput ?? description);
    }
}

internal readonly record struct PngPixelStats(
    int Width,
    int Height,
    int PixelCount,
    int UniqueColors,
    int LumaRange)
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static PngPixelStats FromPng(byte[] png)
    {
        if (png.Length < PngSignature.Length || !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            throw new InvalidDataException("missing PNG signature");

        int width = 0;
        int height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        byte interlace = 0;
        byte[]? palette = null;
        using var idat = new MemoryStream();

        var offset = PngSignature.Length;
        while (offset + 8 <= png.Length)
        {
            var length = ReadBigEndianInt32(png.AsSpan(offset, 4));
            offset += 4;
            if (length < 0 || offset + 4 + length + 4 > png.Length)
                throw new InvalidDataException("invalid PNG chunk length");

            var type = System.Text.Encoding.ASCII.GetString(png, offset, 4);
            offset += 4;
            var data = png.AsSpan(offset, length);
            offset += length;
            offset += 4; // CRC. The decoder validates structure, not integrity.

            switch (type)
            {
                case "IHDR":
                    if (length != 13)
                        throw new InvalidDataException("invalid IHDR length");
                    width = ReadBigEndianInt32(data[..4]);
                    height = ReadBigEndianInt32(data.Slice(4, 4));
                    bitDepth = data[8];
                    colorType = data[9];
                    interlace = data[12];
                    break;

                case "PLTE":
                    palette = data.ToArray();
                    break;

                case "IDAT":
                    idat.Write(data);
                    break;

                case "IEND":
                    offset = png.Length;
                    break;
            }
        }

        if (width <= 0 || height <= 0)
            throw new InvalidDataException("missing IHDR");
        if (bitDepth != 8)
            throw new NotSupportedException($"unsupported PNG bit depth {bitDepth}");
        if (interlace != 0)
            throw new NotSupportedException("interlaced PNG screenshots are not supported");

        var bytesPerPixel = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new NotSupportedException($"unsupported PNG color type {colorType}"),
        };
        if (colorType == 3 && (palette is null || palette.Length < 3))
            throw new InvalidDataException("indexed PNG has no palette");

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        zlib.CopyTo(decompressed);
        var scanlines = decompressed.ToArray();

        var rowBytes = checked(width * bytesPerPixel);
        var expectedBytes = checked((rowBytes + 1) * height);
        if (scanlines.Length < expectedBytes)
            throw new InvalidDataException("PNG image data is truncated");

        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        var unique = new HashSet<int>();
        var minLuma = 255;
        var maxLuma = 0;
        var inputOffset = 0;

        for (var y = 0; y < height; y++)
        {
            var filter = scanlines[inputOffset++];
            Array.Copy(scanlines, inputOffset, current, 0, rowBytes);
            inputOffset += rowBytes;
            ApplyFilter(filter, current, previous, bytesPerPixel);

            for (var x = 0; x < width; x++)
            {
                var baseIndex = x * bytesPerPixel;
                var (r, g, b) = ReadRgb(current, baseIndex, colorType, palette);
                unique.Add((r << 16) | (g << 8) | b);
                var luma = (r * 299 + g * 587 + b * 114) / 1000;
                minLuma = Math.Min(minLuma, luma);
                maxLuma = Math.Max(maxLuma, luma);
            }

            (previous, current) = (current, previous);
        }

        return new PngPixelStats(
            width,
            height,
            checked(width * height),
            unique.Count,
            maxLuma - minLuma);
    }

    private static (int R, int G, int B) ReadRgb(byte[] row, int baseIndex, byte colorType, byte[]? palette)
    {
        return colorType switch
        {
            0 => (row[baseIndex], row[baseIndex], row[baseIndex]),
            2 => (row[baseIndex], row[baseIndex + 1], row[baseIndex + 2]),
            3 => ReadPaletteRgb(row[baseIndex], palette!),
            4 => (row[baseIndex], row[baseIndex], row[baseIndex]),
            6 => (row[baseIndex], row[baseIndex + 1], row[baseIndex + 2]),
            _ => throw new NotSupportedException($"unsupported PNG color type {colorType}"),
        };
    }

    private static (int R, int G, int B) ReadPaletteRgb(byte index, byte[] palette)
    {
        var offset = index * 3;
        if (offset + 2 >= palette.Length)
            throw new InvalidDataException("indexed PNG references a missing palette entry");
        return (palette[offset], palette[offset + 1], palette[offset + 2]);
    }

    private static void ApplyFilter(byte filter, byte[] current, byte[] previous, int bytesPerPixel)
    {
        for (var i = 0; i < current.Length; i++)
        {
            var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
            var up = previous[i];
            var upperLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
            var predictor = filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upperLeft),
                _ => throw new InvalidDataException($"unknown PNG filter {filter}"),
            };
            current[i] = unchecked((byte)(current[i] + predictor));
        }
    }

    private static int Paeth(int left, int up, int upperLeft)
    {
        var p = left + up - upperLeft;
        var pa = Math.Abs(p - left);
        var pb = Math.Abs(p - up);
        var pc = Math.Abs(p - upperLeft);
        if (pa <= pb && pa <= pc) return left;
        return pb <= pc ? up : upperLeft;
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> bytes)
        => (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
}
