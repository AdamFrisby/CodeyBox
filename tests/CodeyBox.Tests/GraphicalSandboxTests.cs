using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Graphical;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class GraphicalSandboxTests
{
    private static readonly byte[] NonUniformPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAD0lEQVR4nGNgYGD4//8/AAYBAv4CsjmuAAAAAElFTkSuQmCC");

    private static readonly byte[] UniformPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAC0lEQVR4nGNgAAMAAAcAAbKGrPQAAAAASUVORK5CYII=");

    private static readonly byte[] InvalidPng = [0x01, 0x02, 0x03];

    [Fact]
    public async Task ComputerUseBridge_MapsScreenshotAndInputActionsToSandboxCapabilities()
    {
        await using var sandbox = new RecordingGraphicalSandbox(NonUniformPng);
        var bridge = new ComputerUseBridge();

        var screenshot = await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "screenshot" });
        await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "left_click", X = 10, Y = 20 });
        await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "keypress", Key = "Return" });
        await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "type", Text = "hello" });
        await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "scroll", ScrollY = 3 });

        Assert.Equal(NonUniformPng, screenshot.ScreenshotPng);
        Assert.Equal(
            [
                new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 10, Y = 20 },
                new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Return" },
                new SandboxInputEvent { Type = SandboxInputEventType.Type, Text = "hello" },
                new SandboxInputEvent { Type = SandboxInputEventType.Scroll, Y = 3 },
            ],
            sandbox.Events);
    }

    [Fact]
    public async Task ComputerUseBridge_MapsDoubleClickMoveAndEventPassThrough()
    {
        await using var sandbox = new RecordingGraphicalSandbox(NonUniformPng);
        var bridge = new ComputerUseBridge();
        var passthroughEvents = new[]
        {
            new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Escape" },
        };

        await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "double_click", X = 10, Y = 20 });
        await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "move", X = 30, Y = 40 });
        await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "mouse_move", X = 50, Y = 60 });
        await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "events", Events = passthroughEvents });

        Assert.Equal(
            [
                new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 10, Y = 20 },
                new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 10, Y = 20 },
                new SandboxInputEvent { Type = SandboxInputEventType.Move, X = 30, Y = 40 },
                new SandboxInputEvent { Type = SandboxInputEventType.Move, X = 50, Y = 60 },
                new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Escape" },
            ],
            sandbox.Events);
    }

    [Fact]
    public async Task ComputerUseBridge_MapsClickKeyAliasesAndScrollFallbacks()
    {
        await using var sandbox = new RecordingGraphicalSandbox(NonUniformPng);
        var bridge = new ComputerUseBridge();

        await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "click", X = 11, Y = 12 });
        await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "key", Text = "Escape" });
        await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "scroll", X = 2 });
        await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "scroll", X = 99, ScrollX = -3 });

        Assert.Equal(
            [
                new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 11, Y = 12 },
                new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Escape" },
                new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = 2 },
                new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = -3 },
            ],
            sandbox.Events);
    }

    [Fact]
    public async Task ComputerUseBridge_RejectsMissingEventsAndUnsupportedActions()
    {
        await using var sandbox = new RecordingGraphicalSandbox(NonUniformPng);
        var bridge = new ComputerUseBridge();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = null! }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "events" }));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "drag" }));
    }

    [Fact]
    public async Task GraphicalSmokeAuditor_PassesForNonUniformScreenshot()
    {
        await using var sandbox = new RecordingGraphicalSandbox(NonUniformPng);
        var auditor = new GraphicalSmokeAuditor(TimeSpan.Zero);

        var result = await auditor.RunAsync(
            sandbox,
            "/work",
            new AuditContext(WorkItemId.New(), "work", "main", 1, "prompt"));

        Assert.True(result.Passed, result.RawOutput);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task GraphicalSmokeAuditor_FailsForUniformScreenshot()
    {
        await using var sandbox = new RecordingGraphicalSandbox(UniformPng);
        var auditor = new GraphicalSmokeAuditor(TimeSpan.Zero);

        var result = await auditor.RunAsync(
            sandbox,
            "/work",
            new AuditContext(WorkItemId.New(), "work", "main", 1, "prompt"));

        Assert.False(result.Passed);
        Assert.Single(result.Findings);
        Assert.Contains("uniform", result.Findings[0].Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GraphicalSmokeAuditor_FailsWhenScreenshotThrows()
    {
        await using var sandbox = new ThrowingScreenshotSandbox(new InvalidOperationException("scrot failed"));
        var auditor = new GraphicalSmokeAuditor(TimeSpan.Zero);

        var result = await auditor.RunAsync(
            sandbox,
            "/work",
            new AuditContext(WorkItemId.New(), "work", "main", 1, "prompt"));

        Assert.False(result.Passed);
        Assert.Single(result.Findings);
        Assert.Contains("graphical screenshot failed", result.Findings[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scrot failed", result.RawOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GraphicalSmokeAuditor_FailsForInvalidScreenshotBytes()
    {
        await using var sandbox = new RecordingGraphicalSandbox(InvalidPng);
        var auditor = new GraphicalSmokeAuditor(TimeSpan.Zero);

        var result = await auditor.RunAsync(
            sandbox,
            "/work",
            new AuditContext(WorkItemId.New(), "work", "main", 1, "prompt"));

        Assert.False(result.Passed);
        Assert.Single(result.Findings);
        Assert.Contains("supported PNG", result.Findings[0].Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectAuditorComposer_AddsGuiSmokeAuditor_ForGraphicalProjects()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("gui"),
            DisplayName = "GUI",
            RepositoryUrl = "https://example.com/gui.git",
            GraphicalSandbox = true,
            Audit = new ProjectAudit
            {
                Languages = [],
                AuditTypes = [],
            },
        };

        var auditors = composer.Compose(project, new ScriptedAgent([]));

        Assert.Equal(["gui:smoke"], auditors.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void ProjectAuditorComposer_DoesNotDuplicateGuiSmokeAuditor()
    {
        var existingGuiSmoke = new QueueAuditor("gui:smoke", new AuditResult(true, []));
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([existingGuiSmoke]));
        var project = new Project
        {
            Id = new ProjectId("gui"),
            DisplayName = "GUI",
            RepositoryUrl = "https://example.com/gui.git",
            GraphicalSandbox = true,
            Audit = new ProjectAudit
            {
                Languages = [],
                AuditTypes = ["scripted"],
            },
        };

        var auditors = composer.Compose(project, new ScriptedAgent([]));

        Assert.Equal(["gui:smoke"], auditors.Select(a => a.Name).ToArray());
    }

    [Fact]
    public async Task PipelineRunner_UsesGraphicalSandboxForWorkReworkAndAuditToolPhases()
    {
        var workspace = Directory.CreateTempSubdirectory("codeybox-graphical-route-").FullName;
        try
        {
            var seed = await TestSupport.CreateSeedRepoAsync(workspace);
            var sandboxes = new CapturingSandboxProvider(
                new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
            var auditor = new QueueAuditor(
                "route:tool",
                new AuditResult(false, [new AuditFinding("route:tool", AuditSeverity.Error, "needs rework", "x")]),
                new AuditResult(true, []));
            var audit = new ProjectAudit
            {
                MaxIterations = 2,
                AuditTypes = ["scripted"],
                ExcludedAuditors = ["gui:smoke"],
            };
            using var tp = TestSupport.BuildPipeline(
                workspace,
                seed,
                auditors: [auditor],
                projectAudit: audit,
                sandboxProvider: sandboxes,
                graphicalSandbox: true);
            tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));
            tp.Agent.WorkPlan.Enqueue(new FileWrite("rework.txt", "rework\n"));
            var item = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("test-project"),
                Title = "graphical routing",
                Prompt = "do work",
                Agent = AgentKind.Claude,
                WorkBranch = "feature/graphical-routing",
            };

            await tp.Store.CreateAsync(item);
            await tp.Pipeline.RunAsync(item, CancellationToken.None);

            var final = await tp.Store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Done, final!.State);
            AssertGraphical(Assert.Single(sandboxes.Specs, s => s.TimingPhase == "work"));
            AssertGraphical(Assert.Single(sandboxes.Specs, s => s.TimingPhase == "rework"));
            var auditSpecs = sandboxes.Specs.Where(s => s.TimingPhase == "audit").ToArray();
            Assert.NotEmpty(auditSpecs);
            Assert.All(auditSpecs, AssertGraphical);

            var merge = Assert.Single(sandboxes.Specs, s => s.TimingPhase == "merge");
            Assert.Equal(SandboxProfileFlavor.Headless, merge.Flavor);
            Assert.NotEqual(SandboxConventions.GraphicalNetworkProfile, merge.Network.ProfileName);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    private static void AssertGraphical(SandboxSpec spec)
    {
        Assert.Equal(SandboxProfileFlavor.Graphical, spec.Flavor);
        Assert.Equal(SandboxConventions.GraphicalNetworkProfile, spec.Network.ProfileName);
    }

    private sealed class RecordingGraphicalSandbox : ISandbox
    {
        private readonly byte[] _screenshot;

        public RecordingGraphicalSandbox(byte[] screenshot)
        {
            _screenshot = screenshot;
        }

        public string Id => "graphical-test";
        public List<SandboxInputEvent> Events { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
            => Task.FromResult(_screenshot);

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
        {
            Events.AddRange(events);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingScreenshotSandbox : ISandbox
    {
        private readonly Exception _exception;

        public ThrowingScreenshotSandbox(Exception exception)
        {
            _exception = exception;
        }

        public string Id => "throwing-graphical-test";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
            => Task.FromException<byte[]>(_exception);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class QueueAuditor : IAuditor
    {
        private readonly Queue<AuditResult> _results;

        public QueueAuditor(string name, params AuditResult[] results)
        {
            Name = name;
            _results = new Queue<AuditResult>(results);
        }

        public string Name { get; }
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            if (_results.Count == 0)
                throw new InvalidOperationException("No queued audit result remains.");
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class CapturingSandboxProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;

        public CapturingSandboxProvider(ISandboxProvider inner)
        {
            _inner = inner;
        }

        public string Name => _inner.Name;
        public List<SandboxSpec> Specs { get; } = [];

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            Specs.Add(spec);
            return _inner.CreateAsync(spec, ct);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }
}
