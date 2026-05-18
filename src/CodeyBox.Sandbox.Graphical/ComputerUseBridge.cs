using CodeyBox.Core;

namespace CodeyBox.Sandbox.Graphical;

/// <summary>
/// Thin adapter from normalized computer-use tool calls to the sandbox
/// graphical capability surface. It intentionally knows nothing about
/// Multipass; callers translate Anthropic/OpenAI tool JSON into
/// <see cref="ComputerUseRequest"/> and pass the active sandbox.
/// </summary>
public sealed class ComputerUseBridge
{
    private readonly ComputerUseBridgeOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _inputBudgetGuard = new();
    private readonly Queue<(DateTimeOffset Timestamp, int Count)> _inputBudget = new();
    private int _inputBudgetUsed;

    public ComputerUseBridge(ComputerUseBridgeOptions? options = null, Func<DateTimeOffset>? utcNow = null)
    {
        _options = options ?? new ComputerUseBridgeOptions();
        ValidateOptions(_options);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ComputerUseResult> ExecuteAsync(
        ISandbox sandbox,
        ComputerUseRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Action))
            throw new ArgumentException("Computer-use Action is required.", nameof(request));

        var action = request.Action.Trim().ToLowerInvariant();
        switch (action)
        {
            case "screenshot":
                return new ComputerUseResult(
                    ScreenshotPng: await WithToolTimeoutAsync(sandbox.GetScreenshotAsync, ct),
                    Message: "screenshot");

            case "click":
            case "left_click":
                return await ExecuteInputAsync(
                    sandbox,
                    [new SandboxInputEvent { Type = SandboxInputEventType.Click, X = request.X, Y = request.Y }],
                    "click",
                    ct);

            case "double_click":
                return await ExecuteInputAsync(
                    sandbox,
                    [
                        new SandboxInputEvent { Type = SandboxInputEventType.Click, X = request.X, Y = request.Y },
                        new SandboxInputEvent { Type = SandboxInputEventType.Click, X = request.X, Y = request.Y },
                    ],
                    "double_click",
                    ct);

            case "move":
            case "mouse_move":
                return await ExecuteInputAsync(
                    sandbox,
                    [new SandboxInputEvent { Type = SandboxInputEventType.Move, X = request.X, Y = request.Y }],
                    "move",
                    ct);

            case "scroll":
                return await ExecuteInputAsync(
                    sandbox,
                    [new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = request.ScrollX ?? request.X, Y = request.ScrollY ?? request.Y }],
                    "scroll",
                    ct);

            case "key":
            case "keypress":
                return await ExecuteInputAsync(
                    sandbox,
                    [new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = request.Key ?? request.Text }],
                    "key",
                    ct);

            case "type":
                return await ExecuteInputAsync(
                    sandbox,
                    [new SandboxInputEvent { Type = SandboxInputEventType.Type, Text = request.Text }],
                    "type",
                    ct);

            case "events":
                if (request.Events is null)
                    throw new ArgumentException("The 'events' action requires Events.", nameof(request));
                return await ExecuteInputAsync(sandbox, request.Events, "events", ct);

            default:
                throw new NotSupportedException($"Unsupported computer-use action '{request.Action}'.");
        }
    }

    private async Task<ComputerUseResult> ExecuteInputAsync(
        ISandbox sandbox,
        IReadOnlyList<SandboxInputEvent> events,
        string message,
        CancellationToken ct)
    {
        var boundedEvents = events.ToArray();
        ValidateEvents(boundedEvents);
        ReserveInputBudget(boundedEvents.Length);
        await WithToolTimeoutAsync(token => sandbox.SynthesizeInputAsync(boundedEvents, token), ct);
        return ComputerUseResult.Ok(message);
    }

    private void ValidateEvents(IReadOnlyList<SandboxInputEvent> events)
    {
        SandboxInputEventValidation.Validate(
            events,
            _options.MaxEventsPerCall,
            _options.MaxTextUtf8Bytes,
            _options.MaxKeyUtf8Bytes,
            _options.MaxCoordinate,
            _options.MaxScrollMagnitude);
    }

    private void ReserveInputBudget(int eventCount)
    {
        var now = _utcNow();
        var cutoff = now - _options.RateLimitWindow;
        lock (_inputBudgetGuard)
        {
            while (_inputBudget.Count > 0 && _inputBudget.Peek().Timestamp <= cutoff)
            {
                _inputBudgetUsed -= _inputBudget.Dequeue().Count;
            }

            if (_inputBudgetUsed + eventCount > _options.MaxInputEventsPerWindow)
                throw new InvalidOperationException($"Computer-use input rate limit exceeded: {_options.MaxInputEventsPerWindow} events per {_options.RateLimitWindow}.");

            _inputBudget.Enqueue((now, eventCount));
            _inputBudgetUsed += eventCount;
        }
    }

    private async Task WithToolTimeoutAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ToolCallTimeout);
        try
        {
            await operation(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Computer-use tool call exceeded timeout {_options.ToolCallTimeout}.");
        }
    }

    private async Task<T> WithToolTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ToolCallTimeout);
        try
        {
            return await operation(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Computer-use tool call exceeded timeout {_options.ToolCallTimeout}.");
        }
    }

    private static void ValidateOptions(ComputerUseBridgeOptions options)
    {
        if (options.MaxEventsPerCall <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxEventsPerCall must be positive.");
        if (options.MaxTextUtf8Bytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxTextUtf8Bytes must be positive.");
        if (options.MaxKeyUtf8Bytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxKeyUtf8Bytes must be positive.");
        if (options.MaxCoordinate < 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxCoordinate cannot be negative.");
        if (options.MaxScrollMagnitude <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxScrollMagnitude must be positive.");
        if (options.ToolCallTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "ToolCallTimeout must be positive.");
        if (options.MaxInputEventsPerWindow <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxInputEventsPerWindow must be positive.");
        if (options.RateLimitWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "RateLimitWindow must be positive.");
    }
}

public sealed record ComputerUseBridgeOptions
{
    public int MaxEventsPerCall { get; init; } = SandboxInputEventValidation.DefaultMaxEvents;
    public int MaxTextUtf8Bytes { get; init; } = SandboxInputEventValidation.DefaultMaxTextUtf8Bytes;
    public int MaxKeyUtf8Bytes { get; init; } = SandboxInputEventValidation.DefaultMaxKeyUtf8Bytes;
    public int MaxCoordinate { get; init; } = SandboxInputEventValidation.DefaultMaxCoordinate;
    public int MaxScrollMagnitude { get; init; } = SandboxInputEventValidation.DefaultMaxScrollMagnitude;
    public TimeSpan ToolCallTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxInputEventsPerWindow { get; init; } = 240;
    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromMinutes(1);
}

public sealed record ComputerUseRequest
{
    public required string Action { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? ScrollX { get; init; }
    public int? ScrollY { get; init; }
    public string? Key { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<SandboxInputEvent>? Events { get; init; }
}

public sealed record ComputerUseResult(byte[]? ScreenshotPng, string Message)
{
    public static ComputerUseResult Ok(string message) => new(ScreenshotPng: null, Message: message);
}
