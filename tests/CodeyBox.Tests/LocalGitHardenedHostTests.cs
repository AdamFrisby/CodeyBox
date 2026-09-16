using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Regression coverage for hosts that inject
/// <c>GIT_CONFIG_*=safe.bareRepository=explicit</c> (this repository's own
/// harness and audit sandboxes do): host-side git must keep working on
/// host-owned bare repositories instead of failing bare-repo discovery.
/// </summary>
public sealed class LocalGitHardenedHostTests : IDisposable
{
    private readonly string _workspace;
    private readonly Dictionary<string, string?> _savedGitEnv;

    public LocalGitHardenedHostTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-hardened-git-").FullName;
        _savedGitEnv = ImposeBareRepoExplicit();
    }

    public void Dispose()
    {
        foreach (var (key, value) in _savedGitEnv)
            Environment.SetEnvironmentVariable(key, value);
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task ResolveCommitAsync_WorksUnderBareRepoExplicitHardening()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var (_, expectedOut, _) = await TestSupport.RunGit(seed, "rev-parse", "main");

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos") },
            NullLogger<LocalGitHost>.Instance);
        var repoId = await gitHost.EnsureRepositoryAsync(WorkItemId.New(), seed, "main");

        var actual = await gitHost.ResolveCommitAsync(repoId, "main", CancellationToken.None);

        Assert.Equal(expectedOut.Trim(), actual);
    }

    [Fact]
    public void ApplyHostConfig_PassesBothPerInvocationFlags()
    {
        var psi = new ProcessStartInfo { FileName = "git" };

        LocalGitInvocation.ApplyHostConfig(psi, "/tmp/codeybox-no-hooks");

        Assert.Equal<string>(
            ["-c", "core.hooksPath=/tmp/codeybox-no-hooks", "-c", "safe.bareRepository=all"],
            psi.ArgumentList);
    }

    private static Dictionary<string, string?> ImposeBareRepoExplicit()
    {
        var saved = new Dictionary<string, string?>();
        for (var i = 0; i < 16; i++)
        {
            saved[$"GIT_CONFIG_KEY_{i}"] = Environment.GetEnvironmentVariable($"GIT_CONFIG_KEY_{i}");
            saved[$"GIT_CONFIG_VALUE_{i}"] = Environment.GetEnvironmentVariable($"GIT_CONFIG_VALUE_{i}");
        }
        saved["GIT_CONFIG_COUNT"] = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");

        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "safe.bareRepository");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "explicit");
        for (var i = 1; i < 16; i++)
        {
            Environment.SetEnvironmentVariable($"GIT_CONFIG_KEY_{i}", null);
            Environment.SetEnvironmentVariable($"GIT_CONFIG_VALUE_{i}", null);
        }
        return saved;
    }
}
