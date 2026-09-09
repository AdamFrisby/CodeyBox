using System.Net;

namespace CodeyBox.Tests;

internal sealed record RetryAfterResponse(
    HttpStatusCode Status,
    string Body,
    TimeSpan? RetryAfter,
    DateTimeOffset? RetryAfterDate = null);

internal sealed record RetryAfterRequest(HttpMethod Method, Uri? RequestUri, string? Authorization);

internal sealed class RetryAfterSequenceHandler : HttpMessageHandler
{
    private readonly Queue<RetryAfterResponse> _responses;
    public int CallCount { get; private set; }
    public List<RetryAfterRequest> Requests { get; } = [];

    public RetryAfterSequenceHandler(params RetryAfterResponse[] responses)
    {
        _responses = new Queue<RetryAfterResponse>(responses);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        Requests.Add(new RetryAfterRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.ToString()));

        if (_responses.Count == 0)
            throw new InvalidOperationException("RetryAfterSequenceHandler ran out of canned responses");

        var responseSpec = _responses.Dequeue();
        var response = new HttpResponseMessage(responseSpec.Status)
        {
            Content = new StringContent(responseSpec.Body),
        };
        if (responseSpec.RetryAfterDate is { } retryAfterDate)
        {
            response.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfterDate);
        }
        else if (responseSpec.RetryAfter is { } retryAfter)
        {
            response.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
        }

        return Task.FromResult(response);
    }
}

/// <summary>
/// Captures <c>Task.Delay(_, TimeProvider, ct)</c> due times and completes the
/// timer synchronously so retry tests do not depend on wall-clock scheduling.
/// </summary>
internal sealed class CapturingDelayTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;
    public List<TimeSpan> Delays { get; } = [];

    public CapturingDelayTimeProvider(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Delays.Add(dueTime);
        callback(state);
        return CompletedTimer.Instance;
    }

    private sealed class CompletedTimer : ITimer
    {
        public static CompletedTimer Instance { get; } = new();

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
