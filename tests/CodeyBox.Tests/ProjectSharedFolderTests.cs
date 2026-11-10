using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Bubblewrap;
using CodeyBox.Sandbox.Incus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class ProjectSharedFolderTests
{
    private static ProjectSharedFolder Folder(
        string name = "samples",
        string? hostPath = null,
        string sandboxPath = "/shared/samples",
        SharedFolderMode mode = SharedFolderMode.ReadOnly,
        SharedFolderPhases phases = SharedFolderPhases.All,
        bool required = true,
        SharedFolderWriterPolicy writerPolicy = SharedFolderWriterPolicy.Exclusive,
        SharedFolderExclusiveFallback fallback = SharedFolderExclusiveFallback.Wait) => new()
        {
            Name = name,
            HostPath = hostPath ?? "/allowed/samples",
            SandboxPath = sandboxPath,
            Mode = mode,
            Phases = phases,
            Required = required,
            WriterPolicy = writerPolicy,
            ExclusiveFallback = fallback,
        };

    private static SharedFolderValidationContext FakeContext(
        IReadOnlyList<string>? allowedRoots = null,
        Func<string, string>? canonicalize = null,
        Func<string, bool>? dirExists = null,
        string? home = "/home/tester",
        string? repos = null,
        string? stateDb = null) => new()
        {
            AllowedRoots = allowedRoots ?? ["/allowed"],
            RepositoriesDirectory = repos,
            StateDatabasePath = stateDb,
            CanonicalizeHostPath = canonicalize ?? (static path => path),
            DirectoryExists = dirExists ?? (_ => true),
            FileExists = _ => false,
            HomeDirectory = () => home,
        };

    // ── Config validation ─────────────────────────────────────────────────

    [Fact]
    public void ValidateAll_AcceptsPathUnderAllowedRoot()
    {
        var canonical = SharedFolderValidator.ValidateAll(
            "demo", [Folder()], FakeContext());
        Assert.Equal("/allowed/samples", canonical["samples"]);
    }

    [Fact]
    public void ValidateAll_EmptyAllowList_RejectsEveryHostPath()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SharedFolderValidator.ValidateAll("demo", [Folder()], FakeContext([])));
        Assert.Contains("no allow-list roots", ex.Message);
    }

    [Fact]
    public void ValidateAll_RejectsPathOutsideAllowedRoots()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SharedFolderValidator.ValidateAll(
                "demo", [Folder(hostPath: "/elsewhere/data")], FakeContext()));
        Assert.Contains("outside the allowed host-mount roots", ex.Message);
        Assert.Contains("demo", ex.Message);
        Assert.Contains("samples", ex.Message);
    }

    [Fact]
    public void ValidateAll_SymlinkEscapeOutsideRoot_Rejected()
    {
        var context = FakeContext(canonicalize: path =>
            string.Equals(path, "/allowed/link", StringComparison.Ordinal)
                ? "/etc/shadow-dir"
                : path);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SharedFolderValidator.ValidateAll(
                "demo", [Folder(hostPath: "/allowed/link")], context));
        Assert.Contains("outside the allowed host-mount roots", ex.Message);
    }

    [Theory]
    [InlineData("/home/tester/.ssh")]
    [InlineData("/home/tester/.codeybox")]
    [InlineData("/allowed/credentials")]
    [InlineData("/allowed/nested/secret/stuff")]
    public void ValidateAll_SensitiveHostPaths_Rejected(string hostPath)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SharedFolderValidator.ValidateAll("demo", [Folder(hostPath: hostPath)], FakeContext()));
        Assert.Contains("cannot be shared", ex.Message);
    }

    [Fact]
    public void ValidateAll_OrchestratorDirs_Rejected()
    {
        var context = FakeContext(
            allowedRoots: ["/", "/srv/repos", "/srv/state"],
            repos: "/srv/repos",
            stateDb: "/srv/state/codeybox.db");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SharedFolderValidator.ValidateAll(
                "demo", [Folder(hostPath: "/srv/repos/demo")], context));
        Assert.Contains("repository directory", ex.Message);
        var stateEx = Assert.Throws<InvalidOperationException>(() =>
            SharedFolderValidator.ValidateAll(
                "demo", [Folder(hostPath: "/srv/state")], context));
        Assert.Contains("state directory", stateEx.Message);
    }

    [Theory]
    [InlineData("/work")]
    [InlineData("/work/sub")]
    public void ValidateAll_WorkspaceOverlap_Rejected(string sandboxPath)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SharedFolderValidator.ValidateAll(
                "demo", [Folder(sandboxPath: sandboxPath)], FakeContext()));
        Assert.Contains("workspace mount", ex.Message);
    }

    [Fact]
    public void ValidateAll_DuplicateAndOverlappingSandboxPaths_Rejected()
    {
        var duplicate = Assert.Throws<InvalidOperationException>(() =>
            SharedFolderValidator.ValidateAll(
                "demo",
                [Folder(), Folder(name: "other", sandboxPath: "/shared/samples")],
                FakeContext()));
        Assert.Contains("duplicate", duplicate.Message);
        var nested = Assert.Throws<InvalidOperationException>(() =>
            SharedFolderValidator.ValidateAll(
                "demo",
                [Folder(), Folder(name: "other", hostPath: "/allowed/other", sandboxPath: "/shared/samples/sub")],
                FakeContext()));
        Assert.Contains("overlapping sandbox paths", nested.Message);
    }

    [Fact]
    public void ValidateAll_DuplicateNames_Rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SharedFolderValidator.ValidateAll(
                "demo",
                [Folder(), Folder(sandboxPath: "/shared/other")],
                FakeContext()));
        Assert.Contains("duplicate shared-folder name", ex.Message);
    }

    [Fact]
    public void ValidateAll_MissingHostPath_IsOperatorVisibleErrorNotSilentSkip()
    {
        var context = FakeContext(dirExists: _ => false);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SharedFolderValidator.ValidateAll("demo", [Folder()], context));
        Assert.Contains("does not exist", ex.Message);
        Assert.Contains("samples", ex.Message);
    }

    [Fact]
    public async Task ResolveSharedFolders_AppliesDocumentedDefaults()
    {
        var scratch = Directory.CreateTempSubdirectory("codeybox-shares-");
        try
        {
            var dataDir = Directory.CreateDirectory(Path.Combine(scratch.FullName, "data")).FullName;
            var policy = new ProjectSharedFolderPolicy
            {
                AllowedRoots = () => [scratch.FullName],
            };
            using var repo = new ProjectRepository(
                Options.Create(new ProjectsOptions
                {
                    Projects =
                    [
                        new ProjectConfig
                        {
                            Id = "demo",
                            RepositoryUrl = "https://example.com/demo.git",
                            SharedFolders =
                            [
                                new ProjectSharedFolderConfig { Name = "data", HostPath = dataDir },
                            ],
                        },
                    ],
                }),
                NullLogger<ProjectRepository>.Instance,
                presetCatalogOptions: null,
                sharedFolderPolicy: policy);
            var project = await repo.GetAsync(new ProjectId("demo"));
            var folder = Assert.Single(project!.SharedFolders);
            Assert.Equal("/shared/data", folder.SandboxPath);
            Assert.Equal(SharedFolderMode.ReadOnly, folder.Mode);
            Assert.Equal(SharedFolderPhases.All, folder.Phases);
            Assert.True(folder.Required);
            Assert.Equal(SharedFolderWriterPolicy.Exclusive, folder.WriterPolicy);
            Assert.Equal(SharedFolderExclusiveFallback.Wait, folder.ExclusiveFallback);
        }
        finally
        {
            Directory.Delete(scratch.FullName, recursive: true);
        }
    }

    // ── Read-only enforcement + auditor-always-read-only ──────────────────

    [Fact]
    public void ToSandboxMount_ReadOnlyFolder_YieldsReadOnlyMountAtVmLevel()
    {
        var mount = SharedFolderPolicy.ToSandboxMount(Folder(), isAudit: false);
        Assert.True(mount.ReadOnly);
        Assert.Equal("/shared/samples", mount.SandboxPath);
        Assert.Equal("/allowed/samples", mount.HostPath);
    }

    [Fact]
    public void ToSandboxMount_ReadWriteFolder_YieldsWritableMountForWork()
    {
        var mount = SharedFolderPolicy.ToSandboxMount(
            Folder(mode: SharedFolderMode.ReadWrite), isAudit: false);
        Assert.False(mount.ReadOnly);
    }

    [Fact]
    public void EffectiveReadOnly_AuditorsAlwaysReadOnlyRegardlessOfMode()
    {
        Assert.True(SharedFolderPolicy.EffectiveReadOnly(
            Folder(mode: SharedFolderMode.ReadWrite), isAudit: true));
        Assert.True(SharedFolderPolicy.EffectiveReadOnly(Folder(), isAudit: true));
        Assert.False(SharedFolderPolicy.EffectiveReadOnly(
            Folder(mode: SharedFolderMode.ReadWrite), isAudit: false));
    }

    [Fact]
    public void AuditMount_PreservesReadOnlyThroughIncusTopologyCheck()
    {
        var mount = SharedFolderPolicy.ToSandboxMount(
            Folder(mode: SharedFolderMode.ReadWrite), isAudit: true);
        Assert.True(mount.ReadOnly);
        var prepared = new IncusPreparedMount("/host/samples", mount.SandboxPath, ReadOnly: mount.ReadOnly);
        IncusDeviceTopology.Verify(
            """
            {
              "type":"sync",
              "metadata":{
                "type":"virtual-machine",
                "config":{},
                "expanded_config":{},
                "profiles":[],
                "expanded_devices":{
                  "root":{"type":"disk","path":"/","pool":"codeybox-zfs"},
                  "m000":{"type":"disk","source":"/host/samples","path":"/shared/samples","io.bus":"virtiofs","readonly":"true"}
                }
              }
            }
            """,
            new IncusSandboxOptions { StoragePoolName = "codeybox-zfs" },
            expectedBridge: null,
            [prepared]);
    }

    [Fact]
    public void Providers_DeclareHostFolderShareCapabilities()
    {
        var incus = new IncusSandboxProvider(
            () => new IncusSandboxOptions { StoragePoolName = "codeybox-zfs" },
            NullLogger<IncusSandboxProvider>.Instance);
        Assert.Contains(SandboxCapabilities.HostFolderShares, incus.DeclaredCapabilities);
        Assert.Contains(SandboxCapabilities.HostFolderSharesReadWrite, incus.DeclaredCapabilities);
        var bubblewrap = new BubblewrapSandboxProvider(
            new BubblewrapSandboxOptions(), NullLogger<BubblewrapSandboxProvider>.Instance);
        Assert.Contains(SandboxCapabilities.HostFolderShares, bubblewrap.DeclaredCapabilities);
        Assert.Contains(SandboxCapabilities.HostFolderSharesReadWrite, bubblewrap.DeclaredCapabilities);
    }

    // ── Provider capability routing ───────────────────────────────────────

    [Fact]
    public void CheckProvider_RequiredReadWriteShare_RefusedOnReadOnlyProvider()
    {
        var folders = new List<ProjectSharedFolder>
        {
            Folder(mode: SharedFolderMode.ReadWrite),
        };
        var (ok, reason) = SharedFolderPolicy.CheckProvider(
            folders, "work", isAudit: false,
            [SandboxCapabilities.HostFolderShares], "tiny-provider");
        Assert.False(ok);
        Assert.Contains("samples", reason);
        Assert.Contains("tiny-provider", reason);
        Assert.Contains(SandboxCapabilities.HostFolderSharesReadWrite, reason);
    }

    [Fact]
    public void CheckProvider_OptionalShare_NeverBlocksRouting()
    {
        var folders = new List<ProjectSharedFolder>
        {
            Folder(mode: SharedFolderMode.ReadWrite, required: false),
        };
        var (ok, reason) = SharedFolderPolicy.CheckProvider(
            folders, "work", isAudit: false, [], "tiny-provider");
        Assert.True(ok);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void CheckProvider_PhaseFilteredShares_DoNotBlockOtherPhases()
    {
        var folders = new List<ProjectSharedFolder>
        {
            Folder(phases: SharedFolderPhases.Work, mode: SharedFolderMode.ReadWrite),
        };
        var (ok, _) = SharedFolderPolicy.CheckProvider(
            folders, "audit", isAudit: true, [], "tiny-provider");
        Assert.True(ok);
    }

    [Fact]
    public void ThrowIfProviderCannotServe_RaisesPlaceableRoutingError()
    {
        var folders = new List<ProjectSharedFolder> { Folder() };
        var ex = Assert.Throws<SandboxPlacementUnplaceableException>(() =>
            SharedFolderSandboxMapper.ThrowIfProviderCannotServe(
                "demo", folders, "work", isAudit: false, [], "tiny-provider"));
        Assert.Contains(SandboxCapabilities.HostFolderShares, ex.UnmetCapability);
        Assert.Contains("samples", ex.Message);
    }

    [Fact]
    public void RequiredCapabilitiesForPhase_OnlyDemandsReadWriteWhenNeeded()
    {
        var readOnly = SharedFolderPolicy.RequiredCapabilitiesForPhase(
            [Folder()], "work", isAudit: false);
        Assert.Equal([SandboxCapabilities.HostFolderShares], readOnly);
        var readWrite = SharedFolderPolicy.RequiredCapabilitiesForPhase(
            [Folder(mode: SharedFolderMode.ReadWrite)], "work", isAudit: false);
        Assert.Equal(
            [SandboxCapabilities.HostFolderShares, SandboxCapabilities.HostFolderSharesReadWrite],
            readWrite);
        var auditDowngraded = SharedFolderPolicy.RequiredCapabilitiesForPhase(
            [Folder(mode: SharedFolderMode.ReadWrite)], "audit", isAudit: true);
        Assert.Equal([SandboxCapabilities.HostFolderShares], auditDowngraded);
    }

    // ── Exclusive-writer behaviour ────────────────────────────────────────

    [Fact]
    public async Task WriterGate_SharedPolicy_AllowsConcurrentWriters()
    {
        var gate = new SharedFolderWriterGate();
        var first = await gate.AcquireAsync(
            "demo\0data", SharedFolderWriterPolicy.Shared, SharedFolderExclusiveFallback.Wait);
        var second = await gate.AcquireAsync(
            "demo\0data", SharedFolderWriterPolicy.Shared, SharedFolderExclusiveFallback.Wait);
        Assert.True(first.ReadWrite);
        Assert.True(second.ReadWrite);
        Assert.False(first.DowngradedToReadOnly);
        Assert.False(second.DowngradedToReadOnly);
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task WriterGate_ExclusiveReadOnlyFallback_DowngradesWaiter()
    {
        var gate = new SharedFolderWriterGate();
        var holder = await gate.AcquireAsync(
            "demo\0data", SharedFolderWriterPolicy.Exclusive, SharedFolderExclusiveFallback.Wait);
        var waiter = await gate.AcquireAsync(
            "demo\0data", SharedFolderWriterPolicy.Exclusive, SharedFolderExclusiveFallback.ReadOnly);
        Assert.True(holder.ReadWrite);
        Assert.False(holder.DowngradedToReadOnly);
        Assert.False(waiter.ReadWrite);
        Assert.True(waiter.DowngradedToReadOnly);
        await waiter.DisposeAsync();
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task WriterGate_ExclusiveWait_TimesOutInsteadOfPilingUp()
    {
        var gate = new SharedFolderWriterGate();
        var holder = await gate.AcquireAsync(
            "demo\0data", SharedFolderWriterPolicy.Exclusive, SharedFolderExclusiveFallback.Wait);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => gate.AcquireAsync(
                "demo\0data",
                SharedFolderWriterPolicy.Exclusive,
                SharedFolderExclusiveFallback.Wait,
                waitTimeout: TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            await holder.DisposeAsync();
        }
    }

    [Fact]
    public async Task WriterGate_Dispose_ReleasesExclusiveLease()
    {
        var gate = new SharedFolderWriterGate();
        var holder = await gate.AcquireAsync(
            "demo\0data", SharedFolderWriterPolicy.Exclusive, SharedFolderExclusiveFallback.Wait);
        await holder.DisposeAsync();
        var next = await gate.AcquireAsync(
            "demo\0data",
            SharedFolderWriterPolicy.Exclusive,
            SharedFolderExclusiveFallback.Wait,
            waitTimeout: TimeSpan.FromSeconds(5));
        Assert.True(next.ReadWrite);
        await next.DisposeAsync();
    }

    [Fact]
    public async Task AcquireLeasesAsync_WaitTimeout_SurfacesDeferralForRequeue()
    {
        var scratch = Directory.CreateTempSubdirectory("codeybox-share-lease-");
        try
        {
            var folder = Folder(hostPath: scratch.FullName, mode: SharedFolderMode.ReadWrite);
            var gate = new SharedFolderWriterGate();
            var holder = await gate.AcquireAsync(
                SharedFolderWriterGate.KeyFor("demo", "samples"),
                SharedFolderWriterPolicy.Exclusive,
                SharedFolderExclusiveFallback.Wait);
            try
            {
                var plan = SharedFolderSandboxMapper.BuildPlan("demo", [folder], "work", isAudit: false);
                using var cts = new CancellationTokenSource(500);
                await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
                    SharedFolderSandboxMapper.AcquireLeasesAsync(
                        "demo", plan, gate, "test-provider", ct: cts.Token));
            }
            finally
            {
                await holder.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(scratch.FullName, recursive: true);
        }
    }

    [Fact]
    public void BuildPlan_MissingRequiredHostPath_FailsOperatorVisible()
    {
        var folder = Folder(hostPath: "/allowed/definitely-not-here");
        var ex = Assert.Throws<SandboxMountSourceMissingException>(() =>
            SharedFolderSandboxMapper.BuildPlan("demo", [folder], "work", isAudit: false));
        Assert.Contains("samples", ex.Message);
    }

    [Fact]
    public void BuildPlan_MissingOptionalHostPath_SkipsMount()
    {
        var folder = Folder(hostPath: "/allowed/definitely-not-here", required: false);
        var plan = SharedFolderSandboxMapper.BuildPlan("demo", [folder], "work", isAudit: false);
        Assert.Empty(plan.Mounts);
    }

    // ── Baseline names unchanged ──────────────────────────────────────────

    [Fact]
    public void BaselineToolchainHash_IgnoresRuntimeShareMounts()
    {
        var options = new IncusSandboxOptions { StoragePoolName = "codeybox-zfs" };
        var before = IncusBaselineNaming.ComputeSharedToolchainHash(options, []);
        var scratch = Directory.CreateTempSubdirectory("codeybox-share-baseline-");
        try
        {
            var folder = Folder(hostPath: scratch.FullName, mode: SharedFolderMode.ReadWrite);
            var plan = SharedFolderSandboxMapper.BuildPlan("demo", [folder], "work", isAudit: false);
            var mounts = new List<SandboxMount>();
            SharedFolderSandboxMapper.AppendTo(mounts, plan, "demo", [folder]);
            var spec = new SandboxSpec { ImageReference = "local-image", Mounts = mounts };
            Assert.Contains(spec.Mounts, m => m.SandboxPath == "/shared/samples" && !m.ReadOnly);
            var after = IncusBaselineNaming.ComputeSharedToolchainHash(options, []);
            Assert.Equal(before, after);
        }
        finally
        {
            Directory.Delete(scratch.FullName, recursive: true);
        }
    }

    [Fact]
    public void BaselineToolchainHash_IsDeterministicAndContentAddressed()
    {
        var inputs = new BaselineToolchainInputs(["echo hi"], [], []);
        var first = BaselineContentHash.ComputeToolchainHash(inputs);
        var second = BaselineContentHash.ComputeToolchainHash(inputs);
        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(first[..12], BaselineContentHash.ToShortHash(first));
        var drifted = BaselineContentHash.ComputeToolchainHash(inputs with { Runcmd = ["echo bye"] });
        Assert.NotEqual(first, drifted);
    }

    // ── Agent context prompt ──────────────────────────────────────────────

    [Fact]
    public void PromptSection_ListsFoldersModesAndWarnsAboutHostWrites()
    {
        var section = SharedFolderPromptSection.Build(
            [Folder(), Folder(name: "scratch", sandboxPath: "/shared/scratch", mode: SharedFolderMode.ReadWrite)],
            "work",
            isAudit: false);
        Assert.Contains("/shared/samples", section);
        Assert.Contains("read-only", section);
        Assert.Contains("/shared/scratch", section);
        Assert.Contains("read-write", section);
        Assert.Contains("modify host files", section);
    }

    [Fact]
    public void PromptSection_AuditAlwaysReportsReadOnly()
    {
        var section = SharedFolderPromptSection.Build(
            [Folder(name: "scratch", sandboxPath: "/shared/scratch", mode: SharedFolderMode.ReadWrite)],
            "audit",
            isAudit: true);
        Assert.Contains("(scratch, read-only)", section);
        Assert.DoesNotContain("modify host files", section);
    }

    [Fact]
    public void PromptSection_EmptyWhenNoFolderApplies()
    {
        Assert.Equal(
            string.Empty,
            SharedFolderPromptSection.Build([Folder(phases: SharedFolderPhases.Work)], "audit", isAudit: true));
        Assert.Equal(string.Empty, SharedFolderPromptSection.Build([], "work", isAudit: false));
    }
}

public sealed class ProjectSharedFolderEndpointTests : IClassFixture<SharedFolderApiFactory>
{
    private readonly SharedFolderApiFactory _factory;
    public ProjectSharedFolderEndpointTests(SharedFolderApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetSharedFolders_UnknownProject_Returns404()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/projects/unknown-project/shared-folders");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetProject_IncludesConfiguredShares()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/projects/share-demo");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("hasSharedFolders").GetBoolean());
        var shares = body.GetProperty("sharedFolders").EnumerateArray().ToList();
        var samples = Assert.Single(shares);
        Assert.Equal("samples", samples.GetProperty("name").GetString());
        Assert.Equal("/shared/samples", samples.GetProperty("sandboxPath").GetString());
        Assert.Equal("ReadOnly", samples.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task GetSharedFolders_ReturnsConfiguredSharesWithResolvedStatus()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/projects/share-demo/shared-folders");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("share-demo", body.GetProperty("projectId").GetString());
        var shares = body.GetProperty("shares").EnumerateArray().ToList();
        var samples = Assert.Single(shares);
        Assert.Equal("samples", samples.GetProperty("name").GetString());
        Assert.Equal("/shared/samples", samples.GetProperty("sandboxPath").GetString());
        Assert.Equal("ReadOnly", samples.GetProperty("mode").GetString());
        Assert.Equal("ReadOnly", samples.GetProperty("auditEffectiveMode").GetString());
        Assert.True(samples.GetProperty("exists").GetBoolean());
        Assert.True(samples.GetProperty("allowed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, samples.GetProperty("error").ValueKind);
    }
}

public sealed class SharedFolderApiFactory : WebApplicationFactory<Program>
{
    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-shares-httptest-");
    private string _dbPath => Path.Combine(_scratch.DirectoryPath, "state", "shares-httptest.db");
    private string _shareDir => Path.Combine(_scratch.DirectoryPath, "samples");

    public SqliteWorkItemStore Store { get; }

    public SharedFolderApiFactory()
    {
        Directory.CreateDirectory(_shareDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        Store = new SqliteWorkItemStore(_dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitHubAppStorePath"] = Path.Combine(_scratch.DirectoryPath, "github-apps"),
                ["CodeyBox:GitRootDirectory"] = Path.Combine(_scratch.DirectoryPath, "test-git"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(_scratch.DirectoryPath, "test-log.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_scratch.DirectoryPath, "test-audit.json"),
                ["CodeyBox:Incus:AllowedHostMountRoots:0"] = _scratch.DirectoryPath,
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(Store);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId("share-demo"),
                    DisplayName = "Share Demo",
                    RepositoryUrl = "https://example.com/demo",
                    SharedFolders =
                    [
                        new ProjectSharedFolder
                        {
                            Name = "samples",
                            HostPath = _shareDir,
                            SandboxPath = "/shared/samples",
                            Mode = SharedFolderMode.ReadOnly,
                        },
                    ],
                }));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Store.Dispose();
            try
            {
                File.Delete(_dbPath);
            }
            catch
            {
            }
            TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
            _scratch.Dispose();
        }
        base.Dispose(disposing);
    }
}
