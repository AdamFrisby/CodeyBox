using System.Diagnostics;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Git;
using LibGit2Sharp;

namespace CodeyBox.Api;

internal static class WorkItemDossierEndpoints
{
    internal const int MaxChangedFilesListed = 200;

    public static void Map(WebApplication app)
    {
        app.MapGet("/workitems/{id}/dossier", GetDossierAsync);
    }

    /// <summary>
    /// GET /workitems/{id}/dossier — one shareable dossier per item: the change,
    /// per-auditor reports, build/test gates, cost and timing evidence, and the
    /// publication legs (local merge vs open PR vs merged PR) as distinct facts.
    /// Assembles only existing stores and the git host; it invents no evidence:
    /// legs that never ran report <c>not_run</c>, never <c>pass</c>.
    /// </summary>
    private static async Task<IResult> GetDossierAsync(
        string id,
        IWorkItemStore store,
        IProjectRepository projects,
        IAuditReportStore reportStore,
        IWorkItemCostStore costs,
        ITimingStore timings,
        LocalGitHost gitHost,
        IUpstreamRemoteFactory upstreams,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g))
            return Results.BadRequest(new { error = "invalid id" });
        var workItemId = new WorkItemId(g);

        var item = await store.GetAsync(workItemId, ct);
        if (item is null)
            return Results.NotFound();

        var reports = await reportStore.GetByWorkItemAsync(item.Id.ToString(), ct);
        var costRows = await costs.GetByWorkItemAsync(item.Id.ToString(), ct);
        var timingRows = await timings.GetByWorkItemAsync(workItemId, ct);
        var iterations = await store.GetIterationsAsync(workItemId, ct);

        var diff = await TryReadDiffAsync(gitHost, item, projects, ct);

        string? prStatus = null;
        string? prMergeSha = null;
        if (item.MergedPrNumber is > 0)
        {
            var project = await projects.GetAsync(item.ProjectId, ct);
            if (project is not null)
            {
                try
                {
                    var pr = await upstreams.Create(project)
                        .GetPullRequestAsync(item.MergedPrNumber.Value, ct);
                    if (pr is not null)
                    {
                        prStatus = pr.Status.ToString().ToLowerInvariant();
                        prMergeSha = pr.MergeCommitSha;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger("CodeyBox.Api.WorkItemDossierEndpoints").LogWarning(
                        ex,
                        "Failed to read upstream PR {PullRequestNumber} for dossier {WorkItemId}",
                        item.MergedPrNumber,
                        item.Id);
                }
            }
        }

        var dossier = WorkItemDossierBuilder.Build(
            item, reports, costRows, timingRows, iterations, diff, prStatus, prMergeSha);
        return Results.Ok(dossier);
    }

    private static async Task<DossierDiffInput?> TryReadDiffAsync(
        LocalGitHost gitHost,
        WorkItem item,
        IProjectRepository projects,
        CancellationToken ct)
    {
        try
        {
            if (!await gitHost.RepositoryExistsAsync(item.Id, ct))
                return null;
            var repoPath = gitHost.GetRepoPath(item.Id.ToString());
            var project = await projects.GetAsync(item.ProjectId, ct);
            var baseBranch = item.BaseBranch ?? project?.DefaultBaseBranch ?? "main";
            var workBranch = item.WorkBranch ?? $"codeybox/{item.Id.ToString()[..8]}";

            string baseSha;
            string workSha;
            try
            {
                using var repo = new Repository(repoPath);
                var baseRef = repo.Branches[baseBranch];
                var workRef = repo.Branches[workBranch];
                if (baseRef is null || workRef is null)
                    return null;
                baseSha = baseRef.Tip.Sha;
                workSha = workRef.Tip.Sha;
            }
            catch (RepositoryNotFoundException)
            {
                return null;
            }

            if (string.Equals(baseSha, workSha, StringComparison.Ordinal))
            {
                return new DossierDiffInput(baseSha, workSha, 0, 0, 0, Array.Empty<string>(), false);
            }

            var nameResult = await RunGitAsync(repoPath, ct, "diff", "--name-only", $"{baseSha}..{workSha}");
            if (nameResult.ExitCode != 0)
                return null;
            var changedFiles = nameResult.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var truncated = changedFiles.Length > MaxChangedFilesListed;
            var listed = changedFiles.Take(MaxChangedFilesListed).ToList();

            var numstatResult = await RunGitAsync(repoPath, ct, "diff", "--numstat", $"{baseSha}..{workSha}");
            long added = 0, removed = 0;
            if (numstatResult.ExitCode == 0)
                (added, removed) = ParseNumstat(numstatResult.Stdout);

            return new DossierDiffInput(
                baseSha, workSha, changedFiles.Length, added, removed, listed, truncated);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workdir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--git-dir=" + workdir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var readStdout = p.StandardOutput.ReadToEndAsync(ct);
        var readStderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await readStdout, await readStderr);
    }

    private static (long Added, long Removed) ParseNumstat(string numstat)
    {
        long added = 0, removed = 0;
        foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            if (long.TryParse(parts[0], out var a)) added += a;
            if (long.TryParse(parts[1], out var r)) removed += r;
        }
        return (added, removed);
    }
}
