using System.Collections.Concurrent;
using System.Collections.Frozen;
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
///   <item>measure the real blast radius where the contract under-declares
///   it (a cancel's queued-dependent cascade) so <c>Decide</c> bounds what
///   would actually run;</item>
///   <item><c>Decide</c> — the only policy check;</item>
///   <item>audit the call record (before any mutation);</item>
///   <item>run the backend: reads execute; mutations run the shared
///   plan/commit machinery — plan only for dry-run and proposals, commit only
///   for executed calls;</item>
///   <item>audit the outcome; spend turn budget only on real mutations.</item>
/// </list>
/// Mutations of one identity are serialized through the whole measure →
/// decide → commit → record sequence by a keyed gate: the transport is
/// stateless and parallel, and without serialization concurrent calls would
/// observe the same pre-call usage and jointly overshoot the turn cap.
/// </remarks>
internal sealed class MajordomoExecutor
{
    /// <summary>Arguments captured into the audit record are truncated to this many characters.</summary>
    internal const int MaxAuditArgumentChars = 8192;

    private delegate Task<(MajordomoToolResult? Result, MajordomoRefusal? Refusal)> ReadHandler(
        MajordomoReadBackend backend, MajordomoToolArgs args, CancellationToken ct);

    /// <param name="cancelCascadeTargets">
    /// For <c>cancel_work_item</c>, the dependents enumerated at decision
    /// time — the plan must commit exactly the measured set, not a re-scan.
    /// Null for every other tool.
    /// </param>
    private delegate Task<MajordomoMutationResult> MutateHandler(
        MajordomoMutateBackend backend,
        MajordomoMutateArgs args,
        WorkInitiator initiator,
        IReadOnlyList<WorkItem>? cancelCascadeTargets,
        bool commit,
        CancellationToken ct);

    /// <summary>
    /// Everything the executor needs to serve one vocabulary descriptor: an
    /// optional argument-binding override (the parameterless tools), plus the
    /// read or mutate handler. Bound once, beside the vocabulary, so an
    /// unwired descriptor fails at registration rather than per call.
    /// </summary>
    private sealed record ToolWiring(
        Func<MajordomoTool, JsonNode?, (MajordomoToolArgs? Args, MajordomoRefusal? Refusal)>? Bind,
        ReadHandler? Read,
        MutateHandler? Mutate);

    private static readonly FrozenDictionary<MajordomoTool, ToolWiring> Wiring =
        new Dictionary<MajordomoTool, ToolWiring>
        {
            [MajordomoTools.GetQueueStatus] = new(
                static (tool, args) => EmptyOrRefuse(tool, args, GetQueueStatusArgs.Instance),
                static async (b, _, ct) => ((MajordomoToolResult?)await b.GetQueueStatusAsync(ct).ConfigureAwait(false), null),
                null),
            [MajordomoTools.GetDispatchStatus] = new(
                static (tool, args) => EmptyOrRefuse(tool, args, GetDispatchStatusArgs.Instance),
                static async (b, _, ct) => ((MajordomoToolResult?)await b.GetDispatchStatusAsync(ct).ConfigureAwait(false), null),
                null),
            [MajordomoTools.GetAgentCapacity] = new(
                null,
                static async (b, a, ct) => await b.GetAgentCapacityAsync((GetAgentCapacityArgs)a, ct).ConfigureAwait(false),
                null),
            [MajordomoTools.ListWorkItems] = new(
                null,
                static async (b, a, ct) => ((MajordomoToolResult?)await b.ListWorkItemsAsync((ListWorkItemsArgs)a, ct).ConfigureAwait(false), null),
                null),
            [MajordomoTools.GetWorkItem] = new(
                null,
                static async (b, a, ct) => ((MajordomoToolResult?)await b.GetWorkItemAsync((GetWorkItemArgs)a, ct).ConfigureAwait(false), null),
                null),
            [MajordomoTools.GetWorkItemAudit] = new(
                null,
                static async (b, a, ct) => ((MajordomoToolResult?)await b.GetWorkItemAuditAsync((GetWorkItemAuditArgs)a, ct).ConfigureAwait(false), null),
                null),
            [MajordomoTools.CreateWorkItem] = new(
                null,
                null,
                static (b, a, initiator, _, commit, ct) => b.CreateAsync((CreateWorkItemArgs)a, initiator, commit, ct)),
            [MajordomoTools.CreateWorkItemChain] = new(
                null,
                null,
                static (b, a, initiator, _, commit, ct) => b.CreateChainAsync((CreateWorkItemChainArgs)a, initiator, commit, ct)),
            [MajordomoTools.UpdateWorkItem] = new(
                null,
                null,
                static (b, a, _, _, commit, ct) => b.UpdateAsync((UpdateWorkItemArgs)a, commit, ct)),
            [MajordomoTools.CancelWorkItem] = new(
                null,
                null,
                static (b, a, _, cascade, commit, ct) => b.CancelAsync((CancelWorkItemArgs)a, cascade, commit, ct)),
            [MajordomoTools.RetryWorkItem] = new(
                null,
                null,
                static (b, a, _, _, commit, ct) => b.RetryAsync((RetryWorkItemArgs)a, commit, ct)),
        }.ToFrozenDictionary();

    private readonly MajordomoReadBackend _reads;
    private readonly MajordomoMutateBackend _mutates;
    private readonly IOptionsMonitor<MajordomoServerOptions> _options;
    private readonly MajordomoTurnLedger _ledger;
    private readonly IHttpContextAccessor _httpContext;

    /// <summary>
    /// Per-identity serialization for mutate calls. Keys are configured API
    /// client names (a bounded operator-defined set), so the map cannot grow
    /// with caller input.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mutationGates = new(StringComparer.Ordinal);

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
    /// Startup-time wiring check called by registration: every vocabulary
    /// descriptor must carry a handler of its own class. Without this a tool
    /// added to <see cref="MajordomoTools.All"/> would publish cleanly and
    /// only fail per call — the gap must surface when the server is composed.
    /// </summary>
    internal static void VerifyVocabularyWiring()
    {
        foreach (var tool in MajordomoTools.All)
        {
            var wired = Wiring.TryGetValue(tool, out var wiring)
                && (tool.Class == MajordomoToolClass.Read ? wiring.Read is not null : wiring.Mutate is not null);
            if (!wired)
                throw new InvalidOperationException(
                    $"majordomo tool '{tool.Name}' is in the vocabulary but has no {tool.Class} handler wired");
        }
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
        MajordomoTool? tool = null;
        if (MajordomoTools.TryGet(name, out var resolved))
        {
            tool = resolved;
            (args, bindRefusal) = BindArguments(tool, argsNode);
        }

        // Serialize one identity's mutations across measure → decide → commit
        // → record. Stateless HTTP serves calls in parallel; without the gate
        // N concurrent calls would snapshot the same pre-call usage, each
        // pass the remaining-budget check, and jointly exceed the cap.
        var gate = args is MajordomoMutateArgs
            ? _mutationGates.GetOrAdd(identity, static _ => new SemaphoreSlim(1, 1))
            : null;
        if (gate is not null)
            await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // The blast radius Decide sees must be the real one: cancelling an
            // item cascades to every queued transitive dependent, a count the
            // argument contract cannot express. Enumerating here — inside the
            // gate — also means the measured set is what the commit replays.
            IReadOnlyList<WorkItem>? cancelCascade = null;
            int? projectedAffected = null;
            if (args is CancelWorkItemArgs cancel)
            {
                cancelCascade = await _mutates
                    .FindCancelCascadeTargetsAsync(cancel.Id, ct).ConfigureAwait(false);
                projectedAffected = 1 + cancelCascade.Count;
            }

            var usage = _ledger.Snapshot(identity, TimeSpan.FromSeconds(options.TurnWindowSeconds));
            var decision = MajordomoAuthorization.Decide(
                name, args, options.ToPolicy(), usage, projectedAffected);

            // The call record is written before any mutation is attempted —
            // refused calls are audited too. The wire name is untrusted input
            // (nothing proven about it reaches this boundary); it is rendered
            // through the echo guard so control characters cannot be smuggled
            // into the audit trail.
            var callId = Guid.NewGuid().ToString("N");
            var auditTool = name is null ? "<missing>" : Validation.DescribeUntrustedValue(name);
            var auditIdentity = Validation.DescribeUntrustedValue(identity);
            AuditLog.MajordomoToolCall(
                callId, auditIdentity, auditTool, DecisionLabel(decision), BoundArguments(argsNode));

            try
            {
                var (envelope, outcome, detail, isError) = await DispatchAsync(
                    decision, name, identity, args, argsNode, bindRefusal, cancelCascade, ct)
                    .ConfigureAwait(false);
                AuditLog.MajordomoToolOutcome(callId, auditIdentity, auditTool, outcome, detail);

                var json = JsonSerializer.SerializeToElement(envelope, MajordomoJson.Options);
                return new CallToolResult
                {
                    IsError = isError,
                    StructuredContent = json,
                    Content = [new TextContentBlock { Text = json.GetRawText() }],
                };
            }
            catch (Exception ex)
            {
                // A mutation that threw mid-commit may have already written —
                // post-commit steps (enqueue, webhooks, audit writes) fault
                // after the row lands. Charge the projected blast radius so a
                // faulted commit is not a free retry, and still emit the
                // outcome record.
                if (decision is MajordomoDecision.Execute
                    && args is MajordomoMutateArgs { DryRun: false } failed)
                {
                    _ledger.Record(
                        identity,
                        Math.Max(failed.AffectedItemCount, projectedAffected ?? failed.AffectedItemCount));
                }

                AuditLog.MajordomoToolOutcome(
                    callId, auditIdentity, auditTool, MajordomoOutcomes.Error,
                    Validation.DescribeUntrustedValue($"{ex.GetType().Name}: {ex.Message}"));
                throw;
            }
        }
        finally
        {
            gate?.Release();
        }
    }

    private async Task<(JsonObject Envelope, string Outcome, string? Detail, bool IsError)> DispatchAsync(
        MajordomoDecision decision,
        string? name,
        string identity,
        MajordomoToolArgs? args,
        JsonNode? argsNode,
        MajordomoRefusal? bindRefusal,
        IReadOnlyList<WorkItem>? cancelCascade,
        CancellationToken ct)
    {
        switch (decision)
        {
            case MajordomoDecision.Refuse refuse:
            {
                var refusal = bindRefusal is not null
                    ? bindRefusal with { Reason = ToReasonCode(refuse.Reason) }
                    : new MajordomoRefusal(ToReasonCode(refuse.Reason), refuse.Detail);
                return (Envelope(MajordomoOutcomes.Refused, refusal: refusal),
                    MajordomoOutcomes.Refused, refusal.Detail, true);
            }

            case MajordomoDecision.Execute execute:
            {
                if (execute.Tool.Class == MajordomoToolClass.Read)
                {
                    var (result, readRefusal) = await RunReadAsync(execute.Tool, args!, ct).ConfigureAwait(false);
                    if (readRefusal is not null)
                        return (Envelope(MajordomoOutcomes.Refused, refusal: readRefusal),
                            MajordomoOutcomes.Refused, readRefusal.Detail, true);
                    return (Envelope(MajordomoOutcomes.Executed, result: result),
                        MajordomoOutcomes.Executed, null, false);
                }

                var mutate = (MajordomoMutateArgs)args!;
                var mutation = await RunMutateAsync(
                        execute.Tool, mutate, cancelCascade, commit: !mutate.DryRun, ct)
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
                    return (Envelope(MajordomoOutcomes.Refused, refusal: mutation.Refusal),
                        MajordomoOutcomes.Refused, mutation.Refusal.Detail, true);

                var label = mutate.DryRun ? MajordomoOutcomes.DryRun : MajordomoOutcomes.Executed;
                return (Envelope(label, result: mutation.ChangeSet), label, null, false);
            }

            case MajordomoDecision.Propose propose:
            {
                var proposalArgs = (MajordomoMutateArgs)args!;
                var mutation = await RunMutateAsync(
                        propose.Proposal.Tool, proposalArgs, cancelCascade, commit: false, ct)
                    .ConfigureAwait(false);
                if (mutation.Refusal is not null)
                    return (Envelope(MajordomoOutcomes.Refused, refusal: mutation.Refusal),
                        MajordomoOutcomes.Refused, mutation.Refusal.Detail, true);
                // Echo the CANONICAL arguments (the bound contract
                // re-serialized), not the raw payload — what the proposal
                // shows is exactly what would execute.
                var view = new MajordomoProposalView(
                    propose.Proposal.Tool.Name,
                    JsonSerializer.SerializeToNode(proposalArgs, proposalArgs.GetType(), MajordomoJson.Options),
                    mutation.ChangeSet!.PlannedItemCount,
                    mutation.ChangeSet);
                return (Envelope(MajordomoOutcomes.Proposed, proposal: view),
                    MajordomoOutcomes.Proposed, null, false);
            }

            default:
                return (Envelope(MajordomoOutcomes.Refused,
                        refusal: new MajordomoRefusal(
                            MajordomoRefusalReasons.UnknownTool, $"unhandled decision for '{name}'")),
                    MajordomoOutcomes.Refused, "unhandled decision", true);
        }
    }

    private Task<(MajordomoToolResult? Result, MajordomoRefusal? Refusal)> RunReadAsync(
        MajordomoTool tool, MajordomoToolArgs args, CancellationToken ct) =>
        Wiring.TryGetValue(tool, out var wiring) && wiring.Read is { } read
            ? read(_reads, args, ct)
            : Task.FromResult<(MajordomoToolResult?, MajordomoRefusal?)>(
                (null, new MajordomoRefusal(
                    MajordomoRefusalReasons.UnknownTool, $"no read backend for '{tool.Name}'")));

    private Task<MajordomoMutationResult> RunMutateAsync(
        MajordomoTool tool,
        MajordomoMutateArgs args,
        IReadOnlyList<WorkItem>? cancelCascade,
        bool commit,
        CancellationToken ct) =>
        Wiring.TryGetValue(tool, out var wiring) && wiring.Mutate is { } mutate
            ? mutate(_mutates, args, ResolveInitiator(), cancelCascade, commit, ct)
            : Task.FromResult(MajordomoMutationResult.Refused(
                new MajordomoRefusal(
                    MajordomoRefusalReasons.UnknownTool, $"no mutate backend for '{tool.Name}'")));

    private ApiClientPrincipal? TryGetPrincipal()
    {
        var context = _httpContext.HttpContext;
        return context is not null && ApiKeyAuth.TryGetPrincipal(context, out var principal)
            ? principal
            : null;
    }

    private string ResolveIdentity()
    {
        var principal = TryGetPrincipal();
        if (principal is null)
            return "unknown";
        return ApiKeyAuth.IsAuthenticationDisabled(principal)
            ? ApiKeyAuth.AuthenticationDisabledClientName
            : principal.Name;
    }

    private WorkInitiator ResolveInitiator() =>
        TryGetPrincipal()?.FixedInitiator
        ?? new WorkInitiator
        {
            Issuer = "codeybox",
            Subject = "majordomo",
            DisplayName = "CodeyBox majordomo",
        };

    private static string BoundArguments(JsonNode? args)
    {
        var raw = args?.ToJsonString() ?? "{}";
        return raw.Length <= MaxAuditArgumentChars ? raw : raw[..MaxAuditArgumentChars] + "…";
    }

    private static string DecisionLabel(MajordomoDecision decision) => decision switch
    {
        MajordomoDecision.Execute => MajordomoDecisions.Execute,
        MajordomoDecision.Propose => MajordomoDecisions.Propose,
        MajordomoDecision.Refuse => MajordomoDecisions.Refuse,
        _ => MajordomoDecisions.Unknown,
    };

    private static string ToReasonCode(MajordomoRefusalReason reason) => reason switch
    {
        MajordomoRefusalReason.UnknownTool => MajordomoRefusalReasons.UnknownTool,
        MajordomoRefusalReason.ArgumentContractMismatch => MajordomoRefusalReasons.ArgumentContractMismatch,
        MajordomoRefusalReason.TooManyItemsInOneCall => MajordomoRefusalReasons.TooManyItemsInOneCall,
        MajordomoRefusalReason.TurnMutationBudgetExhausted => MajordomoRefusalReasons.TurnMutationBudgetExhausted,
        _ => MajordomoRefusalReasons.Refused,
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
    /// Tools wired with a binding override (the parameterless vocabulary
    /// members) substitute their single instances; every other contract goes
    /// through STJ with the shared majordomo serializer, so the validating
    /// constructors run and an invalid call is unrepresentable.
    /// </summary>
    internal static (MajordomoToolArgs? Args, MajordomoRefusal? Refusal) BindArguments(
        MajordomoTool tool, JsonNode? args)
    {
        if (Wiring.TryGetValue(tool, out var wiring) && wiring.Bind is { } bind)
            return bind(tool, args);

        try
        {
            // Absent arguments deserialize as an empty object so missing
            // required fields surface as contract errors, not a null binding.
            var bound = args ?? (JsonNode)new JsonObject();
            var value = bound.Deserialize(tool.ArgumentsType, MajordomoJson.Options);
            if (value is not MajordomoToolArgs typed)
                return (null, new MajordomoRefusal(
                    MajordomoRefusalReasons.ArgumentContractMismatch,
                    $"tool '{tool.Name}' arguments must be a JSON object"));
            return (typed, null);
        }
        catch (JsonException ex)
        {
            return (null, new MajordomoRefusal(
                MajordomoRefusalReasons.ArgumentContractMismatch,
                ex.Message,
                Field: ex.Path));
        }
        catch (ArgumentException ex)
        {
            return (null, new MajordomoRefusal(
                MajordomoRefusalReasons.ArgumentContractMismatch,
                ex.Message,
                Field: ex.ParamName));
        }
        catch (NotSupportedException ex)
        {
            return (null, new MajordomoRefusal(
                MajordomoRefusalReasons.ArgumentContractMismatch,
                ex.Message));
        }
    }

    private static (MajordomoToolArgs?, MajordomoRefusal?) EmptyOrRefuse<T>(
        MajordomoTool tool, JsonNode? args, T instance)
        where T : MajordomoToolArgs
    {
        if (args is JsonObject obj && obj.Count > 0)
            return (null, new MajordomoRefusal(
                MajordomoRefusalReasons.ArgumentContractMismatch,
                $"tool '{tool.Name}' takes no arguments — got {string.Join(", ", obj.Select(kv => kv.Key))}"));
        return (instance, null);
    }
}
