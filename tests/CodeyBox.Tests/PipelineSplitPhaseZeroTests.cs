using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the pipeline-split Phase 0 collaborators:
/// <see cref="PickupRebaseLockRegistry"/> (per-branch rebase mutual
/// exclusion) and <see cref="PipelineItemContext"/> (per-item state).
/// </summary>
public sealed class PipelineSplitPhaseZeroTests
{
    [Fact]
    public void Registry_RetainSameKey_ReturnsSameGateAndRefCounts()
    {
        var registry = new PickupRebaseLockRegistry();
        var first = registry.Retain("repo:branch");
        var second = registry.Retain("repo:branch");
        Assert.Same(first, second);
        Assert.Equal(2, first.ReferenceCount);
        registry.Release("repo:branch", first, releaseSemaphore: false);
        Assert.Equal(1, first.ReferenceCount);
        Assert.Equal(1, registry.Count);
        registry.Release("repo:branch", second, releaseSemaphore: false);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Registry_Semaphore_SerializesHolders()
    {
        var registry = new PickupRebaseLockRegistry();
        var gate = registry.Retain("repo:branch");
        await gate.Semaphore.WaitAsync();
        var acquired = false;
        var waiter = Task.Run(async () =>
        {
            await gate.Semaphore.WaitAsync();
            acquired = true;
            gate.Semaphore.Release();
        });
        await Task.Delay(50);
        Assert.False(acquired);
        gate.Semaphore.Release();
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(acquired);
        registry.Release("repo:branch", gate, releaseSemaphore: false);
    }

    [Fact]
    public void Registry_DistinctKeys_AreIndependent()
    {
        var registry = new PickupRebaseLockRegistry();
        var a = registry.Retain("repo:a");
        var b = registry.Retain("repo:b");
        Assert.NotSame(a, b);
        Assert.Equal(2, registry.Count);
        registry.Release("repo:a", a, releaseSemaphore: false);
        registry.Release("repo:b", b, releaseSemaphore: false);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void ItemContext_Create_ResolvesAgentKindFromItemOrProject()
    {
        var project = TestProject("proj");
        var item = TestItem(agent: new AgentKind("worker"));
        var ctx = PipelineItemContext.Create(item, project);
        Assert.Equal(item, ctx.Item);
        Assert.Equal(project, ctx.Project);
        Assert.Equal(new AgentKind("worker"), ctx.AgentKind);
    }

    [Fact]
    public void ItemContext_Create_FallsBackToProjectDefaultAgent()
    {
        var project = TestProject("proj", defaultAgent: "default-agent");
        var item = TestItem(agent: null);
        var ctx = PipelineItemContext.Create(item, project);
        Assert.Equal(new AgentKind("default-agent"), ctx.AgentKind);
    }

    [Fact]
    public void ItemContext_WithBranches_PreservesIdentityAndSetsBranches()
    {
        var ctx = PipelineItemContext.Create(TestItem(agent: null), TestProject("proj"))
            .WithBranches("repo", "main", "codeybox/abcd1234");
        Assert.Equal("repo", ctx.RepoId);
        Assert.Equal("main", ctx.BaseBranch);
        Assert.Equal("codeybox/abcd1234", ctx.WorkBranch);
    }

    private static WorkItem TestItem(AgentKind? agent) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        Agent = agent,
    };

    private static Project TestProject(string id, string defaultAgent = "default-agent") => new()
    {
        Id = new ProjectId(id),
        DisplayName = id,
        RepositoryUrl = "https://example.invalid/repo.git",
        DefaultAgent = new AgentKind(defaultAgent),
    };
}
