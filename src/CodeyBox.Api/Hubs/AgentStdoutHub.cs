using System.Security.Cryptography;
using System.Text;
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

    public Task<AgentSupervisionSessionPage> ListSupervisionSessionsAsync(
        int? skip = null,
        int? take = null,
        int? outputTailMaxChars = null,
        int? recentCommandsLimit = null,
        CancellationToken ct = default)
        => _supervision.ListSessionsAsync(
            new AgentSupervisionListQuery(
                Skip: skip,
                Take: take,
                IncludeOutputTail: true,
                OutputTailMaxChars: outputTailMaxChars,
                RecentCommandsLimit: recentCommandsLimit),
            ct);

    public Task<AgentSupervisionInjectionReceipt> InjectSupervisionAsync(
        string sessionId,
        AgentSupervisionInjectionRequest request,
        CancellationToken ct = default)
    {
        // Display label only — the authoritative principal for the audit
        // trail is derived from the SignalR connection identity below. Client-
        // supplied actor strings cannot be trusted because the bearer-token
        // auth layer does not bind a user identity to the connection.
        var clientLabel = string.IsNullOrWhiteSpace(request.Actor) ? null : request.Actor!.Trim();
        if (clientLabel is not null && clientLabel.Length > 80)
            clientLabel = clientLabel[..80];
        var authoritative = ResolveAuthoritativeActor();
        var actor = clientLabel is null ? authoritative : $"{authoritative} ({clientLabel})";
        return _supervision.EnqueueInjectionAsync(
            sessionId,
            request with { Actor = actor },
            ct);
    }

    internal static string SupervisionSessionGroup(string sessionId) => $"supervision:session:{sessionId}";

    private string ResolveAuthoritativeActor()
    {
        var name = Context.User?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name))
            return $"user:{name}";

        var http = Context.GetHttpContext();
        var ip = http?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var fingerprint = http is null ? null : FingerprintAuth(http);
        var principal = fingerprint is null
            ? $"signalr:{Context.ConnectionId}@{ip}"
            : $"apikey:{fingerprint}@{ip}";
        return principal;
    }

    private static string? FingerprintAuth(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var values))
            return null;
        var raw = values.ToString();
        const string prefix = "Bearer ";
        if (!raw.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var token = raw[prefix.Length..].Trim();
        if (token.Length == 0)
            return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
    }
}
