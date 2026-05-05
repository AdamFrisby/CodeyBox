using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class StdoutTailRingBufferTests
{
    [Fact]
    public void GetContents_Empty_ReturnsEmptyString()
    {
        var buf = new StdoutRingBuffer();
        Assert.Equal("", buf.GetContents());
    }

    [Fact]
    public void Append_ShortString_GetContents_ReturnsExact()
    {
        var buf = new StdoutRingBuffer();
        buf.Append("hello world");
        Assert.Equal("hello world", buf.GetContents());
    }

    [Fact]
    public void Append_EmptyString_Ignored()
    {
        var buf = new StdoutRingBuffer();
        buf.Append("initial");
        buf.Append("");
        Assert.Equal("initial", buf.GetContents());
    }

    [Fact]
    public void Append_NullStringEmpty_NoChange()
    {
        var buf = new StdoutRingBuffer();
        buf.Append("data");
        buf.Append(string.Empty);
        Assert.Equal("data", buf.GetContents());
    }

    [Fact]
    public void Append_MultipleChunks_Concatenated()
    {
        var buf = new StdoutRingBuffer();
        buf.Append("foo");
        buf.Append("bar");
        buf.Append("baz");
        Assert.Equal("foobarbaz", buf.GetContents());
    }

    [Fact]
    public void Append_ExactlyCapacity_RetainsAll()
    {
        var buf = new StdoutRingBuffer();
        var text = new string('A', StdoutRingBuffer.CapacityBytes);
        buf.Append(text);
        Assert.Equal(text, buf.GetContents());
    }

    [Fact]
    public void Append_OneOverCapacity_EvictsOldestChar()
    {
        var buf = new StdoutRingBuffer();
        buf.Append(new string('X', StdoutRingBuffer.CapacityBytes));
        buf.Append("Z"); // pushes one 'X' out

        var contents = buf.GetContents();
        Assert.Equal(StdoutRingBuffer.CapacityBytes, contents.Length);
        Assert.EndsWith("Z", contents);
    }

    [Fact]
    public void Append_LargeChunk_OverCapacity_TailIsNewestContent()
    {
        var buf = new StdoutRingBuffer();
        buf.Append(new string('A', StdoutRingBuffer.CapacityBytes));
        const string tail = "TAIL";
        buf.Append(tail);

        var contents = buf.GetContents();
        Assert.Equal(StdoutRingBuffer.CapacityBytes, contents.Length);
        Assert.EndsWith(tail, contents);
    }

    [Fact]
    public void Append_DoubleFill_OnlyLastCapacityBytesKept()
    {
        var buf = new StdoutRingBuffer();
        buf.Append(new string('A', StdoutRingBuffer.CapacityBytes));
        buf.Append(new string('B', StdoutRingBuffer.CapacityBytes));

        var contents = buf.GetContents();
        Assert.Equal(StdoutRingBuffer.CapacityBytes, contents.Length);
        Assert.All(contents.ToCharArray(), c => Assert.Equal('B', c));
    }

    [Fact]
    public void Append_WrapsAroundCorrectly_ContentsAreOrdered()
    {
        var buf = new StdoutRingBuffer();
        // Fill buffer with 'A's, then overwrite the front with 'B's
        buf.Append(new string('A', StdoutRingBuffer.CapacityBytes));
        var suffix = new string('B', 10);
        buf.Append(suffix);

        var contents = buf.GetContents();
        Assert.Equal(StdoutRingBuffer.CapacityBytes, contents.Length);
        // The last 10 chars should be 'B'; the rest should be 'A'
        Assert.Equal(new string('A', StdoutRingBuffer.CapacityBytes - 10) + suffix, contents);
    }
}
