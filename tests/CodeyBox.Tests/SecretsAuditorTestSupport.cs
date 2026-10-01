using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// The per-tool constants the shared secrets-auditor scaffolding varies by.
/// Each test class declares one; the probe classifiers then encode the
/// shared secrets-auditor base's exec surface once instead of drifting
/// across per-tool copies.
/// </summary>
internal sealed record SecretsAuditorTestProfile(
    string Tool,
    string PluginId,
    string PluginDisplayName,
    string PluginAssemblyFileName,
    string ExpectedVersion,
    IReadOnlyList<string> WorktreeSuppressionFiles);

/// <summary>
/// Shared scaffolding for the gitleaks-compatible secrets auditor tests
/// (<see cref="GitleaksAuditorTests"/>, <see cref="BetterleaksAuditorTests"/>):
/// the probe-argv classifiers, the scripted sandbox, fixture repositories,
/// SARIF fixtures, and plugin-context wiring, parameterized by a
/// <see cref="SecretsAuditorTestProfile"/>.
/// </summary>
internal static class SecretsAuditorTestSupport
{
    /// <summary>
    /// The path glob the secrets profiles declare for the ruleset-exempted
    /// <c>gitleaks.toml</c> filename family — the worktree find probe's sole
    /// operand and the substring the history probe's pathspec carries.
    /// </summary>
    public const string ScanExemptedPathGlob = "*gitleaks.toml*";

    /// <summary>
    /// SARIF shaped like the tools' <c>--report-format sarif
    /// --report-path -</c> output: no per-result "level" (the shared parser
    /// substitutes "warning"), a ruleId, a message naming rule/file/commit,
    /// and the first physical location's artifact uri plus region.startLine.
    /// The driver semanticVersion is the constant "v8.0.0" both tools stamp
    /// — intentionally not the release version, which is why the plugins
    /// probe <c>&lt;tool&gt; version</c>.
    /// </summary>
    public static string SarifWithFinding(
        string toolName,
        string informationUri,
        string ruleId,
        string ruleTitle,
        string file,
        int startLine)
        => $$"""
            {
              "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
              "version": "2.1.0",
              "runs": [{
                "tool": {
                  "driver": {
                    "name": "{{toolName}}",
                    "semanticVersion": "v8.0.0",
                    "informationUri": "{{informationUri}}",
                    "rules": [{ "id": "{{ruleId}}", "shortDescription": { "text": "{{ruleTitle}}" } }]
                  }
                },
                "results": [{
                  "message": { "text": "{{ruleId}} has detected secret for file {{file}} at commit 0123456789abcdef." },
                  "ruleId": "{{ruleId}}",
                  "locations": [{
                    "physicalLocation": {
                      "artifactLocation": { "uri": "{{file}}" },
                      "region": { "startLine": {{startLine}}, "startColumn": 10, "endLine": {{startLine}}, "endColumn": 65, "snippet": { "text": "REDACTED" } }
                    }
                  }],
                  "partialFingerprints": { "commitSha": "0123456789abcdef", "email": "a@b.c", "author": "a", "date": "2026-01-01", "commitMessage": "x" },
                  "properties": { "tags": [] }
                }]
              }]
            }
            """;

    public static string SarifCleanReport(string toolName)
        => $$"""
            {
              "version": "2.1.0",
              "runs": [{
                "tool": { "driver": { "name": "{{toolName}}", "semanticVersion": "v8.0.0", "rules": [] } },
                "results": []
              }]
            }
            """;

    public static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    public static bool IsVersionProbe(SandboxExec exec, SecretsAuditorTestProfile profile)
        => exec.Argv.Count == 2 && exec.Argv[0] == profile.Tool && exec.Argv[1] == "version";

    public static bool IsSuppressionProbe(SandboxExec exec, SecretsAuditorTestProfile profile)
        => IsWorktreeSuppressionProbe(exec, profile)
            || IsPathGlobProbe(exec)
            || IsHistorySuppressionProbe(exec);

    public static bool IsWorktreeSuppressionProbe(SandboxExec exec, SecretsAuditorTestProfile profile)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && profile.WorktreeSuppressionFiles.All(
                f => exec.Argv.Contains(f, StringComparer.Ordinal));

    // The any-depth worktree probe rides a find -path script with the
    // declared glob as its only operand.
    public static bool IsPathGlobProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv.Contains(ScanExemptedPathGlob, StringComparer.Ordinal);

    public static bool IsHistorySuppressionProbe(SandboxExec exec)
        => exec.Argv.Count >= 2
            && exec.Argv[0] == "git"
            && exec.Argv.Any(static a => a.Contains("gitleaks.toml", StringComparison.Ordinal));

    public static bool IsRealpathProbe(SandboxExec exec)
        => exec.Argv.Count == 5
            && exec.Argv[0] == "realpath"
            && exec.Argv[1] == "-m";

    // Emulates `realpath -m -- <arg> .`: one line for the canonicalized
    // configured path, one for the canonicalized exec working directory.
    // insideWorktree maps a relative operand under the /work scan root.
    public static SandboxExecResult RealpathResult(SandboxExec exec, bool insideWorktree)
    {
        var arg = exec.Argv[3];
        var canonical = insideWorktree && !arg.StartsWith("/", StringComparison.Ordinal)
            ? "/work/" + arg
            : arg;
        return new SandboxExecResult(0, canonical + "\n/work\n", "");
    }

    public static SandboxExecResult Ok(SandboxExec exec, SecretsAuditorTestProfile profile)
        => IsVersionProbe(exec, profile)
            ? new SandboxExecResult(0, profile.ExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    public static ISandbox HealthyTool(
        SecretsAuditorTestProfile profile, int scanExit, string scanStdout)
        => FakeSandbox((exec, _) => Task.FromResult(
            IsPresenceProbe(exec) || IsVersionProbe(exec, profile) || IsSuppressionProbe(exec, profile)
                ? Ok(exec, profile)
                : new SandboxExecResult(scanExit, scanStdout, "")));

    public static ISandbox FakeSandbox(
        Func<SandboxExec, CancellationToken, Task<SandboxExecResult>> onExec)
        => new DelegatingSandbox(onExec);

    public static PluginContext BuildPluginContext(
        SecretsAuditorTestProfile profile, IReadOnlyDictionary<string, string?> scopedValues)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(scopedValues)
            .Build();
        return new PluginContext(
            HostApiVersion: "1.0",
            PluginId: profile.PluginId,
            PluginDisplayName: profile.PluginDisplayName,
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    public static string PluginAssemblyPath(SecretsAuditorTestProfile profile)
    {
        var path = Path.Combine(AppContext.BaseDirectory, profile.PluginAssemblyFileName);
        Assert.True(File.Exists(path), $"Plugin assembly not found at '{path}'.");
        return path;
    }

    public static async Task<string> SeedFixtureRepoAsync(
        SecretsAuditorTestProfile profile, string? secretLine)
    {
        var repo = Path.Combine(
            Path.GetTempPath(),
            "codeybox-" + profile.Tool + "-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        await TestSupport.RunGit(repo, "init", "-b", "main");
        await TestSupport.RunGit(repo, "config", "user.email", "t@l");
        await TestSupport.RunGit(repo, "config", "user.name", "T");
        var (file, content) = secretLine is null
            ? ("README.md", "clean\n")
            : ("secrets.txt", secretLine + "\n");
        await File.WriteAllTextAsync(Path.Combine(repo, file), content);
        await TestSupport.RunGit(repo, "add", "-A");
        await TestSupport.RunGit(repo, "commit", "-m", "seed");
        return repo;
    }

    /// <summary>
    /// Commits <paramref name="content"/> at <paramref name="repoRelativePath"/>
    /// and then deletes it, so the path exists only in git history — the
    /// fixture shape for the ruleset-exempted-path history gate.
    /// </summary>
    public static async Task CommitThenDeleteAsync(
        string repo, string repoRelativePath, string content)
    {
        var fullPath = Path.Combine(repo, repoRelativePath);
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is not null)
            Directory.CreateDirectory(parent);
        await File.WriteAllTextAsync(fullPath, content);
        await TestSupport.RunGit(repo, "add", "-A");
        await TestSupport.RunGit(repo, "commit", "-m", "add " + repoRelativePath);
        File.Delete(fullPath);
        await TestSupport.RunGit(repo, "add", "-A");
        await TestSupport.RunGit(repo, "commit", "-m", "drop " + repoRelativePath);
    }

    /// <summary>
    /// Commits <paramref name="content"/> at <paramref name="repoRelativePath"/>
    /// on a side branch merged back with <c>-s ours</c>, so the merge is
    /// TREESAME to the mainline parent and default history simplification
    /// prunes the commit — the fixture shape that requires the gate's
    /// <c>--full-history</c> traversal.
    /// </summary>
    public static async Task CommitMergeHiddenAsync(
        string repo, string repoRelativePath, string content)
    {
        await TestSupport.RunGit(repo, "checkout", "-b", "side");
        var fullPath = Path.Combine(repo, repoRelativePath);
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is not null)
            Directory.CreateDirectory(parent);
        await File.WriteAllTextAsync(fullPath, content);
        await TestSupport.RunGit(repo, "add", "-A");
        await TestSupport.RunGit(repo, "commit", "-m", "add " + repoRelativePath);
        await TestSupport.RunGit(repo, "checkout", "main");
        await TestSupport.RunGit(repo, "merge", "-s", "ours", "side", "-m", "merge side");
        await TestSupport.RunGit(repo, "branch", "-D", "side");
    }

    public static string? ProbeInstalledToolVersion(SecretsAuditorTestProfile profile)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = profile.Tool,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("version");
            using var process = Process.Start(psi)!;
            // Drain both pipes concurrently and bound the wait BEFORE reading
            // the buffered output: a synchronous ReadToEnd blocks until the
            // child closes the pipe, so a hung `<tool> version` — or one
            // blocked writing to a full stderr pipe — would never reach the
            // timeout.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            var version = stdoutTask.GetAwaiter().GetResult().Trim();
            return process.ExitCode == 0 && version.Length > 0 ? version : null;
        }
        catch
        {
            // Any failure means no usable binary on PATH — the gated tests
            // skip rather than fail on a host without the tool.
            return null;
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort fixture cleanup */ }
    }

    public static AuditContext FakeContext()
        => new(WorkItemId.New(), "feature", "main", 1, "do x");

    private sealed class TestPluginHost(IConfigurationSection scoped) : IPluginHost
    {
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IConfigurationSection ScopedConfig { get; } = scoped;
    }

    private sealed class DelegatingSandbox(
        Func<SandboxExec, CancellationToken, Task<SandboxExecResult>> onExec) : ISandbox
    {
        public string Id => "fake";

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return await onExec(exec, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
