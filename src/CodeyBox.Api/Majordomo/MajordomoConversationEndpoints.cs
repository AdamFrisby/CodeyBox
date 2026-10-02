using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeyBox.Majordomo;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Operator surface for the majordomo conversation and autonomy mode: read
/// the durable conversation, append operator turns, stream new rows, and
/// read or flip the autonomy switch. Served under the API-key middleware
/// like every other operator endpoint — and additionally gated by
/// <see cref="MajordomoProposalEndpoints.CheckProposalOperator"/> so the
/// majordomo's own credential can never drive the panel surface that
/// reviews it.
/// </summary>
internal static class MajordomoConversationEndpoints
{
    public static void MapMajordomoConversation(this WebApplication app)
    {
        var group = app.MapGroup("/majordomo");
        // Same operator-only gate as the proposal surface: the panel acts
        // on the fleet (mode flips, operator turns steer the next majordomo
        // turn), so the prompt-injectable majordomo identity stays out.
        group.AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices
                .GetRequiredService<IOptionsMonitor<MajordomoServerOptions>>()
                .CurrentValue;
            return MajordomoProposalEndpoints.CheckProposalOperator(context.HttpContext, options) is { } error
                ? error
                : await next(context);
        });
        group.MapGet("/conversation", ListAsync);
        group.MapPost("/conversation", PostAsync);
        group.MapGet("/conversation/stream", StreamAsync);
        group.MapGet("/mode", GetModeAsync);
        group.MapPost("/mode", SetModeAsync);
        group.MapDelete("/mode", ClearModeAsync);
    }

    private static async Task<IResult> ListAsync(
        long? afterSequence,
        int? limit,
        IMajordomoConversationStore store,
        CancellationToken ct)
    {
        if ((afterSequence ?? 0) < 0)
            return Results.BadRequest(new { error = "afterSequence must be >= 0" });
        var take = limit ?? IMajordomoConversationStore.MaxListLimit;
        if (take is < 1 or > IMajordomoConversationStore.MaxListLimit)
        {
            return Results.BadRequest(new
            {
                error = $"limit must be within [1, {IMajordomoConversationStore.MaxListLimit}]",
            });
        }

        var rows = await store.ListAsync(afterSequence ?? 0, take, ct).ConfigureAwait(false);
        var summary = await store.GetSummaryAsync(ct).ConfigureAwait(false);
        return Results.Ok(new
        {
            summary = summary == MajordomoConversationSummary.None ? null : summary.Text,
            entries = rows.Select(ToDto).ToList(),
        });
    }

    private static async Task<IResult> PostAsync(
        OperatorTurnRequest? body,
        HttpContext context,
        IMajordomoConversationStore store,
        IOptionsMonitor<MajordomoServerOptions> options,
        CancellationToken ct)
    {
        var text = body?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return Results.BadRequest(new { error = "text must not be empty" });
        var policy = options.CurrentValue.ToHistoryOptions();
        if (text.Length > policy.MaxEntryChars)
        {
            return Results.BadRequest(new
            {
                error = $"text exceeds the {policy.MaxEntryChars}-character conversation entry bound",
            });
        }

        var clock = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        var entry = await store.AppendAsync(
            MajordomoConversationRole.Operator, text, toolName: null,
            clock.GetUtcNow(), policy, ct).ConfigureAwait(false);
        return Results.Created($"/majordomo/conversation?afterSequence={entry.Sequence - 1}", ToDto(entry));
    }

    private static async Task StreamAsync(
        HttpContext context,
        long? afterSequence,
        IMajordomoConversationStore store,
        IOptionsMonitor<MajordomoServerOptions> options,
        CancellationToken ct)
    {
        var since = afterSequence ?? 0;
        if (since < 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("""{"error":"afterSequence must be >= 0"}""", ct)
                .ConfigureAwait(false);
            return;
        }

        // The poll interval is configuration, not a literal: operators tune
        // stream chattiness alongside the conversation bounds in the same
        // Majordomo section, and the endpoint reads it per connection.
        var poll = TimeSpan.FromSeconds(options.CurrentValue.ConversationStreamPollSeconds);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);

        using var timer = new PeriodicTimer(poll);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var rows = await store.ListAsync(since, IMajordomoConversationStore.MaxListLimit, ct)
                    .ConfigureAwait(false);
                foreach (var row in rows)
                {
                    since = row.Sequence;
                    var payload = JsonSerializer.Serialize(ToDto(row), MajordomoJson.Options);
                    await context.Response.WriteAsync($"data: {payload}\n\n", ct).ConfigureAwait(false);
                }
                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The operator hung up; the stream ends with the connection.
        }
    }

    private static Task<IResult> GetModeAsync(
        IOptionsMonitor<MajordomoServerOptions> options,
        MajordomoAutonomySwitch modeSwitch)
    {
        var configured = options.CurrentValue.Mode;
        return Task.FromResult<IResult>(Results.Ok(new
        {
            mode = ModeName(modeSwitch.Resolve(configured)),
            source = modeSwitch.IsOverridden ? "override" : "config",
        }));
    }

    private static Task<IResult> SetModeAsync(
        ModeSwitchRequest? body,
        IOptionsMonitor<MajordomoServerOptions> options,
        MajordomoAutonomySwitch modeSwitch)
    {
        if (body?.Mode is null
            || !Enum.TryParse<MajordomoAutonomyMode>(body.Mode, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            return Task.FromResult<IResult>(Results.BadRequest(new
            {
                error = $"mode must be '{ModeName(MajordomoAutonomyMode.Autonomous)}' or "
                    + $"'{ModeName(MajordomoAutonomyMode.Proposed)}'",
            }));
        }

        // TryParse alone would accept a numeric like "7" as an undefined
        // member; IsDefined keeps unknown inputs on the same 400 path as
        // the proposal state filter.
        var effective = modeSwitch.Set(parsed);
        return Task.FromResult<IResult>(Results.Ok(new
        {
            mode = ModeName(effective),
            source = "override",
        }));
    }

    private static Task<IResult> ClearModeAsync(
        IOptionsMonitor<MajordomoServerOptions> options,
        MajordomoAutonomySwitch modeSwitch)
    {
        var configured = options.CurrentValue.Mode;
        var effective = modeSwitch.Clear(configured);
        return Task.FromResult<IResult>(Results.Ok(new
        {
            mode = ModeName(effective),
            source = "config",
        }));
    }

    internal static string ModeName(MajordomoAutonomyMode mode) => mode switch
    {
        MajordomoAutonomyMode.Autonomous => "autonomous",
        MajordomoAutonomyMode.Proposed => "proposed",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown autonomy mode"),
    };

    private static object ToDto(MajordomoConversationEntry entry) => new
    {
        sequence = entry.Sequence,
        role = entry.Role.ToString().ToLowerInvariant(),
        text = entry.Text,
        toolName = entry.ToolName,
        recordedAt = entry.RecordedAt,
    };
}

/// <summary>Operator turn posted from the fleet-map conversation panel.</summary>
internal sealed record OperatorTurnRequest(string? Text);

/// <summary>Autonomy mode requested from the fleet-map mode switch.</summary>
internal sealed record ModeSwitchRequest(string? Mode);
