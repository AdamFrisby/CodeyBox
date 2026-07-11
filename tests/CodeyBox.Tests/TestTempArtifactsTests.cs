using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Tests.Uat.PipelineAndWorkerLifecycle;
using CodeyBox.Tests.Uat.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

public sealed class TestTempArtifactsTests
{
    [Fact]
    public void DeleteSqliteDatabase_RemovesMainWalAndShmFiles()
    {
        using var temp = TestTempDirectory.Create("codeybox-temp-artifacts-");
        var dbPath = Path.Combine(temp.Root, "state.db");
        File.WriteAllText(dbPath, "db");
        File.WriteAllText(dbPath + "-wal", "wal");
        File.WriteAllText(dbPath + "-shm", "shm");

        TestTempArtifacts.DeleteSqliteDatabase(dbPath);

        Assert.False(File.Exists(dbPath));
        Assert.False(File.Exists(dbPath + "-wal"));
        Assert.False(File.Exists(dbPath + "-shm"));
    }

    [Fact]
    public void TestTempDirectory_Dispose_RemovesRecursiveRoot()
    {
        string root;
        using (var temp = TestTempDirectory.Create("codeybox-temp-root-"))
        {
            root = temp.Root;
            var nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "artifact.txt"), "artifact");
        }

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void TestTempDirectory_Dispose_IsIdempotentAndMissingPathsAreNoOp()
    {
        var temp = TestTempDirectory.Create("codeybox-temp-root-");
        var root = temp.Root;

        temp.Dispose();
        temp.Dispose();

        Assert.False(Directory.Exists(root));
        TestTempArtifacts.DeleteDirectory(Path.Combine(root, "missing"));
        TestTempArtifacts.DeleteSqliteDatabase(Path.Combine(root, "missing.db"));
    }

    [Fact]
    public void DeleteDirectory_RejectsPathsOutsideOwnedTempRoots()
    {
        var path = Path.Combine(Path.GetTempPath(), "not-codeybox-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<ArgumentException>(() => TestTempArtifacts.DeleteDirectory(path));
    }

    [Fact]
    public void Retry_RetriesTransientFailuresAndSurfacesFinalFailure()
    {
        var attempts = 0;
        TestTempArtifacts.Retry(() =>
        {
            attempts++;
            if (attempts < 3)
                throw new IOException("locked");
        });
        Assert.Equal(3, attempts);

        var finalAttempts = 0;
        Assert.Throws<UnauthorizedAccessException>(() => TestTempArtifacts.Retry(() =>
        {
            finalAttempts++;
            throw new UnauthorizedAccessException("still locked");
        }));
        Assert.Equal(5, finalAttempts);
    }

    [Fact]
    public void CleanupAll_RunsEveryActionAndReportsFailures()
    {
        var ranMiddleCleanup = false;
        var ranFinalCleanup = false;

        var ex = Assert.Throws<AggregateException>(() => TestTempArtifacts.CleanupAll(
            () => throw new IOException("first cleanup failed"),
            () => ranMiddleCleanup = true,
            () =>
            {
                ranFinalCleanup = true;
                throw new UnauthorizedAccessException("final cleanup failed");
            }));

        Assert.True(ranMiddleCleanup);
        Assert.True(ranFinalCleanup);
        Assert.Collection(
            ex.InnerExceptions,
            failure => Assert.IsType<IOException>(failure),
            failure => Assert.IsType<UnauthorizedAccessException>(failure));
    }

    [Fact]
    public void CleanupAll_RethrowsSingleFailureWithoutWrapping()
    {
        var expected = new IOException("single cleanup failed");
        var ranFinalCleanup = false;

        var actual = Assert.Throws<IOException>(() => TestTempArtifacts.CleanupAll(
            () => throw expected,
            () => ranFinalCleanup = true));

        Assert.Same(expected, actual);
        Assert.True(ranFinalCleanup);
    }

    [Fact]
    public void CodeyBoxWebApplicationFactory_Dispose_RemovesOwnedTempRoot()
    {
        string root;
        using (var factory = new FactoryTempCleanupApiFactory())
        {
            root = factory.TempRoot;
            using var client = factory.CreateClient();
            Directory.CreateDirectory(factory.GitRoot);
            Directory.CreateDirectory(factory.AgentStreamRoot);
            File.WriteAllText(Path.Combine(factory.GitRoot, "repo.txt"), "repo");
            File.WriteAllText(Path.Combine(factory.AgentStreamRoot, "stream.jsonl"), "{}");
            File.WriteAllText(factory.AuditLogPath, "{}");
            File.WriteAllText(factory.AuditPath, "{}");
        }

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task DisposeHostThenDeleteSqliteDatabase_RemovesOwnedTempRootFromDerivedFactory()
    {
        var factory = new HelperDisposeApiFactory();
        var root = factory.TempRoot;
        var stateDb = factory.StateDbPath;
        var gitRoot = factory.GitRoot;
        var agentStreamsPath = factory.AgentStreamRoot;
        var client = factory.CreateClient();
        try
        {
            Directory.CreateDirectory(gitRoot);
            Directory.CreateDirectory(agentStreamsPath);
            File.WriteAllText(Path.Combine(gitRoot, "repo.txt"), "repo");
            File.WriteAllText(Path.Combine(agentStreamsPath, "stream.jsonl"), "{}");
            File.WriteAllText(stateDb + "-wal", "wal");
            File.WriteAllText(stateDb + "-shm", "shm");
            File.WriteAllText(factory.AuditLogPath, "{}");
            File.WriteAllText(factory.AuditPath, "{}");
        }
        finally
        {
            client.Dispose();
            factory.Dispose();
        }

        await AssertWorkItemStoreDisposedAsync(factory.Store);
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(stateDb));
        Assert.False(File.Exists(stateDb + "-wal"));
        Assert.False(File.Exists(stateDb + "-shm"));
        Assert.False(Directory.Exists(gitRoot));
        Assert.False(Directory.Exists(agentStreamsPath));
    }

    [Fact]
    public async Task WorkItemApiFactory_Dispose_ConfiguresOwnedPathsUnderTempRootAndRemovesThem()
    {
        var factory = new WorkItemApiFactory();
        var root = factory.TempRoot;
        string stateDb;
        string gitRoot;
        string auditLogPath;
        string auditPath;
        string agentStreamsPath;
        var client = factory.CreateClient();
        try
        {
            var config = factory.Services.GetRequiredService<IConfiguration>();
            stateDb = RequiredConfig(config, "CodeyBox:StateDatabasePath");
            gitRoot = RequiredConfig(config, "CodeyBox:GitRootDirectory");
            auditLogPath = RequiredConfig(config, "CodeyBox:AuditLog:Path");
            auditPath = RequiredConfig(config, "CodeyBox:AuditLog:AuditPath");
            agentStreamsPath = RequiredConfig(config, "CodeyBox:AgentStreams:Path");

            AssertPathUnderRoot(root, stateDb);
            AssertPathUnderRoot(root, gitRoot);
            AssertPathUnderRoot(root, auditLogPath);
            AssertPathUnderRoot(root, auditPath);
            AssertPathUnderRoot(root, agentStreamsPath);

            Directory.CreateDirectory(gitRoot);
            Directory.CreateDirectory(agentStreamsPath);
            File.WriteAllText(Path.Combine(gitRoot, "repo.txt"), "repo");
            File.WriteAllText(Path.Combine(agentStreamsPath, "stream.jsonl"), "{}");
            if (!File.Exists(stateDb + "-wal"))
                File.WriteAllText(stateDb + "-wal", "wal");
            if (!File.Exists(stateDb + "-shm"))
                File.WriteAllText(stateDb + "-shm", "shm");
            File.WriteAllText(auditLogPath, "{}");
            File.WriteAllText(auditPath, "{}");
        }
        finally
        {
            client.Dispose();
            factory.Dispose();
        }

        await AssertWorkItemStoreDisposedAsync(factory.Store);
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(stateDb));
        Assert.False(File.Exists(stateDb + "-wal"));
        Assert.False(File.Exists(stateDb + "-shm"));
        Assert.False(Directory.Exists(gitRoot));
        Assert.False(Directory.Exists(agentStreamsPath));
    }

    [Fact]
    public async Task TestPipeline_Dispose_RemovesOwnedDatabaseTripletAndGitRoot()
    {
        using var temp = TestTempDirectory.Create("codeybox-pipeline-cleanup-");
        var disabledHookArtifactsBefore = SnapshotDisabledHostHookArtifacts();
        string stateDb;
        string gitRoot;

        var pipeline = TestSupport.BuildPipeline(temp.Root, "https://example.invalid/repo.git");
        stateDb = pipeline.StateDbPath!;
        gitRoot = pipeline.GitRoot;
        Directory.CreateDirectory(gitRoot);
        File.WriteAllText(Path.Combine(gitRoot, "repo.txt"), "repo");
        File.WriteAllText(stateDb + "-wal", "wal");
        File.WriteAllText(stateDb + "-shm", "shm");

        pipeline.Dispose();

        await AssertWorkItemStoreDisposedAsync(pipeline.Store);
        Assert.False(File.Exists(stateDb));
        Assert.False(File.Exists(stateDb + "-wal"));
        Assert.False(File.Exists(stateDb + "-shm"));
        Assert.False(Directory.Exists(gitRoot));
        Assert.Empty(SnapshotDisabledHostHookArtifacts().Except(disabledHookArtifactsBefore, StringComparer.Ordinal));
    }

    [Fact]
    public void TestPipeline_Dispose_PreservesCallerOwnedDatabaseAndRemovesGitRoot()
    {
        using var temp = TestTempDirectory.Create("codeybox-pipeline-cleanup-");
        var stateDb = Path.Combine(temp.Root, "caller-owned.db");
        string gitRoot;

        try
        {
            var pipeline = TestSupport.BuildPipeline(
                temp.Root,
                "https://example.invalid/repo.git",
                stateDbPathOverride: stateDb);
            gitRoot = pipeline.GitRoot;
            Directory.CreateDirectory(gitRoot);
            File.WriteAllText(Path.Combine(gitRoot, "repo.txt"), "repo");

            pipeline.Dispose();

            Assert.True(File.Exists(stateDb));
            Assert.False(Directory.Exists(gitRoot));
        }
        finally
        {
            TestTempArtifacts.DeleteSqliteDatabase(stateDb);
        }
    }

    [Fact]
    public async Task UatPipelineContext_Dispose_RemovesDatabaseTripletAndGitRoot()
    {
        using var temp = TestTempDirectory.Create("codeybox-uat-pipeline-cleanup-");
        var context = PipelineLifecycleUatHelpers.BuildPipeline(
            temp.Root,
            "https://example.invalid/repo.git");
        var stateDb = context.StateDbPath;
        var gitRoot = context.GitRoot;
        Directory.CreateDirectory(gitRoot);
        File.WriteAllText(Path.Combine(gitRoot, "repo.txt"), "repo");
        File.WriteAllText(stateDb + "-wal", "wal");
        File.WriteAllText(stateDb + "-shm", "shm");

        context.Dispose();

        await AssertWorkItemStoreDisposedAsync(context.Store);
        Assert.False(File.Exists(stateDb));
        Assert.False(File.Exists(stateDb + "-wal"));
        Assert.False(File.Exists(stateDb + "-shm"));
        Assert.False(Directory.Exists(gitRoot));
    }

    [Fact]
    public async Task PluginPipelineContext_Dispose_RemovesDatabaseTripletAndGitRoot()
    {
        using var temp = TestTempDirectory.Create("codeybox-plugin-pipeline-cleanup-");
        var context = PluginsUatHelpers.BuildPluginAuditPipeline(
            temp.Root,
            "https://example.invalid/repo.git",
            new CleanupNoopAuditor(),
            new ProjectAudit { MaxIterations = 1 });
        var stateDb = context.StateDbPath;
        var gitRoot = context.GitRoot;
        Directory.CreateDirectory(gitRoot);
        File.WriteAllText(Path.Combine(gitRoot, "repo.txt"), "repo");
        File.WriteAllText(stateDb + "-wal", "wal");
        File.WriteAllText(stateDb + "-shm", "shm");

        context.Dispose();

        await AssertWorkItemStoreDisposedAsync(context.Store);
        Assert.False(File.Exists(stateDb));
        Assert.False(File.Exists(stateDb + "-wal"));
        Assert.False(File.Exists(stateDb + "-shm"));
        Assert.False(Directory.Exists(gitRoot));
    }

    private static string RequiredConfig(IConfiguration config, string key)
        => config[key] ?? throw new InvalidOperationException($"Missing test configuration value: {key}");

    private static void AssertPathUnderRoot(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        Assert.StartsWith(fullRoot, fullPath, comparison);
    }

    private static async Task AssertWorkItemStoreDisposedAsync(SqliteWorkItemStore store)
    {
        var ex = await Record.ExceptionAsync(() => store.GetAsync(WorkItemId.New()));
        Assert.True(
            ex is ObjectDisposedException or InvalidOperationException,
            $"Expected disposed SQLite store to reject use, but got {ex?.GetType().FullName ?? "no exception"}.");
    }

    private static HashSet<string> SnapshotDisabledHostHookArtifacts()
    {
        var tempRoot = Path.GetTempPath();
        return Directory.Exists(tempRoot)
            ? Directory.EnumerateFileSystemEntries(tempRoot, "codeybox-disabled-host-hooks-*").ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    private sealed class FactoryTempCleanupApiFactory : CodeyBoxWebApplicationFactory
    {
        public FactoryTempCleanupApiFactory()
        {
            GitRoot = Temp.NewDirectoryPath("git-");
            AgentStreamRoot = Temp.NewDirectoryPath("agent-streams-");
            AuditLogPath = Temp.NewLogPath("log");
            AuditPath = Temp.NewLogPath("audit");
            StateDbPath = TempDatabasePath("state");
        }

        public string GitRoot { get; }
        public string AgentStreamRoot { get; }
        public string AuditLogPath { get; }
        public string AuditPath { get; }
        public string StateDbPath { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = StateDbPath,
                    ["CodeyBox:GitRootDirectory"] = GitRoot,
                    ["CodeyBox:AuditLog:Path"] = AuditLogPath,
                    ["CodeyBox:AuditLog:AuditPath"] = AuditPath,
                    ["CodeyBox:AgentStreams:Path"] = AgentStreamRoot,
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        }
    }

    private sealed class HelperDisposeApiFactory : CodeyBoxWebApplicationFactory
    {
        public HelperDisposeApiFactory()
        {
            GitRoot = Temp.NewDirectoryPath("git-");
            AgentStreamRoot = Temp.NewDirectoryPath("agent-streams-");
            AuditLogPath = Temp.NewLogPath("log");
            AuditPath = Temp.NewLogPath("audit");
            StateDbPath = TempDatabasePath("state");
            Store = new SqliteWorkItemStore(StateDbPath);
        }

        public string GitRoot { get; }
        public string AgentStreamRoot { get; }
        public string AuditLogPath { get; }
        public string AuditPath { get; }
        public string StateDbPath { get; }
        public SqliteWorkItemStore Store { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = StateDbPath,
                    ["CodeyBox:GitRootDirectory"] = GitRoot,
                    ["CodeyBox:AuditLog:Path"] = AuditLogPath,
                    ["CodeyBox:AuditLog:AuditPath"] = AuditPath,
                    ["CodeyBox:AgentStreams:Path"] = AgentStreamRoot,
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IWorkItemStore>();
                services.AddSingleton<IWorkItemStore>(Store);
            });
        }

        protected override void Dispose(bool disposing)
            => DisposeHostThenDeleteSqliteDatabase(disposing, StateDbPath, Store.Dispose);
    }

    private sealed class CleanupNoopAuditor : IAuditor
    {
        public string Name => "cleanup:noop";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }
}
