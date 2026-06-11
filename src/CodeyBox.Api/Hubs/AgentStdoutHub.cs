using Microsoft.AspNetCore.SignalR;
using CodeyBox.Orchestrator;

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
    internal const string SupervisionAllGroup = "supervision:all";

    private readonly IAgentSupervisionService _supervision;

    public AgentStdoutHub(IAgentSupervisionService supervision) => _supervision = supervision;

    public Task SubscribeAsync(string workItemId)
        => Groups.AddToGroupAsync(Context.ConnectionId, $"wi:{workItemId}");

    public Task UnsubscribeAsync(string workItemId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"wi:{workItemId}");

    public Task SubscribeAllSupervisionAsync()
        => Groups.AddToGroupAsync(Context.ConnectionId, SupervisionAllGroup);

    public Task UnsubscribeAllSupervisionAsync()
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, SupervisionAllGroup);

    public Task SubscribeSupervisionSessionAsync(string sessionId)
        => Groups.AddToGroupAsync(Context.ConnectionId, SupervisionSessionGroup(sessionId));

    public Task UnsubscribeSupervisionSessionAsync(string sessionId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, SupervisionSessionGroup(sessionId));

    public Task<IReadOnlyList<AgentSupervisionSessionSnapshot>> ListSupervisionSessionsAsync(
        CancellationToken ct = default)
        => _supervision.ListSessionsAsync(ct);

    public Task<AgentSupervisionInjectionReceipt> InjectSupervisionAsync(
        string sessionId,
        AgentSupervisionInjectionRequest request,
        CancellationToken ct = default)
    {
        var actor = string.IsNullOrWhiteSpace(request.Actor)
            ? $"signalr:{Context.ConnectionId}"
            : request.Actor;
        return _supervision.EnqueueInjectionAsync(
            sessionId,
            request with { Actor = actor },
            ct);
    }

    internal static string SupervisionSessionGroup(string sessionId) => $"supervision:session:{sessionId}";
}
