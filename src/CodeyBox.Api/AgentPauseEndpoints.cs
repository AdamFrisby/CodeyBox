using CodeyBox.Core;

namespace CodeyBox.Api;

internal static class AgentPauseEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/agents");
        group.MapGet("/paused", ListPausedAsync);
        group.MapPost("/{kind}/pause", PauseAsync);
        group.MapPost("/{kind}/resume", ResumeAsync);
    }

    private static async Task<IResult> ListPausedAsync(
        IAgentPauseController pauses,
        CancellationToken ct)
    {
        var states = await pauses.ListPausedAsync(ct);
        return Results.Ok(states.Select(ToDto));
    }

    private static async Task<IResult> PauseAsync(
        string kind,
        PauseAgentRequest body,
        IAgentPauseController pauses,
        IAgentRegistry agents,
        IWebhookDispatcher webhooks,
        CancellationToken ct)
    {
        var agent = NormaliseKind(kind);
        if (!agents.Available.Contains(agent))
            return Results.NotFound(new { error = $"unknown agent '{kind}'", available = agents.Available.Select(a => a.Value) });

        var validation = ValidateReason(body.Reason);
        if (validation is not null) return validation;

        DateTimeOffset? expiresAt;
        try
        {
            expiresAt = ResolveExpiresAt(body);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var state = await pauses.PauseAsync(agent, body.Reason!.Trim(), "api", expiresAt, ct);
        _ = webhooks.PublishAsync(new WebhookEvent
        {
            Event = "agent.paused",
            Details = new
            {
                agent = state.Agent.Value,
                reason = state.PausedReason,
                pausedAt = state.PausedAt,
                pausedBy = state.PausedBy,
                expiresAt = state.ExpiresAt,
            },
        }, CancellationToken.None);

        return Results.Ok(ToDto(state));
    }

    private static async Task<IResult> ResumeAsync(
        string kind,
        ResumeAgentRequest? body,
        IAgentPauseController pauses,
        IAgentRegistry agents,
        IWebhookDispatcher webhooks,
        CancellationToken ct)
    {
        var agent = NormaliseKind(kind);
        if (!agents.Available.Contains(agent))
            return Results.NotFound(new { error = $"unknown agent '{kind}'", available = agents.Available.Select(a => a.Value) });

        var wasPaused = await pauses.ResumeAsync(agent, "api", body?.Reason, ct);
        if (wasPaused)
        {
            _ = webhooks.PublishAsync(new WebhookEvent
            {
                Event = "agent.resumed",
                Details = new
                {
                    agent = agent.Value,
                    resumedAt = DateTimeOffset.UtcNow,
                    resumedBy = "api",
                    reason = body?.Reason,
                },
            }, CancellationToken.None);
        }

        return Results.Ok(new
        {
            agent = agent.Value,
            paused = false,
        });
    }

    private static AgentKind NormaliseKind(string kind) =>
        new(kind.Trim().ToLowerInvariant());

    private static IResult? ValidateReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Results.BadRequest(new { error = "reason is required" });
        if (reason.Any(char.IsControl))
            return Results.BadRequest(new { error = "reason must not contain control characters" });
        if (reason.Length > 500)
            return Results.BadRequest(new { error = "reason must be <= 500 chars" });
        return null;
    }

    private static DateTimeOffset? ResolveExpiresAt(PauseAgentRequest body)
    {
        var hasDuration = body.DurationSeconds is not null || !string.IsNullOrWhiteSpace(body.Duration);
        if (hasDuration && body.ExpiresAt is not null)
            throw new ArgumentException("provide either duration/durationSeconds or expiresAt, not both");

        if (body.ExpiresAt is { } expiresAt)
        {
            if (expiresAt <= DateTimeOffset.UtcNow)
                throw new ArgumentException("expiresAt must be in the future");
            return expiresAt;
        }

        TimeSpan? duration = null;
        if (body.DurationSeconds is { } seconds)
        {
            if (seconds <= 0)
                throw new ArgumentException("durationSeconds must be positive");
            duration = TimeSpan.FromSeconds(seconds);
        }

        if (!string.IsNullOrWhiteSpace(body.Duration))
            duration = ParseDuration(body.Duration);

        return duration is null ? null : DateTimeOffset.UtcNow.Add(duration.Value);
    }

    private static TimeSpan ParseDuration(string raw)
    {
        var value = raw.Trim();
        if (TimeSpan.TryParse(value, out var parsed) && parsed > TimeSpan.Zero)
            return parsed;

        var suffix = value[^1];
        if (!double.TryParse(value[..^1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var amount)
            || amount <= 0)
        {
            throw new ArgumentException("duration must be a positive TimeSpan or suffixed value such as 30m, 6h, or 2d");
        }

        return suffix switch
        {
            's' or 'S' => TimeSpan.FromSeconds(amount),
            'm' or 'M' => TimeSpan.FromMinutes(amount),
            'h' or 'H' => TimeSpan.FromHours(amount),
            'd' or 'D' => TimeSpan.FromDays(amount),
            _ => throw new ArgumentException("duration suffix must be one of s, m, h, or d"),
        };
    }

    private static object ToDto(AgentPauseState state) => new
    {
        agent = state.Agent.Value,
        paused = state.Paused,
        pausedAt = state.PausedAt,
        pausedReason = state.PausedReason,
        pausedBy = state.PausedBy,
        expiresAt = state.ExpiresAt,
        updatedAt = state.UpdatedAt,
    };
}

public sealed record PauseAgentRequest(
    string? Reason,
    double? DurationSeconds = null,
    string? Duration = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record ResumeAgentRequest(string? Reason = null);
