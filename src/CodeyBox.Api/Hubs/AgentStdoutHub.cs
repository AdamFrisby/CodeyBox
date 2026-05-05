using Microsoft.AspNetCore.SignalR;

namespace CodeyBox.Api.Hubs;

/// <summary>
/// SignalR hub that streams live agent stdout to connected dashboard clients.
///
/// Auth: protected by the existing ApiKeyAuth middleware, which validates
/// the bearer token on the HTTP upgrade request before SignalR completes
/// the connection handshake. No separate [Authorize] attribute is needed
/// because no ASP.NET Core auth scheme is registered — the middleware gate
/// is the only boundary.
///
/// Clients join a per-work-item group via SubscribeAsync and receive
/// "stdoutChunk" and "streamComplete" messages while the agent runs.
/// </summary>
public sealed class AgentStdoutHub : Hub
{
    public Task SubscribeAsync(string workItemId)
        => Groups.AddToGroupAsync(Context.ConnectionId, $"wi:{workItemId}");

    public Task UnsubscribeAsync(string workItemId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"wi:{workItemId}");
}
