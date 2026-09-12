using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the merge-to-main admission flake gate: the
/// <c>Audit:Flake:AdmissionReruns</c> knob (default 3, hot-reloadable,
/// config-driven) and the pre-merge verifier's rerun comparison —
/// deterministic-green passes, deterministic-red blocks as today, and a
/// non-deterministic (flaky) outcome blocks as a new finding.
/// </summary>
public sealed class FlakeAdmissionTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-flake-admission-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void AdmissionRerunsDefaultsToThree()
    {
        Assert.Equal(3, new FlakeAdmissionOptions().AdmissionReruns);
        Assert.Equal(FlakeAdmissionOptions.DefaultAdmissionReruns, new FlakeAdmissionOptions().AdmissionReruns);
    }

    [Fact]
    public void EffectiveAdmissionRerunsClampsToOneAtMinimum()
    {
        Assert.Equal(1, new FlakeAdmissionOptions { AdmissionReruns = 0 }.EffectiveAdmissionReruns());
        Assert.Equal(1, new FlakeAdmissionOptions { AdmissionReruns = -5 }.EffectiveAdmissionReruns());
    }

    [Fact]
    public void EffectiveAdmissionRerunsClampsToMax()
    {
        Assert.Equal(
            FlakeAdmissionOptions.MaxAdmissionReruns,
            new FlakeAdmissionOptions { AdmissionReruns = FlakeAdmissionOptions.MaxAdmissionReruns + 100 }.EffectiveAdmissionReruns());
    }

    [Fact]
    public void BindsAdmissionRerunsFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Audit:Flake:AdmissionReruns"] = "5",
            })
            .Build();

        var section = new AuditSectionOptions();
        config.GetSection("CodeyBox:Audit").Bind(section);

        Assert.Equal(5, section.Flake.AdmissionReruns);
    }

    [Fact]
    public void FlakeSectionDefaultsToThreeRunsWhenUnset()
    {
        Assert.Equal(3, new AuditSectionOptions().Flake.AdmissionReruns);
    }

    [Fact]
    public void EvaluatorPassesWhenEveryRunIsGreen()
    {
        var runs = new[]
        {
            new FlakeAdmissionRunResult(true, []),
            new FlakeAdmissionRunResult(true, []),
            new FlakeAdmissionRunResult(true, []),
        };

        var (verdict, flaky) = FlakeAdmissionEvaluator.Evaluate(runs);

        Assert.Equal(FlakeAdmissionVerdict.Pass, verdict);
        Assert.Empty(flaky);
    }

    [Fact]
    public void EvaluatorFailsDeterministicallyWhenEveryRunFailsIdentically()
    {
        var runs = new[]
        {
            new FlakeAdmissionRunResult(false, ["Ns.RedTest"]),
            new FlakeAdmissionRunResult(false, ["Ns.RedTest"]),
            new FlakeAdmissionRunResult(false, ["Ns.RedTest"]),
        };

        var (verdict, flaky) = FlakeAdmissionEvaluator.Evaluate(runs);

        Assert.Equal(FlakeAdmissionVerdict.FailDeterministic, verdict);
        Assert.Empty(flaky);
    }

    [Fact]
    public void EvaluatorFlagsFlakyWhenExitCodesDifferAcrossRuns()
    {
        var runs = new[]
        {
            new FlakeAdmissionRunResult(false, ["Ns.FlakyTest"]),
            new FlakeAdmissionRunResult(true, []),
            new FlakeAdmissionRunResult(false, ["Ns.FlakyTest"]),
        };

        var (verdict, flaky) = FlakeAdmissionEvaluator.Evaluate(runs);

        Assert.Equal(FlakeAdmissionVerdict.Flaky, verdict);
        Assert.Contains("Ns.FlakyTest", flaky);
    }

    [Fact]
    public void EvaluatorFlagsFlakyWhenFailureSetsDifferAcrossFailingRuns()
    {
        var runs = new[]
        {
            new FlakeAdmissionRunResult(false, ["Ns.TestA"]),
            new FlakeAdmissionRunResult(false, ["Ns.TestB"]),
        };

        var (verdict, flaky) = FlakeAdmissionEvaluator.Evaluate(runs);

        Assert.Equal(FlakeAdmissionVerdict.Flaky, verdict);
        Assert.Contains("Ns.TestA", flaky);
        Assert.Contains("Ns.TestB", flaky);
    }

    [Fact]
    public void FlakyReasonNamesTheNonDeterministicTests()
    {
        var reason = FlakeAdmissionEvaluator.BuildFlakyReason(3, 1, ["Ns.FlakyTest"]);

        Assert.Contains("flaky", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ns.FlakyTest", reason);
        Assert.Contains("3", reason);
    }

    [Fact]
    public void IsTestArgvMatchesDotnetTestButNotBuild()
    {
        Assert.True(LocalGitPreMergeVerifier.IsTestArgv(["dotnet", "test"]));
        Assert.True(LocalGitPreMergeVerifier.IsTestArgv(["dotnet", "test", "--no-build"]));
        Assert.True(LocalGitPreMergeVerifier.IsTestArgv(["/usr/share/dotnet/dotnet", "test"]));
        Assert.True(LocalGitPreMergeVerifier.IsTestArgv(["dotnet.exe", "vstest"]));
        Assert.False(LocalGitPreMergeVerifier.IsTestArgv(["dotnet", "build"]));
        Assert.False(LocalGitPreMergeVerifier.IsTestArgv(["dotnet"]));
        Assert.False(LocalGitPreMergeVerifier.IsTestArgv(["/usr/bin/false"]));
        Assert.False(LocalGitPreMergeVerifier.IsTestArgv([]));
    }

    /// <summary>
    /// Deterministic-green: an always-passing test command passes the gate,
    /// having run once per admission rerun (not just once).
    /// </summary>
    [Fact]
    public async Task RealVerifier_DeterministicGreen_PassesGateAfterAllReruns()
    {
        var (gitHost, repoId, mergeSha) = await SetupBareRepoWithCommitAsync();
        var counter = CounterPath();
        var fakeDotnet = WriteFakeDotnet("green", "exit 0", counter);
        var verifier = new LocalGitPreMergeVerifier(
            gitHost,
            NullLogger<LocalGitPreMergeVerifier>.Instance,
            commandTimeout: TimeSpan.FromSeconds(60),
            admissionRerunsProvider: () => 3);

        var result = await verifier.VerifyAsync(TestRequest(repoId, mergeSha, fakeDotnet), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, CountRuns(counter));
    }

    /// <summary>
    /// Deterministic-red: an always-failing test command blocks as today,
    /// with the failing test named in the reason.
    /// </summary>
    [Fact]
    public async Task RealVerifier_DeterministicRed_BlocksGate()
    {
        var (gitHost, repoId, mergeSha) = await SetupBareRepoWithCommitAsync();
        var counter = CounterPath();
        var fakeDotnet = WriteFakeDotnet(
            "red",
            "printf 'Failed Ns.RedTest [1 s]\\nFailed! - Failed: 1, Passed: 0\\n'\nexit 1",
            counter);
        var verifier = new LocalGitPreMergeVerifier(
            gitHost,
            NullLogger<LocalGitPreMergeVerifier>.Instance,
            commandTimeout: TimeSpan.FromSeconds(60),
            admissionRerunsProvider: () => 3);

        var result = await verifier.VerifyAsync(TestRequest(repoId, mergeSha, fakeDotnet), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(PreMergeVerifyFailureMode.BuildOrTestFailed, result.FailureMode);
        Assert.Contains("Ns.RedTest", result.FailureReason);
        Assert.DoesNotContain("flaky", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, CountRuns(counter));
    }

    /// <summary>
    /// Flaky: a test command that fails on odd runs and passes on even runs
    /// must BLOCK the merge with a finding naming the test — it must not be
    /// waved through on a single green run.
    /// </summary>
    [Fact]
    public async Task RealVerifier_FlakyTest_BlocksGateAsNewFinding()
    {
        var (gitHost, repoId, mergeSha) = await SetupBareRepoWithCommitAsync();
        var counter = CounterPath();
        var fakeDotnet = WriteFakeDotnet(
            "flaky",
            "n=$(grep -c . '" + counter + "' 2>/dev/null || echo 0)\n" +
            "if [ $((n % 2)) -eq 0 ]; then printf 'Failed Ns.FlakyTest [1 s]\\nFailed! - Failed: 1, Passed: 0\\n'; exit 1; fi\n" +
            "printf 'Passed! - Failed: 0, Passed: 1\\n'\nexit 0",
            counter);
        var verifier = new LocalGitPreMergeVerifier(
            gitHost,
            NullLogger<LocalGitPreMergeVerifier>.Instance,
            commandTimeout: TimeSpan.FromSeconds(60),
            admissionRerunsProvider: () => 3);

        var result = await verifier.VerifyAsync(TestRequest(repoId, mergeSha, fakeDotnet), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(PreMergeVerifyFailureMode.BuildOrTestFailed, result.FailureMode);
        Assert.Contains("flaky", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ns.FlakyTest", result.FailureReason);
        Assert.Equal(3, CountRuns(counter));
    }

    /// <summary>
    /// The rerun count is config-driven, not hardcoded: a provider value of
    /// 2 runs the command exactly twice.
    /// </summary>
    [Fact]
    public async Task RealVerifier_RerunCountComesFromProviderNotSource()
    {
        var (gitHost, repoId, mergeSha) = await SetupBareRepoWithCommitAsync();
        var counter = CounterPath();
        var fakeDotnet = WriteFakeDotnet("green", "exit 0", counter);
        var verifier = new LocalGitPreMergeVerifier(
            gitHost,
            NullLogger<LocalGitPreMergeVerifier>.Instance,
            commandTimeout: TimeSpan.FromSeconds(60),
            admissionRerunsProvider: () => 2);

        var result = await verifier.VerifyAsync(TestRequest(repoId, mergeSha, fakeDotnet), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, CountRuns(counter));
    }

    /// <summary>
    /// The knob hot-reloads: the provider is read on every verification, so
    /// a config change between gates changes the next gate's run count.
    /// </summary>
    [Fact]
    public async Task RealVerifier_ProviderIsReadLiveOnEveryVerification()
    {
        var (gitHost, repoId, mergeSha) = await SetupBareRepoWithCommitAsync();
        var counter = CounterPath();
        var fakeDotnet = WriteFakeDotnet("green", "exit 0", counter);
        var reruns = 1;
        var verifier = new LocalGitPreMergeVerifier(
            gitHost,
            NullLogger<LocalGitPreMergeVerifier>.Instance,
            commandTimeout: TimeSpan.FromSeconds(60),
            admissionRerunsProvider: () => reruns);

        Assert.True((await verifier.VerifyAsync(TestRequest(repoId, mergeSha, fakeDotnet), CancellationToken.None)).Success);
        Assert.Equal(1, CountRuns(counter));

        reruns = 3;
        Assert.True((await verifier.VerifyAsync(TestRequest(repoId, mergeSha, fakeDotnet), CancellationToken.None)).Success);
        Assert.Equal(4, CountRuns(counter));
    }

    /// <summary>
    /// Reruns are scoped to test commands: a build command runs once even
    /// when the knob asks for more, bounding cost.
    /// </summary>
    [Fact]
    public async Task RealVerifier_NonTestArgv_RunsOnce()
    {
        var (gitHost, repoId, mergeSha) = await SetupBareRepoWithCommitAsync();
        var counter = CounterPath();
        var script = Path.Combine(_workspace, "build-" + Guid.NewGuid().ToString("N")[..8] + ".sh");
        await File.WriteAllTextAsync(
            script,
            "#!/bin/sh\necho run >> '" + counter + "'\nexit 0\n");
        MakeExecutable(script);
        var verifier = new LocalGitPreMergeVerifier(
            gitHost,
            NullLogger<LocalGitPreMergeVerifier>.Instance,
            commandTimeout: TimeSpan.FromSeconds(60),
            admissionRerunsProvider: () => 3);

        var result = await verifier.VerifyAsync(new PreMergeVerifyRequest
        {
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            RepositoryId = repoId,
            BaseBranch = "main",
            WorkBranch = "feature/x",
            MergeSha = mergeSha,
            Argv = [script],
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, CountRuns(counter));
    }

    private PreMergeVerifyRequest TestRequest(string repoId, string mergeSha, string fakeDotnet) =>
        new()
        {
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            RepositoryId = repoId,
            BaseBranch = "main",
            WorkBranch = "feature/x",
            MergeSha = mergeSha,
            Argv = [fakeDotnet, "test"],
        };

    private string CounterPath() => Path.Combine(_workspace, "runs-" + Guid.NewGuid().ToString("N")[..8] + ".log");

    private static int CountRuns(string counter) =>
        File.Exists(counter) ? File.ReadAllLines(counter).Length : 0;

    /// <summary>
    /// Writes an executable script literally named <c>dotnet</c> so the
    /// verifier's test-command detection fires while the behaviour stays
    /// fully scripted (no real SDK invocation). Every invocation appends
    /// one line to <paramref name="counter"/> so tests can assert the exact
    /// run count.
    /// </summary>
    private string WriteFakeDotnet(string tag, string body, string counter)
    {
        var dir = Path.Combine(_workspace, tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "dotnet");
        File.WriteAllText(
            path,
            "#!/bin/sh\necho run >> '" + counter + "'\n" + body + "\n");
        MakeExecutable(path);
        return path;
    }

    private static void MakeExecutable(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/chmod",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("+x");
        psi.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(TimeSpan.FromSeconds(30));
    }

    private async Task<(IGitHost GitHost, string RepoId, string MergeSha)> SetupBareRepoWithCommitAsync()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var repoId = await gitHost.EnsureRepositoryAsync(WorkItemId.New(), seed);
        var mergeSha = await gitHost.ResolveCommitAsync(repoId, "main", CancellationToken.None);
        return (gitHost, repoId, mergeSha);
    }
}
