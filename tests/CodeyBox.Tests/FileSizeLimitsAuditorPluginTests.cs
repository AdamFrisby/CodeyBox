using System.Diagnostics;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.FileSizeLimitsAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Projects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class FileSizeLimitsAuditorPluginTests
{
    [Fact]
    public async Task ByteOnlyTrip_Blocks_WhenLineCountIsUnderCap()
    {
        using var repo = await AuditRepo.CreateAsync();
        await repo.CheckoutNewBranchAsync("feature/byte-only");
        await repo.WriteAndCommitAsync("src/BigBytes.cs", "public sealed class BigBytes { public string Value = \"" + new string('x', 80) + "\"; }\n");

        var result = await RunAuditorAsync(repo.Path, Config(
            ("WarnFileBytes", "0"),
            ("MaxFileBytes", "40"),
            ("WarnFileLines", "0"),
            ("MaxFileLines", "10")));

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("bytes", finding.Description);
        Assert.Contains("MaxFileBytes 40", finding.Description);
        Assert.DoesNotContain("lines", finding.Description);
    }

    [Fact]
    public async Task LocOnlyTrip_Blocks_WhenByteCountIsUnderCap()
    {
        using var repo = await AuditRepo.CreateAsync();
        await repo.CheckoutNewBranchAsync("feature/loc-only");
        await repo.WriteAndCommitAsync("src/ManyLines.cs", "a\nb\nc\nd\n");

        var result = await RunAuditorAsync(repo.Path, Config(
            ("WarnFileBytes", "0"),
            ("MaxFileBytes", "1000"),
            ("WarnFileLines", "0"),
            ("MaxFileLines", "3")));

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("lines", finding.Description);
        Assert.Contains("MaxFileLines 3", finding.Description);
        Assert.DoesNotContain("bytes", finding.Description);
    }

    [Fact]
    public async Task BothDimensionsTrip_ReportsBothNumbers()
    {
        using var repo = await AuditRepo.CreateAsync();
        await repo.CheckoutNewBranchAsync("feature/both");
        await repo.WriteAndCommitAsync("src/Both.cs", "aaaaaa\nbbbbbb\ncccccc\n");

        var result = await RunAuditorAsync(repo.Path, Config(
            ("WarnFileBytes", "0"),
            ("MaxFileBytes", "10"),
            ("WarnFileLines", "0"),
            ("MaxFileLines", "2")));

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("bytes", finding.Description);
        Assert.Contains("MaxFileBytes 10", finding.Description);
        Assert.Contains("lines", finding.Description);
        Assert.Contains("MaxFileLines 2", finding.Description);
    }

    [Fact]
    public async Task WarnTier_ProducesNonBlockingFinding_BlockTierProducesError()
    {
        using var repo = await AuditRepo.CreateAsync();
        await repo.CheckoutNewBranchAsync("feature/warn-and-block");
        await repo.WriteAndCommitAsync("src/WarnOnly.cs", "a\nb\nc\n");

        var warn = await RunAuditorAsync(repo.Path, Config(
            ("WarnFileBytes", "0"),
            ("MaxFileBytes", "0"),
            ("WarnFileLines", "2"),
            ("MaxFileLines", "5")));

        Assert.True(warn.Passed);
        var warning = Assert.Single(warn.Findings);
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Contains("WarnFileLines 2", warning.Description);

        await repo.WriteAndCommitAsync("src/Block.cs", "a\nb\nc\nd\ne\nf\n");
        var block = await RunAuditorAsync(repo.Path, Config(
            ("WarnFileBytes", "0"),
            ("MaxFileBytes", "0"),
            ("WarnFileLines", "2"),
            ("MaxFileLines", "5")));

        Assert.False(block.Passed);
        Assert.Contains(block.Findings, f =>
            f.Location == "src/Block.cs" &&
            f.Severity == AuditSeverity.Error &&
            f.Description.Contains("MaxFileLines 5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExcludeGlob_SkipsMatchingFiles()
    {
        using var repo = await AuditRepo.CreateAsync();
        await repo.CheckoutNewBranchAsync("feature/exclude");
        await repo.WriteAndCommitAsync("src/Generated.generated.cs", "a\nb\nc\nd\n");

        var result = await RunAuditorAsync(repo.Path, Config(
            ("WarnFileBytes", "0"),
            ("MaxFileBytes", "0"),
            ("WarnFileLines", "0"),
            ("MaxFileLines", "1")));

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Grandfathering_PreExistingOverCapUnchanged_WarnsOnly()
    {
        using var repo = await AuditRepo.CreateAsync();
        await repo.WriteAndCommitAsync("src/Legacy.cs", "a\nb\nc\nd\n");
        await repo.CheckoutNewBranchAsync("feature/legacy-unchanged");

        var result = await RunAuditorAsync(repo.Path, Config(
            ("WarnFileBytes", "0"),
            ("MaxFileBytes", "0"),
            ("WarnFileLines", "2"),
            ("MaxFileLines", "3")));

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("grandfathered", finding.Description);
        Assert.Equal("src/Legacy.cs", finding.Location);
    }

    [Fact]
    public async Task Grandfathering_PreExistingOverCapGrew_Blocks()
    {
        using var repo = await AuditRepo.CreateAsync();
        await repo.WriteAndCommitAsync("src/Legacy.cs", "a\nb\nc\nd\n");
        await repo.CheckoutNewBranchAsync("feature/legacy-grew");
        await repo.WriteAndCommitAsync("src/Legacy.cs", "a\nb\nc\nd\ne\n");

        var result = await RunAuditorAsync(repo.Path, Config(
            ("WarnFileBytes", "0"),
            ("MaxFileBytes", "0"),
            ("WarnFileLines", "2"),
            ("MaxFileLines", "3")));

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("grew from 4", finding.Description);
    }

    [Fact]
    public async Task StrictGrandfatherMode_PreExistingOverCapUnchanged_Blocks()
    {
        using var repo = await AuditRepo.CreateAsync();
        await repo.WriteAndCommitAsync("src/Legacy.cs", "a\nb\nc\nd\n");
        await repo.CheckoutNewBranchAsync("feature/strict");

        var result = await RunAuditorAsync(repo.Path, Config(
            ("WarnFileBytes", "0"),
            ("MaxFileBytes", "0"),
            ("WarnFileLines", "2"),
            ("MaxFileLines", "3"),
            ("GrandfatherMode", "strict")));

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("blocking threshold exceeded", finding.Description);
        Assert.DoesNotContain("grandfathered", finding.Description);
    }

    [Fact]
    public async Task HotReloadedConfiguration_ChangesCapsForNextRun()
    {
        using var repo = await AuditRepo.CreateAsync();
        await repo.CheckoutNewBranchAsync("feature/hot-reload");
        await repo.WriteAndCommitAsync("src/Reload.cs", "a\nb\nc\nd\n");

        var config = Config(
            ("WarnFileBytes", "0"),
            ("MaxFileBytes", "0"),
            ("WarnFileLines", "0"),
            ("MaxFileLines", "10"));
        var auditor = new FileSizeLimitsAuditor(config);
        var sandbox = new ProcessExecSandbox();

        var before = await auditor.RunAsync(sandbox, repo.Path, Context(), CancellationToken.None);
        Assert.True(before.Passed);
        Assert.Empty(before.Findings);

        config["CodeyBox:Auditors:FileSizeLimits:MaxFileLines"] = "3";

        var after = await auditor.RunAsync(sandbox, repo.Path, Context(), CancellationToken.None);
        Assert.False(after.Passed);
        var finding = Assert.Single(after.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("MaxFileLines 3", finding.Description);
    }

    [Fact]
    public async Task AddCodeyBoxPlugins_LoadsAuditorAndComposerIncludesItByPluginId()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Plugins:AssemblyPaths:0"] = typeof(FileSizeLimitsAuditor).Assembly.Location,
                ["CodeyBox:Plugins:Allowlist:0"] = FileSizeLimitsAuditor.PluginId,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);

        var loaded = services.AddCodeyBoxPlugins(config);

        await using var provider = services.BuildServiceProvider();
        var auditor = Assert.Single(provider.GetServices<IAuditor>());
        Assert.Equal(FileSizeLimitsAuditor.AuditorName, auditor.Name);
        Assert.Equal(FileSizeLimitsAuditor.PluginId, Assert.Single(loaded).PluginId);
        Assert.NotNull(auditor.GetType().GetCustomAttributes(typeof(CodeyBoxPluginAttribute), inherit: false).SingleOrDefault());

        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [auditor],
            NullLogger<ProjectAuditorComposer>.Instance);
        var project = new Project
        {
            Id = new ProjectId("file-size-plugin"),
            DisplayName = "File Size Plugin",
            RepositoryUrl = "https://example.invalid/repo.git",
            Audit = new ProjectAudit
            {
                Custom =
                [
                    new CustomAuditorDescriptor
                    {
                        Kind = "plugin",
                        PluginId = FileSizeLimitsAuditor.PluginId,
                    },
                ],
            },
        };

        var auditors = composer.Compose(project, new FakeAgent());

        Assert.Contains(auditors, a => a.Name == FileSizeLimitsAuditor.AuditorName);
    }

    [Fact]
    public async Task GitListFailure_ReturnsErrorFinding()
    {
        var auditor = new FileSizeLimitsAuditor(Config());
        var result = await auditor.RunAsync(
            new StubSandbox(_ => new SandboxExecResult(128, "", "fatal: not a git repository")),
            "/work",
            Context(),
            CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("failed to list repository files", finding.Title);
        Assert.Contains("fatal: not a git repository", finding.Description);
    }

    private static AuditContext Context() => new(
        WorkItemId.New(),
        WorkBranch: "feature/test",
        BaseBranch: "main",
        Iteration: 1,
        OriginalPrompt: "test");

    private static IConfigurationRoot Config(params (string Key, string Value)[] values)
    {
        var data = values.ToDictionary(
            kvp => "CodeyBox:Auditors:FileSizeLimits:" + kvp.Key,
            kvp => (string?)kvp.Value,
            StringComparer.OrdinalIgnoreCase);
        data["CodeyBox:Auditors:FileSizeLimits:IncludeGlobs:0"] = "**/*.cs";
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private static async Task<AuditResult> RunAuditorAsync(string repoPath, IConfiguration config)
    {
        var auditor = new FileSizeLimitsAuditor(config);
        return await auditor.RunAsync(new ProcessExecSandbox(), repoPath, Context(), CancellationToken.None);
    }

    private sealed class FakeAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    private sealed class ProcessExecSandbox : ISandbox
    {
        public string Id => "process";

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exec.Argv[0],
                WorkingDirectory = exec.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = exec.Stdin is not null,
                UseShellExecute = false,
            };
            foreach (var arg in exec.Argv.Skip(1))
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            if (exec.Stdin is not null)
            {
                await process.StandardInput.WriteAsync(exec.Stdin);
                await process.StandardInput.DisposeAsync();
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new SandboxExecResult(process.ExitCode, stdout, stderr);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSandbox(Func<SandboxExec, SandboxExecResult> handler) : ISandbox
    {
        public string Id => "stub";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(handler(exec));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AuditRepo : IDisposable
    {
        private AuditRepo(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static async Task<AuditRepo> CreateAsync()
        {
            var path = Directory.CreateTempSubdirectory("codeybox-file-size-auditor-").FullName;
            var repo = new AuditRepo(path);
            await repo.GitAsync("init", "-b", "main");
            await repo.GitAsync("config", "user.email", "test@example.invalid");
            await repo.GitAsync("config", "user.name", "Test");
            await repo.WriteAndCommitAsync("README.md", "seed\n");
            return repo;
        }

        public Task CheckoutNewBranchAsync(string branch)
            => GitAsync("checkout", "-b", branch);

        public async Task WriteAndCommitAsync(string relativePath, string content)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content);
            await GitAsync("add", relativePath);
            await GitAsync("commit", "-m", "test commit");
        }

        private async Task GitAsync(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout}{stderr}");
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
