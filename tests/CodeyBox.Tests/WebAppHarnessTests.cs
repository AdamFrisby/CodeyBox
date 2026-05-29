using System.Collections.Concurrent;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.ExploratoryTesting;
using CodeyBox.ExploratoryTesting.Recipes;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Graphical;
using CodeyBox.Sandbox.Multipass;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class WebAppHarnessTests
{
    // Same fixtures as GraphicalSandboxTests — non-uniform vs uniform PNGs.
    private static readonly byte[] NonUniformPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAD0lEQVR4nGNgYGD4//8/AAYBAv4CsjmuAAAAAElFTkSuQmCC");

    private static readonly byte[] UniformPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAC0lEQVR4nGNgAAMAAAcAAbKGrPQAAAAASUVORK5CYII=");

    [Fact]
    public async Task LaunchAsync_HappyPath_ReturnsSessionWithRenderedScreenshot()
    {
        // Blank frame first proves the readiness probe rejects before accepting.
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [UniformPng, NonUniformPng]);
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);

        await using var session = await harness.LaunchAsync(MinimalRecipe());

        Assert.Same(sandbox, session.Sandbox);
        Assert.Equal("http://localhost:5080", session.EntryUrl);
        Assert.Equal(NonUniformPng, session.ReadinessScreenshotPng);
        Assert.NotNull(session.ComputerUse);
        Assert.True(PngRenderedUiReadiness.LooksLikeRenderedUi(session.ReadinessScreenshotPng));
        Assert.Equal(2, sandbox.ScreenshotCalls);

        var execs = sandbox.Execs.ToList();
        var mkdirIdx = execs.FindIndex(e => e.Argv[0] == "mkdir");
        var buildIdx = execs.FindIndex(e => e.Argv.SequenceEqual(new[] { "dotnet", "build" }));
        var seedIdx = execs.FindIndex(e => e.Argv.SequenceEqual(new[] { "dotnet", "ef", "database", "update" }));
        var runIdx = execs.FindIndex(e => e.Argv.Contains("harness-run"));
        var curlIdx = execs.FindIndex(e => e.Argv[0] == "curl");
        var browserIdx = execs.FindIndex(e => e.Argv.Contains("harness-browser"));
        Assert.True(mkdirIdx < buildIdx && buildIdx < seedIdx && seedIdx < runIdx);
        Assert.True(runIdx < curlIdx && curlIdx < browserIdx);
    }

    [Fact]
    public async Task LaunchAsync_ProvisionsGraphicalSandboxWithRecipeFields()
    {
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [NonUniformPng]);
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);
        var customLimits = SandboxResourceLimits.Default with { MemoryBytes = 4L * 1024 * 1024 * 1024 };
        var recipe = MinimalRecipe() with
        {
            ImageReference = "snapshot:test-image",
            Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = "/host/repo", ReadOnly = false }],
            Environment = new Dictionary<string, string> { ["FOO"] = "bar" },
            NetworkProfile = "harness-graphical",
            Limits = customLimits,
        };

        await using var _ = await harness.LaunchAsync(recipe);

        var spec = Assert.Single(provider.CreatedSpecs);
        Assert.Equal(SandboxProfileFlavor.Graphical, spec.Flavor);
        Assert.Equal("harness-graphical", spec.Network.ProfileName);
        Assert.Equal("bar", spec.Environment["FOO"]);
        Assert.Single(spec.Mounts);
        Assert.Equal("snapshot:test-image", spec.ImageReference);
        Assert.Same(customLimits, spec.Limits);
    }

    [Fact]
    public async Task LaunchAsync_LimitsUnset_FallsBackToDefaultSandboxLimits()
    {
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [NonUniformPng]);
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);

        await using var _ = await harness.LaunchAsync(MinimalRecipe());

        var spec = Assert.Single(provider.CreatedSpecs);
        Assert.Same(SandboxResourceLimits.Default, spec.Limits);
    }

    [Fact]
    public async Task LaunchAsync_RunCommandEnvironment_MergedWithHarnessVars()
    {
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [NonUniformPng]);
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);
        var recipe = MinimalRecipe() with
        {
            RunCommand = new RecipeStep
            {
                Command = ["dotnet", "run"],
                Environment = new Dictionary<string, string>
                {
                    ["ASPNETCORE_URLS"] = "http://+:5080",
                    // Recipe-supplied HARNESS_RUN_LOG MUST NOT shadow the harness's own var.
                    ["HARNESS_RUN_LOG"] = "/tmp/hostile.log",
                },
            },
        };

        await using var _ = await harness.LaunchAsync(recipe);

        var runExec = sandbox.Execs.Single(e => e.Argv.Contains("harness-run"));
        Assert.NotNull(runExec.ExtraEnvironment);
        Assert.Equal("http://+:5080", runExec.ExtraEnvironment["ASPNETCORE_URLS"]);
        Assert.Equal("/var/log/codeybox-harness/harness-test.log", runExec.ExtraEnvironment["HARNESS_RUN_LOG"]);
        Assert.Equal("/var/log/codeybox-harness/harness-test.pid", runExec.ExtraEnvironment["HARNESS_RUN_PID"]);
    }

    [Fact]
    public async Task LaunchAsync_FailingBuildStep_TearsDownSandboxAndSurfacesError()
    {
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [NonUniformPng])
        {
            CommandResultPredicate = (argv, _) =>
                argv.SequenceEqual(new[] { "dotnet", "build" })
                    ? new SandboxExecResult(2, "", "MSBUILD: error CS9999")
                    : null,
        };
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);

        var ex = await Assert.ThrowsAsync<HarnessRecipeStepFailedException>(() =>
            harness.LaunchAsync(MinimalRecipe()));
        Assert.Equal("build", ex.Phase);
        Assert.Equal(2, ex.ExitCode);
        Assert.True(sandbox.Disposed);
    }

    [Fact]
    public async Task LaunchAsync_FailingSeedStep_TearsDownSandboxAndSurfacesError()
    {
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [NonUniformPng])
        {
            CommandResultPredicate = (argv, _) =>
                argv.SequenceEqual(new[] { "dotnet", "ef", "database", "update" })
                    ? new SandboxExecResult(1, "", "migration failed")
                    : null,
        };
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);

        var ex = await Assert.ThrowsAsync<HarnessRecipeStepFailedException>(() =>
            harness.LaunchAsync(MinimalRecipe()));
        Assert.Equal("seed", ex.Phase);
        Assert.True(sandbox.Disposed);
    }

    [Fact]
    public async Task LaunchAsync_FailingRunCommand_TearsDownSandboxAndSurfacesError()
    {
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [NonUniformPng])
        {
            CommandResultPredicate = (argv, _) =>
                argv.Count >= 2 && argv[0] == "sh" && argv.Contains("harness-run")
                    ? new SandboxExecResult(1, "", "setsid failed")
                    : null,
        };
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);

        var ex = await Assert.ThrowsAsync<HarnessRecipeStepFailedException>(() =>
            harness.LaunchAsync(MinimalRecipe()));
        Assert.Equal("run", ex.Phase);
        Assert.True(sandbox.Disposed);
    }

    [Fact]
    public async Task LaunchAsync_FailingBrowserLaunch_TearsDownSandboxAndSurfacesError()
    {
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [NonUniformPng])
        {
            CommandResultPredicate = (argv, _) =>
                argv.Count >= 2 && argv[0] == "sh" && argv.Contains("harness-browser")
                    ? new SandboxExecResult(1, "", "firefox missing")
                    : null,
        };
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);

        var ex = await Assert.ThrowsAsync<HarnessRecipeStepFailedException>(() =>
            harness.LaunchAsync(MinimalRecipe()));
        Assert.Equal("browser", ex.Phase);
        Assert.True(sandbox.Disposed);
    }

    [Fact]
    public async Task LaunchAsync_MkdirLogDirFails_ThrowsAndTearsDownSandbox()
    {
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [NonUniformPng])
        {
            CommandResultPredicate = (argv, _) =>
                argv.SequenceEqual(new[] { "mkdir", "-p", "/var/log/codeybox-harness" })
                    ? new SandboxExecResult(1, "", "permission denied")
                    : null,
        };
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.LaunchAsync(MinimalRecipe()));
        Assert.Contains("/var/log/codeybox-harness", ex.Message, StringComparison.Ordinal);
        Assert.True(sandbox.Disposed);
    }

    [Fact]
    public async Task LaunchAsync_HttpProbeNeverSucceeds_TimesOutAndTearsDownSandbox()
    {
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [NonUniformPng])
        {
            CommandResultPredicate = (argv, _) => argv[0] == "curl"
                ? new SandboxExecResult(7, "", "curl: Failed to connect")
                : null,
        };
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);
        var recipe = MinimalRecipe() with
        {
            ReadinessTimeout = TimeSpan.FromMilliseconds(50),
            ReadinessPollInterval = TimeSpan.FromMilliseconds(10),
            BrowserSettleDelay = TimeSpan.Zero,
        };

        var ex = await Assert.ThrowsAsync<HarnessReadinessTimeoutException>(() =>
            harness.LaunchAsync(recipe));
        Assert.Contains("did not respond", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sandbox.Disposed);
    }

    [Fact]
    public async Task LaunchAsync_ScreenshotStaysUniform_TimesOutAndTearsDownSandbox()
    {
        var sandbox = new RecordingHarnessSandbox(
            screenshotsToReturn: Enumerable.Repeat(UniformPng, 50).ToArray());
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);
        var recipe = MinimalRecipe() with
        {
            ReadinessTimeout = TimeSpan.FromMilliseconds(50),
            ReadinessPollInterval = TimeSpan.FromMilliseconds(10),
            BrowserSettleDelay = TimeSpan.Zero,
        };

        var ex = await Assert.ThrowsAsync<HarnessReadinessTimeoutException>(() =>
            harness.LaunchAsync(recipe));
        Assert.Contains("UI did not render", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sandbox.Disposed);
    }

    [Fact]
    public async Task LaunchAsync_ScreenshotThrows_TimesOutWithLastErrorDetail()
    {
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [NonUniformPng])
        {
            ScreenshotException = new InvalidOperationException("scrot failed"),
        };
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);
        var recipe = MinimalRecipe() with
        {
            ReadinessTimeout = TimeSpan.FromMilliseconds(50),
            ReadinessPollInterval = TimeSpan.FromMilliseconds(10),
            BrowserSettleDelay = TimeSpan.Zero,
        };

        var ex = await Assert.ThrowsAsync<HarnessReadinessTimeoutException>(() =>
            harness.LaunchAsync(recipe));
        Assert.Contains("scrot failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sandbox.Disposed);
    }

    [Theory]
    [InlineData("", "EntryUrl")]
    [InlineData("INVALID", "TargetName")]
    [InlineData("HasUpper", "TargetName")]
    public async Task LaunchAsync_InvalidRecipe_ThrowsArgumentException(string badValue, string field)
    {
        var harness = new WebAppHarness(new ScriptedSandboxProvider(
            new RecordingHarnessSandbox(screenshotsToReturn: [])));
        var recipe = field switch
        {
            "EntryUrl" => MinimalRecipe() with { EntryUrl = badValue },
            "TargetName" => MinimalRecipe() with { TargetName = badValue },
            _ => MinimalRecipe(),
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => harness.LaunchAsync(recipe));
        Assert.Contains(field, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchAsync_ZeroReadinessTimeout_ThrowsArgumentException()
    {
        var harness = new WebAppHarness(new ScriptedSandboxProvider(
            new RecordingHarnessSandbox(screenshotsToReturn: [])));
        var recipe = MinimalRecipe() with { ReadinessTimeout = TimeSpan.Zero };

        await Assert.ThrowsAsync<ArgumentException>(() => harness.LaunchAsync(recipe));
    }

    [Theory]
    [InlineData("under_score")]
    [InlineData("has.dot")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public async Task LaunchAsync_TargetName_NonAlphanumericNonDash_ThrowsArgumentException(string badName)
    {
        // Distinct from the IsUpper path: characters that are not letter/digit/dash
        // must hit the IsAsciiLetterOrDigit branch and fail with the "lowercase
        // ASCII letters / digits / dashes" message.
        var harness = new WebAppHarness(new ScriptedSandboxProvider(
            new RecordingHarnessSandbox(screenshotsToReturn: [])));
        var recipe = MinimalRecipe() with { TargetName = badName };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => harness.LaunchAsync(recipe));
        Assert.Contains("TargetName", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dashes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LaunchAsync_BlankNetworkProfile_ThrowsArgumentException(string blank)
    {
        var harness = new WebAppHarness(new ScriptedSandboxProvider(
            new RecordingHarnessSandbox(screenshotsToReturn: [])));
        var recipe = MinimalRecipe() with { NetworkProfile = blank };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => harness.LaunchAsync(recipe));
        Assert.Contains("NetworkProfile", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchAsync_EmptyRunCommand_ThrowsArgumentException()
    {
        var harness = new WebAppHarness(new ScriptedSandboxProvider(
            new RecordingHarnessSandbox(screenshotsToReturn: [])));
        var recipe = MinimalRecipe() with { RunCommand = new RecipeStep { Command = Array.Empty<string>() } };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => harness.LaunchAsync(recipe));
        Assert.Contains("RunCommand", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchAsync_EmptyBrowserCommand_ThrowsArgumentException()
    {
        var harness = new WebAppHarness(new ScriptedSandboxProvider(
            new RecordingHarnessSandbox(screenshotsToReturn: [])));
        var recipe = MinimalRecipe() with { BrowserCommand = Array.Empty<string>() };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => harness.LaunchAsync(recipe));
        Assert.Contains("BrowserCommand", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchAsync_NonPositiveReadinessPollInterval_ThrowsArgumentException()
    {
        var harness = new WebAppHarness(new ScriptedSandboxProvider(
            new RecordingHarnessSandbox(screenshotsToReturn: [])));
        var recipe = MinimalRecipe() with { ReadinessPollInterval = TimeSpan.Zero };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => harness.LaunchAsync(recipe));
        Assert.Contains("ReadinessPollInterval", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchAsync_EmptyBuildStepCommand_ThrowsArgumentException()
    {
        var harness = new WebAppHarness(new ScriptedSandboxProvider(
            new RecordingHarnessSandbox(screenshotsToReturn: [])));
        var recipe = MinimalRecipe() with
        {
            BuildSteps = [new RecipeStep { Command = Array.Empty<string>() }],
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => harness.LaunchAsync(recipe));
        Assert.Contains("non-empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchAsync_EmptySeedStepCommand_ThrowsArgumentException()
    {
        var harness = new WebAppHarness(new ScriptedSandboxProvider(
            new RecordingHarnessSandbox(screenshotsToReturn: [])));
        var recipe = MinimalRecipe() with
        {
            SeedSteps = [new RecipeStep { Command = Array.Empty<string>() }],
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => harness.LaunchAsync(recipe));
        Assert.Contains("non-empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchAsync_DisposingSessionDisposesSandbox()
    {
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [NonUniformPng]);
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);

        var session = await harness.LaunchAsync(MinimalRecipe());
        Assert.False(sandbox.Disposed);
        await session.DisposeAsync();
        Assert.True(sandbox.Disposed);
    }

    [Fact]
    public async Task LaunchAsync_BrowserCommand_SubstitutesUrlToken()
    {
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [NonUniformPng]);
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);
        var recipe = MinimalRecipe() with { BrowserCommand = ["firefox", "--new-window", "$URL"] };

        await using var _ = await harness.LaunchAsync(recipe);

        var browserCall = sandbox.Execs.Single(e =>
            e.Argv.Count >= 2 && e.Argv[0] == "sh" && e.Argv.Contains("harness-browser"));
        Assert.Contains(recipe.EntryUrl, browserCall.Argv);
        Assert.DoesNotContain("$URL", browserCall.Argv);
    }

    [Fact]
    public async Task LaunchAsync_JobTrackRecipe_Default_LaunchesWithStubSandbox()
    {
        var sandbox = new RecordingHarnessSandbox(screenshotsToReturn: [NonUniformPng]);
        var provider = new ScriptedSandboxProvider(sandbox);
        var harness = new WebAppHarness(provider);
        var recipe = JobTrackRecipe.Default("/host/jobtrack");

        await using var session = await harness.LaunchAsync(recipe);

        Assert.Equal(JobTrackRecipe.DefaultEntryUrl, session.EntryUrl);
        var argv = sandbox.Execs.SelectMany(e => e.Argv).ToArray();
        Assert.Contains("ef", argv);
        Assert.Contains(argv, s => s.Contains("SeedFixtures", StringComparison.Ordinal));
    }

    [Fact]
    public void JobTrackRecipe_Default_Validates()
    {
        var recipe = JobTrackRecipe.Default("/host/jobtrack");
        Assert.Equal("jobtrack", recipe.TargetName);
        Assert.Equal(JobTrackRecipe.DefaultEntryUrl, recipe.EntryUrl);
        Assert.Equal(SandboxConventions.GraphicalNetworkProfile, recipe.NetworkProfile);
        Assert.Contains(recipe.Mounts, m => m.HostPath == "/host/jobtrack");
        Assert.NotEmpty(recipe.BuildSteps);
        Assert.NotEmpty(recipe.SeedSteps);
        Assert.NotEmpty(recipe.RunCommand.Command);
        Assert.NotEmpty(recipe.BrowserCommand);
    }

    [Fact]
    public void JobTrackRecipe_Default_RejectsBlankSourceMount()
    {
        Assert.Throws<ArgumentException>(() => JobTrackRecipe.Default(""));
    }

    [Fact]
    public void PngRenderedUiReadiness_RejectsUniformPng()
    {
        Assert.False(PngRenderedUiReadiness.LooksLikeRenderedUi(UniformPng));
    }

    [Fact]
    public void PngRenderedUiReadiness_RejectsNonPng()
    {
        Assert.False(PngRenderedUiReadiness.LooksLikeRenderedUi([0x01, 0x02, 0x03, 0x04]));
    }

    [Fact]
    public void PngRenderedUiReadiness_AcceptsNonUniformPng()
    {
        Assert.True(PngRenderedUiReadiness.LooksLikeRenderedUi(NonUniformPng));
    }

    [Fact]
    public void PngRenderedUiReadiness_RejectsUnsupportedPngBitDepth()
    {
        // Mutate the IHDR bit-depth byte (offset 24) to 16, which hits the
        // NotSupportedException branch ("unsupported PNG bit depth").
        var unsupported = (byte[])NonUniformPng.Clone();
        unsupported[24] = 16;
        Assert.False(PngRenderedUiReadiness.LooksLikeRenderedUi(unsupported));
    }

    /// <summary>
    /// Real-VM integration: JobTrack when <c>JOBTRACK_SOURCE</c> is set, otherwise
    /// a trivial smoke recipe. Skipped unless <c>CODEYBOX_HARNESS_INTEGRATION=1</c>.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task LaunchAsync_RealMultipassPath_ReadyAndTeardown()
    {
        Skip.If(
            Environment.GetEnvironmentVariable("CODEYBOX_HARNESS_INTEGRATION") != "1",
            "Set CODEYBOX_HARNESS_INTEGRATION=1 to run the real Multipass harness path.");

        var bridge = Environment.GetEnvironmentVariable("CODEYBOX_GRAPHICAL_BRIDGE");
        if (string.IsNullOrWhiteSpace(bridge))
            bridge = "cb-graphical";

        var provider = new MultipassSandboxProvider(
            new MultipassSandboxOptions
            {
                NetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [SandboxConventions.GraphicalNetworkProfile] = bridge,
                },
            },
            NullLogger<MultipassSandboxProvider>.Instance);
        var harness = new WebAppHarness(provider);

        var jobTrackSource = Environment.GetEnvironmentVariable("JOBTRACK_SOURCE");
        WebAppRecipe recipe;
        if (!string.IsNullOrWhiteSpace(jobTrackSource) && Directory.Exists(jobTrackSource))
            recipe = JobTrackRecipe.Default(Path.GetFullPath(jobTrackSource));
        else
            recipe = IntegrationSmokeRecipe();

        await using var session = await harness.LaunchAsync(recipe);

        Assert.True(PngRenderedUiReadiness.LooksLikeRenderedUi(session.ReadinessScreenshotPng));
        Assert.False(string.IsNullOrWhiteSpace(session.Sandbox.Id));
        Assert.NotNull(session.ComputerUse);

        var followUp = await session.ComputerUse.ExecuteAsync(
            session.Sandbox,
            new ComputerUseRequest { Action = "screenshot" });
        Assert.NotNull(followUp.ScreenshotPng);
        Assert.True(PngRenderedUiReadiness.LooksLikeRenderedUi(followUp.ScreenshotPng));
    }

    private static WebAppRecipe IntegrationSmokeRecipe() => new()
    {
        TargetName = "harness-smoke",
        BuildSteps =
        [
            new RecipeStep
            {
                Label = "install-firefox",
                Command = ["sudo", "apt-get", "install", "-y", "--no-install-recommends", "firefox-esr"],
            },
            new RecipeStep
            {
                Label = "write-fixture",
                Command =
                [
                    "sh", "-c",
                    """
                    mkdir -p /work/harness-smoke && cat > /work/harness-smoke/index.html <<'EOF'
                    <!DOCTYPE html><html><head><title>CodeyBox harness smoke</title></head>
                    <body style="background:#1a73e8;color:#fff;font:48px sans-serif;padding:2em">
                    <h1>Harness integration ready</h1><p>Rendered UI probe target.</p>
                    </body></html>
                    EOF
                    """,
                ],
            },
        ],
        RunCommand = new RecipeStep
        {
            Label = "http-server",
            Command = ["python3", "-m", "http.server", "8765", "--bind", "127.0.0.1", "--directory", "/work/harness-smoke"],
        },
        EntryUrl = "http://127.0.0.1:8765/",
        BrowserCommand = ["firefox", "--new-window", "$URL"],
        NetworkProfile = SandboxConventions.GraphicalNetworkProfile,
        ReadinessTimeout = TimeSpan.FromMinutes(10),
        ReadinessPollInterval = TimeSpan.FromSeconds(3),
        BrowserSettleDelay = TimeSpan.FromSeconds(3),
    };

    private static WebAppRecipe MinimalRecipe() => new()
    {
        TargetName = "harness-test",
        ImageReference = "ignored",
        BuildSteps =
        [
            new RecipeStep { Command = ["dotnet", "restore"] },
            new RecipeStep { Command = ["dotnet", "build"] },
        ],
        SeedSteps =
        [
            new RecipeStep { Command = ["dotnet", "ef", "database", "update"] },
        ],
        RunCommand = new RecipeStep { Command = ["dotnet", "run"] },
        EntryUrl = "http://localhost:5080",
        BrowserCommand = ["firefox", "$URL"],
        NetworkProfile = SandboxConventions.GraphicalNetworkProfile,
        ReadinessTimeout = TimeSpan.FromSeconds(2),
        ReadinessPollInterval = TimeSpan.FromMilliseconds(10),
        BrowserSettleDelay = TimeSpan.Zero,
    };

    private sealed class ScriptedSandboxProvider : ISandboxProvider
    {
        private readonly ISandbox _sandbox;

        public ScriptedSandboxProvider(ISandbox sandbox) => _sandbox = sandbox;

        public string Name => "scripted";
        public List<SandboxSpec> CreatedSpecs { get; } = [];

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            CreatedSpecs.Add(spec);
            return Task.FromResult(_sandbox);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingHarnessSandbox : ISandbox
    {
        private readonly byte[][] _screenshots;
        private int _screenshotIndex;

        public RecordingHarnessSandbox(IReadOnlyList<byte[]> screenshotsToReturn)
        {
            _screenshots = screenshotsToReturn.ToArray();
        }

        public string Id => "recording-harness-sandbox";
        public ConcurrentQueue<SandboxExec> Execs { get; } = new();
        public int ScreenshotCalls { get; private set; }
        public bool Disposed { get; private set; }

        public Func<IReadOnlyList<string>, int, SandboxExecResult?>? CommandResultPredicate { get; init; }

        public Exception? ScreenshotException { get; init; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var callIndex = Execs.Count;
            Execs.Enqueue(exec);
            var custom = CommandResultPredicate?.Invoke(exec.Argv, callIndex);
            return Task.FromResult(custom ?? new SandboxExecResult(0, "200", ""));
        }

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
        {
            ScreenshotCalls++;
            if (ScreenshotException is not null)
                throw ScreenshotException;
            var idx = Math.Min(_screenshotIndex++, _screenshots.Length - 1);
            return Task.FromResult(_screenshots.Length == 0 ? Array.Empty<byte>() : _screenshots[idx]);
        }

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

}
