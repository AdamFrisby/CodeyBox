using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for temp-path hygiene: constructing
/// <see cref="PipelineRunner"/> must not grow the temp directory, and test
/// fixtures must remove their temp state whether the test body passes or
/// throws (xUnit still calls <c>Dispose</c> on failure).
/// </summary>
public sealed class TempHygieneTests
{
    private static IReadOnlyList<string> PerInstanceHooksDirs() =>
        [.. Directory.EnumerateFileSystemEntries(
            Path.GetTempPath(), "codeybox-disabled-host-hooks-*")];

    private static (PipelineRunner Pipeline, SqliteWorkItemStore Store) CreatePipeline(
        TestScratchDirectory scratch, string dbFileName)
    {
        var stateDb = scratch.DbPath(dbFileName);
        var store = new SqliteWorkItemStore(stateDb);
        var webhooks = new NullWebhookDispatcher();
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = "https://github.com/test/repo",
        };
        var projects = new InMemoryProjectRepository(project);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions
            {
                RootDirectory = Path.Combine(scratch.DirectoryPath, "repos"),
            },
            NullLogger<LocalGitHost>.Instance);
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
        var pipeline = new PipelineRunner(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost,
            new AgentRegistry([]),
            new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            projects,
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<PipelineRunner>.Instance,
            authAvailability: new AgentAvailabilityRegistry(
                new AvailabilityOptions(),
                TimeProvider.System,
                NullLogger<AgentAvailabilityRegistry>.Instance),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);
        return (pipeline, store);
    }

    [Fact]
    public void PipelineRunner_Construction_CreatesNoPerInstanceTempDirectory()
    {
        // Warm up process-lifetime shared state before snapshotting: the
        // hooks suppression directory is resolved once per process (per-user
        // location, or a single GUID-suffixed fallback), not once per
        // PipelineRunner.
        var shared = PipelineRunner.SharedDisabledHostHooksPath;
        Assert.True(Directory.Exists(shared), "The shared hooks directory should exist after resolution.");
        Assert.True(
            new DirectoryInfo(shared).LinkTarget is null,
            "The shared hooks path must be a real directory, never a symlink.");
        Assert.False(
            string.Equals(
                Path.Combine(Path.GetTempPath(), "codeybox-disabled-host-hooks"),
                shared,
                StringComparison.Ordinal),
            "The shared hooks path must not be a well-known name in the shared temp path, where any local user could pre-create it.");

        var before = PerInstanceHooksDirs();
        using (var scratch = TestScratchDirectory.Create("codeybox-pipelinetemp-"))
        {
            var created = new List<(PipelineRunner Pipeline, SqliteWorkItemStore Store)>();
            try
            {
                for (var i = 0; i < 2; i++)
                    created.Add(CreatePipeline(scratch, $"state-{i}.db"));
                Assert.Equal(2, created.Count);
            }
            finally
            {
                foreach (var entry in created)
                    entry.Store.Dispose();
            }
        }

        // The scratch directory above is disposed before asserting so the
        // pipeline's own stores cannot pollute the temp-entry count. A delta
        // assertion (not an emptiness assertion) is used: leftover entries
        // from other runs on the same host must not fail this test.
        var after = PerInstanceHooksDirs();
        Assert.Equal(before.Count, after.Count);
    }

    [Fact]
    public void WorkItemApiFactory_Dispose_RemovesDatabaseAndScratchDirectory()
    {
        var factory = new WorkItemApiFactory();
        using var client = factory.CreateClient();
        var dbPath = factory.DbPath;
        var scratchPath = factory.ScratchPath;

        Assert.True(File.Exists(dbPath), "Precondition: the factory database should exist before disposal.");
        Assert.True(Directory.Exists(scratchPath), "Precondition: the scratch directory should exist before disposal.");

        client.Dispose();
        factory.Dispose();

        Assert.False(Directory.Exists(scratchPath), "Factory disposal should remove the scratch directory.");
        Assert.False(File.Exists(dbPath), "Factory disposal should remove the database file.");
        Assert.False(File.Exists(dbPath + "-wal"), "Factory disposal should remove the WAL sidecar.");
        Assert.False(File.Exists(dbPath + "-shm"), "Factory disposal should remove the SHM sidecar.");
    }

    [Fact]
    public async Task ProcessSandbox_DisablePreserveAfterPreservedDispose_RemovesRoot()
    {
        // DisablePreserveOnDispose must be honoured even when a preserved
        // disposal already ran (phase disposal reaps converted retained VMs
        // this way; test teardown of preempted sandboxes too). Previously the
        // disposed flag short-circuited the second call and the root leaked.
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        var pwd = await sandbox.ExecAsync(new SandboxExec { Argv = ["pwd"] });
        Assert.True(pwd.Success);
        var root = Directory.GetParent(pwd.Stdout.Trim())!.FullName;

        await ((IPreemptibleSandbox)sandbox).StopAndPreserveAsync();
        await sandbox.DisposeAsync();
        Assert.True(Directory.Exists(root), "Preserved disposal should keep the root.");

        ((IPreserveOnDisposeSandbox)sandbox).DisablePreserveOnDispose();
        await sandbox.DisposeAsync();
        Assert.False(Directory.Exists(root), "Disable-then-dispose should remove the root.");
    }

    [Fact]
    public void WorkItemApiFactory_Dispose_RemovesScratchDirectory_WhenTestBodyThrows()
    {
        var factory = new WorkItemApiFactory();
        using var client = factory.CreateClient();
        var dbPath = factory.DbPath;
        var scratchPath = factory.ScratchPath;
        Assert.True(File.Exists(dbPath), "Precondition: the factory database should exist before disposal.");

        try
        {
            try
            {
                throw new InvalidOperationException("simulated test failure");
            }
            finally
            {
                client.Dispose();
                factory.Dispose();
            }
        }
        catch (InvalidOperationException)
        {
            // Expected: the simulated test-body failure. Cleanup already ran above.
        }

        Assert.False(Directory.Exists(scratchPath), "Scratch must be removed even when the test body throws.");
        Assert.False(File.Exists(dbPath), "Database must be removed even when the test body throws.");
    }
}
