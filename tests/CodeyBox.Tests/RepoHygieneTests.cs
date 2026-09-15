namespace CodeyBox.Tests;

/// <summary>
/// Guards repository invariants that are cheap to state and expensive to
/// rediscover. These are not product behaviour — they are properties of the
/// checkout itself that have broken before.
/// </summary>
public sealed class RepoHygieneTests
{
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CodeyBox.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// CLAUDE.md and AGENTS.md are the same engineering contract under two
    /// names: AGENTS.md is injected as the work-agent "Project rules", and
    /// CLAUDE.md is what Claude Code reads. They are kept as separate files
    /// rather than a symlink because a git symlink checks out as a plain text
    /// file containing the target path on Windows without Developer Mode,
    /// which would silently empty the contract. Copies drift, so assert they
    /// do not.
    /// </summary>
    [Fact]
    public void ClaudeMd_And_AgentsMd_AreIdentical()
    {
        var root = FindRepoRoot();
        var agents = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
        var claude = File.ReadAllText(Path.Combine(root, "CLAUDE.md"));

        Assert.True(
            string.Equals(agents, claude, StringComparison.Ordinal),
            "AGENTS.md and CLAUDE.md have drifted. They must stay byte-identical: "
            + "AGENTS.md is the canonical engineering contract; update it and copy it to CLAUDE.md.");
    }

    /// <summary>
    /// Two tracked paths differing only in case cannot both exist in a
    /// checkout on a case-insensitive filesystem (default on macOS and
    /// Windows): one silently clobbers the other, and which one wins is not
    /// deterministic. This previously happened with NuGet.Config/nuget.config,
    /// where the two files also carried different restore settings.
    /// </summary>
    [Fact]
    public void NoTrackedPathsCollideOnCaseInsensitiveFilesystems()
    {
        var root = FindRepoRoot();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var collisions = new List<string>();

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path);
            if (relative.Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is ".git" or "bin" or "obj" or "node_modules" or ".nuget"))
                continue;

            var key = relative.ToLowerInvariant();
            if (seen.TryGetValue(key, out var first))
                collisions.Add($"{first} <=> {relative}");
            else
                seen[key] = relative;
        }

        Assert.True(
            collisions.Count == 0,
            "Paths differing only in case cannot coexist on macOS/Windows checkouts: "
            + string.Join("; ", collisions));
    }

    /// <summary>
    /// A <c>RestoreConfigFile</c> pointing at a path that does not exist is
    /// silently ignored by NuGet: restore still succeeds, but the repository's
    /// pinned package sources stop being applied and nothing says so. That
    /// happened when <c>Directory.Solution.props</c> still referenced
    /// <c>NuGet.Config</c> after only <c>nuget.config</c> remained. Assert every
    /// configured path resolves to a real file.
    /// </summary>
    [Fact]
    public void EveryRestoreConfigFilePathExists()
    {
        var root = FindRepoRoot();
        var missing = new List<string>();

        foreach (var props in Directory.EnumerateFiles(root, "Directory.*.props", SearchOption.TopDirectoryOnly))
        {
            foreach (var line in File.ReadLines(props))
            {
                var open = line.IndexOf("<RestoreConfigFile>", StringComparison.Ordinal);
                if (open < 0) continue;
                var start = open + "<RestoreConfigFile>".Length;
                var end = line.IndexOf("</RestoreConfigFile>", start, StringComparison.Ordinal);
                if (end < 0) continue;

                var value = line[start..end].Replace("$(MSBuildThisFileDirectory)", root + Path.DirectorySeparatorChar);
                if (!File.Exists(value))
                    missing.Add($"{Path.GetFileName(props)} -> {value}");
            }
        }

        Assert.True(
            missing.Count == 0,
            "RestoreConfigFile points at a path that does not exist. NuGet ignores this "
            + "silently, so the repository's pinned package sources stop applying: "
            + string.Join("; ", missing));
    }
}
