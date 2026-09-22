using System.Reflection;
using CodeyBox.Core;
using CodeyBox.Majordomo;

namespace CodeyBox.Tests;

/// <summary>
/// Contract-level guarantees of the majordomo vocabulary: the tool set is
/// closed and exactly what we declare, every argument type is a sealed
/// in-assembly record, no tool's contract can carry a shell command, host
/// path, or service-control intent, and the argument types themselves refuse
/// invalid shapes at construction.
/// </summary>
public sealed class MajordomoVocabularyTests
{
    private static NewWorkItemSpec Spec() =>
        new(new ProjectId("demo"), "A title", "A prompt");

    // ── Closed vocabulary ────────────────────────────────────────────────────

    [Fact]
    public void Vocabulary_IsExactlyTheDeclaredSet()
    {
        var names = MajordomoTools.All.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(
            [
                "cancel_work_item",
                "create_work_item",
                "create_work_item_chain",
                "get_agent_capacity",
                "get_dispatch_status",
                "get_queue_status",
                "get_work_item",
                "get_work_item_audit",
                "list_work_items",
                "retry_work_item",
                "update_work_item",
            ],
            names);
    }

    [Fact]
    public void ToolNames_AreCanonicalSnakeCase()
    {
        foreach (var tool in MajordomoTools.All)
            Assert.Matches("^[a-z][a-z0-9_]*$", tool.Name);
    }

    [Fact]
    public void EveryArgumentAndResultType_IsSealedAndInVocabulary()
    {
        var declaredArgs = MajordomoTools.All.Select(t => t.ArgumentsType).ToHashSet();
        var declaredResults = MajordomoTools.All.Select(t => t.ResultType).ToHashSet();

        foreach (var tool in MajordomoTools.All)
        {
            Assert.True(tool.ArgumentsType.IsSealed, $"{tool.ArgumentsType.Name} must be sealed");
            Assert.True(tool.ResultType.IsSealed, $"{tool.ResultType.Name} must be sealed");
            Assert.True(typeof(MajordomoToolArgs).IsAssignableFrom(tool.ArgumentsType));
            Assert.True(typeof(MajordomoToolResult).IsAssignableFrom(tool.ResultType));
        }

        // No argument or result type may exist outside the declared catalog:
        // the unions are closed by the internal constructors, and every
        // reachable member must appear in it.
        var reachable = typeof(MajordomoTools).Assembly.GetTypes()
            .Where(t => typeof(MajordomoToolArgs).IsAssignableFrom(t) && !t.IsAbstract);
        Assert.True(declaredArgs.SetEquals(reachable),
            "every concrete argument type must be declared in the catalog");

        var reachableResults = typeof(MajordomoTools).Assembly.GetTypes()
            .Where(t => typeof(MajordomoToolResult).IsAssignableFrom(t) && !t.IsAbstract);
        Assert.True(declaredResults.SetEquals(reachableResults),
            "every concrete result type must be declared in the catalog");
    }

    // ── No dangerous capability in the contract ──────────────────────────────

    [Fact]
    public void NoTool_PermitsShellFilesystemHostPathConfigOrServiceControl()
    {
        // The vocabulary's job is the queue and nothing else. Scan every
        // argument type's public surface for members that would carry a
        // command, a host path, a filesystem target, a service/config
        // mutation, or a process handle.
        string[] forbidden =
        [
            "command", "shell", "path", "file", "directory", "process",
            "url", "uri", "endpoint", "connection", "config",
            "service", "environment",
        ];

        var contractTypes = MajordomoTools.All
            .Select(t => t.ArgumentsType)
            .Concat([typeof(NewWorkItemSpec), typeof(WorkItemPatch), typeof(WorkItemChainNode)]);

        foreach (var type in contractTypes)
        foreach (var member in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var words = PascalCaseSegments(member.Name).ToList();
            Assert.DoesNotContain(
                forbidden, token => words.Contains(token, StringComparer.Ordinal));
        }
    }

    /// <summary>Splits a PascalCase member name into lower-case word segments.</summary>
    private static IEnumerable<string> PascalCaseSegments(string name)
    {
        var start = 0;
        for (var i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                yield return name[start..i].ToLowerInvariant();
                start = i;
            }
        }
        yield return name[start..].ToLowerInvariant();
    }

    // ── Retry-from parity with the orchestrator's policy strings ─────────────

    [Fact]
    public void RetryFrom_MapsToEveryPolicyValue()
    {
        var policyValues = new HashSet<string>(StringComparer.Ordinal)
        {
            RetryFromPolicy.Planning, RetryFromPolicy.PlanReview, RetryFromPolicy.PlanApproved,
            RetryFromPolicy.Work, RetryFromPolicy.Rework, RetryFromPolicy.Audit,
            RetryFromPolicy.Delegation, RetryFromPolicy.ConflictRework,
            RetryFromPolicy.Merge, RetryFromPolicy.Upstream,
        };

        var mapped = Enum.GetValues<WorkItemRetryFrom>().Select(f => f.ToPolicyValue()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(policyValues, mapped);
        foreach (var value in mapped)
            Assert.True(RetryFromPolicy.TryNormalize(value, out _));
    }

    // ── Argument shapes the type system enforces ─────────────────────────────

    [Fact]
    public void Chain_EdgesMustPointStrictlyBackwards()
    {
        var forward = new[] { new WorkItemChainNode(Spec()), new WorkItemChainNode(Spec(), [1]) };
        Assert.Throws<ArgumentException>(() => new CreateWorkItemChainArgs(forward));

        var selfEdge = new[] { new WorkItemChainNode(Spec(), [0]) };
        // single-node chain is already invalid, but the self-edge is too
        Assert.Throws<ArgumentException>(() => new CreateWorkItemChainArgs(selfEdge));

        var negative = new[] { new WorkItemChainNode(Spec()), new WorkItemChainNode(Spec(), [-1]) };
        Assert.Throws<ArgumentException>(() => new CreateWorkItemChainArgs(negative));
    }

    [Fact]
    public void Chain_RejectsDegenerateShapes()
    {
        Assert.Throws<ArgumentException>(() => new CreateWorkItemChainArgs([]));
        Assert.Throws<ArgumentException>(() => new CreateWorkItemChainArgs([new WorkItemChainNode(Spec())]));

        // No edges at all is not a chain.
        Assert.Throws<ArgumentException>(() => new CreateWorkItemChainArgs(
            [new WorkItemChainNode(Spec()), new WorkItemChainNode(Spec())]));

        // Duplicate edge.
        Assert.Throws<ArgumentException>(() => new CreateWorkItemChainArgs(
            [new WorkItemChainNode(Spec()), new WorkItemChainNode(Spec(), [0, 0])]));

        // Past the contract bound.
        var oversized = Enumerable.Range(0, CreateWorkItemChainArgs.MaxItems + 1)
            .Select(i => i == 0
                ? new WorkItemChainNode(Spec())
                : new WorkItemChainNode(Spec(), [0]))
            .ToList();
        Assert.Throws<ArgumentException>(() => new CreateWorkItemChainArgs(oversized));
    }

    [Fact]
    public void Chain_BlastRadius_IsItemCount()
    {
        var chain = new CreateWorkItemChainArgs(
            [new WorkItemChainNode(Spec()), new WorkItemChainNode(Spec(), [0]),
             new WorkItemChainNode(Spec(), [0, 1])]);
        Assert.Equal(3, chain.AffectedItemCount);
    }

    [Fact]
    public void Patch_WithNoChanges_IsUnrepresentable()
    {
        Assert.Throws<ArgumentException>(() => new UpdateWorkItemArgs(WorkItemId.New(), new WorkItemPatch()));
        Assert.Throws<ArgumentException>(() => new WorkItemPatch());
    }

    [Fact]
    public void Patch_ExternalIds_FollowReplaceSetSemantics()
    {
        var patch = new WorkItemPatch(externalIds: new Dictionary<string, string>
        {
            ["github"] = "gh-42",
        });
        Assert.NotNull(patch.ExternalIds);
        Assert.Equal("gh-42", patch.ExternalIds!["github"]);

        // An empty map is a real value: it clears the stored map.
        var clearing = new WorkItemPatch(externalIds: new Dictionary<string, string>());
        Assert.NotNull(clearing.ExternalIds);
        Assert.Empty(clearing.ExternalIds!);

        // Invalid namespaces and null values fail at the contract.
        Assert.Throws<ArgumentException>(() => new WorkItemPatch(
            externalIds: new Dictionary<string, string> { ["BAD NS"] = "x" }));
        Assert.Throws<ArgumentException>(() => new WorkItemPatch(
            externalIds: new Dictionary<string, string?> { ["github"] = null! }
                .ToDictionary(kv => kv.Key, kv => kv.Value!)));
    }

    [Fact]
    public void Spec_KnobKeys_CollapseCaseInsensitively_LikeTheRestSurface()
    {
        var spec = new NewWorkItemSpec(new ProjectId("demo"), "t", "p",
            knobs: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ChangeScope"] = "surgical",
                ["CHANGESCOPE"] = "refactor",
            });
        // Same last-wins case-insensitive resolution as the REST surface —
        // a case-variant duplicate cannot survive contract validation.
        Assert.Single(spec.Knobs);
        Assert.Equal("refactor", spec.Knobs["changescope"]);
    }

    [Fact]
    public void Spec_WhitespaceAuditComplexity_NormalisesToUnset()
    {
        var spec = new NewWorkItemSpec(new ProjectId("demo"), "t", "p", auditComplexity: "   ");
        Assert.Null(spec.AuditComplexity);
    }

    [Fact]
    public void Spec_RejectsOutOfContractValues()
    {
        Assert.Throws<ArgumentException>(() =>
            new NewWorkItemSpec(new ProjectId("demo"), "", "prompt"));
        Assert.Throws<ArgumentException>(() =>
            new NewWorkItemSpec(new ProjectId("demo"), "title", ""));
        Assert.Throws<ArgumentException>(() =>
            new NewWorkItemSpec(new ProjectId("demo"), new string('x', WorkItemLimits.MaxTitleLength + 1), "prompt"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NewWorkItemSpec(new ProjectId("demo"), "title", "prompt", priority: WorkItemLimits.MaxPriority + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NewWorkItemSpec(new ProjectId("demo"), "title", "prompt",
                workTimeout: TimeSpan.FromMinutes(WorkTimeoutPolicy.MaxMinutes + 1)));
        Assert.Throws<ArgumentException>(() =>
            new NewWorkItemSpec(new ProjectId("demo"), "title", "prompt",
                baseBranch: "main", workBranch: "main"));
        Assert.Throws<ArgumentException>(() =>
            new NewWorkItemSpec(new ProjectId("demo"), "title", "prompt", baseBranch: "-rf"));
        Assert.Throws<ArgumentException>(() =>
            new NewWorkItemSpec(new ProjectId("demo"), "title", "prompt",
                dependsOn: Enumerable.Range(0, WorkItemLimits.MaxDependsOn + 1).Select(_ => WorkItemId.New()).ToList()));
    }

    [Fact]
    public void Cancel_RequiresAReason()
    {
        Assert.Throws<ArgumentException>(() => new CancelWorkItemArgs(WorkItemId.New(), ""));
        Assert.Throws<ArgumentException>(() => new CancelWorkItemArgs(WorkItemId.New(), "   "));
        Assert.Throws<ArgumentException>(() =>
            new CancelWorkItemArgs(WorkItemId.New(), new string('x', AgentPauseValidation.MaxReasonLength + 1)));

        var ok = new CancelWorkItemArgs(WorkItemId.New(), "superseded");
        Assert.Equal(1, ok.AffectedItemCount);
    }

    [Fact]
    public void ListArgs_BoundThePage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListWorkItemsArgs(limit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListWorkItemsArgs(limit: ListWorkItemsArgs.MaxLimit + 1));
        Assert.Throws<ArgumentException>(() => new ListWorkItemsArgs(states: new HashSet<WorkItemState>()));

        var ok = new ListWorkItemsArgs(
            projectId: new ProjectId("demo"),
            states: new HashSet<WorkItemState> { WorkItemState.Failed },
            limit: 25);
        Assert.Equal(25, ok.Limit);
    }

    [Fact]
    public void ListArgs_RejectOutOfVocabularyStates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ListWorkItemsArgs(states: new HashSet<WorkItemState> { (WorkItemState)999 }));
    }

    [Fact]
    public void ListArgs_CopyTheCallerSuppliedStateSet()
    {
        var callerSet = new HashSet<WorkItemState> { WorkItemState.Failed };
        var args = new ListWorkItemsArgs(states: callerSet);

        callerSet.Add(WorkItemState.Queued);
        callerSet.Clear();

        Assert.NotNull(args.States);
        Assert.True(args.States!.SetEquals([WorkItemState.Failed]));
    }

    [Fact]
    public void Vocabulary_CannotBeMutatedThroughTheExposedList()
    {
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MajordomoTool>)MajordomoTools.All).RemoveAt(0));
    }

    [Fact]
    public void EveryMutateArgs_ReportsItsBlastRadius()
    {
        Assert.Equal(1, new CreateWorkItemArgs(Spec()).AffectedItemCount);
        Assert.Equal(1, new UpdateWorkItemArgs(WorkItemId.New(), new WorkItemPatch(prompt: "x")).AffectedItemCount);
        Assert.Equal(1, new CancelWorkItemArgs(WorkItemId.New(), "r").AffectedItemCount);
        Assert.Equal(1, new RetryWorkItemArgs(WorkItemId.New()).AffectedItemCount);
        Assert.Equal(2, new CreateWorkItemChainArgs(
            [new WorkItemChainNode(Spec()), new WorkItemChainNode(Spec(), [0])]).AffectedItemCount);
    }

    [Fact]
    public void Retry_RejectsOutOfRangeTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryWorkItemArgs(WorkItemId.New(), workTimeout: TimeSpan.Zero));
        var ok = new RetryWorkItemArgs(WorkItemId.New(), WorkItemRetryFrom.Merge, TimeSpan.FromMinutes(30));
        Assert.Equal(WorkItemRetryFrom.Merge, ok.From);
        Assert.Equal("merge", ok.From.ToPolicyValue());
    }

    [Fact]
    public void Retry_RejectsOutOfVocabularyPhase()
    {
        // A deserialized or cast enum must fail at the contract, not later in
        // ToPolicyValue — out-of-vocabulary values are unrepresentable.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryWorkItemArgs(WorkItemId.New(), (WorkItemRetryFrom)999));
    }
}
