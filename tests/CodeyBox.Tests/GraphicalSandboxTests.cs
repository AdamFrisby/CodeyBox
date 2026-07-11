using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
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

    public static IEnumerable<object[]> MalformedPngCases()
    {
        yield return new object[] { WithChunkLength(NonUniformPng, int.MaxValue), "invalid PNG chunk length" };
        yield return new object[] { WithChunkLength(NonUniformPng, 12), "invalid IHDR length" };
        yield return new object[] { WithByte(NonUniformPng, 24, 16), "unsupported PNG bit depth" };
        yield return new object[] { WithByte(NonUniformPng, 25, 5), "unsupported PNG color type" };
        yield return new object[] { WithByte(NonUniformPng, 28, 1), "interlaced PNG screenshots are not supported" };
        yield return new object[] { WithByte(NonUniformPng, 25, 3), "indexed PNG has no palette" };
        yield return new object[]
        {
            BuildPng(2, 1, colorType: 3, decompressedScanlines: [0, 0, 1], palette: [0, 0, 0]),
            "missing palette entry",
        };
        yield return new object[] { BuildPng(2, 1, colorType: 2, decompressedScanlines: [0, 0, 0]), "truncated" };
        yield return new object[] { BuildPng(1, 1, colorType: 2, decompressedScanlines: [99, 0, 0, 0]), "unknown PNG filter" };
        yield return new object[] { BuildPng(4097, 1, colorType: 0, decompressedScanlines: [0, 0]), "dimensions exceed" };
        yield return new object[] { BuildPng(1, 1, colorType: 0, decompressedScanlines: [0, 0, 0]), "exceeds expected decoded size" };
    }

    public static IEnumerable<object[]> FilteredPngSuccessCases()
    {
        yield return new object[]
        {
            "Sub",
            BuildPng(2, 1, colorType: 2, decompressedScanlines: [1, 0, 0, 0, 255, 255, 255]),
        };
        yield return new object[]
        {
            "Up",
            BuildPng(1, 2, colorType: 2, decompressedScanlines: [0, 0, 0, 0, 2, 255, 255, 255]),
        };
        yield return new object[]
        {
            "Average",
            BuildPng(2, 1, colorType: 2, decompressedScanlines: [3, 0, 0, 0, 255, 255, 255]),
        };
        yield return new object[]
        {
            "Paeth",
            BuildPng(2, 1, colorType: 2, decompressedScanlines: [4, 0, 0, 0, 255, 255, 255]),
        };
    }

    public static IEnumerable<object[]> InvalidComputerUseEventCases()
    {
        yield return new object[] { Array.Empty<SandboxInputEvent>() };
        yield return new object[] { new SandboxInputEvent[] { null! } };
        yield return new object[] { new[] { new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 10 } } };
        yield return new object[] { new[] { new SandboxInputEvent { Type = SandboxInputEventType.Click, X = -1, Y = 0 } } };
        yield return new object[] { new[] { new SandboxInputEvent { Type = SandboxInputEventType.Move, X = 10 } } };
        yield return new object[] { new[] { new SandboxInputEvent { Type = SandboxInputEventType.Move, X = 0, Y = -1 } } };
        yield return new object[] { new[] { new SandboxInputEvent { Type = SandboxInputEventType.Key } } };
        yield return new object[] { new[] { new SandboxInputEvent { Type = SandboxInputEventType.Type } } };
        yield return new object[] { new[] { new SandboxInputEvent { Type = SandboxInputEventType.Type, Text = "" } } };
        yield return new object[] { new[] { new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = 1, Y = 1 } } };
        yield return new object[] { new[] { new SandboxInputEvent { Type = SandboxInputEventType.Scroll, Y = 1001 } } };
        yield return new object[] { new[] { new SandboxInputEvent { Type = (SandboxInputEventType)999 } } };
    }

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
    public async Task ComputerUseBridge_AllowsWhitespaceTypeTextButRejectsWhitespaceKeys()
    {
        await using var sandbox = new RecordingGraphicalSandbox(NonUniformPng);
        var bridge = new ComputerUseBridge();

        await bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "type", Text = " \n\t" });
        await Assert.ThrowsAsync<ArgumentException>(() =>
            bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "key", Key = " " }));

        var inputEvent = Assert.Single(sandbox.Events);
        Assert.Equal(SandboxInputEventType.Type, inputEvent.Type);
        Assert.Equal(" \n\t", inputEvent.Text);
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

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            bridge.ExecuteAsync(null!, new ComputerUseRequest { Action = "screenshot" }));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            bridge.ExecuteAsync(sandbox, null!));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = null! }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "events" }));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "drag" }));
    }

    [Fact]
    public async Task ComputerUseBridge_RejectsOversizedInputBeforeCallingSandbox()
    {
        await using var sandbox = new RecordingGraphicalSandbox(NonUniformPng);
        var bridge = new ComputerUseBridge(new ComputerUseBridgeOptions
        {
            MaxEventsPerCall = 2,
            MaxTextUtf8Bytes = 4,
            MaxKeyUtf8Bytes = 4,
            MaxCoordinate = 20,
            MaxScrollMagnitude = 2,
            MaxInputEventsPerWindow = 100,
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            bridge.ExecuteAsync(sandbox, new ComputerUseRequest
            {
                Action = "events",
                Events =
                [
                    new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "A" },
                    new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "B" },
                    new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "C" },
                ],
            }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "type", Text = "hello" }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "key", Key = "Return" }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "move", X = 21, Y = 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            bridge.ExecuteAsync(sandbox, new ComputerUseRequest { Action = "scroll" }));

        Assert.Empty(sandbox.Events);
    }

    [Theory]
    [MemberData(nameof(InvalidComputerUseEventCases))]
    public async Task ComputerUseBridge_RejectsMalformedInputEventsBeforeCallingSandbox(IReadOnlyList<SandboxInputEvent> events)
    {
        await using var sandbox = new RecordingGraphicalSandbox(NonUniformPng);
        var bridge = new ComputerUseBridge();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            bridge.ExecuteAsync(sandbox, new ComputerUseRequest
            {
                Action = "events",
                Events = events,
            }));

        Assert.Empty(sandbox.Events);
    }

    [Fact]
    public void ComputerUseBridge_RejectsInvalidOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ComputerUseBridge(new ComputerUseBridgeOptions { MaxEventsPerCall = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ComputerUseBridge(new ComputerUseBridgeOptions { MaxTextUtf8Bytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ComputerUseBridge(new ComputerUseBridgeOptions { MaxKeyUtf8Bytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ComputerUseBridge(new ComputerUseBridgeOptions { MaxCoordinate = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ComputerUseBridge(new ComputerUseBridgeOptions { MaxScrollMagnitude = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ComputerUseBridge(new ComputerUseBridgeOptions { ToolCallTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ComputerUseBridge(new ComputerUseBridgeOptions { MaxInputEventsPerWindow = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ComputerUseBridge(new ComputerUseBridgeOptions { RateLimitWindow = TimeSpan.Zero }));
    }

    [Fact]
    public async Task ComputerUseBridge_AppliesInputRateBudgetExpiryAndToolTimeouts()
    {
        await using var rateSandbox = new RecordingGraphicalSandbox(NonUniformPng);
        var rateTime = new ManualTimeProvider();
        var rateLimited = new ComputerUseBridge(new ComputerUseBridgeOptions
        {
            MaxInputEventsPerWindow = 1,
            RateLimitWindow = TimeSpan.FromMinutes(1),
        }, timeProvider: rateTime);

        await rateLimited.ExecuteAsync(rateSandbox, new ComputerUseRequest { Action = "click" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rateLimited.ExecuteAsync(rateSandbox, new ComputerUseRequest { Action = "click" }));
        rateTime.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1));
        await rateLimited.ExecuteAsync(rateSandbox, new ComputerUseRequest { Action = "click" });
        Assert.Equal(2, rateSandbox.Events.Count);

        await using var hangingSandbox = new HangingGraphicalSandbox();
        var time = new ManualTimeProvider();
        var toolTimeout = TimeSpan.FromMilliseconds(10);
        var timeoutBridge = new ComputerUseBridge(new ComputerUseBridgeOptions
        {
            ToolCallTimeout = toolTimeout,
        }, timeProvider: time);

        var screenshotTask = timeoutBridge.ExecuteAsync(hangingSandbox, new ComputerUseRequest { Action = "screenshot" });
        await hangingSandbox.WaitUntilOperationEnteredAsync();
        time.Advance(toolTimeout);
        await Assert.ThrowsAsync<TimeoutException>(() => screenshotTask);

        hangingSandbox.PrepareForNextOperation();
        var clickTask = timeoutBridge.ExecuteAsync(hangingSandbox, new ComputerUseRequest { Action = "click" });
        await hangingSandbox.WaitUntilOperationEnteredAsync();
        time.Advance(toolTimeout);
        await Assert.ThrowsAsync<TimeoutException>(() => clickTask);
    }

    [Fact]
    public async Task ComputerUseBridge_ToolCallTimeout_UsesSystemTimeProvider()
    {
        await using var hangingSandbox = new HangingGraphicalSandbox();
        var bridge = new ComputerUseBridge(new ComputerUseBridgeOptions
        {
            ToolCallTimeout = TimeSpan.FromMilliseconds(200),
        });

        await Assert.ThrowsAsync<TimeoutException>(() =>
            bridge.ExecuteAsync(hangingSandbox, new ComputerUseRequest { Action = "screenshot" }));
    }

    [Fact]
    public async Task ComputerUseBridge_PropagatesCallerCancellationAsOperationCanceled()
    {
        await using var hangingSandbox = new HangingGraphicalSandbox();
        using var cts = new CancellationTokenSource();
        var bridge = new ComputerUseBridge(new ComputerUseBridgeOptions
        {
            ToolCallTimeout = TimeSpan.FromMinutes(1),
        });

        var task = bridge.ExecuteAsync(
            hangingSandbox,
            new ComputerUseRequest { Action = "screenshot" },
            cts.Token);
        await hangingSandbox.WaitUntilOperationEnteredAsync();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => task);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<TimeoutException>(ex);
    }

    [Fact]
    public async Task ISandbox_DefaultGraphicalCapabilitiesRejectUnsupportedSandbox()
    {
        await using ISandbox sandbox = new HeadlessOnlySandbox();

        var screenshot = await Assert.ThrowsAsync<NotSupportedException>(() => sandbox.GetScreenshotAsync());
        var input = await Assert.ThrowsAsync<NotSupportedException>(() =>
            sandbox.SynthesizeInputAsync([new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Return" }]));

        Assert.Contains("graphical desktop", screenshot.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("graphical desktop", input.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.True(auditor.Required.HasFlag(AuditCapabilities.Graphical));
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
    public async Task GraphicalSmokeAuditor_FailsForMalformedPngVariants()
    {
        foreach (var variant in MalformedPngCases())
        {
            var png = Assert.IsType<byte[]>(variant[0]);
            var expected = Assert.IsType<string>(variant[1]);
            await using var sandbox = new RecordingGraphicalSandbox(png);
            var auditor = new GraphicalSmokeAuditor(TimeSpan.Zero);

            var result = await auditor.RunAsync(
                sandbox,
                "/work",
                new AuditContext(WorkItemId.New(), "work", "main", 1, "prompt"));

            Assert.False(result.Passed);
            var finding = Assert.Single(result.Findings);
            Assert.Contains(expected, finding.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [MemberData(nameof(FilteredPngSuccessCases))]
    public async Task GraphicalSmokeAuditor_PassesForFilteredPngVariants(string filterName, byte[] png)
    {
        await using var sandbox = new RecordingGraphicalSandbox(png);
        var auditor = new GraphicalSmokeAuditor(TimeSpan.Zero);

        var result = await auditor.RunAsync(
            sandbox,
            "/work",
            new AuditContext(WorkItemId.New(), "work", "main", 1, "prompt"));

        Assert.True(result.Passed, $"{filterName} filter failed: {result.RawOutput}");
    }

    [Fact]
    public void ProjectAuditorComposer_AddsRegisteredGuiSmokeAuditor_ForGraphicalProjects()
    {
        var guiSmoke = new GraphicalSmokeAuditor(TimeSpan.Zero);
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [guiSmoke],
            NullLogger<ProjectAuditorComposer>.Instance);
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
        Assert.Same(guiSmoke, Assert.Single(auditors));
    }

    [Fact]
    public void ProjectAuditorComposer_DoesNotAddRegisteredGuiSmokeAuditor_ForHeadlessProjects()
    {
        var guiSmoke = new GraphicalSmokeAuditor(TimeSpan.Zero);
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [guiSmoke],
            NullLogger<ProjectAuditorComposer>.Instance);
        var project = new Project
        {
            Id = new ProjectId("headless"),
            DisplayName = "Headless",
            RepositoryUrl = "https://example.com/headless.git",
            GraphicalSandbox = false,
            Audit = new ProjectAudit
            {
                Languages = [],
                AuditTypes = [],
            },
        };

        var auditors = composer.Compose(project, new ScriptedAgent([]));

        Assert.Empty(auditors);
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
    public async Task PipelineRunner_UsesGraphicalFlavorAndDedicatedProfileForEligiblePhases()
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
            var graphicalAuditor = new QueueAuditor(
                "gui:contract",
                AuditCapabilities.Graphical,
                "tool",
                new AuditResult(true, []),
                new AuditResult(true, []));
            var audit = new ProjectAudit
            {
                MaxIterations = 2,
                AuditTypes = ["scripted"],
                ExcludedAuditors = ["gui:smoke"],
            };
            var profiles = new ProjectNetworkProfiles
            {
                Work = "work-profile",
                Rework = "rework-profile",
                AuditTool = "audit-tool-profile",
                Merge = "merge-profile",
            };
            // A clean merge is completed host-side with no sandbox, so the merge
            // sandbox profile/flavor is observable only on the agentic conflict
            // resolver path. Induce a README conflict (work writes README; the
            // one-shot auditor advances main's README during audit) so the merge
            // phase creates its sandbox, then resolve it.
            var mergeConflictAuditor = new MainAdvancingAuditor(workspace, "README.md", "main side\n");
            using var tp = TestSupport.BuildPipeline(
                workspace,
                seed,
                auditors: [auditor, graphicalAuditor, mergeConflictAuditor],
                projectAudit: audit,
                sandboxProvider: sandboxes,
                graphicalSandbox: true,
                networkProfiles: profiles,
                credentials: new ConstantCredentialProvider(new AgentCredential(
                    AgentKind.Claude,
                    new Dictionary<string, string> { ["WORK_TOKEN"] = "secret" },
                    new Dictionary<string, string>())),
                pipelineTuning: new PipelineTuningSnapshot(new PipelineTuningOptions { EnableSandboxReuse = false }));
            mergeConflictAuditor.GitRoot = tp.GitRoot;
            tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work\n"));
            tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "rework\n"));
            tp.Agent.ConflictResolutionPlan.Enqueue(_ => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["README.md"] = "main side\nrework\n",
            });
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
            var workSpec = Assert.Single(sandboxes.Specs, s => s.TimingPhase == "work");
            AssertGraphicalProfile(workSpec, SandboxConventions.GraphicalNetworkProfile);
            Assert.Equal("secret", workSpec.Environment["WORK_TOKEN"]);
            var reworkSpec = Assert.Single(sandboxes.Specs, s => s.TimingPhase == "rework");
            AssertGraphicalProfile(reworkSpec, SandboxConventions.GraphicalNetworkProfile);
            Assert.Equal("secret", reworkSpec.Environment["WORK_TOKEN"]);
            var auditSpecs = sandboxes.Specs.Where(s => s.TimingPhase == "audit").ToArray();
            Assert.NotEmpty(auditSpecs);
            Assert.Contains(auditSpecs, spec =>
                spec.Flavor == SandboxProfileFlavor.Headless
                && spec.Network.ProfileName == "audit-tool-profile");
            Assert.Contains(auditSpecs, spec =>
                spec.Flavor == SandboxProfileFlavor.Graphical
                && spec.Network.ProfileName == SandboxConventions.GraphicalNetworkProfile);

            var merge = Assert.Single(sandboxes.Specs, s => s.TimingPhase == "merge");
            Assert.Equal(SandboxProfileFlavor.Headless, merge.Flavor);
            Assert.Equal("merge-profile", merge.Network.ProfileName);
        }
        finally
        {
            CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void SandboxTargetResolver_GraphicalSandboxUsesDedicatedProfileForEligiblePhases()
    {
        var project = new Project
        {
            Id = new ProjectId("gui"),
            DisplayName = "GUI",
            RepositoryUrl = "https://example.com/gui.git",
            GraphicalSandbox = true,
        };

        var missing = SandboxTargetResolver.ResolveProjectPhase(project, null);
        var blank = SandboxTargetResolver.ResolveProjectPhase(project, "   ");
        var configured = SandboxTargetResolver.ResolveProjectPhase(project, "work-profile");

        Assert.Equal(SandboxProfileFlavor.Graphical, missing.Flavor);
        Assert.Equal(SandboxConventions.GraphicalNetworkProfile, missing.NetworkProfile);
        Assert.Equal(SandboxProfileFlavor.Graphical, blank.Flavor);
        Assert.Equal(SandboxConventions.GraphicalNetworkProfile, blank.NetworkProfile);
        Assert.Equal(SandboxProfileFlavor.Graphical, configured.Flavor);
        Assert.Equal(SandboxConventions.GraphicalNetworkProfile, configured.NetworkProfile);
    }

    [Fact]
    public void SandboxTargetResolver_UsesGraphicalAuditCapability()
    {
        var ordinaryTool = SandboxTargetResolver.ResolveAudit(
            "audit-tool-profile",
            AuditCapabilities.None);
        var graphicalTool = SandboxTargetResolver.ResolveAudit(
            "audit-tool-profile",
            AuditCapabilities.Graphical);

        Assert.Equal(SandboxProfileFlavor.Headless, ordinaryTool.Flavor);
        Assert.Equal("audit-tool-profile", ordinaryTool.NetworkProfile);
        Assert.Equal(SandboxProfileFlavor.Graphical, graphicalTool.Flavor);
        Assert.Equal(SandboxConventions.GraphicalNetworkProfile, graphicalTool.NetworkProfile);
    }

    [Fact]
    public async Task PipelineRunner_KeepsCredentialedAuditAgentHeadlessForGraphicalProjects()
    {
        var workspace = Directory.CreateTempSubdirectory("codeybox-graphical-audit-agent-").FullName;
        try
        {
            var seed = await TestSupport.CreateSeedRepoAsync(workspace);
            var sandboxes = new CapturingSandboxProvider(
                new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
            var auditor = new QueueAuditor(
                "security:llm-review",
                AuditCapabilities.AgentCredentials | AuditCapabilities.Network,
                "llm",
                new AuditResult(true, []));
            var profiles = new ProjectNetworkProfiles
            {
                Work = "work-profile",
                AuditAgent = "audit-agent-profile",
                AuditTool = "audit-tool-profile",
            };
            using var tp = TestSupport.BuildPipeline(
                workspace,
                seed,
                auditors: TestAuditGates.WithPassedBuildAndTest(auditor),
                projectAudit: new ProjectAudit
                {
                    MaxIterations = 1,
                    AuditTypes = ["scripted"],
                    ExcludedAuditors = ["gui:smoke"],
                },
                sandboxProvider: sandboxes,
                graphicalSandbox: true,
                networkProfiles: profiles,
                pipelineOptions: new PipelineOptions
                {
                    SandboxImageReference = "ignored",
                    AgentAllowedHosts = ["api.anthropic.com"],
                    AuditToolAllowedHosts = ["registry.npmjs.org"],
                },
                credentials: new ConstantCredentialProvider(new AgentCredential(
                    AgentKind.Claude,
                    new Dictionary<string, string> { ["TEST_TOKEN"] = "secret" },
                    new Dictionary<string, string>())));
            tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));
            var item = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("test-project"),
                Title = "graphical audit agent routing",
                Prompt = "do work",
                Agent = AgentKind.Claude,
                WorkBranch = "feature/graphical-audit-agent-routing",
            };

            await tp.Store.CreateAsync(item);
            await tp.Pipeline.RunAsync(item, CancellationToken.None);

            var final = await tp.Store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Done, final!.State);
            var auditSpec = Assert.Single(sandboxes.Specs, s =>
                s.TimingPhase == "audit" &&
                s.Network.ProfileName == "audit-agent-profile");
            Assert.Equal(SandboxProfileFlavor.Headless, auditSpec.Flavor);
            Assert.Equal("audit-agent-profile", auditSpec.Network.ProfileName);
            Assert.Contains("api.anthropic.com", auditSpec.Network.AllowedHosts);
            Assert.DoesNotContain("registry.npmjs.org", auditSpec.Network.AllowedHosts);
            Assert.Equal("secret", auditSpec.Environment["TEST_TOKEN"]);
            Assert.Contains(auditSpec.Mounts, m => m.SandboxPath == SandboxConventions.CredentialsDir);
        }
        finally
        {
            CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(workspace);
        }
    }

    [Fact]
    public async Task PipelineRunner_KeepsConfiguredHeadlessProfilesWhenGraphicalSandboxDisabled()
    {
        var workspace = Directory.CreateTempSubdirectory("codeybox-headless-route-").FullName;
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
            };
            var profiles = new ProjectNetworkProfiles
            {
                Work = "work-profile",
                Rework = "rework-profile",
                AuditTool = "audit-tool-profile",
                Merge = "merge-profile",
            };
            using var tp = TestSupport.BuildPipeline(
                workspace,
                seed,
                auditors: [auditor],
                projectAudit: audit,
                sandboxProvider: sandboxes,
                graphicalSandbox: false,
                networkProfiles: profiles,
                pipelineTuning: new PipelineTuningSnapshot(new PipelineTuningOptions { EnableSandboxReuse = false }));
            tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));
            tp.Agent.WorkPlan.Enqueue(new FileWrite("rework.txt", "rework\n"));
            var item = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("test-project"),
                Title = "headless routing",
                Prompt = "do work",
                Agent = AgentKind.Claude,
                WorkBranch = "feature/headless-routing",
            };

            await tp.Store.CreateAsync(item);
            await tp.Pipeline.RunAsync(item, CancellationToken.None);

            var final = await tp.Store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Done, final!.State);
            AssertHeadlessProfile(Assert.Single(sandboxes.Specs, s => s.TimingPhase == "work"), "work-profile");
            AssertHeadlessProfile(Assert.Single(sandboxes.Specs, s => s.TimingPhase == "rework"), "rework-profile");
            var auditSpecs = sandboxes.Specs.Where(s => s.TimingPhase == "audit").ToArray();
            Assert.NotEmpty(auditSpecs);
            Assert.All(auditSpecs, spec => AssertHeadlessProfile(spec, "audit-tool-profile"));
        }
        finally
        {
            CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(workspace);
        }
    }

    private static void AssertGraphicalProfile(SandboxSpec spec, string expectedProfile)
    {
        Assert.Equal(SandboxProfileFlavor.Graphical, spec.Flavor);
        Assert.Equal(expectedProfile, spec.Network.ProfileName);
    }

    private static void AssertHeadlessProfile(SandboxSpec spec, string expectedProfile)
    {
        Assert.Equal(SandboxProfileFlavor.Headless, spec.Flavor);
        Assert.Equal(expectedProfile, spec.Network.ProfileName);
    }

    private static byte[] WithByte(byte[] png, int offset, byte value)
    {
        var copy = png.ToArray();
        copy[offset] = value;
        return copy;
    }

    private static byte[] WithChunkLength(byte[] png, int length)
    {
        var copy = png.ToArray();
        Span<byte> destination = copy.AsSpan(8, 4);
        BinaryPrimitives.WriteInt32BigEndian(destination, length);
        return copy;
    }

    private static byte[] BuildPng(
        int width,
        int height,
        byte colorType,
        byte[] decompressedScanlines,
        byte bitDepth = 8,
        byte interlace = 0,
        byte[]? palette = null)
    {
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = interlace;
        WriteChunk(png, "IHDR", ihdr);

        if (palette is not null)
            WriteChunk(png, "PLTE", palette);

        WriteChunk(png, "IDAT", CompressZlib(decompressedScanlines));
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte[] CompressZlib(byte[] data)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }
        return compressed.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        png.Write(Encoding.ASCII.GetBytes(type));
        png.Write(data);
        png.Write([0, 0, 0, 0]);
    }

    private sealed class HeadlessOnlySandbox : ISandbox
    {
        public string Id => "headless-test";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class HangingGraphicalSandbox : ISandbox
    {
        private TaskCompletionSource _operationEntered = CreateOperationEnteredSource();

        public string Id => "hanging-graphical-test";

        public Task WaitUntilOperationEnteredAsync(CancellationToken ct = default) =>
            _operationEntered.Task.WaitAsync(ct);

        public void PrepareForNextOperation() =>
            _operationEntered = CreateOperationEnteredSource();

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));

        public async Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
        {
            SignalOperationEntered();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return NonUniformPng;
        }

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
        {
            SignalOperationEntered();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void SignalOperationEntered() => _operationEntered.TrySetResult();

        private static TaskCompletionSource CreateOperationEnteredSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// One-shot tool auditor that advances <c>main</c>'s copy of a file on the
    /// first audit iteration so a work branch touching the same file merges with
    /// a conflict — routing the merge phase through the agentic conflict resolver
    /// (which creates the merge sandbox this test inspects). Advancing only once
    /// keeps later audit iterations from re-committing an unchanged tree.
    /// </summary>
    private sealed class MainAdvancingAuditor : IAuditor
    {
        private readonly string _workspace;
        private readonly string _path;
        private readonly string _content;
        private bool _advanced;

        public string? GitRoot { get; set; }
        public string Name => "advance-main";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public MainAdvancingAuditor(string workspace, string path, string content)
        {
            _workspace = workspace;
            _path = path;
            _content = content;
        }

        public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
            if (_advanced)
                return new AuditResult(true, []);
            if (GitRoot is null)
                throw new InvalidOperationException("GitRoot must be assigned before the auditor runs.");
            var barePath = Path.Combine(GitRoot, context.WorkItemId + ".git");
            var clone = Path.Combine(_workspace, "advance-main-" + Guid.NewGuid().ToString("N")[..8]);
            await TestSupport.RunGit(_workspace, "clone", barePath, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", context.BaseBranch);
            await File.WriteAllTextAsync(Path.Combine(clone, _path), _content);
            await TestSupport.RunGit(clone, "commit", "-am", "advance main during audit");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{context.BaseBranch}");
            _advanced = true;
            return new AuditResult(true, []);
        }
    }

    private sealed class QueueAuditor : IAuditor
    {
        private readonly Queue<AuditResult> _results;
        private readonly AuditCapabilities _required;
        private readonly string _kind;

        public QueueAuditor(string name, params AuditResult[] results)
            : this(name, AuditCapabilities.None, "tool", results)
        {
        }

        public QueueAuditor(string name, AuditCapabilities required, string kind, params AuditResult[] results)
        {
            Name = name;
            _required = required;
            _kind = kind;
            _results = new Queue<AuditResult>(results);
        }

        public string Name { get; }
        public string Kind => _kind;
        public AuditCapabilities Required => _required;

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
