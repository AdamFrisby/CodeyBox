using CodeyBox.AdminSeed;
using CodeyBox.Core;
using CodeyBox.ExploratoryTesting.Recipes;
using CodeyBox.Harness;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the seeded admin-instance harness: fake-runner behavior
/// selection and outcomes, seed-data determinism, real-store round-trips,
/// the admin recipe shape, and the harness CLI surface. The live
/// API+Admin.Web serving path is currently unverified without a VM: the E2E
/// replay suite uses an in-memory sandbox stub, and no test starts real
/// processes or contacts servers; these tests pin everything around that
/// gap without needing a VM.
/// </summary>
public sealed class AdminSeedTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static IOptionsMonitor<SeededFakeAgentOptions> MonitorOf(SeededFakeAgentOptions options)
    {
        var stub = new StubOptionsMonitor<SeededFakeAgentOptions>(options);
        return stub;
    }

    private sealed class StubOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;
        public StubOptionsMonitor(T value) => _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class StubSandbox : ISandbox
    {
        public string Id { get; } = "stub-" + Guid.NewGuid().ToString("N");
        public List<SandboxExec> Execs { get; } = new();
        public Func<SandboxExec, CancellationToken, Task<SandboxExecResult>>? OnExec { get; set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            return OnExec?.Invoke(exec, ct) ?? Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default) => Task.FromResult<SandboxAccessibilitySnapshot?>(null);
        public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── Behavior selection ────────────────────────────────────────────────

    [Theory]
    [InlineData("do work [seeded-fake:quota]", SeededFakeBehavior.QuotaPark)]
    [InlineData("do work [SEEDED-FAKE:QUOTA]", SeededFakeBehavior.QuotaPark)]
    [InlineData("do work [seeded-fake:auth]", SeededFakeBehavior.AuthFailure)]
    [InlineData("do work [seeded-fake:fail]", SeededFakeBehavior.NormalFailure)]
    [InlineData("do work [seeded-fake:empty]", SeededFakeBehavior.EmptyDiff)]
    public void Select_MarkersForceBranches(string prompt, SeededFakeBehavior expected)
    {
        var options = new SeededFakeAgentOptions { Seed = 42 };
        Assert.Equal(expected, SeededFakeBehaviorSelector.Select(prompt, options));
    }

    [Fact]
    public void Select_NoMarker_IsDeterministicForSeedAndPrompt()
    {
        var options = new SeededFakeAgentOptions { Seed = 7 };
        var first = SeededFakeBehaviorSelector.Select("implement seeded change", options);
        var second = SeededFakeBehaviorSelector.Select("implement seeded change", options);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Select_DefaultDistribution_FavorsSuccess()
    {
        var options = new SeededFakeAgentOptions { Seed = 42, DefaultSuccessBuckets = 70 };
        var successes = 0;
        for (var i = 0; i < 200; i++)
        {
            if (SeededFakeBehaviorSelector.Select($"seeded prompt {i:000}", options) == SeededFakeBehavior.Success)
                successes++;
        }
        Assert.InRange(successes, 100, 180);
    }

    // ── Runner outcomes ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Success_StagesFileViaTeeArgv()
    {
        var sandbox = new StubSandbox();
        var chunks = new List<string>();
        var runner = new SeededFakeAgentRunner(
            MonitorOf(new SeededFakeAgentOptions { Seed = 42 }));

        var result = await runner.RunAsync(
            sandbox, "/work/repo", "implement seeded change 001", credential: null,
            stdoutChunkCallback: chunks.Add);

        Assert.True(result.Success);
        Assert.Contains("seeded-fake", result.Summary);
        var staged = Assert.Single(sandbox.Execs);
        Assert.Equal(["tee", "--", "seeded-fake-change.md"], staged.Argv);
        Assert.Equal("/work/repo", staged.WorkingDirectory);
        Assert.Contains("seed 42", staged.Stdin);
        Assert.NotEmpty(chunks);
    }

    [Fact]
    public async Task RunAsync_Success_ContentIsDeterministic()
    {
        var first = new StubSandbox();
        var second = new StubSandbox();
        var runner = new SeededFakeAgentRunner(MonitorOf(new SeededFakeAgentOptions { Seed = 42 }));
        const string prompt = "implement seeded change 001";

        await runner.RunAsync(first, "/w", prompt, null);
        await runner.RunAsync(second, "/w", prompt, null);

        Assert.Equal(first.Execs[0].Stdin, second.Execs[0].Stdin);
    }

    [Fact]
    public async Task RunAsync_QuotaMarker_ParksWithQuotaDiagnostic()
    {
        var sandbox = new StubSandbox();
        var runner = new SeededFakeAgentRunner(MonitorOf(new SeededFakeAgentOptions { Seed = 42 }));

        var result = await runner.RunAsync(sandbox, "/w", "x [seeded-fake:quota]", null);

        Assert.True(result.Success);
        Assert.Empty(sandbox.Execs);
        Assert.Contains("RESOURCE_EXHAUSTED", result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_AuthMarker_FailsWithAuthSignal()
    {
        var sandbox = new StubSandbox();
        var runner = new SeededFakeAgentRunner(MonitorOf(new SeededFakeAgentOptions { Seed = 42 }));

        var result = await runner.RunAsync(sandbox, "/w", "x [seeded-fake:auth]", null);

        Assert.False(result.Success);
        Assert.Contains("401", result.Stderr);
    }

    [Fact]
    public async Task RunAsync_FailMarker_FailsNormally()
    {
        var sandbox = new StubSandbox();
        var runner = new SeededFakeAgentRunner(MonitorOf(new SeededFakeAgentOptions { Seed = 42 }));

        var result = await runner.RunAsync(sandbox, "/w", "x [seeded-fake:fail]", null);

        Assert.False(result.Success);
        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_EmptyMarker_SucceedsWithoutStaging()
    {
        var sandbox = new StubSandbox();
        var runner = new SeededFakeAgentRunner(MonitorOf(new SeededFakeAgentOptions { Seed = 42 }));

        var result = await runner.RunAsync(sandbox, "/w", "x [seeded-fake:empty]", null);

        Assert.True(result.Success);
        Assert.Empty(sandbox.Execs);
        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_ExecFailure_ReturnsFailure()
    {
        var sandbox = new StubSandbox
        {
            OnExec = (_, _) => Task.FromResult(new SandboxExecResult(1, "", "tee: boom")),
        };
        var runner = new SeededFakeAgentRunner(MonitorOf(new SeededFakeAgentOptions { Seed = 42 }));

        var result = await runner.RunAsync(sandbox, "/w", "implement seeded change", null);

        Assert.False(result.Success);
        Assert.Contains("exit 1", result.Summary);
    }

    [Fact]
    public async Task RunAsync_ExecutionUnavailable_SurfacesTypedSignal()
    {
        var sandbox = new StubSandbox
        {
            OnExec = (_, _) => throw new SandboxExecutionUnavailableException(127),
        };
        var runner = new SeededFakeAgentRunner(MonitorOf(new SeededFakeAgentOptions { Seed = 42 }));

        var result = await runner.RunAsync(sandbox, "/w", "implement seeded change", null);

        Assert.False(result.Success);
        Assert.True(result.ExecutionUnavailable);
    }

    [Theory]
    [InlineData("../evil.md")]
    [InlineData("/abs/path.md")]
    [InlineData("..")]
    [InlineData("sub/dir.md")]
    [InlineData("")]
    [InlineData("-a")]
    [InlineData("--help")]
    [InlineData("-")]
    public void ValidateArtifactFileName_RejectsNonBareNames(string fileName)
    {
        Assert.Throws<ArgumentException>(() => SeededFakeAgentRunner.ValidateArtifactFileName(fileName));
    }

    [Fact]
    public void Runner_Kind_IsSeededFake()
    {
        var runner = new SeededFakeAgentRunner(MonitorOf(new SeededFakeAgentOptions()));
        Assert.Equal("seeded-fake", runner.Kind.Value);
    }

    // ── Seed data ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildWorkItems_CoversEveryLifecycleState()
    {
        var items = AdminSeedData.BuildWorkItems(new AdminSeedSpec { Seed = 42, Now = FixedNow });

        var states = items.Select(i => i.State).ToHashSet();
        foreach (var state in Enum.GetValues<WorkItemState>())
        {
            Assert.Contains(state, states);
        }
        Assert.All(items, i => Assert.Equal(SeededFakeAgentRunner.FakeKind, i.Agent));
    }

    [Fact]
    public void BuildWorkItems_IsDeterministicForFixedSpec()
    {
        var spec = new AdminSeedSpec { Seed = 42, Now = FixedNow };
        var first = AdminSeedData.BuildWorkItems(spec);
        var second = AdminSeedData.BuildWorkItems(spec);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(
            first.Select(i => i.Id.ToString()),
            second.Select(i => i.Id.ToString()));
        Assert.Equal(
            first.Select(i => i.Title),
            second.Select(i => i.Title));
    }

    [Fact]
    public void BuildWorkItems_DifferentSeeds_DivergeIds()
    {
        var first = AdminSeedData.BuildWorkItems(new AdminSeedSpec { Seed = 42, Now = FixedNow });
        var second = AdminSeedData.BuildWorkItems(new AdminSeedSpec { Seed = 43, Now = FixedNow });

        Assert.NotEqual(
            first.Select(i => i.Id.ToString()),
            second.Select(i => i.Id.ToString()));
    }

    [Fact]
    public void BuildAuditReports_LinkToAuditedItems()
    {
        var spec = new AdminSeedSpec { Seed = 42, Now = FixedNow };
        var items = AdminSeedData.BuildWorkItems(spec);
        var reports = AdminSeedData.BuildAuditReports(spec, items);

        Assert.NotEmpty(reports);
        var ids = items.Select(i => i.Id.ToString()).ToHashSet();
        Assert.All(reports, r => Assert.Contains(r.WorkItemId, ids));
        Assert.Contains(reports, r => r.WorstSeverity == "blocking");
    }

    [Fact]
    public void BuildReleases_CoversOpenAndReleased()
    {
        var releases = AdminSeedData.BuildReleases(new AdminSeedSpec { Seed = 42, Now = FixedNow });
        Assert.Equal(2, releases.Count);
        Assert.Contains(releases, r => r.State == ReleaseState.Open);
        Assert.Contains(releases, r => r.State == ReleaseState.Released);
    }

    // ── Real-store round-trip ─────────────────────────────────────────────

    [Fact]
    public async Task Seeder_WritesReadableRowsThroughRealStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "codeybox-tests", Guid.NewGuid().ToString("N"));
        var db = Path.Combine(root, "seed.db");

        var seeder = new AdminSeeder();
        var summary = await seeder.SeedAsync(db, root, new AdminSeedSpec { Seed = 42, Now = FixedNow });

        Assert.Equal(Enum.GetValues<WorkItemState>().Length, summary.WorkItemCount);
        Assert.True(summary.AuditReportCount > 0);
        Assert.Equal(2, summary.ReleaseCount);
        Assert.Equal(1, summary.SuggestionCount);

        using var store = new SqliteWorkItemStore(summary.DbPath);
        var queued = await store.GetAsync(
            AdminSeedData.BuildWorkItems(new AdminSeedSpec { Seed = 42, Now = FixedNow })[0].Id);
        Assert.NotNull(queued);
        Assert.Equal(WorkItemState.Queued, queued.State);
    }

    [Fact]
    public async Task Seeder_ReseedIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "codeybox-tests", Guid.NewGuid().ToString("N"));
        var db = Path.Combine(root, "seed.db");
        var seeder = new AdminSeeder();
        var spec = new AdminSeedSpec { Seed = 42, Now = FixedNow };

        var first = await seeder.SeedAsync(db, root, spec);
        var second = await seeder.SeedAsync(db, root, spec);

        Assert.Equal(first.WorkItemCount, second.WorkItemCount);
        using var store = new SqliteWorkItemStore(second.DbPath);
        var count = 0;
        await foreach (var item in store.ListAsync())
            count++;
        Assert.Equal(first.WorkItemCount, count);
    }

    [Fact]
    public async Task Seeder_EscapingDbPath_Throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "codeybox-tests", Guid.NewGuid().ToString("N"));
        var seeder = new AdminSeeder();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            seeder.SeedAsync(Path.Combine("..", "evil.db"), root));
    }

    // ── Quota probe ───────────────────────────────────────────────────────

    [Fact]
    public async Task QuotaProbe_ReturnsDeterministicSnapshots()
    {
        var probe = new SeededFakeQuotaProbe(MonitorOf(new SeededFakeAgentOptions { Seed = 42 }));
        var member = new AgentMembership { Agent = SeededFakeAgentRunner.FakeKind, Billing = AgentBilling.Subscription, QualityScore = 50 };

        var first = await probe.GetAvailabilityAsync(member, CancellationToken.None);
        var second = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.True(first.IsKnown);
        Assert.Equal(first.AvailablePct, second.AvailablePct);
        Assert.Equal(SeededFakeAgentRunner.FakeKind, probe.Kind);
    }

    // ── Recipe ────────────────────────────────────────────────────────────

    [Fact]
    public void AdminRecipe_Default_IsDeterministicAndValid()
    {
        var first = CodeyBoxAdminRecipe.Default("/tmp/src", seed: 42);
        var second = CodeyBoxAdminRecipe.Default("/tmp/src", seed: 42);

        Assert.Equal("codeybox-admin", first.TargetName);
        Assert.Equal(CodeyBoxAdminRecipe.DefaultEntryUrl, first.EntryUrl);
        Assert.Equal(
            first.SeedSteps.Select(s => string.Join(' ', s.Command)),
            second.SeedSteps.Select(s => string.Join(' ', s.Command)));
        Assert.Contains(first.SeedSteps, s => s.Command.Contains("42"));
        Assert.Contains(first.RunCommand.Command, c => c == "42");
        Assert.Contains("$URL", string.Join(' ', first.BrowserCommand));
        Assert.NotEmpty(first.BuildSteps);
        Assert.NotEmpty(first.SeedSteps);
    }

    [Fact]
    public void AdminRecipe_RejectsBadInput()
    {
        Assert.Throws<ArgumentException>(() => CodeyBoxAdminRecipe.Default(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => CodeyBoxAdminRecipe.Default("/tmp/src", seed: -1));
    }

    // ── Harness CLI parsing ───────────────────────────────────────────────

    [Fact]
    public void AdminSeededParse_SeedVerb_Defaults()
    {
        var parsed = AdminSeededCommand.Parse(["seed"]);
        Assert.Equal(AdminSeededCommand.ParseStatus.Ok, parsed.Status);
        Assert.Equal("seed", parsed.Verb);
        Assert.Equal(42, parsed.Seed);
    }

    [Fact]
    public void AdminSeededParse_ServeVerb_ParsesOptions()
    {
        var parsed = AdminSeededCommand.Parse(
            ["serve", "--seed", "7", "--db", "/tmp/a.db", "--live", "--ready-timeout-sec", "30"]);
        Assert.Equal(AdminSeededCommand.ParseStatus.Ok, parsed.Status);
        Assert.Equal(7, parsed.Seed);
        Assert.Equal("/tmp/a.db", parsed.Db);
        Assert.False(parsed.FreezeQueue);
        Assert.Equal(TimeSpan.FromSeconds(30), parsed.ReadyTimeout);
    }

    [Theory]
    [InlineData("--seed", "-3")]
    [InlineData("--seed", "nope")]
    [InlineData("--ready-timeout-sec", "0")]
    public void AdminSeededParse_RejectsBadNumbers(string flag, string value)
    {
        var parsed = AdminSeededCommand.Parse(["serve", flag, value]);
        Assert.Equal(AdminSeededCommand.ParseStatus.Usage, parsed.Status);
    }

    [Fact]
    public void AdminSeededParse_RejectsNonLoopbackUrls()
    {
        var parsed = AdminSeededCommand.Parse(["serve", "--admin-url", "http://example.com:5070"]);
        Assert.Equal(AdminSeededCommand.ParseStatus.Invalid, parsed.Status);
    }

    [Fact]
    public void AdminSeededParse_RejectsUnknownVerb()
    {
        var parsed = AdminSeededCommand.Parse(["frobnicate"]);
        Assert.Equal(AdminSeededCommand.ParseStatus.Usage, parsed.Status);
    }

    [Fact]
    public void SeededInstanceEnv_CarriesSeedAndProjects()
    {
        var env = AdminSeededCommand.SeededInstanceEnv(
            "/tmp/seed/admin.db", "42",
            new Dictionary<string, string> { ["ASPNETCORE_URLS"] = "http://localhost:5050" });

        Assert.Equal("true", env["CodeyBox__SeededFakeAgents__Enabled"]);
        Assert.Equal("42", env["CodeyBox__SeededFakeAgents__Seed"]);
        Assert.Equal("seeded-shop", env["CodeyBox__Projects__0__Id"]);
        Assert.Equal("seeded-fake", env["CodeyBox__AgentClasses__0__Members__0__Agent"]);
    }
}
