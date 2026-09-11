using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.AdminSeed;

/// <summary>
/// Writes deterministic <see cref="AdminSeedData"/> content into a throwaway
/// SQLite database through the REAL store implementations
/// (<see cref="SqliteWorkItemStore"/>, <see cref="SqliteAuditReportStore"/>,
/// <see cref="SqliteReleaseStore"/>, <see cref="SqliteSuggestionStore"/>), so
/// the seeded admin instance serves exactly what the production API serves —
/// no parallel fake schema, no drift.
///
/// <para>The database path is canonicalized and contained: it must resolve
/// inside the caller-supplied root directory (the harness's temp dir), and
/// any pre-existing file is deleted first so re-seeds are reproducible.</para>
/// </summary>
public sealed class AdminSeeder
{
    public async Task<AdminSeedSummary> SeedAsync(
        string dbPath,
        string containingRoot,
        AdminSeedSpec? spec = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(containingRoot);
        spec ??= new AdminSeedSpec();

        var resolved = ContainPath(dbPath, containingRoot);
        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(resolved)) File.Delete(resolved);

        var items = AdminSeedData.BuildWorkItems(spec);
        var reports = AdminSeedData.BuildAuditReports(spec, items);
        var releases = AdminSeedData.BuildReleases(spec);
        var suggestions = AdminSeedData.BuildSuggestions(spec, items);

        using var workItems = new SqliteWorkItemStore(resolved);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            await workItems.CreateAsync(item, ct).ConfigureAwait(false);
        }

        using var auditReports = new SqliteAuditReportStore(resolved);
        foreach (var report in reports)
        {
            ct.ThrowIfCancellationRequested();
            await auditReports.CreateAsync(report, ct).ConfigureAwait(false);
        }

        using var releaseStore = new SqliteReleaseStore(resolved);
        foreach (var release in releases)
        {
            ct.ThrowIfCancellationRequested();
            await releaseStore.CreateAsync(release, ct).ConfigureAwait(false);
        }

        using var suggestionStore = new SqliteSuggestionStore(resolved);
        foreach (var suggestion in suggestions)
        {
            ct.ThrowIfCancellationRequested();
            await suggestionStore.CreateAsync(suggestion, ct).ConfigureAwait(false);
        }

        // The Microsoft.Data.Sqlite pool survives store disposal: without this,
        // a re-seed in the same process reuses a pooled connection bound to the
        // deleted inode and hits UNIQUE constraints on the "fresh" file.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        return new AdminSeedSummary(
            DbPath: resolved,
            Seed: spec.Seed,
            WorkItemCount: items.Count,
            AuditReportCount: reports.Count,
            ReleaseCount: releases.Count,
            SuggestionCount: suggestions.Count);
    }

    internal static string ContainPath(string dbPath, string containingRoot)
    {
        var fullRoot = Path.GetFullPath(containingRoot);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, dbPath));
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(fullPath, fullRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Seed database path '{dbPath}' escapes the containing root.",
                nameof(dbPath));
        }
        return fullPath;
    }
}

public sealed record AdminSeedSummary(
    string DbPath,
    int Seed,
    int WorkItemCount,
    int AuditReportCount,
    int ReleaseCount,
    int SuggestionCount);
