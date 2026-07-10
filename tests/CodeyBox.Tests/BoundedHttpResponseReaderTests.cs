using System.Net;
using CodeyBox.Agents;

namespace CodeyBox.Tests;

public sealed class BoundedHttpResponseReaderTests
{
    [Fact]
    public async Task SendAsync_StopsStreamingAfterByteCapWithoutContentLength()
    {
        const int cap = 1024;
        var stream = new CountingDataStream(totalBytes: 1024 * 1024);
        using var client = new HttpClient(new StreamingHandler(stream));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://provider.invalid/review");

        var response = await BoundedHttpResponseReader.SendAsync(client, request, cap);

        Assert.True(response.BodyTooLarge);
        Assert.Null(response.Body);
        Assert.Equal(cap + 1, stream.BytesRead);
    }

    private sealed class StreamingHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            });
    }

    private sealed class CountingDataStream(long totalBytes) : Stream
    {
        private long _remaining = totalBytes;
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, _remaining);
            buffer.AsSpan(offset, read).Fill((byte)'x');
            _remaining -= read;
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer));
        }

        private int Read(Memory<byte> buffer)
        {
            var read = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..read].Fill((byte)'x');
            _remaining -= read;
            BytesRead += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
