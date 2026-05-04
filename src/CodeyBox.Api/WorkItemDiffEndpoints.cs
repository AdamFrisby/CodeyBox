using System.Diagnostics;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Git;
using LibGit2Sharp;

namespace CodeyBox.Api;

internal static class WorkItemDiffEndpoints
{
    internal const long MaxJsonDiffBytes = 1 * 1024 * 1024;
    internal const long MaxRawDiffBytes = 10 * 1024 * 1024;
    internal const string JsonTruncationMarker =
        "\n[... diff truncated at 1 MB. Download the raw diff for full output. ...]";
    internal const string RawTruncationMarker =
        "\n[... diff truncated at 10 MB. ...]";
    internal const int MaxFilesForFullDiff = 1000;

    public static void Map(WebApplication app)
    {
        app.MapGet("/workitems/{id}/diff", GetDiffAsync);
    }

    private static async Task GetDiffAsync(
        string id,
        HttpContext ctx,
        IWorkItemStore store,
        IProjectRepository projects,
        LocalGitHost gitHost,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "invalid id" }, ct);
            return;
        }

        var workItemId = new WorkItemId(g);
        var item = await store.GetAsync(workItemId, ct);
        if (item is null)
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        // Bare repo not yet created (agent hasn't started work phase yet).
        if (!await gitHost.RepositoryExistsAsync(item.Id, ct))
        {
            ctx.Response.StatusCode = 204;
            return;
        }

        var repoPath = gitHost.GetRepoPath(item.Id.ToString());
        var project = await projects.GetAsync(item.ProjectId, ct);
        var baseBranch = item.BaseBranch ?? project?.DefaultBaseBranch ?? "main";
        var workBranch = item.WorkBranch ?? $"codeybox/{item.Id.ToString()[..8]}";

        string? baseSha;
        string? workSha;
        try
        {
            using var repo = new Repository(repoPath);
            var baseRef = repo.Branches[baseBranch];
            var workRef = repo.Branches[workBranch];

            if (baseRef is null || workRef is null)
            {
                // Agent hasn't pushed to the work branch yet — no diff available.
                ctx.Response.StatusCode = 204;
                return;
            }

            baseSha = baseRef.Tip.Sha;
            workSha = workRef.Tip.Sha;
        }
        catch (RepositoryNotFoundException)
        {
            ctx.Response.StatusCode = 204;
            return;
        }

        if (string.Equals(baseSha, workSha, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 204;
            return;
        }

        // Count changed files. Fail gracefully if git is unavailable.
        var nameResult = await RunGitAsync(repoPath, ct, "diff", "--name-only", $"{baseSha}..{workSha}");
        if (nameResult.ExitCode != 0)
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new { error = "git diff failed" }, ct);
            return;
        }

        var changedFiles = nameResult.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var filesChanged = changedFiles.Length;

        // Too many files: return file list only with a hint.
        if (filesChanged > MaxFilesForFullDiff)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new
            {
                workItemId = item.Id.ToString(),
                baseBranch,
                workBranch,
                baseCommitSha = baseSha,
                workCommitSha = workSha,
                filesChanged,
                linesAdded = 0,
                linesRemoved = 0,
                diff = (string?)null,
                truncated = true,
                hint = $"This diff spans {filesChanged} files and is too large to display inline. Review on GitHub.",
                changedFiles,
            }, ct);
            return;
        }

        // Get line-change counts from numstat (handles binary files gracefully
        // because numstat emits "-" for binary rows, which parseInt ignores).
        var numstatResult = await RunGitAsync(repoPath, ct, "diff", "--numstat", $"{baseSha}..{workSha}");
        var (linesAdded, linesRemoved) = ParseNumstat(numstatResult.Stdout);

        var wantsJson = ctx.Request.Headers.Accept
            .Any(a => a?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);

        if (wantsJson)
        {
            await ServeJsonDiffAsync(
                ctx, item.Id.ToString(), baseBranch, workBranch, baseSha, workSha,
                filesChanged, linesAdded, linesRemoved, repoPath, ct);
        }
        else
        {
            await ServeRawDiffAsync(ctx, repoPath, baseSha, workSha, ct);
        }
    }

    private static async Task ServeJsonDiffAsync(
        HttpContext ctx,
        string workItemId,
        string baseBranch,
        string workBranch,
        string baseSha,
        string workSha,
        int filesChanged,
        int linesAdded,
        int linesRemoved,
        string repoPath,
        CancellationToken ct)
    {
        var psi = BuildGitDiffPsi(repoPath, baseSha, workSha);
        using var proc = Process.Start(psi)!;

        var sb = new StringBuilder();
        long totalBytes = 0;
        var truncated = false;

        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                var redacted = SecretRedactor.Redact(line);
                // +1 for the newline we'll append
                var lineBytes = Encoding.UTF8.GetByteCount(redacted) + 1;
                if (totalBytes + lineBytes > MaxJsonDiffBytes)
                {
                    truncated = true;
                    break;
                }
                sb.AppendLine(redacted);
                totalBytes += lineBytes;
            }
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }

        if (truncated) sb.Append(JsonTruncationMarker);

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new
        {
            workItemId,
            baseBranch,
            workBranch,
            baseCommitSha = baseSha,
            workCommitSha = workSha,
            filesChanged,
            linesAdded,
            linesRemoved,
            diff = sb.ToString(),
            truncated,
        }, ct);
    }

    private static async Task ServeRawDiffAsync(
        HttpContext ctx,
        string repoPath,
        string baseSha,
        string workSha,
        CancellationToken ct)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/x-diff; charset=utf-8";
        ctx.Response.Headers.ContentDisposition = "inline; filename=\"diff.patch\"";

        var psi = BuildGitDiffPsi(repoPath, baseSha, workSha);
        using var proc = Process.Start(psi)!;

        long totalBytes = 0;
        var hitLimit = false;

        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                line = SecretRedactor.Redact(line);
                var lineBytes = Encoding.UTF8.GetBytes(line + "\n");
                totalBytes += lineBytes.Length;
                if (totalBytes > MaxRawDiffBytes)
                {
                    hitLimit = true;
                    break;
                }
                await ctx.Response.Body.WriteAsync(lineBytes, ct);
            }
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }

        if (hitLimit)
            await ctx.Response.Body.WriteAsync(
                Encoding.UTF8.GetBytes(RawTruncationMarker), ct);
    }

    private static ProcessStartInfo BuildGitDiffPsi(string repoPath, string baseSha, string workSha)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("diff");
        psi.ArgumentList.Add("--unified=3");
        psi.ArgumentList.Add($"{baseSha}..{workSha}");
        return psi;
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
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, stdout, stderr);
    }

    private static (int Added, int Removed) ParseNumstat(string numstat)
    {
        int added = 0, removed = 0;
        foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            // Binary files report "-" instead of a number; skip those.
            if (int.TryParse(parts[0], out var a)) added += a;
            if (int.TryParse(parts[1], out var r)) removed += r;
        }
        return (added, removed);
    }
}
