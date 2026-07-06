using System.IO.Compression;
using System.Text;

namespace CodeyBox.ExploratoryTesting.Replay;

internal readonly record struct PngPixelMatchOptions(
    int MaxChannelDelta,
    double MaxMismatchedPixelRatio,
    double MaxMeanChannelDelta)
{
    public static PngPixelMatchOptions Exact { get; } = new(0, 0, 0);
    public static PngPixelMatchOptions ReplayTolerance { get; } = new(16, 0.08, 4.0);
}

internal sealed class PngBitmap
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private const int MaxCompressedPngBytes = 64 * 1024 * 1024;
    private const int MaxScreenshotDimension = 4096;
    private const int MaxDecodedScanlineBytes = 80 * 1024 * 1024;

    private readonly byte[] _rgb;

    private PngBitmap(int width, int height, byte[] rgb)
    {
        Width = width;
        Height = height;
        _rgb = rgb;
    }

    public int Width { get; }
    public int Height { get; }
    public int PixelCount => Width * Height;

    public static bool TryDecode(byte[] png, out PngBitmap bitmap)
    {
        try
        {
            bitmap = Decode(png);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        {
            bitmap = null!;
            return false;
        }
    }

    public bool TryCrop(TraceBoundingRegion region, out PngBitmap crop)
    {
        crop = null!;
        if (region.Width <= 0 || region.Height <= 0) return false;
        if (region.X < 0 || region.Y < 0) return false;
        var right = (long)region.X + region.Width;
        var bottom = (long)region.Y + region.Height;
        if (right > Width || bottom > Height) return false;

        var pixels = new byte[checked(region.Width * region.Height * 3)];
        for (var y = 0; y < region.Height; y++)
        {
            var srcOffset = (((region.Y + y) * Width) + region.X) * 3;
            var dstOffset = y * region.Width * 3;
            Array.Copy(_rgb, srcOffset, pixels, dstOffset, region.Width * 3);
        }

        crop = new PngBitmap(region.Width, region.Height, pixels);
        return true;
    }

    public bool TryFindBestExact(
        PngBitmap template,
        CancellationToken ct,
        out (int X, int Y) point)
        => TryFindBestMatch(template, PngPixelMatchOptions.Exact, ct, out point);

    public bool TryFindBestTolerant(
        PngBitmap template,
        CancellationToken ct,
        out (int X, int Y) point)
        => TryFindBestMatch(template, PngPixelMatchOptions.ReplayTolerance, ct, out point);

    public bool TryFindBestMatch(
        PngBitmap template,
        PngPixelMatchOptions options,
        CancellationToken ct,
        out (int X, int Y) point)
    {
        point = default;
        if (template.Width <= 0 || template.Height <= 0) return false;
        if (template.Width > Width || template.Height > Height) return false;

        if (template.PixelCount > 512 * 512) return false;
        var candidatePositions = ((long)Width - template.Width + 1) * (Height - template.Height + 1);
        if (candidatePositions > 2_000_000) return false;
        if (template.PixelCount > 64 * 64 && template.HasLowSampledColorDiversity(2))
            return false;

        var found = false;
        for (var y = 0; y <= Height - template.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x <= Width - template.Width; x++)
            {
                if (!LikelyMatchAt(template, x, y, options)) continue;
                if (!TryScoreMatchAt(template, x, y, options, ct, out _)) continue;
                if (found) return false;
                point = (x, y);
                found = true;
            }
        }

        return found;
    }

    public bool MatchesAt(PngBitmap template, int x, int y, CancellationToken ct = default)
        => MatchesAt(template, x, y, PngPixelMatchOptions.Exact, ct);

    public bool MatchesAt(
        PngBitmap template,
        int x,
        int y,
        PngPixelMatchOptions options,
        CancellationToken ct = default)
        => TryScoreMatchAt(template, x, y, options, ct, out _);

    public bool HasSamePixelsAs(PngBitmap other)
    {
        if (other.Width != Width || other.Height != Height) return false;
        return _rgb.AsSpan().SequenceEqual(other._rgb);
    }

    private bool LikelyMatchAt(PngBitmap template, int x, int y, PngPixelMatchOptions options)
    {
        return PixelMatches(template, x, y, 0, 0, options)
            && PixelMatches(template, x + template.Width - 1, y, template.Width - 1, 0, options)
            && PixelMatches(template, x, y + template.Height - 1, 0, template.Height - 1, options)
            && PixelMatches(template, x + template.Width - 1, y + template.Height - 1, template.Width - 1, template.Height - 1, options);
    }

    private bool PixelMatches(PngBitmap template, int x, int y, int tx, int ty, PngPixelMatchOptions options)
    {
        var source = ((y * Width) + x) * 3;
        var target = ((ty * template.Width) + tx) * 3;
        return Math.Abs(_rgb[source] - template._rgb[target]) <= options.MaxChannelDelta
            && Math.Abs(_rgb[source + 1] - template._rgb[target + 1]) <= options.MaxChannelDelta
            && Math.Abs(_rgb[source + 2] - template._rgb[target + 2]) <= options.MaxChannelDelta;
    }

    private bool TryScoreMatchAt(
        PngBitmap template,
        int x,
        int y,
        PngPixelMatchOptions options,
        CancellationToken ct,
        out long totalDelta)
    {
        totalDelta = 0;
        if (x < 0 || y < 0) return false;
        if (template.Width <= 0 || template.Height <= 0) return false;
        if ((long)x + template.Width > Width || (long)y + template.Height > Height) return false;

        var maxMismatchedPixels = (int)Math.Floor(template.PixelCount * options.MaxMismatchedPixelRatio);
        var maxTotalDelta = (long)Math.Ceiling(template.PixelCount * 3 * options.MaxMeanChannelDelta);
        var mismatchedPixels = 0;
        for (var ty = 0; ty < template.Height; ty++)
        {
            if ((ty & 0x3F) == 0) ct.ThrowIfCancellationRequested();
            for (var tx = 0; tx < template.Width; tx++)
            {
                var source = (((y + ty) * Width) + x + tx) * 3;
                var target = ((ty * template.Width) + tx) * 3;
                var dr = Math.Abs(_rgb[source] - template._rgb[target]);
                var dg = Math.Abs(_rgb[source + 1] - template._rgb[target + 1]);
                var db = Math.Abs(_rgb[source + 2] - template._rgb[target + 2]);
                totalDelta += dr + dg + db;
                if (dr > options.MaxChannelDelta
                    || dg > options.MaxChannelDelta
                    || db > options.MaxChannelDelta)
                {
                    mismatchedPixels++;
                    if (mismatchedPixels > maxMismatchedPixels) return false;
                }
                if (totalDelta > maxTotalDelta) return false;
            }
        }
        return true;
    }

    private bool HasLowSampledColorDiversity(int maxDistinctColors)
    {
        var distinct = new HashSet<int>();
        var pixelCount = PixelCount;
        var step = Math.Max(1, pixelCount / 4096);
        for (var pixel = 0; pixel < pixelCount; pixel += step)
        {
            var offset = pixel * 3;
            var color = (_rgb[offset] << 16) | (_rgb[offset + 1] << 8) | _rgb[offset + 2];
            distinct.Add(color);
            if (distinct.Count > maxDistinctColors) return false;
        }
        return true;
    }

    private static PngBitmap Decode(byte[] png)
    {
        if (png.Length < PngSignature.Length || !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            throw new InvalidDataException("missing PNG signature");
        if (png.Length > MaxCompressedPngBytes)
            throw new InvalidDataException("PNG exceeds maximum compressed screenshot size");

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
            var chunkEnd = (long)offset + 4L + length + 4L;
            if (length < 0 || chunkEnd > png.Length)
                throw new InvalidDataException("invalid PNG chunk length");

            var type = Encoding.ASCII.GetString(png, offset, 4);
            offset += 4;
            var data = png.AsSpan(offset, length);
            offset += length;
            offset += 4;

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
        if (width > MaxScreenshotDimension || height > MaxScreenshotDimension)
            throw new InvalidDataException($"PNG dimensions exceed maximum screenshot size {MaxScreenshotDimension}x{MaxScreenshotDimension}");
        _ = checked(width * height);
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

        var rowBytes = checked(width * bytesPerPixel);
        var expectedBytes = checked((rowBytes + 1) * height);
        if (expectedBytes > MaxDecodedScanlineBytes)
            throw new InvalidDataException("PNG decoded scanline data exceeds maximum screenshot size");
        var scanlines = DecompressScanlines(idat, expectedBytes);

        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        var rgb = new byte[checked(width * height * 3)];
        var inputOffset = 0;

        for (var y = 0; y < height; y++)
        {
            var filter = scanlines[inputOffset++];
            Array.Copy(scanlines, inputOffset, current, 0, rowBytes);
            inputOffset += rowBytes;
            ApplyFilter(filter, current, previous, bytesPerPixel);

            for (var x = 0; x < width; x++)
            {
                var source = x * bytesPerPixel;
                var (r, g, b) = ReadRgb(current, source, colorType, palette);
                var target = ((y * width) + x) * 3;
                rgb[target] = (byte)r;
                rgb[target + 1] = (byte)g;
                rgb[target + 2] = (byte)b;
            }

            (previous, current) = (current, previous);
        }

        return new PngBitmap(width, height, rgb);
    }

    private static byte[] DecompressScanlines(MemoryStream idat, int expectedBytes)
    {
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        var scanlines = new byte[expectedBytes];
        var offset = 0;
        while (offset < scanlines.Length)
        {
            var read = zlib.Read(scanlines.AsSpan(offset));
            if (read == 0)
                throw new InvalidDataException("PNG image data is truncated");
            offset += read;
        }

        Span<byte> extra = stackalloc byte[1];
        if (zlib.Read(extra) > 0)
            throw new InvalidDataException("PNG image data exceeds expected decoded size");

        return scanlines;
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
