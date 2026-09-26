using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Majordomo;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Operator surface for the majordomo proposal queue: list and inspect
/// queued proposals, approve them (committing through the mutate backend),
/// or reject/supersede them (recording the outcome without mutating).
/// Served under the API-key middleware like every other operator endpoint.
/// </summary>
internal static class MajordomoProposalEndpoints
{
    public static void MapMajordomoProposals(this WebApplication app)
    {
        var group = app.MapGroup("/majordomo/proposals");
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPost("/{id}/approve", ApproveAsync);
        group.MapPost("/{id}/reject", RejectAsync);
        group.MapPost("/{id}/supersede", SupersedeAsync);
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
            if (!Enum.TryParse<MajordomoProposalState>(state, ignoreCase: true, out var parsed))
                return Results.BadRequest(new
                {
                    error = $"unknown proposal state '{Validation.DescribeUntrustedValue(state)}'",
                });
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
            return Results.Ok(new { id, corrupt = true, detail = ex.Message });
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
        var outcome = await proposals.ApproveAsync(id, DecideIdentity(context), ResolveInitiator(context), ct)
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
        var outcome = await proposals.RejectAsync(id, DecideIdentity(context), body?.Reason ?? string.Empty, ct)
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
        var outcome = await proposals.SupersedeAsync(id, DecideIdentity(context), body?.Reason ?? string.Empty, ct)
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
            or MajordomoRefusalReasons.ProposalToolRetired => Results.Conflict(new
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
        proposedBy = record.ProposedBy,
        proposedAt = record.ProposedAt,
        state = record.State.ToString().ToLowerInvariant(),
        decidedAt = record.DecidedAt,
        decidedBy = record.DecidedBy,
        decisionReason = record.DecisionReason,
        resultAffectedItems = record.ResultAffectedItems?.Select(i => i.ToString()).ToList(),
    };

    private static string DecideIdentity(HttpContext context) =>
        ApiKeyAuth.TryGetPrincipal(context, out var principal) && principal is not null
            ? ApiKeyAuth.IsAuthenticationDisabled(principal)
                ? ApiKeyAuth.AuthenticationDisabledClientName
                : principal.Name
            : "unknown";

    private static WorkInitiator ResolveInitiator(HttpContext context) =>
        ApiKeyAuth.TryGetPrincipal(context, out var principal) && principal?.FixedInitiator is { } initiator
            ? initiator
            : new WorkInitiator
            {
                Issuer = "codeybox",
                Subject = "majordomo-proposal",
                DisplayName = "CodeyBox majordomo proposal approval",
            };
}

/// <summary>Operator-supplied justification for rejecting or superseding a proposal.</summary>
internal sealed record ProposalDecisionRequest(string? Reason);
