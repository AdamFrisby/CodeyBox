using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.ExploratoryTesting;
using CodeyBox.ExploratoryTesting.Replay;
using CodeyBox.Sandbox.Graphical;
using CodeyBox.Tests.E2eAuthoring;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Admin.Web E2E suite: the cheap-model CUA authoring flow explores a seeded
/// admin UI and emits a deterministic e2e-replay artifact; the replay engine
/// then drives a FRESH instance of that UI with real mouse/keyboard input
/// through <see cref="ComputerUseBridge"/> and asserts on rendered state.
///
/// <para>Committed artifacts live in <c>E2eArtifacts/Admin/</c>:
/// <c>admin-queue.trace.json</c> (the recorded session) and
/// <c>admin-queue.replay.json</c> (the emitted deterministic artifact).
/// Running with <c>CODEYBOX_WRITE_ARTIFACTS=1</c> regenerates them from the
/// same deterministic pipeline; default runs assert byte-stability so silent
/// drift fails loudly.</para>
/// </summary>
public sealed class AdminWebE2eReplayTests
{
    private static readonly DateTimeOffset FrozenNow = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] StableScreenshot = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];

    private static string ArtifactsDir =>
        Path.Combine(AppContext.BaseDirectory, "E2eArtifacts", "Admin");

    // ── Scripted admin UI ─────────────────────────────────────────────────
    // Two layouts: A (recorded during authoring) and B (replayed against).
    // Bounds differ deliberately — replay must re-locate by recognition.

    private sealed record UiNode(string Role, string Name, int X, int Y, int Width, int Height)
    {
        public int CenterX => X + Width / 2;
        public int CenterY => Y + Height / 2;
        public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
    }

    private static IReadOnlyList<UiNode> NodesFor(string view, bool layoutA) => view switch
    {
        "queue" => layoutA
            ? [new("heading", "Work Queue", 10, 10, 200, 30),
               new("textbox", "Queue filter", 10, 60, 200, 28),
               new("button", "All", 220, 60, 60, 28)]
            : [new("heading", "Work Queue", 20, 20, 240, 36),
               new("textbox", "Queue filter", 100, 120, 200, 28),
               new("button", "All", 400, 120, 60, 28)],
        "queue-all" => layoutA
            ? [new("heading", "Work Queue", 10, 10, 200, 30),
               new("link", "Seeded Working item 01", 10, 150, 300, 24)]
            : [new("heading", "Work Queue", 20, 20, 240, 36),
               new("link", "Seeded Working item 01", 100, 300, 300, 24)],
        "detail" => layoutA
            ? [new("heading", "Seeded Working item 01", 10, 10, 260, 30),
               new("button", "Quota", 10, 60, 80, 28)]
            : [new("heading", "Seeded Working item 01", 30, 30, 300, 36),
               new("button", "Quota", 500, 200, 80, 28)],
        "quota" => layoutA
            ? [new("heading", "Quota", 10, 10, 120, 30),
               new("text", "seeded-fake", 10, 60, 120, 20)]
            : [new("heading", "Quota", 40, 40, 140, 36),
               new("text", "seeded-fake", 200, 400, 120, 20)],
        _ => [],
    };

    private static string TreeJson(string view, bool layoutA)
    {
        var nodes = NodesFor(view, layoutA).Select(n =>
            $"{{\"role\":\"{n.Role}\",\"name\":\"{n.Name}\"," +
            $"\"bounds\":{{\"x\":{n.X},\"y\":{n.Y},\"width\":{n.Width},\"height\":{n.Height}}}}}");
        return $"{{\"nodes\":[{string.Join(",", nodes)}]}}";
    }

    private sealed class AdminUiSandbox : ISandbox
    {
        private readonly bool _layoutA;
        public AdminUiSandbox(bool layoutA) => _layoutA = layoutA;

        public string Id { get; } = "admin-ui-" + Guid.NewGuid().ToString("N");
        public string View { get; private set; } = "queue";
        public List<SandboxInputEvent> RecordedInputEvents { get; } = new();

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
            => Task.FromResult(StableScreenshot);

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
        {
            RecordedInputEvents.AddRange(events);
            foreach (var e in events)
            {
                if (e.Type != SandboxInputEventType.Click || e.X is null || e.Y is null)
                    continue;
                var hit = NodesFor(View, _layoutA).FirstOrDefault(n => n.Contains(e.X.Value, e.Y.Value));
                if (hit is null) continue;
                View = (View, hit.Name) switch
                {
                    ("queue", "All") => "queue-all",
                    ("queue-all", "Seeded Working item 01") => "detail",
                    ("detail", "Quota") => "quota",
                    _ => View,
                };
            }
            return Task.CompletedTask;
        }

        public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default)
        {
            var hit = NodesFor(View, _layoutA).FirstOrDefault(n => n.Contains(x, y));
            return Task.FromResult<SandboxAccessibilitySnapshot?>(
                hit is null ? null : new SandboxAccessibilitySnapshot { Role = hit.Role, Name = hit.Name });
        }

        public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(TreeJson(View, _layoutA));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static E2eExplorationPlan QueueFlowPlan() => new()
    {
        TargetName = "codeybox-admin",
        EntryUrl = "http://localhost:5070",
        Actions =
        [
            new E2eExplorationAction { Kind = "click", X = 110, Y = 74 },
            new E2eExplorationAction { Kind = "type", Text = "auditing" },
            new E2eExplorationAction { Kind = "click", X = 250, Y = 74 },
            new E2eExplorationAction { Kind = "click", X = 160, Y = 162 },
            new E2eExplorationAction { Kind = "click", X = 50, Y = 74 },
        ],
        Assertions = [],
        EmitOptions = new E2eReplayEmitOptions { Name = "admin-queue-flow" },
    };

    private static async Task<(SessionTrace Trace, E2eReplayArtifact Artifact)> AuthorQueueFlowAsync()
    {
        var clock = new ControllableTimeProvider(FrozenNow);
        var sandbox = new AdminUiSandbox(layoutA: true);
        var session = new AppUnderTestSession(
            sandbox, new ComputerUseBridge(), "http://localhost:5070", StableScreenshot);
        var author = new CheapModelCuaAuthor(
            new CheapModelCuaAuthorOptions
            {
                ModelId = "claude-haiku-4-5-20251001",
                AuthoringLimits = new ComputerUseAuthoringLimits
                {
                    AllowedOrigins = ["http://localhost:5070"],
                },
            },
            clock);
        await using (session)
        {
            var result = await author.ExploreAndEmitAsync(
                session, QueueFlowPlan(), new ScriptedE2eCuaExplorer());
            return (result.Trace, result.Artifact);
        }
    }

    private static string ReadOrRegenerate(string fileName, string content)
    {
        var path = Path.Combine(ArtifactsDir, fileName);
        if (Environment.GetEnvironmentVariable("CODEYBOX_WRITE_ARTIFACTS") == "1")
        {
            Directory.CreateDirectory(ArtifactsDir);
            File.WriteAllText(path, content);
            return content;
        }
        Assert.True(File.Exists(path), $"Committed artifact missing: {path}");
        var committed = File.ReadAllText(path);
        Assert.Equal(Normalize(committed), Normalize(content));
        return committed;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AuthorQueueFlow_EmitsDeterministicValidArtifact()
    {
        var (trace, artifact) = await AuthorQueueFlowAsync();

        Assert.Equal(5, trace.Entries.Count);
        Assert.Equal("codeybox-admin", trace.TargetName);

        Assert.True(
            E2eReplayArtifactValidation.TryValidate(artifact, out var kind, out var detail),
            $"artifact invalid: {kind}: {detail}");
        var actions = artifact.Steps.Select(s => s.Action).ToList();
        Assert.Contains("navigate", actions);
        Assert.Contains("click", actions);
        Assert.Contains("fill", actions);
    }

    [Fact]
    public async Task AuthorQueueFlow_IsDeterministicAcrossRuns()
    {
        var (firstTrace, firstArtifact) = await AuthorQueueFlowAsync();
        var (secondTrace, secondArtifact) = await AuthorQueueFlowAsync();

        Assert.Equal(SessionTraceJson.Serialize(firstTrace), SessionTraceJson.Serialize(secondTrace));
        Assert.Equal(
            E2eReplayArtifactEmitter.SerializeArtifact(firstArtifact),
            E2eReplayArtifactEmitter.SerializeArtifact(secondArtifact));
    }

    [Fact]
    public async Task CommittedTrace_ReplaysViaRealMouseInput_AgainstShiftedLayout()
    {
        var (trace, _) = await AuthorQueueFlowAsync();
        var committed = ReadOrRegenerate("admin-queue.trace.json", SessionTraceJson.Serialize(trace));
        var replayTrace = SessionTraceJson.Deserialize(committed);

        var sandbox = new AdminUiSandbox(layoutA: false);
        var engine = new ReplayEngine();
        var result = await engine.ReplayAsync(sandbox, replayTrace);

        Assert.True(result.Passed, result.FailedStep?.Diagnostic);
        Assert.Equal(5, result.Steps.Count);
        Assert.All(result.Steps, s => Assert.True(s.Passed));

        var clicks = sandbox.RecordedInputEvents
            .Where(e => e.Type == SandboxInputEventType.Click).ToList();
        Assert.NotEmpty(clicks);
        var allButton = NodesFor("queue", layoutA: false).First(n => n.Name == "All");
        Assert.Contains(clicks, c => c.X == allButton.CenterX && c.Y == allButton.CenterY);
        var quotaButton = NodesFor("detail", layoutA: false).First(n => n.Name == "Quota");
        Assert.Contains(clicks, c => c.X == quotaButton.CenterX && c.Y == quotaButton.CenterY);

        var types = sandbox.RecordedInputEvents
            .Where(e => e.Type == SandboxInputEventType.Type).ToList();
        Assert.Contains(types, t => t.Text == "auditing");

        Assert.Equal("quota", sandbox.View);
        var finalTree = await sandbox.GetAccessibilityTreeJsonAsync();
        Assert.Contains("seeded-fake", finalTree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommittedReplayArtifact_IsSchemaValid()
    {
        var (_, artifact) = await AuthorQueueFlowAsync();
        var committed = ReadOrRegenerate(
            "admin-queue.replay.json", E2eReplayArtifactEmitter.SerializeArtifact(artifact));

        var parsed = JsonSerializer.Deserialize<E2eReplayArtifact>(
            committed, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(parsed);
        Assert.True(
            E2eReplayArtifactValidation.TryValidate(parsed, out var kind, out var detail),
            $"committed artifact invalid: {kind}: {detail}");
        Assert.NotEmpty(parsed.Steps);
        Assert.Equal("http://localhost:5070/healthz", parsed.Readiness?.Url);
    }
}
