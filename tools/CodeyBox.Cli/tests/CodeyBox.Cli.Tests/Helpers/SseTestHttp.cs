using System.Net;
using System.Text;

namespace CodeyBox.Cli.Tests.Helpers;

internal static class SseTestHttp
{
    internal static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        TimeSpan? sseTimeout = null)
    {
        var sharedHandler = new FakeHttpMessageHandler(handler);
        return config =>
        {
            var baseUri = new Uri(config.ApiBaseUrl);
            var http = new HttpClient(sharedHandler)
            {
                BaseAddress = baseUri,
                Timeout = TimeSpan.FromSeconds(30),
            };
            var sse = new HttpClient(sharedHandler)
            {
                BaseAddress = baseUri,
                Timeout = sseTimeout ?? Timeout.InfiniteTimeSpan,
            };
            return new CodeyBoxClient(http, sse);
        };
    }

    internal sealed class NeverCompletesHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    internal sealed class DelayingEventsHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _inner;

        internal DelayingEventsHandler(
            TimeSpan delay,
            Func<HttpRequestMessage, HttpResponseMessage> inner)
        {
            _delay = delay;
            _inner = inner;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                await Task.Delay(_delay, cancellationToken);
            return _inner(request);
        }
    }

    internal sealed class TimeoutOnSecondReadStream : Stream
    {
        private readonly byte[] _firstLine;
        private int _readCount;

        internal TimeoutOnSecondReadStream(string firstState)
        {
            _firstLine = Encoding.UTF8.GetBytes(
                "data: {\"event\":\"work_item.state\",\"workItem\":{\"id\":\"aabbccdd-0000-0000-0000-000000000000\",\"state\":\"" +
                firstState + "\"}}\n\n");
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_readCount == 0)
            {
                _readCount++;
                _firstLine.CopyTo(buffer.Span);
                return _firstLine.Length;
            }

            throw new TaskCanceledException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
