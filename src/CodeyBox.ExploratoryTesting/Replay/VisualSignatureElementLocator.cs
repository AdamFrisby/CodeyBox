using CodeyBox.Core;
using System.IO.Compression;
using System.Text;

namespace CodeyBox.ExploratoryTesting.Replay;

internal enum VisualTargetVerificationStatus
{
    Verified,
    Mismatch,
    Unverifiable,
}

/// <summary>
/// Non-accessibility recognition fallback. The locator consumes the recorded
/// visual descriptor in this order:
///
/// <list type="number">
///   <item>Search the current screenshot for <see cref="TraceVisualDescriptor.TemplatePng"/>.</item>
///   <item>When no explicit template exists, crop the recorded
///   <see cref="TraceVisualDescriptor.SourceScreenshotPng"/> at
///   <see cref="TraceVisualDescriptor.Region"/> and search for that crop on
///   the current screenshot.</item>
///   <item>Use <see cref="TraceVisualDescriptor.OcrText"/> against the
///   current accessibility/OCR tree when the sandbox exposes text bounds.</item>
///   <item>As a final compatibility fallback, accept a full current/source
///   byte match.</item>
/// </list>
///
/// <para>All positive paths require some recorded visual signal to match the
/// current screen. When matching cannot be proven, the locator returns null so
/// replay fails with NotFound instead of clicking stale coordinates.</para>
/// </summary>
public sealed class VisualSignatureElementLocator : IElementLocator
{
    private const int MaxExactSearchPositions = 2_000_000;
    private const int MaxExactTemplatePixels = 512 * 512;
    private const int MaxLowEntropyTemplatePixels = 64 * 64;
    private const int MaxLowEntropyDistinctColors = 2;

    private readonly IElementLocator _accessibilityLocator;
    private readonly IOcrTextLocator _ocrLocator;

    public VisualSignatureElementLocator()
        : this(DefaultAccessibilityMatcher.Instance)
    {
    }

    public VisualSignatureElementLocator(IAccessibilityMatcher matcher)
        : this(new AccessibilityElementLocator(matcher))
    {
    }

    public VisualSignatureElementLocator(IElementLocator accessibilityLocator)
        : this(accessibilityLocator, null)
    {
    }

    public VisualSignatureElementLocator(IElementLocator accessibilityLocator, IOcrTextLocator? ocrLocator)
    {
        _accessibilityLocator = accessibilityLocator
            ?? throw new ArgumentNullException(nameof(accessibilityLocator));
        _ocrLocator = ocrLocator ?? TesseractOcrTextLocator.Instance;
    }

    public async Task<LocatedTarget?> LocateAsync(
        ISandbox sandbox,
        TraceTargetDescriptor descriptor,
        ReplayOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);

        var visual = descriptor.Visual;
        var region = visual.Region;
        if (!HasAnyVisualSignal(visual)) return null;

        var current = await sandbox.GetScreenshotAsync(ct).ConfigureAwait(false);

        if (current is null || current.Length == 0) return null;

        if (TryLocateTemplate(current, visual, ct, out var templateHit))
            return templateHit;

        if (TryLocateSourceCrop(current, visual, ct, out var cropHit))
            return cropHit;

        var ocrTreeHit = await LocateOcrTextAsync(sandbox, current, visual, options, ct).ConfigureAwait(false);
        if (ocrTreeHit is not null) return ocrTreeHit;

        var source = visual.SourceScreenshotPng;
        if (source is not null
            && source.Length > 0
            && region.Width > 0
            && region.Height > 0
            && current.Length == source.Length
            && current.AsSpan().SequenceEqual(source))
        {
            return FromRegion(visual, "visual-signature", 0.85);
        }

        return null;
    }

    internal static VisualTargetVerificationStatus VerifyVisualTargetAt(
        byte[] currentPng,
        TraceVisualDescriptor visual,
        LocatedTarget target)
    {
        if (visual.TemplatePng is { Length: > 0 }
            && PngBitmap.TryDecode(currentPng, out var currentTemplateScreen)
            && PngBitmap.TryDecode(visual.TemplatePng, out var template))
        {
            return currentTemplateScreen.MatchesAt(template, target.Region.X, target.Region.Y)
                ? VisualTargetVerificationStatus.Verified
                : VisualTargetVerificationStatus.Mismatch;
        }

        if (visual.SourceScreenshotPng is { Length: > 0 } sourcePng)
        {
            if (PngBitmap.TryDecode(sourcePng, out var source)
                && PngBitmap.TryDecode(currentPng, out var current)
                && source.TryCrop(visual.Region, out var crop))
            {
                return current.MatchesAt(crop, target.Region.X, target.Region.Y)
                    ? VisualTargetVerificationStatus.Verified
                    : VisualTargetVerificationStatus.Mismatch;
            }

            if (currentPng.Length == sourcePng.Length && currentPng.AsSpan().SequenceEqual(sourcePng))
                return VisualTargetVerificationStatus.Verified;
        }

        return VisualTargetVerificationStatus.Unverifiable;
    }

    private static bool TryLocateTemplate(
        byte[] currentPng,
        TraceVisualDescriptor visual,
        CancellationToken ct,
        out LocatedTarget? hit)
    {
        hit = null;
        var templatePng = visual.TemplatePng;
        if (templatePng is null || templatePng.Length == 0) return false;

        if (PngBitmap.TryDecode(currentPng, out var current)
            && PngBitmap.TryDecode(templatePng, out var template)
            && current.TryFindBestExact(template, PreferredTopLeft(visual.Region), ct, out var point))
        {
            var (offsetX, offsetY) = ResolveClickOffset(visual, template.Width, template.Height);
            hit = new LocatedTarget
            {
                CenterX = point.X + offsetX,
                CenterY = point.Y + offsetY,
                Region = new TraceBoundingRegion
                {
                    X = point.X,
                    Y = point.Y,
                    Width = template.Width,
                    Height = template.Height,
                },
                Source = "visual-template",
                Confidence = 0.9,
            };
            return true;
        }

        return false;
    }

    private static bool TryLocateSourceCrop(
        byte[] currentPng,
        TraceVisualDescriptor visual,
        CancellationToken ct,
        out LocatedTarget? hit)
    {
        hit = null;
        var sourcePng = visual.SourceScreenshotPng;
        if (sourcePng is null || sourcePng.Length == 0) return false;

        if (PngBitmap.TryDecode(sourcePng, out var source)
            && PngBitmap.TryDecode(currentPng, out var current)
            && source.TryCrop(visual.Region, out var crop)
            && current.TryFindBestExact(crop, PreferredTopLeft(visual.Region), ct, out var point))
        {
            var (offsetX, offsetY) = ResolveClickOffset(visual, crop.Width, crop.Height);
            hit = new LocatedTarget
            {
                CenterX = point.X + offsetX,
                CenterY = point.Y + offsetY,
                Region = new TraceBoundingRegion
                {
                    X = point.X,
                    Y = point.Y,
                    Width = crop.Width,
                    Height = crop.Height,
                },
                Source = "visual-source-crop",
                Confidence = 0.88,
            };
            return true;
        }

        return false;
    }

    private async Task<LocatedTarget?> LocateOcrTextAsync(
        ISandbox sandbox,
        byte[] currentPng,
        TraceVisualDescriptor visual,
        ReplayOptions options,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(visual.OcrText)) return null;

        var descriptor = new TraceTargetDescriptor
        {
            Accessibility = new TraceAccessibilityDescriptor { Text = visual.OcrText },
            Visual = new TraceVisualDescriptor { Region = visual.Region },
        };
        var hit = await _accessibilityLocator.LocateAsync(sandbox, descriptor, options, ct).ConfigureAwait(false);
        if (hit is not null) return hit with { Source = "visual-ocr-tree" };

        descriptor = descriptor with
        {
            Accessibility = new TraceAccessibilityDescriptor { Name = visual.OcrText },
        };
        hit = await _accessibilityLocator.LocateAsync(sandbox, descriptor, options, ct).ConfigureAwait(false);
        if (hit is not null) return hit with { Source = "visual-ocr-tree" };

        return await _ocrLocator.LocateTextAsync(sandbox, currentPng, visual, ct).ConfigureAwait(false);
    }

    private static bool HasAnyVisualSignal(TraceVisualDescriptor visual)
    {
        var region = visual.Region;
        return region.Width > 0 && region.Height > 0
            || visual.TemplatePng is { Length: > 0 }
            || visual.SourceScreenshotPng is { Length: > 0 }
            || !string.IsNullOrWhiteSpace(visual.OcrText);
    }

    private static LocatedTarget FromRegion(TraceVisualDescriptor visual, string source, double confidence)
    {
        var region = visual.Region;
        var (offsetX, offsetY) = ResolveClickOffset(visual, region.Width, region.Height);
        return new LocatedTarget
        {
            CenterX = region.X + offsetX,
            CenterY = region.Y + offsetY,
            Region = region,
            Source = source,
            Confidence = confidence,
        };
    }

    private static (int X, int Y)? PreferredTopLeft(TraceBoundingRegion region) =>
        region.Width > 0 && region.Height > 0 ? (region.X, region.Y) : null;

    private static (int X, int Y) ResolveClickOffset(
        TraceVisualDescriptor visual,
        int width,
        int height)
    {
        var offsetX = visual.ClickOffsetX is int x && x >= 0 && x < width ? x : width / 2;
        var offsetY = visual.ClickOffsetY is int y && y >= 0 && y < height ? y : height / 2;
        return (offsetX, offsetY);
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
            (int X, int Y)? preferredTopLeft,
            CancellationToken ct,
            out (int X, int Y) point)
        {
            point = default;
            if (template.Width <= 0 || template.Height <= 0) return false;
            if (template.Width > Width || template.Height > Height) return false;

            if (preferredTopLeft is { } preferred
                && MatchesAt(template, preferred.X, preferred.Y, ct))
            {
                point = preferred;
                return true;
            }

            if (template.PixelCount > MaxExactTemplatePixels) return false;
            var candidatePositions = ((long)Width - template.Width + 1) * (Height - template.Height + 1);
            if (candidatePositions > MaxExactSearchPositions) return false;
            if (template.PixelCount > MaxLowEntropyTemplatePixels
                && template.HasLowSampledColorDiversity(MaxLowEntropyDistinctColors))
            {
                return false;
            }

            var found = false;
            var ambiguous = false;
            var bestScore = long.MaxValue;
            for (var y = 0; y <= Height - template.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (var x = 0; x <= Width - template.Width; x++)
                {
                    if (!LikelyMatchAt(template, x, y)) continue;
                    if (!MatchesAt(template, x, y, ct)) continue;
                    if (preferredTopLeft is not { } target)
                    {
                        if (found) return false;
                        point = (x, y);
                        found = true;
                        continue;
                    }

                    var score = DistanceSquared(x, y, target.X, target.Y);
                    if (score < bestScore)
                    {
                        point = (x, y);
                        bestScore = score;
                        ambiguous = false;
                        found = true;
                    }
                    else if (score == bestScore)
                    {
                        ambiguous = true;
                    }
                }
            }

            return found && !ambiguous;
        }

        public bool MatchesAt(PngBitmap template, int x, int y, CancellationToken ct = default)
        {
            if (x < 0 || y < 0) return false;
            if (template.Width <= 0 || template.Height <= 0) return false;
            if ((long)x + template.Width > Width || (long)y + template.Height > Height) return false;

            var rowBytes = template.Width * 3;
            for (var ty = 0; ty < template.Height; ty++)
            {
                if ((ty & 0x3F) == 0) ct.ThrowIfCancellationRequested();
                var source = (((y + ty) * Width) + x) * 3;
                var target = ty * rowBytes;
                if (!_rgb.AsSpan(source, rowBytes).SequenceEqual(template._rgb.AsSpan(target, rowBytes)))
                    return false;
            }
            return true;
        }

        public bool HasSamePixelsAs(PngBitmap other)
        {
            if (other.Width != Width || other.Height != Height) return false;
            return _rgb.AsSpan().SequenceEqual(other._rgb);
        }

        private bool LikelyMatchAt(PngBitmap template, int x, int y)
        {
            return PixelMatches(template, x, y, 0, 0)
                && PixelMatches(template, x + template.Width - 1, y, template.Width - 1, 0)
                && PixelMatches(template, x, y + template.Height - 1, 0, template.Height - 1)
                && PixelMatches(template, x + template.Width - 1, y + template.Height - 1, template.Width - 1, template.Height - 1);
        }

        private bool PixelMatches(PngBitmap template, int x, int y, int tx, int ty)
        {
            var source = ((y * Width) + x) * 3;
            var target = ((ty * template.Width) + tx) * 3;
            return _rgb[source] == template._rgb[target]
                && _rgb[source + 1] == template._rgb[target + 1]
                && _rgb[source + 2] == template._rgb[target + 2];
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

        private static long DistanceSquared(int x1, int y1, int x2, int y2)
        {
            var dx = (long)x1 - x2;
            var dy = (long)y1 - y2;
            return dx * dx + dy * dy;
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
}
