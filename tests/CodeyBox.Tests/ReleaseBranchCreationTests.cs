using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Tests the release branch creation flow:
/// (a) SETIFNULL atomic branch-assignment at the DB layer (SqliteReleaseStore.TrySetBranchAsync)
/// (b) End-to-end EnsureReleaseBranchAsync: first work item creates the branch and persists it;
///     subsequent work items get the existing branch without spinning up a sandbox.
/// </summary>
public sealed class ReleaseBranchCreationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cb-branch-{Guid.NewGuid():N}.db");
    private readonly SqliteReleaseStore _store;
    private readonly SqliteWorkItemStore _workItemStore;

    public ReleaseBranchCreationTests()
    {
        _store = new SqliteReleaseStore(_dbPath);
        _workItemStore = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _workItemStore.Dispose();
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task TrySetBranchAsync_FirstCaller_Wins()
    {
        var rel = await SeedAsync();

        var won = await _store.TrySetBranchAsync(rel.Id, "release/v1.0", "abc123");

        Assert.True(won);
        var refreshed = await _store.GetAsync(rel.Id);
        Assert.Equal("release/v1.0", refreshed!.BranchName);
        Assert.Equal("abc123", refreshed.BaseCommitSha);
    }

    [Fact]
    public async Task TrySetBranchAsync_SecondCaller_LosesRace()
    {
        var rel = await SeedAsync();

        var first = await _store.TrySetBranchAsync(rel.Id, "release/v1.0", "sha-first");
        var second = await _store.TrySetBranchAsync(rel.Id, "release/v1.0-other", "sha-second");

        Assert.True(first);
        Assert.False(second);

        // The store keeps the first writer's values.
        var refreshed = await _store.GetAsync(rel.Id);
        Assert.Equal("release/v1.0", refreshed!.BranchName);
        Assert.Equal("sha-first", refreshed.BaseCommitSha);
    }

    [Fact]
    public async Task TrySetBranchAsync_ConcurrentCallers_ExactlyOneWins()
    {
        var rel = await SeedAsync();

        var tasks = Enumerable.Range(0, 8).Select(i =>
            _store.TrySetBranchAsync(rel.Id, $"release/v{i}", $"sha{i}"));

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_Throws()
    {
        var rel = await SeedAsync("duplicate-name");
        var duplicate = ReleaseTestHelper.SeedRelease(ReleaseState.Open) with
        {
            Name = rel.Name,
        };

        await Assert.ThrowsAnyAsync<Exception>(() => _store.CreateAsync(duplicate));
    }

    [Fact]
    public async Task ListAsync_FilterByState_ReturnsMatchingOnly()
    {
        var open1 = ReleaseTestHelper.SeedRelease(ReleaseState.Open);
        var open2 = ReleaseTestHelper.SeedRelease(ReleaseState.Open);
        var closed = ReleaseTestHelper.SeedRelease(ReleaseState.Closed);
        await _store.CreateAsync(open1);
        await _store.CreateAsync(open2);
        await _store.CreateAsync(closed);

        var openList = await _store.ListAsync(state: ReleaseState.Open);
        var closedList = await _store.ListAsync(state: ReleaseState.Closed);

        Assert.Equal(2, openList.Count);
        Assert.Single(closedList);
    }

    [Fact]
    public async Task GetByNameAsync_ExistingName_Returns()
    {
        var rel = await SeedAsync("my-release");

        var found = await _store.GetByNameAsync(rel.ProjectId, rel.Name);

        Assert.NotNull(found);
        Assert.Equal(rel.Id, found!.Id);
    }

    [Fact]
    public async Task GetByNameAsync_DifferentProject_ReturnsNull()
    {
        var rel = await SeedAsync("shared-name");

        var found = await _store.GetByNameAsync(new ProjectId("other-project"), rel.Name);

        Assert.Null(found);
    }

    // ── End-to-end EnsureReleaseBranchAsync tests ────────────────────────────

    [Fact]
    public async Task EnsureReleaseBranch_FirstWorkItem_CreatesBranchAndPersistsInDb()
    {
        // Arrange: release has no branch name yet (first work item scenario).
        var rel = await SeedAsync();
        var project = ReleaseTestHelper.EnabledProject();
        var svc = BuildBranchService(sandboxes: new AlwaysSucceedSandboxProvider(),
                                     gitHost: new DeepAuditTestGitHost());

        // Act: first caller triggers branch creation.
        var (branchName, _) = await svc.EnsureReleaseBranchAsync(rel, project, default);

        // Assert: returned name follows the default template "release/{name}".
        Assert.Equal($"release/{rel.Name}", branchName);

        // Assert: DB row has been updated with the branch name.
        var refreshed = await _store.GetAsync(rel.Id);
        Assert.NotNull(refreshed);
        Assert.Equal(branchName, refreshed!.BranchName);
    }

    [Fact]
    public async Task EnsureReleaseBranch_UsesConfiguredBaseBranchInsteadOfBareHead()
    {
        var rel = await SeedAsync();
        var project = ReleaseTestHelper.EnabledProject() with { DefaultBaseBranch = "develop" };
        var sandbox = new CapturingReleaseSandbox(
            new SandboxExecResult(0, "", ""),
            new SandboxExecResult(0, "develop-sha\n", ""),
            new SandboxExecResult(0, "", ""),
            new SandboxExecResult(0, "", ""));
        var svc = BuildBranchService(
            sandboxes: new SingleSandboxProvider(sandbox),
            gitHost: new DeepAuditTestGitHost());

        var (branchName, baseCommitSha) = await svc.EnsureReleaseBranchAsync(rel, project, default);

        Assert.Equal($"release/{rel.Name}", branchName);
        Assert.Equal("develop-sha", baseCommitSha);
        Assert.Contains(sandbox.Argv, argv => argv.SequenceEqual(["git", "-C", "/work/repo", "rev-parse", "origin/develop"]));
        Assert.Contains(sandbox.Argv, argv => argv.SequenceEqual(["git", "-C", "/work/repo", "checkout", "-b", branchName, "develop-sha"]));
        Assert.Contains(sandbox.Argv, argv => argv.SequenceEqual(["git", "-C", "/work/repo", "push", "origin", $"{branchName}:{branchName}"]));
        Assert.DoesNotContain(sandbox.Argv, argv => argv.SequenceEqual(["git", "-C", "/work/repo", "rev-parse", "origin/main"]));
        Assert.DoesNotContain(sandbox.Argv, argv => argv.SequenceEqual(["git", "-C", "/work/repo", "checkout", "-b", branchName]));
    }

    [Fact]
    public async Task ReleaseTransition_CompletesIntoConfiguredBaseBranchInsteadOfBareHead()
    {
        var project = ReleaseTestHelper.EnabledProject() with { DefaultBaseBranch = "develop" };
        var rel = await SeedAsync();
        await _store.TryTransitionStateAsync(rel with { State = ReleaseState.Closed }, ReleaseState.Open);
        await _store.TrySetBranchAsync(rel.Id, $"release/{rel.Name}", "release-base");
        var upstream = new CapturingUpstreamRemote();
        var svc = ReleaseTestHelper.BuildService(
            _store,
            _workItemStore,
            new InMemoryProjectRepository(project),
            new NullWebhookDispatcher(),
            gitHost: new DeepAuditTestGitHost(),
            upstreamFactory: new FixedUpstreamFactory(upstream));

        var (ok, error) = await svc.ForceBeginReviewAsync(rel.Id, default);

        Assert.True(ok, error);
        await WaitUntilAsync(async () =>
        {
            var current = await _store.GetAsync(rel.Id);
            return current?.State == ReleaseState.Released;
        });
        var request = Assert.Single(upstream.CompletionRequests);
        Assert.Equal("develop", request.BaseBranch);
    }

    [Fact]
    public async Task EnsureReleaseBranch_SubsequentWorkItem_ReturnsPreviousBranchWithoutSandbox()
    {
        // Arrange: branch already set, simulating a second (or later) work item pickup.
        var rel = await SeedAsync();
        await _store.TrySetBranchAsync(rel.Id, $"release/{rel.Name}", "existing-sha");
        var relWithBranch = (await _store.GetAsync(rel.Id))!;
        var project = ReleaseTestHelper.EnabledProject();

        // NullSandboxProvider throws NotSupportedException if CreateAsync is ever called —
        // this proves the short-circuit path is taken and no sandbox is provisioned.
        var svc = BuildBranchService(sandboxes: new NullSandboxProvider(),
                                     gitHost: new NullGitHost());

        // Act.
        var (branchName, baseCommitSha) = await svc.EnsureReleaseBranchAsync(relWithBranch, project, default);

        // Assert: values come directly from the pre-existing release record.
        Assert.Equal($"release/{rel.Name}", branchName);
        Assert.Equal("existing-sha", baseCommitSha);
    }

    [Fact]
    public async Task EnsureReleaseBranch_ConcurrentCallers_AllReturnSameBranchName()
    {
        // Arrange: multiple callers race on the same branchless release.
        var rel = await SeedAsync();
        var project = ReleaseTestHelper.EnabledProject();
        var svc = BuildBranchService(sandboxes: new AlwaysSucceedSandboxProvider(),
                                     gitHost: new DeepAuditTestGitHost());

        // Act: fire multiple concurrent EnsureReleaseBranchAsync calls.
        var tasks = Enumerable.Range(0, 4)
            .Select(_ => svc.EnsureReleaseBranchAsync(rel, project, default));
        var results = await Task.WhenAll(tasks);

        // Assert: every caller gets the same branch name (winner's value propagates to losers).
        var expectedBranch = $"release/{rel.Name}";
        Assert.All(results, r => Assert.Equal(expectedBranch, r.BranchName));

        // Assert: DB holds exactly one branch name.
        var refreshed = await _store.GetAsync(rel.Id);
        Assert.Equal(expectedBranch, refreshed!.BranchName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ReleaseService BuildBranchService(ISandboxProvider sandboxes, IGitHost gitHost)
    {
        var project = ReleaseTestHelper.EnabledProject();
        return ReleaseTestHelper.BuildService(
            _store,
            _workItemStore,
            new InMemoryProjectRepository(project),
            new NullWebhookDispatcher(),
            sandboxes: sandboxes,
            gitHost: gitHost);
    }

    private async Task<Release> SeedAsync(string? name = null)
    {
        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Open);
        if (name is not null)
            rel = rel with { Name = name };
        await _store.CreateAsync(rel);
        return rel;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
                return;
            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for release transition");
    }

    private sealed class SingleSandboxProvider : ISandboxProvider
    {
        private readonly ISandbox _sandbox;

        public SingleSandboxProvider(ISandbox sandbox) => _sandbox = sandbox;

        public string Name => "single";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) => Task.FromResult(_sandbox);
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CapturingReleaseSandbox : ISandbox
    {
        private readonly Queue<SandboxExecResult> _results;

        public CapturingReleaseSandbox(params SandboxExecResult[] results) => _results = new Queue<SandboxExecResult>(results);

        public string Id => "capturing-release";
        public List<IReadOnlyList<string>> Argv { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Argv.Add([.. exec.Argv]);
            var result = _results.Count > 0 ? _results.Dequeue() : new SandboxExecResult(0, "", "");
            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
