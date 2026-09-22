using CodeyBox.Core;
using CodeyBox.Majordomo;

namespace CodeyBox.Tests;

/// <summary>
/// The majordomo authorization decision is the single gate every tool call
/// passes through. These tests pin the whole contract: per-tool mode
/// boundaries, dry-run never counting as a mutation, default-deny on unknown
/// names, and the per-turn blast-radius cap binding in both modes.
/// </summary>
public sealed class MajordomoAuthorizationTests
{
    private static readonly MajordomoOptions Autonomous =
        new() { Mode = MajordomoAutonomyMode.Autonomous };

    private static readonly MajordomoOptions Proposed =
        new() { Mode = MajordomoAutonomyMode.Proposed };

    private static NewWorkItemSpec Spec() =>
        new(new ProjectId("demo"), "Add a thing", "Implement it.");

    private static CreateWorkItemChainArgs Chain(int items)
    {
        var nodes = new List<WorkItemChainNode> { new(Spec()) };
        for (var i = 1; i < items; i++)
            nodes.Add(new WorkItemChainNode(Spec(), [i - 1]));
        return new CreateWorkItemChainArgs(nodes);
    }

    /// <summary>A valid argument instance for every tool in the vocabulary.</summary>
    private static MajordomoToolArgs ArgsFor(MajordomoTool tool) => tool.Name switch
    {
        "get_queue_status" => GetQueueStatusArgs.Instance,
        "get_dispatch_status" => GetDispatchStatusArgs.Instance,
        "get_agent_capacity" => new GetAgentCapacityArgs(AgentKind.Claude),
        "list_work_items" => new ListWorkItemsArgs(limit: 10),
        "get_work_item" => new GetWorkItemArgs(WorkItemId.New()),
        "get_work_item_audit" => new GetWorkItemAuditArgs(WorkItemId.New(), iteration: 2),
        "create_work_item" => new CreateWorkItemArgs(Spec()),
        "create_work_item_chain" => Chain(3),
        "update_work_item" => new UpdateWorkItemArgs(WorkItemId.New(), new WorkItemPatch(title: "New title")),
        "cancel_work_item" => new CancelWorkItemArgs(WorkItemId.New(), "superseded by a different fix"),
        "retry_work_item" => new RetryWorkItemArgs(WorkItemId.New(), WorkItemRetryFrom.Audit),
        _ => throw new InvalidOperationException($"no args fixture for {tool.Name}"),
    };

    public static IEnumerable<object[]> AllTools() =>
        MajordomoTools.All.Select(t => new object[] { t.Name });

    public static IEnumerable<object[]> MutateTools() =>
        MajordomoTools.All.Where(t => t.Class == MajordomoToolClass.Mutate)
            .Select(t => new object[] { t.Name });

    // ── Table-driven mode boundary for every tool ────────────────────────────

    [Theory]
    [MemberData(nameof(AllTools))]
    public void Decide_EveryTool_BothModes(string toolName)
    {
        var tool = MajordomoTools.All.Single(t => t.Name == toolName);
        var args = ArgsFor(tool);

        var autonomous = MajordomoAuthorization.Decide(
            toolName, args, Autonomous, MajordomoTurnUsage.None);
        var proposed = MajordomoAuthorization.Decide(
            toolName, args, Proposed, MajordomoTurnUsage.None);

        if (tool.Class == MajordomoToolClass.Read)
        {
            // READ tools always execute in both modes.
            Assert.IsType<MajordomoDecision.Execute>(autonomous);
            Assert.IsType<MajordomoDecision.Execute>(proposed);
        }
        else
        {
            Assert.IsType<MajordomoDecision.Execute>(autonomous);
            var proposal = Assert.IsType<MajordomoDecision.Propose>(proposed);
            Assert.Same(tool, proposal.Proposal.Tool);
            Assert.Same(args, proposal.Proposal.Arguments);
            Assert.Equal(((MajordomoMutateArgs)args).AffectedItemCount,
                proposal.Proposal.AffectedItemCount);
        }
    }

    [Theory]
    [MemberData(nameof(AllTools))]
    public void Decide_IsPure(string toolName)
    {
        var tool = MajordomoTools.All.Single(t => t.Name == toolName);
        var args = ArgsFor(tool);
        var usage = new MajordomoTurnUsage { MutatedItems = 1 };

        var first = MajordomoAuthorization.Decide(toolName, args, Proposed, usage);
        var second = MajordomoAuthorization.Decide(toolName, args, Proposed, usage);

        Assert.Equal(first, second);
    }

    // ── Dry-run is never a mutation ──────────────────────────────────────────

    [Theory]
    [MemberData(nameof(MutateTools))]
    public void EveryMutateTool_CarriesDryRunInItsContract(string toolName)
    {
        var tool = MajordomoTools.All.Single(t => t.Name == toolName);
        Assert.True(typeof(MajordomoMutateArgs).IsAssignableFrom(tool.ArgumentsType),
            $"{tool.Name} args must derive from MajordomoMutateArgs");
        var dryRun = tool.ArgumentsType.GetProperty("DryRun");
        Assert.NotNull(dryRun);
        Assert.Equal(typeof(bool), dryRun!.PropertyType);
    }

    [Theory]
    [MemberData(nameof(MutateTools))]
    public void DryRun_Executes_InBothModes_EvenOverBudget(string toolName)
    {
        var args = (MajordomoMutateArgs)ArgsFor(
            MajordomoTools.All.Single(t => t.Name == toolName));
        var dry = args with { DryRun = true };

        // Usage already at the cap: a real mutation would be refused here.
        var spent = new MajordomoTurnUsage
        {
            MutatedItems = MajordomoOptions.DefaultMaxMutatedItemsPerTurn,
        };

        Assert.IsType<MajordomoDecision.Execute>(
            MajordomoAuthorization.Decide(toolName, dry, Autonomous, spent));
        Assert.IsType<MajordomoDecision.Execute>(
            MajordomoAuthorization.Decide(toolName, dry, Proposed, spent));
    }

    [Fact]
    public void DryRun_OversizedChain_StillExecutes()
    {
        // A chain larger than the per-turn cap would be refused as a real
        // call; as a dry-run it answers "what would this do" instead.
        var args = Chain(CreateWorkItemChainArgs.MaxItems) with { DryRun = true };
        var options = new MajordomoOptions
        {
            Mode = MajordomoAutonomyMode.Proposed,
            MaxMutatedItemsPerTurn = 4,
        };

        Assert.IsType<MajordomoDecision.Execute>(
            MajordomoAuthorization.Decide("create_work_item_chain", args, options, MajordomoTurnUsage.None));
    }

    // ── Unknown tools: default-deny, exact match ─────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("get_work_items")]          // plural — not a name
    [InlineData("GET_WORK_ITEM")]           // case variant
    [InlineData("Get_Work_Item")]           // case variant
    [InlineData("get_work_item ")]          // trailing whitespace
    [InlineData(" get_work_item")]          // leading whitespace
    [InlineData("work_item")]               // substring
    [InlineData("get")]                     // prefix
    [InlineData("cancel_work_items")]       // superstring
    [InlineData("retry-work-item")]         // separator variant
    [InlineData("run_shell_command")]       // plausible but never in vocabulary
    [InlineData("pause_queue")]             // service control — deliberately absent
    public void UnknownTool_IsRefused(string toolName)
    {
        var decision = MajordomoAuthorization.Decide(
            toolName, GetQueueStatusArgs.Instance, Autonomous, MajordomoTurnUsage.None);

        var refuse = Assert.IsType<MajordomoDecision.Refuse>(decision);
        Assert.Equal(MajordomoRefusalReason.UnknownTool, refuse.Reason);
        Assert.False(MajordomoTools.TryGet(toolName, out _));
    }

    [Fact]
    public void NullToolName_IsRefused()
    {
        var decision = MajordomoAuthorization.Decide(
            null, GetQueueStatusArgs.Instance, Autonomous, MajordomoTurnUsage.None);
        Assert.Equal(MajordomoRefusalReason.UnknownTool,
            Assert.IsType<MajordomoDecision.Refuse>(decision).Reason);
    }

    [Fact]
    public void EveryCanonicalName_ResolvesExactly()
    {
        foreach (var tool in MajordomoTools.All)
        {
            Assert.True(MajordomoTools.TryGet(tool.Name, out var resolved));
            Assert.Same(tool, resolved);
        }
    }

    [Fact]
    public void ArgumentTypeMismatch_IsRefused()
    {
        // The call names a mutate tool but carries a read tool's arguments.
        var decision = MajordomoAuthorization.Decide(
            "cancel_work_item", GetQueueStatusArgs.Instance, Autonomous, MajordomoTurnUsage.None);
        var refuse = Assert.IsType<MajordomoDecision.Refuse>(decision);
        Assert.Equal(MajordomoRefusalReason.ArgumentContractMismatch, refuse.Reason);
    }

    [Fact]
    public void NullArguments_AreRefused()
    {
        var decision = MajordomoAuthorization.Decide(
            "get_queue_status", null, Autonomous, MajordomoTurnUsage.None);
        Assert.Equal(MajordomoRefusalReason.ArgumentContractMismatch,
            Assert.IsType<MajordomoDecision.Refuse>(decision).Reason);
    }

    // ── Blast-radius bounds apply in both modes ──────────────────────────────

    public static IEnumerable<object[]> Modes() =>
        new MajordomoOptions[] { Autonomous, Proposed }.Select(o => new object[] { o });

    [Theory]
    [MemberData(nameof(Modes))]
    public void SingleCall_ExceedingCap_IsRefused(MajordomoOptions options)
    {
        var opts = options with { MaxMutatedItemsPerTurn = 3 };
        var args = Chain(4); // mutates 4 items, cap is 3

        var decision = MajordomoAuthorization.Decide(
            "create_work_item_chain", args, opts, MajordomoTurnUsage.None);

        var refuse = Assert.IsType<MajordomoDecision.Refuse>(decision);
        Assert.Equal(MajordomoRefusalReason.TooManyItemsInOneCall, refuse.Reason);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Call_ExceedingRemainingTurnBudget_IsRefused(MajordomoOptions options)
    {
        var opts = options with { MaxMutatedItemsPerTurn = 3 };
        var usage = new MajordomoTurnUsage { MutatedItems = 2 };

        // A 2-item chain fits the cap alone but not with 2 items already spent.
        var decision = MajordomoAuthorization.Decide(
            "create_work_item_chain", Chain(2), opts, usage);

        var refuse = Assert.IsType<MajordomoDecision.Refuse>(decision);
        Assert.Equal(MajordomoRefusalReason.TurnMutationBudgetExhausted, refuse.Reason);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Call_WhenBudgetFullySpent_IsRefused(MajordomoOptions options)
    {
        var opts = options with { MaxMutatedItemsPerTurn = 3 };
        var usage = new MajordomoTurnUsage { MutatedItems = 3 };

        var decision = MajordomoAuthorization.Decide(
            "cancel_work_item",
            new CancelWorkItemArgs(WorkItemId.New(), "no longer needed"),
            opts, usage);

        Assert.Equal(MajordomoRefusalReason.TurnMutationBudgetExhausted,
            Assert.IsType<MajordomoDecision.Refuse>(decision).Reason);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Call_ExactlyAtCapBoundary_IsAllowed(MajordomoOptions options)
    {
        var opts = options with { MaxMutatedItemsPerTurn = 3 };
        var usage = new MajordomoTurnUsage { MutatedItems = 1 };

        // 1 spent + 2 in this call == cap exactly.
        var decision = MajordomoAuthorization.Decide(
            "create_work_item_chain", Chain(2), opts, usage);

        if (options.Mode == MajordomoAutonomyMode.Autonomous)
            Assert.IsType<MajordomoDecision.Execute>(decision);
        else
            Assert.IsType<MajordomoDecision.Propose>(decision);
    }

    [Fact]
    public void Options_RejectOutOfRangeCap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MajordomoOptions { MaxMutatedItemsPerTurn = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MajordomoOptions { MaxMutatedItemsPerTurn = -5 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MajordomoOptions
            {
                MaxMutatedItemsPerTurn = MajordomoOptions.MaxAllowedMutatedItemsPerTurn + 1,
            });
    }

    [Fact]
    public void Options_Defaults_AreReviewFirst()
    {
        var options = new MajordomoOptions();
        Assert.Equal(MajordomoAutonomyMode.Proposed, options.Mode);
        Assert.Equal(MajordomoOptions.DefaultMaxMutatedItemsPerTurn, options.MaxMutatedItemsPerTurn);
    }

    [Fact]
    public void TurnUsage_RejectsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MajordomoTurnUsage { MutatedItems = -1 });
    }
}
