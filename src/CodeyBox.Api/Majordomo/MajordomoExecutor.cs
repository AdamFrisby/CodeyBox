using System.Text.Json;
using System.Text.Json.Nodes;
using CodeyBox.Core;
using CodeyBox.Majordomo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// The single dispatch path for every majordomo tool call arriving over MCP.
/// Each call binds the tool's typed argument contract, passes through
/// <see cref="MajordomoAuthorization.Decide"/> as the sole policy gate, is
/// audit-logged before any mutation runs, and is answered with a structured
/// result or a refusal precise enough to retry from.
/// </summary>
/// <remarks>
/// Decision ordering:
/// <list type="number">
///   <item>bind arguments to the declared contract (failures still call
///   <c>Decide</c> with a null payload, which refuses as
///   <see cref="MajordomoRefusalReason.ArgumentContractMismatch"/>);</item>
///   <item><c>Decide</c> — the only policy check;</item>
///   <item>audit the call record (before any mutation);</item>
///   <item>run the backend: reads execute; mutations run the shared
///   plan/commit machinery — plan only for dry-run and proposals, commit only
///   for executed calls;</item>
///   <item>audit the outcome; spend turn budget only on real mutations.</item>
/// </list>
/// </remarks>
internal sealed class MajordomoExecutor
{
    /// <summary>Arguments captured into the audit record are truncated to this many characters.</summary>
    internal const int MaxAuditArgumentChars = 8192;

    private readonly MajordomoReadBackend _reads;
    private readonly MajordomoMutateBackend _mutates;
    private readonly IOptionsMonitor<MajordomoServerOptions> _options;
    private readonly MajordomoTurnLedger _ledger;
    private readonly IHttpContextAccessor _httpContext;

    public MajordomoExecutor(
        MajordomoReadBackend reads,
        MajordomoMutateBackend mutates,
        IOptionsMonitor<MajordomoServerOptions> options,
        MajordomoTurnLedger ledger,
        IHttpContextAccessor httpContext)
    {
        _reads = reads;
        _mutates = mutates;
        _options = options;
        _ledger = ledger;
        _httpContext = httpContext;
    }

    /// <summary>
    /// Executes one tool call: <paramref name="name"/> is the wire tool name,
    /// <paramref name="argsNode"/> the raw JSON arguments (null when absent).
    /// </summary>
    public async ValueTask<CallToolResult> ExecuteAsync(
        string? name, JsonNode? argsNode, CancellationToken ct)
    {
        var identity = ResolveIdentity();
        var options = _options.CurrentValue;

        // Bind the declared argument contract. A binding failure still flows
        // through Decide with a null payload — every call routes through the
        // authorization function, which reports it as an argument contract
        // mismatch.
        MajordomoToolArgs? args = null;
        MajordomoRefusal? bindRefusal = null;
        if (MajordomoTools.TryGet(name, out var tool))
            (args, bindRefusal) = BindArguments(tool, argsNode);

        var usage = _ledger.Snapshot(identity, TimeSpan.FromSeconds(options.TurnWindowSeconds));
        var decision = MajordomoAuthorization.Decide(name, args, options.ToPolicy(), usage);

        // The call record is written before any mutation is attempted —
        // refused calls are audited too.
        var callId = Guid.NewGuid().ToString("N");
        var decisionLabel = decision switch
        {
            MajordomoDecision.Execute => "execute",
            MajordomoDecision.Propose => "propose",
            MajordomoDecision.Refuse => "refuse",
            _ => "unknown",
        };
        AuditLog.MajordomoToolCall(
            callId, identity, name ?? "<missing>", decisionLabel, BoundArguments(argsNode));

        var (envelope, outcomeLabel, outcomeDetail, isError) = await DispatchAsync(
            decision, name, identity, args, argsNode, bindRefusal, ct).ConfigureAwait(false);
        AuditLog.MajordomoToolOutcome(callId, identity, name ?? "<missing>", outcomeLabel, outcomeDetail);

        var json = JsonSerializer.SerializeToElement(envelope, MajordomoJson.Options);
        var text = JsonSerializer.Serialize(envelope, MajordomoJson.Options);
        return new CallToolResult
        {
            IsError = isError,
            StructuredContent = json,
            Content = [new TextContentBlock { Text = text }],
        };
    }

    private async Task<(JsonObject Envelope, string Outcome, string? Detail, bool IsError)> DispatchAsync(
        MajordomoDecision decision,
        string? name,
        string identity,
        MajordomoToolArgs? args,
        JsonNode? argsNode,
        MajordomoRefusal? bindRefusal,
        CancellationToken ct)
    {
        switch (decision)
        {
            case MajordomoDecision.Refuse refuse:
            {
                var refusal = bindRefusal is not null
                    ? bindRefusal with { Reason = ToReasonCode(refuse.Reason) }
                    : new MajordomoRefusal(ToReasonCode(refuse.Reason), refuse.Detail);
                return (Envelope("refused", refusal: refusal), "refused", refusal.Detail, true);
            }

            case MajordomoDecision.Execute execute:
            {
                if (execute.Tool.Class == MajordomoToolClass.Read)
                {
                    var (result, readRefusal) = await RunReadAsync(execute.Tool, args!, ct).ConfigureAwait(false);
                    if (readRefusal is not null)
                        return (Envelope("refused", refusal: readRefusal), "refused", readRefusal.Detail, true);
                    return (Envelope("executed", result: result), "executed", null, false);
                }

                var mutate = (MajordomoMutateArgs)args!;
                var mutation = await RunMutateAsync(execute.Tool, mutate, commit: !mutate.DryRun, ct)
                    .ConfigureAwait(false);

                // Budget is spent by real mutations only — dry-runs write
                // nothing. A refusal after writes still counts: partial work
                // reached the queue and must not be retried for free. The
                // count is the committed change set's real affected list when
                // present (cancel cascades count their children), else the
                // call's declared blast radius.
                if (mutation.WritesApplied)
                    _ledger.Record(
                        identity,
                        Math.Max(
                            mutation.ChangeSet?.AffectedItems.Count ?? 0,
                            mutate.AffectedItemCount));

                if (mutation.Refusal is not null)
                    return (Envelope("refused", refusal: mutation.Refusal), "refused", mutation.Refusal.Detail, true);

                var label = mutate.DryRun ? "dry_run" : "executed";
                return (Envelope(label, result: mutation.ChangeSet), label, null, false);
            }

            case MajordomoDecision.Propose propose:
            {
                var mutation = await RunMutateAsync(propose.Proposal.Tool, (MajordomoMutateArgs)args!, commit: false, ct)
                    .ConfigureAwait(false);
                if (mutation.Refusal is not null)
                    return (Envelope("refused", refusal: mutation.Refusal), "refused", mutation.Refusal.Detail, true);
                // Echo the CANONICAL arguments (the bound contract
                // re-serialized), not the raw payload — what the proposal
                // shows is exactly what would execute.
                var view = new MajordomoProposalView(
                    propose.Proposal.Tool.Name,
                    args is null ? argsNode : JsonSerializer.SerializeToNode(args, args.GetType(), MajordomoJson.Options),
                    propose.Proposal.AffectedItemCount,
                    mutation.ChangeSet!);
                return (Envelope("proposed", proposal: view), "proposed", null, false);
            }

            default:
                return (Envelope("refused",
                        refusal: new MajordomoRefusal("unknown_tool", $"unhandled decision for '{name}'")),
                    "refused", "unhandled decision", true);
        }
    }

    private async Task<(MajordomoToolResult? Result, MajordomoRefusal? Refusal)> RunReadAsync(
        MajordomoTool tool, MajordomoToolArgs args, CancellationToken ct)
    {
        if (tool == MajordomoTools.GetQueueStatus)
            return (await _reads.GetQueueStatusAsync(ct).ConfigureAwait(false), null);
        if (tool == MajordomoTools.GetDispatchStatus)
            return (await _reads.GetDispatchStatusAsync(ct).ConfigureAwait(false), null);
        if (tool == MajordomoTools.GetAgentCapacity)
            return await _reads.GetAgentCapacityAsync((GetAgentCapacityArgs)args, ct).ConfigureAwait(false);
        if (tool == MajordomoTools.ListWorkItems)
            return (await _reads.ListWorkItemsAsync((ListWorkItemsArgs)args, ct).ConfigureAwait(false), null);
        if (tool == MajordomoTools.GetWorkItem)
            return (await _reads.GetWorkItemAsync((GetWorkItemArgs)args, ct).ConfigureAwait(false), null);
        if (tool == MajordomoTools.GetWorkItemAudit)
            return (await _reads.GetWorkItemAuditAsync((GetWorkItemAuditArgs)args, ct).ConfigureAwait(false), null);
        return (null, new MajordomoRefusal("unknown_tool", $"no read backend for '{tool.Name}'"));
    }

    private Task<MajordomoMutationResult> RunMutateAsync(
        MajordomoTool tool, MajordomoMutateArgs args, bool commit, CancellationToken ct)
    {
        var initiator = ResolveInitiator();
        if (tool == MajordomoTools.CreateWorkItem)
            return _mutates.CreateAsync((CreateWorkItemArgs)args, initiator, commit, ct);
        if (tool == MajordomoTools.CreateWorkItemChain)
            return _mutates.CreateChainAsync((CreateWorkItemChainArgs)args, initiator, commit, ct);
        if (tool == MajordomoTools.UpdateWorkItem)
            return _mutates.UpdateAsync((UpdateWorkItemArgs)args, commit, ct);
        if (tool == MajordomoTools.CancelWorkItem)
            return _mutates.CancelAsync((CancelWorkItemArgs)args, commit, ct);
        if (tool == MajordomoTools.RetryWorkItem)
            return _mutates.RetryAsync((RetryWorkItemArgs)args, commit, ct);
        return Task.FromResult(MajordomoMutationResult.Refused(
            new MajordomoRefusal("unknown_tool", $"no mutate backend for '{tool.Name}'")));
    }

    private string ResolveIdentity()
    {
        var context = _httpContext.HttpContext;
        if (context is not null && ApiKeyAuth.TryGetPrincipal(context, out var principal) && principal is not null)
            return ApiKeyAuth.IsAuthenticationDisabled(principal)
                ? "authentication-disabled"
                : principal.Name;
        return "unknown";
    }

    private WorkInitiator ResolveInitiator()
    {
        var context = _httpContext.HttpContext;
        if (context is not null && ApiKeyAuth.TryGetPrincipal(context, out var principal) && principal is not null)
            return principal.FixedInitiator;
        return new WorkInitiator
        {
            Issuer = "codeybox",
            Subject = "majordomo",
            DisplayName = "CodeyBox majordomo",
        };
    }

    private static string BoundArguments(JsonNode? args)
    {
        var raw = args?.ToJsonString() ?? "{}";
        return raw.Length <= MaxAuditArgumentChars ? raw : raw[..MaxAuditArgumentChars] + "…";
    }

    private static string ToReasonCode(MajordomoRefusalReason reason) => reason switch
    {
        MajordomoRefusalReason.UnknownTool => "unknown_tool",
        MajordomoRefusalReason.ArgumentContractMismatch => "argument_contract_mismatch",
        MajordomoRefusalReason.TooManyItemsInOneCall => "too_many_items_in_one_call",
        MajordomoRefusalReason.TurnMutationBudgetExhausted => "turn_mutation_budget_exhausted",
        _ => "refused",
    };

    private static JsonObject Envelope(
        string outcome,
        MajordomoToolResult? result = null,
        MajordomoProposalView? proposal = null,
        MajordomoRefusal? refusal = null)
    {
        var envelope = new JsonObject { ["outcome"] = outcome };
        if (result is not null)
            envelope["result"] = JsonSerializer.SerializeToNode(result, result.GetType(), MajordomoJson.Options);
        if (proposal is not null)
            envelope["proposal"] = JsonSerializer.SerializeToNode(proposal, MajordomoJson.Options);
        if (refusal is not null)
            envelope["refusal"] = JsonSerializer.SerializeToNode(refusal, MajordomoJson.Options);
        return envelope;
    }

    // ── Argument binding ────────────────────────────────────────────────────

    /// <summary>
    /// Deserializes the wire arguments into the tool's declared contract type.
    /// The parameterless tools substitute their single instances; every other
    /// contract goes through STJ with the shared majordomo serializer, so the
    /// validating constructors run and an invalid call is unrepresentable.
    /// </summary>
    internal static (MajordomoToolArgs? Args, MajordomoRefusal? Refusal) BindArguments(
        MajordomoTool tool, JsonNode? args)
    {
        if (tool == MajordomoTools.GetQueueStatus)
            return EmptyOrRefuse<GetQueueStatusArgs>(tool, args, GetQueueStatusArgs.Instance);
        if (tool == MajordomoTools.GetDispatchStatus)
            return EmptyOrRefuse<GetDispatchStatusArgs>(tool, args, GetDispatchStatusArgs.Instance);

        try
        {
            // Absent arguments deserialize as an empty object so missing
            // required fields surface as contract errors, not a null binding.
            var bound = args ?? (JsonNode)new JsonObject();
            var value = bound.Deserialize(tool.ArgumentsType, MajordomoJson.Options);
            if (value is not MajordomoToolArgs typed)
                return (null, new MajordomoRefusal(
                    "argument_contract_mismatch",
                    $"tool '{tool.Name}' arguments must be a JSON object"));
            return (typed, null);
        }
        catch (JsonException ex)
        {
            return (null, new MajordomoRefusal(
                "argument_contract_mismatch",
                ex.Message,
                Field: ex.Path));
        }
        catch (ArgumentException ex)
        {
            return (null, new MajordomoRefusal(
                "argument_contract_mismatch",
                ex.Message,
                Field: ex.ParamName));
        }
        catch (NotSupportedException ex)
        {
            return (null, new MajordomoRefusal(
                "argument_contract_mismatch",
                ex.Message));
        }
    }

    private static (MajordomoToolArgs?, MajordomoRefusal?) EmptyOrRefuse<T>(
        MajordomoTool tool, JsonNode? args, T instance)
        where T : MajordomoToolArgs
    {
        if (args is JsonObject obj && obj.Count > 0)
            return (null, new MajordomoRefusal(
                "argument_contract_mismatch",
                $"tool '{tool.Name}' takes no arguments — got {string.Join(", ", obj.Select(kv => kv.Key))}"));
        return (instance, null);
    }
}
