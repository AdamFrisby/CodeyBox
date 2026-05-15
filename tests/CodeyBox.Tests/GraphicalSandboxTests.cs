using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.Tests;

public sealed class GraphicalSandboxTests
{
    private static readonly byte[] NonUniformPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAD0lEQVR4nGNgYGD4//8/AAYBAv4CsjmuAAAAAElFTkSuQmCC");

    private static readonly byte[] UniformPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAC0lEQVR4nGNgAAMAAAcAAbKGrPQAAAAASUVORK5CYII=");

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

        Assert.Contains(auditors, a => a.Name == "gui:smoke");
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
}
