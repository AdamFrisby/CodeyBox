using System.Text;
using Microsoft.Extensions.Logging;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

// Mechanical-edit phase orchestration is split into this partial so the
// PipelineRunner's "core" file stays focused on cross-phase plumbing.
// Adding a new fixer is DI + config only (registry pattern); the only file
// that needs editing for phase-semantics changes is THIS one. The methods
// stay private to PipelineRunner because they reference fields/helpers
// (_gitHost, _sandboxes, BuildSandboxSpec, RunWithCancellation, etc.) that
// are tightly coupled to the runner's state — extracting to a separate
// class would force threading those dependencies through a constructor
// with no behavioral payoff.
public sealed partial class PipelineRunner
{
    private async Task RunMechanicalFixersAsync(
        WorkItem item,
        Project project,
        string repoId,
        string baseBranch,
        string workBranch,
        IReadOnlyList<IAuditor> auditors,
        int auditIteration,
        CancellationToken ct,
        CancellationToken hostShutdownToken)
    {
        var fixers = _mechanicalFixerComposer.Compose(project);
        if (fixers.Count == 0)
            return;

        using var phaseScope = BeginPhaseScope(item, "mechanical-edit");
        using var mechanicalPhase = new PhaseCancellation("mechanical-edit", ct, _opts.TimeProvider);
        mechanicalPhase.SetPhaseTimeout(project.Audit.PerIterationTimeout);
        mechanicalPhase.HookHostShutdown(hostShutdownToken, _opts.ShutdownGrace);
        var phaseCt = mechanicalPhase.Token;

        try
        {
            var access = _gitHost.GetSandboxAccess(repoId);
            var readOnlyAccess = access with
            {
                Mounts = access.Mounts.Select(MakeReadOnlyRepositoryMount).ToList(),
            };
            var sandboxTarget = SandboxTargetResolver.ResolveAudit(project.NetworkProfiles.AuditTool, AuditCapabilities.None);
            var spec = BuildSandboxSpec(
                readOnlyAccess,
                includeAgentCredential: null,
                allowAgentNetwork: false,
                hostNetworkProfile: sandboxTarget.NetworkProfile,
                timingWorkItemId: item.Id,
                timingPhase: "mechanical-edit",
                flavor: sandboxTarget.Flavor,
                baselineImageRef: SandboxTargetResolver.BaselineRefForTarget(project, sandboxTarget, item.BaselineImageRef));

            await using var sandbox = await _sandboxes.CreateAsync(spec, phaseCt);
            await RunWithCancellation(sandbox, phaseCt, "git", "clone", readOnlyAccess.CloneUrlInsideSandbox, SandboxConventions.WorkDir);
            await RunWithCancellation(
                sandbox,
                phaseCt,
                "git",
                "-C",
                SandboxConventions.WorkDir,
                "checkout",
                "-B",
                workBranch,
                $"origin/{workBranch}");

            var (gitName, gitEmail) = ResolveGitIdentity(project, _opts.HostGitIdentity);
            await RunWithCancellation(sandbox, phaseCt, "git", "-C", SandboxConventions.WorkDir, "config", "user.name", gitName);
            await RunMasked(sandbox, phaseCt, "git", "-C", SandboxConventions.WorkDir, "config", "user.email", gitEmail);

            var ctx = new MechanicalFixerContext(
                item.Id,
                workBranch,
                baseBranch,
                auditIteration,
                project.Id.Value,
                BuildMechanicalFixerInputs(auditors));

            var changedFixers = new List<IMechanicalFixer>();
            foreach (var fixer in fixers)
            {
                var result = await fixer.ApplyAsync(sandbox, SandboxConventions.WorkDir, ctx, phaseCt);
                if (result.Changed)
                    changedFixers.Add(fixer);

                _log.LogInformation(
                    "Mechanical fixer {FixerName} completed for work item {WorkItemId} audit iteration {AuditIteration}: changed={Changed}; {Summary}",
                    fixer.Name,
                    item.Id,
                    auditIteration,
                    result.Changed,
                    result.Summary ?? "(no summary)");
            }

            var status = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "status", "--porcelain", "--untracked-files=no"],
            }, phaseCt);
            if (!status.Success)
                throw new MechanicalFixerException($"mechanical-edit could not read git status: {status.Stderr}");
            if (string.IsNullOrWhiteSpace(status.Stdout))
                return;

            await RunWithCancellation(sandbox, phaseCt, "git", "-C", SandboxConventions.WorkDir, "add", "-u");
            var staged = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "diff", "--cached", "--quiet"],
            }, phaseCt);
            if (staged.ExitCode == 0)
                return;
            if (staged.ExitCode != 1)
                throw new MechanicalFixerException($"mechanical-edit could not inspect staged diff: {staged.Stderr}");

            var revision = await ResolveMechanicalPromptRevisionForCommitAsync(item, auditIteration, phaseCt);
            var commitFixers = changedFixers.Count == 0 ? fixers : changedFixers;
            var fixerNames = changedFixers.Count == 0
                ? string.Join("+", fixers.Select(f => f.Name))
                : string.Join("+", changedFixers.Select(f => f.Name));
            var trailerBlock = CodeyBoxTrailers.ComposeMechanical(
                item.Id,
                fixerNames,
                promptRevisionAtDispatch: revision);
            var subject = commitFixers.Count == 1 && !string.IsNullOrWhiteSpace(commitFixers[0].CommitSubject)
                ? commitFixers[0].CommitSubject.Trim()
                : MechanicalFixerCommitSubjects.Default;
            var commitMessage = $"{subject}\n\n{trailerBlock}";

            await using (var commitScope = await TimingScope.BeginAsync(
                _timings,
                item.Id,
                "mechanical-edit",
                "git.commit",
                activitySource: CodeyBoxActivities.Sandbox,
                log: _log))
            {
                await RunWithCancellation(sandbox, phaseCt, "git", "-C", SandboxConventions.WorkDir, "commit", "-m", commitMessage);
            }

            var patch = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "diff", "--binary", "HEAD^", "HEAD"],
                MaxStdoutBytes = MechanicalEditLimits.PatchCaptureMaxBytes,
                MaxStderrBytes = MechanicalEditLimits.GitDiagnosticCaptureMaxBytes,
            }, phaseCt);
            if (patch.OutputLimitExceeded)
            {
                throw new MechanicalFixerException(
                    $"mechanical-edit formatter commit diff exceeded the {MechanicalEditLimits.PatchCaptureMaxBytes} byte patch cap; rejecting oversized mechanical commit");
            }

            if (!patch.Success || string.IsNullOrWhiteSpace(patch.Stdout))
            {
                throw new MechanicalFixerException(
                    $"mechanical-edit could not export formatter commit diff (exit {patch.ExitCode}): {patch.Stderr}");
            }

            await using (var pushScope = await TimingScope.BeginAsync(
                _timings,
                item.Id,
                "mechanical-edit",
                "git.push_back_to_bare_repo",
                activitySource: CodeyBoxActivities.Sandbox,
                log: _log))
            {
                await ImportMechanicalCommitPatchAsync(
                    project,
                    item,
                    repoId,
                    workBranch,
                    patch.Stdout,
                    commitMessage,
                    gitName,
                    gitEmail,
                    phaseCt);
            }
        }
        catch (OperationCanceledException oce) when (oce is not PhaseCancellationException)
        {
            throw mechanicalPhase.Wrap(oce);
        }
        catch (MechanicalFixerException)
        {
            throw;
        }
        catch (SandboxDiskDeferredException)
        {
            throw;
        }
        catch (SandboxProvisioningDeferredException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MechanicalFixerException($"mechanical-edit failed: {ex.Message}", ex);
        }
    }

    private static SandboxMount MakeReadOnlyRepositoryMount(SandboxMount mount)
        => mount.HostPath is null || mount.Tmpfs
            ? mount
            : mount with { ReadOnly = true };

    private IReadOnlyList<IMechanicalFixerInput> BuildMechanicalFixerInputs(
        IReadOnlyList<IAuditor> auditors)
    {
        if (_mechanicalFixerInputProviders.Count == 0)
            return [];

        var inputs = new List<IMechanicalFixerInput>();
        foreach (var provider in _mechanicalFixerInputProviders)
            inputs.AddRange(provider.BuildInputs(auditors));

        return inputs;
    }

    private async Task<int?> ResolveMechanicalPromptRevisionForCommitAsync(
        WorkItem item,
        int auditIteration,
        CancellationToken ct)
    {
        var dispatchedRevision = await TryLookupIterationRevisionAsync(item.Id, auditIteration, ct) ?? item.PromptRevision;
        WorkItem? freshItem;
        try
        {
            freshItem = await _store.GetAsync(item.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex,
                "Failed to re-read work item {Id} during mechanical commit trailer composition; using in-memory snapshot",
                item.Id);
            freshItem = item;
        }

        var currentRevision = freshItem?.PromptRevision ?? item.PromptRevision;
        if (currentRevision == dispatchedRevision)
            return dispatchedRevision;

        _log.LogInformation(
            "Work item {Id}: omitting {Trailer} from mechanical commit because dispatched revision {Dispatched} differs from current {Current}; auditor will preserve the stale-prompt signal",
            item.Id,
            CodeyBoxTrailers.PromptRevisionTrailerKey,
            dispatchedRevision,
            currentRevision);
        return null;
    }

    private async Task ImportMechanicalCommitPatchAsync(
        Project project,
        WorkItem item,
        string repoId,
        string workBranch,
        string patch,
        string commitMessage,
        string gitName,
        string gitEmail,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(patch))
            throw new MechanicalFixerException("mechanical-edit produced an empty patch");
        if (Encoding.UTF8.GetByteCount(patch) > MechanicalEditLimits.PatchCaptureMaxBytes)
        {
            throw new MechanicalFixerException(
                $"mechanical-edit formatter commit diff exceeded the {MechanicalEditLimits.PatchCaptureMaxBytes} byte patch cap; rejecting oversized mechanical commit");
        }

        try
        {
            var access = _gitHost.GetSandboxAccess(repoId);
            var sandboxTarget = SandboxTargetResolver.ResolveAudit(project.NetworkProfiles.AuditTool, AuditCapabilities.None);
            var spec = BuildSandboxSpec(
                access,
                includeAgentCredential: null,
                allowAgentNetwork: false,
                hostNetworkProfile: sandboxTarget.NetworkProfile,
                timingWorkItemId: item.Id,
                timingPhase: "mechanical-edit",
                flavor: sandboxTarget.Flavor,
                baselineImageRef: SandboxTargetResolver.BaselineRefForTarget(project, sandboxTarget, item.BaselineImageRef));

            await using var sandbox = await _sandboxes.CreateAsync(spec, ct);
            await RunWithCancellation(sandbox, ct, "git", "clone", access.CloneUrlInsideSandbox, SandboxConventions.WorkDir);
            await RunWithCancellation(
                sandbox,
                ct,
                "git",
                "-C",
                SandboxConventions.WorkDir,
                "checkout",
                "-B",
                workBranch,
                $"origin/{workBranch}");
            await RunWithCancellation(sandbox, ct, "git", "-C", SandboxConventions.WorkDir, "config", "user.name", gitName);
            await RunMasked(sandbox, ct, "git", "-C", SandboxConventions.WorkDir, "config", "user.email", gitEmail);

            const string patchPath = "/tmp/codeybox-mechanical.patch";
            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", patchPath],
                Stdin = patch,
            }, ct);
            if (!write.Success)
                throw new MechanicalFixerException($"mechanical-edit could not materialize formatter patch: {write.Stderr}");

            await RunWithCancellation(sandbox, ct, "git", "-C", SandboxConventions.WorkDir, "apply", "--index", patchPath);
            await RunWithCancellation(sandbox, ct, "git", "-C", SandboxConventions.WorkDir, "commit", "-m", commitMessage);
            await PushSandboxWorkBranchWithReconcileAsync(sandbox, workBranch, ct);
        }
        catch (MechanicalFixerException)
        {
            throw;
        }
        catch (SandboxDiskDeferredException)
        {
            throw;
        }
        catch (SandboxProvisioningDeferredException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MechanicalFixerException($"mechanical-edit could not import formatter commit to '{workBranch}': {ex.Message}", ex);
        }
    }
}
