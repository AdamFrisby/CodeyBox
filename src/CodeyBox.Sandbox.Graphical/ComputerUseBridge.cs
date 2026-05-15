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
    public async Task<ComputerUseResult> ExecuteAsync(
        ISandbox sandbox,
        ComputerUseRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(request);

        var action = request.Action.Trim().ToLowerInvariant();
        switch (action)
        {
            case "screenshot":
                return new ComputerUseResult(
                    ScreenshotPng: await sandbox.GetScreenshotAsync(ct),
                    Message: "screenshot");

            case "click":
            case "left_click":
                await sandbox.SynthesizeInputAsync(
                    [new SandboxInputEvent { Type = SandboxInputEventType.Click, X = request.X, Y = request.Y }],
                    ct);
                return ComputerUseResult.Ok("click");

            case "double_click":
                await sandbox.SynthesizeInputAsync(
                    [
                        new SandboxInputEvent { Type = SandboxInputEventType.Click, X = request.X, Y = request.Y },
                        new SandboxInputEvent { Type = SandboxInputEventType.Click, X = request.X, Y = request.Y },
                    ],
                    ct);
                return ComputerUseResult.Ok("double_click");

            case "move":
            case "mouse_move":
                await sandbox.SynthesizeInputAsync(
                    [new SandboxInputEvent { Type = SandboxInputEventType.Move, X = request.X, Y = request.Y }],
                    ct);
                return ComputerUseResult.Ok("move");

            case "scroll":
                await sandbox.SynthesizeInputAsync(
                    [new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = request.ScrollX ?? request.X, Y = request.ScrollY ?? request.Y }],
                    ct);
                return ComputerUseResult.Ok("scroll");

            case "key":
            case "keypress":
                await sandbox.SynthesizeInputAsync(
                    [new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = request.Key ?? request.Text }],
                    ct);
                return ComputerUseResult.Ok("key");

            case "type":
                await sandbox.SynthesizeInputAsync(
                    [new SandboxInputEvent { Type = SandboxInputEventType.Type, Text = request.Text }],
                    ct);
                return ComputerUseResult.Ok("type");

            case "events":
                if (request.Events is null)
                    throw new ArgumentException("The 'events' action requires Events.", nameof(request));
                await sandbox.SynthesizeInputAsync(request.Events, ct);
                return ComputerUseResult.Ok("events");

            default:
                throw new NotSupportedException($"Unsupported computer-use action '{request.Action}'.");
        }
    }
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
