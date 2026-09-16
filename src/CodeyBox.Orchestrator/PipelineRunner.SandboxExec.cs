using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

// PipelineRunner.SandboxExec.cs — Sandbox/host exec glue: spec building, credential materialization, git capture, push reconcile, and masked runs.
public sealed partial class PipelineRunner
{
    private SandboxSpec BuildSandboxSpec(
        SandboxRepositoryAccess access,
        AgentCredential? includeAgentCredential,
        bool allowAgentNetwork,
        string? hostNetworkProfile = null,
        WorkItemId? timingWorkItemId = null,
        string? timingPhase = null,
        SandboxProfileFlavor flavor = SandboxProfileFlavor.Headless,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        string? baselineImageRef = null,
        IReadOnlyList<SandboxMount>? additionalCredentialMounts = null,
        bool includeCredentialsTmpfs = false,
        bool includeAgentTurnScratchpadTmpfs = false,
        bool agentCredentialScope = false,
        IAgentRunner? credentialRunner = null)
    {
        var mounts = new List<SandboxMount>(access.Mounts)
        {
            new() { SandboxPath = SandboxConventions.WorkDir, Tmpfs = true },
        };

        var env = new Dictionary<string, string>();
        if (includeAgentCredential is not null)
        {
            if (credentialRunner is null)
            {
                throw new ArgumentException(
                    "A credential runner is required when a sandbox includes an agent credential.",
                    nameof(credentialRunner));
            }
            mounts.Add(new SandboxMount
            {
                SandboxPath = SandboxConventions.CredentialsDir,
                Tmpfs = true,
                SizeBytes = SandboxConventions.CredentialsTmpfsBytes,
            });
            var directEnvironment = SandboxEnvironmentVariablePolicy.SelectDirectCredentialEnvironment(
                includeAgentCredential,
                credentialRunner,
                nameof(includeAgentCredential.EnvironmentVariables));
            foreach (var (k, v) in directEnvironment)
                env[k] = v;
            foreach (var m in includeAgentCredential.Mounts)
                mounts.Add(m);
        }
        else if (includeCredentialsTmpfs)
        {
            mounts.Add(new SandboxMount
            {
                SandboxPath = SandboxConventions.CredentialsDir,
                Tmpfs = true,
                SizeBytes = SandboxConventions.CredentialsTmpfsBytes,
            });
        }
        if (includeAgentTurnScratchpadTmpfs)
        {
            mounts.Add(new SandboxMount
            {
                SandboxPath = SandboxConventions.AgentTurnScratchpadDir,
                Tmpfs = true,
                SizeBytes = SandboxConventions.AgentTurnScratchpadTmpfsBytes,
            });
        }
        if (additionalCredentialMounts is not null)
            mounts.AddRange(additionalCredentialMounts);
        var distinctMounts = new List<SandboxMount>(mounts.Count);
        var mountsByDestination = new Dictionary<string, SandboxMount>(StringComparer.Ordinal);
        foreach (var mount in mounts)
        {
            if (mountsByDestination.TryGetValue(mount.SandboxPath, out var existing))
            {
                if (existing != mount)
                    throw new InvalidOperationException($"Sandbox mounts conflict at destination '{mount.SandboxPath}'.");
                continue;
            }
            mountsByDestination.Add(mount.SandboxPath, mount);
            distinctMounts.Add(mount);
        }
        if (extraEnvironment is not null)
        {
            // Extra env overrides credential env on key collision so the
            // orchestrator can stamp known-good values (e.g. revision counters)
            // without a credential provider silently shadowing them.
            foreach (var (k, v) in extraEnvironment)
                env[k] = v;
        }
        DotnetCliHomeConventions.ApplyIfAbsent(env, SandboxConventions.WorkDir);
        var allowedHosts = allowAgentNetwork
            ? includeAgentCredential is null && !agentCredentialScope
                ? _opts.AuditToolAllowedHosts
                : _opts.AgentAllowedHosts
            : Array.Empty<string>();
        if (access.Network.AllowedHosts.Count > 0)
        {
            allowedHosts = allowedHosts
                .Concat(access.Network.AllowedHosts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        var net = new SandboxNetworkPolicy
        {
            AllowedHosts = allowedHosts,
            HostGitEndpoint = access.Network.HostGitEndpoint,
            ProfileName = hostNetworkProfile,
        };

        return SandboxConventions.WithTimingEnvironment(new SandboxSpec
        {
            ImageReference = _opts.SandboxImageReference,
            Mounts = distinctMounts,
            Environment = env,
            Network = net,
            Flavor = flavor,
            WorkingDirectory = SandboxConventions.WorkDir,
            TimingWorkItemId = timingWorkItemId,
            TimingPhase = timingPhase,
            BaselineImageRef = baselineImageRef,
        });
    }

    private static async Task MaterialiseCredentialFilesAsync(ISandbox sandbox, AgentCredential credential, CancellationToken ct)
    {
        SandboxCredentialFileWriter.ValidateMaterializationPlan(credential, []);
        foreach (var (relativePath, contents) in credential.Files)
        {
            var safePath = SanitiseCredentialFileName(relativePath);
            await SandboxCredentialFileWriter.WriteAsync(
                sandbox,
                new SandboxCredentialFileTarget(SandboxCredentialFileRoot.CredentialsDirectory, safePath),
                contents,
                SandboxCredentialOverwritePolicy.Overwrite,
                ct).ConfigureAwait(false);
        }
    }

    private static async Task Run(ISandbox sandbox, params string[] argv)
    {
        var r = await sandbox.ExecAsync(new SandboxExec { Argv = argv });
        if (r.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(r.ExitCode);
        if (!r.Success)
            throw new InvalidOperationException($"command failed (exit {r.ExitCode}): {string.Join(' ', argv)}\n{r.Stderr}");
    }

    private async Task RunHostGitAsync(string workdir, CancellationToken ct, params string[] args)
    {
        var (stdout, stderr, exitCode) = await RunHostGitCaptureNoThrowAsync(workdir, ct, args);
        if (exitCode != 0)
            throw new InvalidOperationException($"host git command failed (exit {exitCode}): git {string.Join(' ', args)}\n{stderr}{stdout}");
    }

    /// <summary>
    /// Runs a host-side git command and returns its captured stdout/stderr.
    /// Throws on non-zero exit. Conflict-rework helpers (rev-list, etc.) call
    /// this when they need the command's output rather than just success.
    /// </summary>
    private async Task<(string Stdout, string Stderr)> RunHostGitCaptureAsync(
        string workdir, CancellationToken ct, params string[] args)
    {
        var (stdout, stderr, exitCode) = await RunHostGitCaptureNoThrowAsync(workdir, ct, args);
        if (exitCode != 0)
            throw new InvalidOperationException($"host git command failed (exit {exitCode}): git {string.Join(' ', args)}\n{stderr}{stdout}");
        return (stdout, stderr);
    }

    /// <summary>
    /// Runs a host-side git command and captures stdout, stderr, and the exit
    /// code without throwing. Used for probes like <c>merge-base --is-ancestor</c>
    /// where a non-zero exit is a meaningful answer rather than an error.
    /// </summary>
    private async Task<(string Stdout, string Stderr, int ExitCode)> RunHostGitCaptureNoThrowAsync(
        string workdir, CancellationToken ct, params string[] args)
    {
        SanitizeBareRepositoryConfigIfPresent(workdir);
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Host-owned bare repos (merge-ancestry probes target the bare host
        // repo): same per-invocation hardening relaxation as LocalGitHost.
        LocalGitInvocation.ApplyHostConfig(psi, _disabledHostHooksPath);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (stdout, stderr, process.ExitCode);
    }

    private static void SanitizeBareRepositoryConfigIfPresent(string workdir)
    {
        if (!Directory.Exists(workdir)
            || !File.Exists(Path.Combine(workdir, "HEAD"))
            || !Directory.Exists(Path.Combine(workdir, "objects"))
            || !File.Exists(Path.Combine(workdir, "config")))
        {
            return;
        }

        var configPath = Path.Combine(workdir, "config");
        var tempPath = Path.Combine(workdir, "config.codeybox-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(
            tempPath,
            """
            [core]
                repositoryformatversion = 0
                filemode = true
                bare = true

            """);
        File.Move(tempPath, configPath, overwrite: true);
    }

    private async Task PushSandboxWorkBranchWithReconcileAsync(ISandbox sandbox, string branch, CancellationToken ct)
    {
        string[] pushArgv = ["git", "-C", SandboxConventions.WorkDir, "push", "origin", $"{branch}:{branch}"];
        var push = await sandbox.ExecAsync(new SandboxExec { Argv = pushArgv }, ct);
        if (push.Success)
            return;

        if (!IsNonFastForwardRejection(push.Stdout, push.Stderr))
            throw CommandFailed(push, pushArgv);

        _log.LogWarning(
            "Sandbox push of work branch {Branch} was rejected as non-fast-forward; fetching and rebasing once",
            branch);

        var fetch = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "fetch", "--no-tags", "origin",
                $"+refs/heads/{branch}:refs/remotes/origin/{branch}"],
        }, ct);
        if (!fetch.Success)
            throw new InvalidOperationException(
                $"sandbox push reconcile fetch failed for branch '{branch}': {fetch.Stderr}");

        var rebase = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir,
                "-c", "user.name=CodeyBox",
                "-c", "user.email=codeybox@localhost",
                "rebase", $"origin/{branch}"],
        }, ct);
        if (!rebase.Success)
        {
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "rebase", "--abort"],
            }, CancellationToken.None);
            throw new SandboxPushReconcileConflictException(branch, "rebase");
        }

        push = await sandbox.ExecAsync(new SandboxExec { Argv = pushArgv }, ct);
        if (!push.Success)
            throw new InvalidOperationException(
                $"sandbox push of work branch '{branch}' failed after reconcile: {push.Stderr}");
    }

    private static bool IsNonFastForwardRejection(string stdout, string stderr)
    {
        var output = stdout + "\n" + stderr;
        return output.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            || output.Contains("! [rejected]", StringComparison.OrdinalIgnoreCase)
            || output.Contains("fetch first", StringComparison.OrdinalIgnoreCase);
    }

    private static InvalidOperationException CommandFailed(SandboxExecResult result, IReadOnlyList<string> argv)
        => new($"command failed (exit {result.ExitCode}): {string.Join(' ', argv)}\n{result.Stderr}");

    private static async Task RunWithCancellation(ISandbox sandbox, CancellationToken ct, params string[] argv)
    {
        var r = await sandbox.ExecAsync(new SandboxExec { Argv = argv }, ct);
        if (r.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(r.ExitCode);
        if (!r.Success)
            throw new InvalidOperationException($"command failed (exit {r.ExitCode}): {string.Join(' ', argv)}\n{r.Stderr}");
    }

    // Best-effort recovery of a COW-inherited, root-owned per-user NuGet home run
    // once when preparing a tool-audit sandbox, before its `dotnet build`/`test`/
    // `format` gates. A broken home otherwise aborts restore with "Failed to read
    // NuGet.Config due to unauthorized access". The branch's MSBuild InitialTargets
    // hook (Directory.NuGetHomeHeal.targets) already heals every `dotnet build`/
    // `test` invocation on its own; this setup step is a complementary safety net
    // that also covers gate commands which do not evaluate those props (e.g. a bare
    // `dotnet restore` or `dotnet format`) and does the repair once so the shared
    // sandbox's later gates inherit a healthy home. It dot-sources the checked-out
    // branch's own repository-owned recovery (scripts/nuget-home-heal.sh), whose
    // on-disk repair persists for every gate sharing the sandbox; the trailing
    // `true` keeps the step best-effort so a missing script or unhealable home
    // never masks the real gate error. This adds no capability the audit sandbox
    // lacks — it already runs the branch's arbitrary build logic via `dotnet build`
    // in this same credential-free sandbox — and is a no-op when the home is
    // already usable.
    private static Task HealAuditNuGetHomeAsync(ISandbox sandbox, CancellationToken ct)
        => RunWithCancellation(
            sandbox,
            ct,
            "sh",
            "-c",
            "cd \"$1\" 2>/dev/null && [ -f scripts/nuget-home-heal.sh ] && "
                + ". ./scripts/nuget-home-heal.sh; true",
            "sh",
            SandboxConventions.WorkDir);

    private static void ThrowIfExecutionUnavailable(SandboxExecResult result)
    {
        if (result.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(result.ExitCode);
    }


    // Runs a command but replaces the last argv element with "***" in any exception message,
    // used when the last element is a sensitive value (e.g. user.email) that must not reach
    // audit-tier logs.
    private static async Task RunMasked(ISandbox sandbox, params string[] argv)
    {
        await RunMasked(sandbox, CancellationToken.None, argv);
    }

    private static async Task RunMasked(ISandbox sandbox, CancellationToken ct, params string[] argv)
    {
        var r = await sandbox.ExecAsync(new SandboxExec { Argv = argv }, ct);
        if (!r.Success)
        {
            var masked = argv.Length > 0
                ? argv[..^1].Append("***").ToArray()
                : argv;
            throw new InvalidOperationException($"command failed (exit {r.ExitCode}): {string.Join(' ', masked)}\n{r.Stderr}");
        }
    }

    private static string SanitiseCredentialFileName(string path)
    {
        SandboxCredentialFileWriter.ValidateRelativePath(path, nameof(path));
        return path;
    }

    // ── Stuck-probe integration ──────────────────────────────────────────────

}
