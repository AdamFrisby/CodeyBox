using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Majordomo;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Operator surface for the majordomo proposal queue: list and inspect
/// queued proposals, approve them (committing through the mutate backend),
/// or reject/supersede them (recording the outcome without mutating).
/// Served under the API-key middleware like every other operator endpoint —
/// and additionally gated by <see cref="CheckProposalOperator"/> so the
/// majordomo's own credential can never approve the calls it queued.
/// </summary>
internal static class MajordomoProposalEndpoints
{
    /// <summary>
    /// The initiator stamped on a committed mutation when the deciding
    /// request carries no API principal at all — a host driving the service
    /// without an authenticated request still commits as an operator
    /// decision. (An auth-disabled host does carry a principal: the
    /// loopback-operator sentinel.)
    /// </summary>
    private static readonly WorkInitiator ProposalDecisionInitiator = new()
    {
        Issuer = "codeybox",
        Subject = "majordomo-proposal",
        DisplayName = "CodeyBox majordomo proposal approval",
    };

    public static void MapMajordomoProposals(this WebApplication app)
    {
        var group = app.MapGroup("/majordomo/proposals");
        // The bearer middleware authenticates; this filter authorizes — the
        // whole proposal surface is operator-only. Proposed mode exists to
        // put the model's mutations behind human review, so the majordomo
        // client (the prompt-injectable identity being gated) must never
        // reach the decision routes.
        group.AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices
                .GetRequiredService<IOptionsMonitor<MajordomoServerOptions>>()
                .CurrentValue;
            return CheckProposalOperator(context.HttpContext, options) is { } error
                ? error
                : await next(context);
        });
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPost("/{id}/approve", ApproveAsync);
        group.MapPost("/{id}/reject", RejectAsync);
        group.MapPost("/{id}/supersede", SupersedeAsync);
    }

    /// <summary>
    /// The operator gate for the proposal surface: any authenticated
    /// principal may proceed EXCEPT the configured majordomo client — the
    /// less-trusted identity whose calls proposed mode exists to review —
    /// and executor-bound tokens, which prove a host identity, not an
    /// operator's. Returns null when the caller may proceed, else the
    /// failure result to write.
    /// </summary>
    internal static IResult? CheckProposalOperator(HttpContext context, MajordomoServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        if (!ApiKeyAuth.TryGetPrincipal(context, out var principal) || principal is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "unauthenticated");
        }

        // The auth-disabled sentinel is the loopback operator.
        if (ApiKeyAuth.IsAuthenticationDisabled(principal))
            return null;

        if (string.Equals(principal.Name, options.ClientName, StringComparison.Ordinal)
            || principal.ExecutorHostId is not null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "forbidden",
                detail: "proposal decisions are operator-only — the majordomo client and " +
                    "executor-bound tokens cannot decide proposals");
        }

        return null;
    }

    private static async Task<IResult> ListAsync(
        string? state,
        int? limit,
        IMajordomoProposalStore store,
        CancellationToken ct)
    {
        MajordomoProposalState? filter = null;
        if (state is not null)
        {
            // TryParse alone would accept a numeric like "7" as an undefined
            // member; IsDefined keeps unknown inputs on the same 400 path.
            if (!Enum.TryParse<MajordomoProposalState>(state, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                return Results.BadRequest(new
                {
                    error = $"unknown proposal state '{Validation.DescribeUntrustedValue(state)}'",
                });
            }
            filter = parsed;
        }

        IReadOnlyList<MajordomoProposalRecord> rows;
        try
        {
            rows = await store.ListAsync(filter, limit ?? IMajordomoProposalStore.MaxListLimit, ct).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        return Results.Ok(rows.Select(ToDto).ToList());
    }

    private static async Task<IResult> GetAsync(
        string id,
        IMajordomoProposalStore store,
        CancellationToken ct)
    {
        MajordomoProposalRecord? record;
        try
        {
            record = await store.GetAsync(id, ct).ConfigureAwait(false);
        }
        catch (MajordomoProposalCorruptException ex)
        {
            return Results.Ok(new
            {
                id = Validation.DescribeUntrustedValue(id),
                corrupt = true,
                detail = ex.Message,
            });
        }

        return record is null
            ? Results.NotFound(new { error = $"proposal '{Validation.DescribeUntrustedValue(id)}' does not exist" })
            : Results.Ok(ToDto(record));
    }

    private static async Task<IResult> ApproveAsync(
        string id,
        HttpContext context,
        MajordomoProposalService proposals,
        CancellationToken ct)
    {
        var outcome = await proposals.ApproveAsync(
                id, MajordomoCallerContext.Identity(context),
                MajordomoCallerContext.Initiator(context, ProposalDecisionInitiator), ct)
            .ConfigureAwait(false);
        if (outcome.Refusal is not null)
            return ProposalRefusalResult(outcome.Refusal);
        if (outcome.AlreadyApprovedIds is not null)
            return Results.Ok(new
            {
                status = "already-approved",
                affectedItems = outcome.AlreadyApprovedIds.Select(i => i.ToString()).ToList(),
            });
        return Results.Ok(new
        {
            status = "approved",
            changeSet = JsonSerializer.SerializeToNode(
                outcome.ChangeSet!, outcome.ChangeSet!.GetType(), MajordomoJson.Options),
            affectedItems = outcome.ChangeSet!.AffectedItems.Select(i => i.ToString()).ToList(),
        });
    }

    private static async Task<IResult> RejectAsync(
        string id,
        ProposalDecisionRequest body,
        HttpContext context,
        MajordomoProposalService proposals,
        CancellationToken ct)
    {
        var outcome = await proposals.RejectAsync(
                id, MajordomoCallerContext.Identity(context), body?.Reason ?? string.Empty, ct)
            .ConfigureAwait(false);
        return outcome.Refusal is not null
            ? ProposalRefusalResult(outcome.Refusal)
            : Results.Ok(new { status = "rejected", proposal = ToDto(outcome.Record!) });
    }

    private static async Task<IResult> SupersedeAsync(
        string id,
        ProposalDecisionRequest body,
        HttpContext context,
        MajordomoProposalService proposals,
        CancellationToken ct)
    {
        var outcome = await proposals.SupersedeAsync(
                id, MajordomoCallerContext.Identity(context), body?.Reason ?? string.Empty, ct)
            .ConfigureAwait(false);
        return outcome.Refusal is not null
            ? ProposalRefusalResult(outcome.Refusal)
            : Results.Ok(new { status = "superseded", proposal = ToDto(outcome.Record!) });
    }

    private static IResult ProposalRefusalResult(MajordomoRefusal refusal) => refusal.Reason switch
    {
        MajordomoRefusalReasons.ProposalNotFound => Results.NotFound(new
        {
            error = refusal.Detail,
            reason = refusal.Reason,
        }),
        MajordomoRefusalReasons.ProposalNotPending
            or MajordomoRefusalReasons.ProposalExpired
            or MajordomoRefusalReasons.ProposalCorrupt
            or MajordomoRefusalReasons.ProposalToolRetired
            or MajordomoRefusalReasons.ProposalDrifted
            or MajordomoRefusalReasons.ProposalCommitIncomplete => Results.Conflict(new
            {
                error = refusal.Detail,
                reason = refusal.Reason,
            }),
        _ => Results.BadRequest(new { error = refusal.Detail, reason = refusal.Reason }),
    };

    private static object ToDto(MajordomoProposalRecord record) => new
    {
        id = record.Id,
        tool = record.ToolName,
        arguments = JsonSerializer.SerializeToNode(record.Arguments, record.Arguments.GetType(), MajordomoJson.Options),
        reasoning = record.Reasoning,
        reviewedChangeSet = record.ReviewedChangeSet is { } plan
            ? JsonSerializer.SerializeToNode(plan, MajordomoJson.Options)
            : null,
        proposedBy = record.ProposedBy,
        proposedAt = record.ProposedAt,
        state = record.State.ToString().ToLowerInvariant(),
        decidedAt = record.DecidedAt,
        decidedBy = record.DecidedBy,
        decisionReason = record.DecisionReason,
        resultAffectedItems = record.ResultAffectedItems?.Select(i => i.ToString()).ToList(),
    };
}

/// <summary>Operator-supplied justification for rejecting or superseding a proposal.</summary>
internal sealed record ProposalDecisionRequest(string? Reason);
