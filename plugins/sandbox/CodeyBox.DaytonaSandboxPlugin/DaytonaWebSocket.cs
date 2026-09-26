using System.Net.WebSockets;

namespace CodeyBox.DaytonaSandboxPlugin;

/// <summary>
/// Minimal WebSocket surface the exec log-follow path needs, abstracted so
/// tests can drive the demux/exit-code loop without a live daemon. Mirrors the
/// <c>ISpritesWebSocket</c> seam in the Sprites provider.
/// </summary>
internal interface IDaytonaWebSocket : IAsyncDisposable
{
    WebSocketState State { get; }
    WebSocketCloseStatus? CloseStatus { get; }
    Task ConnectAsync(Uri uri, string bearerToken, string? organizationId, CancellationToken ct);
    Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct);
    Task CloseAsync(CancellationToken ct);
}

internal interface IDaytonaWebSocketFactory
{
    IDaytonaWebSocket Create();
}

internal sealed class ClientWebSocketDaytonaWebSocketFactory : IDaytonaWebSocketFactory
{
    public IDaytonaWebSocket Create() => new ClientWebSocketDaytonaWebSocket();
}

internal sealed class ClientWebSocketDaytonaWebSocket : IDaytonaWebSocket
{
    private readonly ClientWebSocket _socket = new();

    public WebSocketState State => _socket.State;
    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

    public async Task ConnectAsync(Uri uri, string bearerToken, string? organizationId, CancellationToken ct)
    {
        // The API key authenticates the toolbox proxy hop the same way it does
        // control-plane REST. It is set as a WS handshake header — never on the
        // URI — so it cannot leak through logged request URIs.
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
        if (!string.IsNullOrEmpty(organizationId))
            _socket.Options.SetRequestHeader("X-Daytona-Organization-ID", organizationId);
        try
        {
            await _socket.ConnectAsync(uri, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new DaytonaApiException(
                DaytonaFailureKind.Unreachable, "connect log stream", ex.Message, null, null, ex);
        }
    }

    public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) =>
        _socket.ReceiveAsync(buffer, ct);

    public async Task CloseAsync(CancellationToken ct)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Closing handshake is best-effort; the daemon may already be gone.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _socket.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
