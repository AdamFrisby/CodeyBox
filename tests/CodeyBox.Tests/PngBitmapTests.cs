using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using CodeyBox.ExploratoryTesting;
using CodeyBox.ExploratoryTesting.Replay;

namespace CodeyBox.Tests;

/// <summary>
/// Direct unit coverage for the hand-rolled <see cref="PngBitmap"/> decoder and
/// its template-match search. The decoder is exercised through fixed
/// golden-vector PNGs (truecolor, grayscale, indexed, and a Sub+Paeth filtered
/// image whose expected pixels are computed by hand) so a wrong filter
/// predictor, palette-index overflow, bad signature, or template-match
/// ambiguity bug fails a test instead of only surfacing as a generic replay
/// NotFound.
/// </summary>
public sealed class PngBitmapTests
{
    private const int None = 0;
    private const int Sub = 1;
    private const int Paeth = 4;

    [Fact]
    public void TryDecode_ReturnsFalse_OnMissingSignature()
    {
        Assert.False(PngBitmap.TryDecode([1, 2, 3, 4, 5, 6, 7, 8, 9], out _));
    }

    [Fact]
    public void TryDecode_ReturnsFalse_OnTruncatedIdat()
    {
        // Valid header + IEND but a too-short IDAT stream: decompression cannot
        // fill the expected scanline buffer → decode fails cleanly (no throw
        // escaping TryDecode).
        var png = AssemblePng(2, 2, colorType: 2, idat: [0x00, 0x01, 0x02], palette: null);
        Assert.False(PngBitmap.TryDecode(png, out _));
    }

    [Fact]
    public void TryDecode_DecodesTruecolor_AndTemplateMatchLocatesEachPixel()
    {
        // 2x2, four distinct colours, filter None throughout.
        var scan = new byte[]
        {
            None, 10, 0, 0,  20, 0, 0,
            None, 0, 30, 0,  0, 0, 40,
        };
        var png = AssemblePng(2, 2, colorType: 2, idat: Deflate(scan), palette: null);

        Assert.True(PngBitmap.TryDecode(png, out var bitmap));
        Assert.Equal(2, bitmap.Width);
        Assert.Equal(2, bitmap.Height);

        AssertPixelAt(bitmap, 10, 0, 0, 0, 0);
        AssertPixelAt(bitmap, 20, 0, 0, 1, 0);
        AssertPixelAt(bitmap, 0, 30, 0, 0, 1);
        AssertPixelAt(bitmap, 0, 0, 40, 1, 1);
    }

    [Fact]
    public void TryDecode_DecodesGrayscale_WithSubAndPaethFilters()
    {
        // 2x2 grayscale gradient {10,20 / 30,40}. Row 0 Sub-filtered, row 1
        // Paeth-filtered. The filtered bytes below are computed by hand from
        // the PNG filter definitions, so a wrong Sub or Paeth predictor
        // reconstructs the wrong pixels.
        //  row0 Sub:   [10, 10]  -> decode 10, 10+10=20
        //  row1 Paeth: [20, 10]  -> Paeth(0,10,0)=10 => 30 ; Paeth(30,20,10)=30 => 40
        var scan = new byte[]
        {
            Sub, 10, 10,
            Paeth, 20, 10,
        };
        var png = AssemblePng(2, 2, colorType: 0, idat: Deflate(scan), palette: null);

        Assert.True(PngBitmap.TryDecode(png, out var bitmap));
        // Grayscale expands to (v,v,v); each value is unique so a 1x1 template
        // search must find it at exactly its coordinate.
        AssertPixelAt(bitmap, 10, 10, 10, 0, 0);
        AssertPixelAt(bitmap, 20, 20, 20, 1, 0);
        AssertPixelAt(bitmap, 30, 30, 30, 0, 1);
        AssertPixelAt(bitmap, 40, 40, 40, 1, 1);
    }

    [Fact]
    public void TryDecode_DecodesIndexedPalette()
    {
        var palette = new byte[] { 200, 100, 50 }; // index 0 -> (200,100,50)
        var scan = new byte[] { None, 0 };          // 1x1, index 0
        var png = AssemblePng(1, 1, colorType: 3, idat: Deflate(scan), palette: palette);

        Assert.True(PngBitmap.TryDecode(png, out var bitmap));
        AssertPixelAt(bitmap, 200, 100, 50, 0, 0);
    }

    [Fact]
    public void TryDecode_ReturnsFalse_OnPaletteIndexOutOfRange()
    {
        var palette = new byte[] { 200, 100, 50 }; // only index 0 valid
        var scan = new byte[] { None, 5 };          // index 5 -> out of range
        var png = AssemblePng(1, 1, colorType: 3, idat: Deflate(scan), palette: palette);

        Assert.False(PngBitmap.TryDecode(png, out _));
    }

    [Fact]
    public void TryCrop_ExtractsRegion_MatchingDirectlyDecodedCrop()
    {
        var bitmap = DecodeTruecolor(3, 1, [10, 0, 0, 20, 0, 0, 30, 0, 0]);
        var expected = DecodeTruecolor(2, 1, [20, 0, 0, 30, 0, 0]);

        Assert.True(bitmap.TryCrop(new TraceBoundingRegion { X = 1, Y = 0, Width = 2, Height = 1 }, out var crop));
        Assert.True(crop.HasSamePixelsAs(expected));
        Assert.False(crop.HasSamePixelsAs(bitmap));
    }

    [Fact]
    public void TryCrop_ReturnsFalse_WhenRegionOutOfBounds()
    {
        var bitmap = DecodeTruecolor(2, 1, [10, 0, 0, 20, 0, 0]);
        Assert.False(bitmap.TryCrop(new TraceBoundingRegion { X = 1, Y = 0, Width = 5, Height = 1 }, out _));
    }

    [Fact]
    public void TryFindBestMatch_ReturnsFalse_WhenTemplateAppearsTwice()
    {
        // Ambiguous: the (10,0,0) template appears at both x=0 and x=2. The
        // search must report "no unique match" (false), NOT silently pick the
        // first hit.
        var haystack = DecodeTruecolor(3, 1, [10, 0, 0, 20, 0, 0, 10, 0, 0]);
        var template = DecodeTruecolor(1, 1, [10, 0, 0]);

        Assert.False(haystack.TryFindBestExact(template, CancellationToken.None, out _));
    }

    [Fact]
    public void TryFindBestMatch_ReturnsFalse_WhenTemplateAbsent()
    {
        var haystack = DecodeTruecolor(2, 1, [10, 0, 0, 20, 0, 0]);
        var template = DecodeTruecolor(1, 1, [99, 99, 99]);

        Assert.False(haystack.TryFindBestExact(template, CancellationToken.None, out _));
    }

    [Fact]
    public void TryFindBestTolerant_MatchesWithinChannelDelta()
    {
        // Haystack pixel differs from the template by 3 per channel — inside the
        // ReplayTolerance envelope (per-channel delta 16) so tolerant search
        // matches while an exact search does not.
        var haystack = DecodeTruecolor(1, 1, [103, 103, 103]);
        var template = DecodeTruecolor(1, 1, [100, 100, 100]);

        Assert.True(haystack.TryFindBestTolerant(template, CancellationToken.None, out var point));
        Assert.Equal((0, 0), point);
        Assert.False(haystack.TryFindBestExact(template, CancellationToken.None, out _));
    }

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    private static PngBitmap DecodeTruecolor(int width, int height, byte[] rgb)
    {
        var scan = new byte[(width * 3 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (width * 3 + 1);
            scan[rowStart] = None;
            Array.Copy(rgb, y * width * 3, scan, rowStart + 1, width * 3);
        }
        var png = AssemblePng(width, height, colorType: 2, idat: Deflate(scan), palette: null);
        Assert.True(PngBitmap.TryDecode(png, out var bitmap));
        return bitmap;
    }

    private static void AssertPixelAt(PngBitmap bitmap, int r, int g, int b, int x, int y)
    {
        // A 1x1 exact-match search of the given colour must land on (x,y); this
        // is the only way to read decoded pixels without exposing internals.
        var template = DecodeTruecolor(1, 1, [(byte)r, (byte)g, (byte)b]);
        Assert.True(bitmap.MatchesAt(template, x, y), $"expected ({r},{g},{b}) at ({x},{y})");
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }
        return output.ToArray();
    }

    private static byte[] AssemblePng(int width, int height, byte colorType, byte[] idat, byte[]? palette)
    {
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;          // bit depth
        ihdr[9] = colorType;  // colour type
        WriteChunk(png, "IHDR", ihdr);
        if (palette is not null)
            WriteChunk(png, "PLTE", palette);
        WriteChunk(png, "IDAT", idat);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        png.Write(Encoding.ASCII.GetBytes(type));
        png.Write(data);
        png.Write([0, 0, 0, 0]); // CRC placeholder — decoder does not validate CRCs
    }
}
