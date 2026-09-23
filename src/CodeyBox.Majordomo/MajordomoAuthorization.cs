using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// The single authorization gate for every majordomo tool call. Pure: same
/// inputs always yield the same decision, and nothing here touches I/O, the
/// transport, or the LLM.
///
/// <para>
/// This is the only place the rules live — transport, runtime, and UI items
/// must route every call through <see cref="Decide"/> rather than
/// re-implementing the policy. The rules, in order:
/// </para>
///
/// <list type="number">
///   <item>Unknown tool names are refused (default-deny, exact ordinal match — no substring or case tolerance).</item>
///   <item>An argument payload that is not the named tool's contract type is refused.</item>
///   <item>READ tools always execute, in both modes.</item>
///   <item>A dry-run MUTATE call always executes — it produces a change set and mutates nothing.</item>
///   <item>A MUTATE call affecting more items than <see cref="MajordomoOptions.MaxMutatedItemsPerTurn"/> is refused outright.</item>
///   <item>A MUTATE call that would push the turn's cumulative mutations past the cap is refused.</item>
///   <item>Otherwise: <see cref="MajordomoAutonomyMode.Autonomous"/> executes; <see cref="MajordomoAutonomyMode.Proposed"/> produces a proposal.</item>
/// </list>
///
/// <para>
/// The blast-radius checks run before the mode check on purpose: a proposal
/// the operator waves through is still a mutation, so the caps bind in both
/// modes.
/// </para>
/// </summary>
public static class MajordomoAuthorization
{
    /// <summary>
    /// Decides whether one tool call executes, becomes a proposal, or is refused.
    /// </summary>
    /// <param name="toolName">The tool name as received; resolved by exact ordinal match.</param>
    /// <param name="arguments">The typed argument payload for the call.</param>
    /// <param name="options">The current operator policy (mode + bounds).</param>
    /// <param name="turnUsage">Mutations already spent this turn.</param>
    public static MajordomoDecision Decide(
        string? toolName,
        MajordomoToolArgs? arguments,
        MajordomoOptions options,
        MajordomoTurnUsage turnUsage)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(turnUsage);

        if (!MajordomoTools.TryGet(toolName, out var tool))
        {
            return new MajordomoDecision.Refuse(
                MajordomoRefusalReason.UnknownTool,
                $"unknown tool '{DescribeToolName(toolName)}'");
        }

        if (arguments is null || arguments.GetType() != tool.ArgumentsType)
        {
            return new MajordomoDecision.Refuse(
                MajordomoRefusalReason.ArgumentContractMismatch,
                $"tool '{tool.Name}' requires arguments of type {tool.ArgumentsType.Name}");
        }

        if (tool.Class == MajordomoToolClass.Read)
            return new MajordomoDecision.Execute(tool);

        // A Mutate descriptor's argument type always derives from
        // MajordomoMutateArgs: MajordomoTool's constructor refuses a
        // classification/argument-type mismatch, and the exact-type check
        // above pinned the payload to that declared type. The cast is the
        // invariant, not a guess.
        var mutate = (MajordomoMutateArgs)arguments;

        // A dry-run emits the would-be change set without mutating: it is
        // never a mutation and never consumes turn budget, in either mode.
        if (mutate.DryRun)
            return new MajordomoDecision.Execute(tool);

        var cap = options.MaxMutatedItemsPerTurn;
        var affected = mutate.AffectedItemCount;

        // A mutate contract reporting fewer than one affected item would slip
        // past both budget checks and execute without consuming turn budget —
        // fail closed rather than trust the count.
        if (affected < 1)
        {
            return new MajordomoDecision.Refuse(
                MajordomoRefusalReason.ArgumentContractMismatch,
                $"tool '{tool.Name}' reported {affected} affected items; a mutation must affect at least one");
        }

        if (affected > cap)
        {
            return new MajordomoDecision.Refuse(
                MajordomoRefusalReason.TooManyItemsInOneCall,
                $"call would mutate {affected} items; the per-turn cap is {cap}");
        }

        // Remaining-budget form avoids overflow and also refuses the call
        // when the turn budget is already exhausted.
        var remaining = cap - turnUsage.MutatedItems;
        if (affected > remaining)
        {
            return new MajordomoDecision.Refuse(
                MajordomoRefusalReason.TurnMutationBudgetExhausted,
                $"call would mutate {affected} items but only {Math.Max(remaining, 0)} remain " +
                $"of this turn's cap of {cap} ({turnUsage.MutatedItems} already mutated)");
        }

        return options.Mode == MajordomoAutonomyMode.Autonomous
            ? new MajordomoDecision.Execute(tool)
            : new MajordomoDecision.Propose(new MajordomoProposal(tool, mutate));
    }

    /// <summary>
    /// Renders an untrusted tool name for an operator/model-facing detail
    /// string: control characters are stripped and the value is truncated, so
    /// terminal escapes and unbounded echoes cannot ride the refusal.
    /// </summary>
    private static string DescribeToolName(string? toolName)
        => Validation.DescribeUntrustedValue(toolName);
}
