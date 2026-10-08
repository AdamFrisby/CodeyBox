using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.IwyuAuditorPlugin;

/// <summary>
/// The coverage plan a validated compilation database yields for one run:
/// which translation units the driver's in-worktree selection will analyse
/// and the relative spellings the report can use for each selected file.
/// It is computed BEFORE the scan — the entries' compile flags reach the
/// clang driver verbatim and can rewrite the database file mid-run
/// (<c>-o</c>, <c>-MJ</c>, <c>-MF</c>, a loaded plugin), so a post-scan
/// re-read could describe a different database than the one
/// <c>iwyu_tool</c> parsed — and carried to the parser through the
/// auditor's per-run channel.
/// </summary>
internal sealed record CompilationDatabasePlan(
    /// <summary>
    /// Repository-relative canonical path of every selected in-worktree
    /// translation unit — each must carry a verdict record in the report.
    /// </summary>
    IReadOnlySet<string> InScopeFiles,
    /// <summary>Entries whose canonical file resolves outside the worktree and are never analysed.</summary>
    int OutOfScopeCount,
    /// <summary>Dot-collapsed relative report spellings → repository-relative path.</summary>
    IReadOnlyDictionary<string, string> RelativePathAnchors);

/// <summary>
/// The compilation-database subsystem of <see cref="IwyuAuditor"/>: the
/// sandbox probe script that resolves the configured path, plus the
/// host-side JSON analysis deciding which entries the driver's in-worktree
/// selection runs and which path spellings the report may use. The
/// analysis and path algebra are pure functions — the auditor supplies the
/// bounded sandbox execs that feed them.
/// </summary>
internal static class IwyuCompilationDatabase
{
    // Per-field bound on untrusted database strings (file/directory): enough
    // for the deepest real path, small enough that a hostile entry cannot
    // bloat argv or failure text.
    private const int MaxDatabaseFieldChars = 1024;

    // Bounds on an entry's compile invocation: a single 'command' string or
    // each 'arguments' element, and the element count. Compile command lines
    // are legitimately long; these bound per-entry work without excluding
    // real-world databases.
    private const int MaxCompileCommandChars = 8192;
    private const int MaxCompileArguments = 512;

    // Canonicalization + file-type probe for the configured database path.
    // Two realpath lines first (the configured path resolved in the
    // sandbox's own path space — providers may translate the exec working
    // directory — and the canonical worktree root), then for a directory
    // holding a compile_commands.json a third realpath line resolving the
    // LEAF — [ -f ] follows symlinks, so without it a repo-controlled
    // compile_commands.json symlink would pass the directory containment
    // check yet redirect the bounded read (and the -p operand) outside the
    // worktree — then a marker line: dir (leaf emitted), dir-no-db, file, or
    // missing. Structured argv: the configured path travels as "$1", never
    // inside the script text.
    internal const string PathProbeScript =
        "realpath -m -- \"$1\" . || exit 1\n"
        + "if [ -d \"$1\" ]; then\n"
        + "  if [ -f \"$1/compile_commands.json\" ]; then\n"
        + "    realpath -m -- \"$1/compile_commands.json\" || exit 1\n"
        + "    echo dir\n"
        + "  else\n"
        + "    echo dir-no-db\n"
        + "  fi\n"
        + "elif [ -f \"$1\" ]; then\n"
        + "  echo file\n"
        + "else\n"
        + "  echo missing\n"
        + "fi";

    /// <summary>
    /// Validates the database document and derives the coverage plan: which
    /// entries' canonicalized <c>file</c> resolves inside the audited
    /// worktree — a lexical approximation of the set
    /// <c>iwyu_tool … .</c> selects (see
    /// <see cref="CanonicalizeEntryFile"/>) — and the relative-path anchors
    /// the parser uses to resolve path spellings reported against each
    /// entry's <c>directory</c>. The JSON is repository content: a malformed
    /// document, a malformed entry, more than <paramref name="maxEntries"/>
    /// entries, or zero in-worktree translation units are all deterministic
    /// infrastructure failures — a vacuous or unaccountable-coverage run is
    /// never a pass.
    /// </summary>
    internal static CompilationDatabasePlan Analyze(
        string json,
        string worktreeRoot,
        int maxEntries,
        string tool)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' read a compilation database that is not "
                + $"valid JSON: {ExternalToolJsonHelpers.SingleLine(ex.Message)}",
                ex)
            { IsDeterministic = true };
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw MalformedDatabase(tool, "the root is not a JSON array");

            var inScopeFiles = new HashSet<string>(StringComparer.Ordinal);
            var outOfScope = 0;
            var anchors = new Dictionary<string, string>(StringComparer.Ordinal);
            var ambiguousAnchors = new HashSet<string>(StringComparer.Ordinal);
            var prefix = worktreeRoot == "/" ? "/" : worktreeRoot + "/";
            var entryIndex = 0;
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (++entryIndex > maxEntries)
                    throw new AuditUnavailableException(
                        $"could-not-verify: audit tool '{tool}' compilation database holds more "
                        + $"than {maxEntries} entries — the per-run bound keeps "
                        + "coverage accounting finite. Narrow the build or raise "
                        + $"CodeyBox:Plugins:{IwyuAuditor.PluginId}:{IwyuAuditor.MaxCompilationDatabaseEntriesKey}.")
                    { IsDeterministic = true };

                if (entry.ValueKind != JsonValueKind.Object)
                    throw MalformedDatabase(tool, $"entry #{entryIndex} is not an object");

                var file = ExternalToolJsonHelpers.GetString(entry, "file"u8);
                if (file is null || file.Length == 0)
                    throw MalformedDatabase(tool, $"entry #{entryIndex} has no usable 'file' path");
                if (file.Length > MaxDatabaseFieldChars || file.Any(char.IsControl))
                    throw MalformedDatabase(
                        tool, $"entry #{entryIndex} has an overlong or control-carrying 'file' path");

                var directory = ExternalToolJsonHelpers.GetString(entry, "directory"u8);
                if (directory is null)
                    // iwyu_tool reads entry['directory'] unconditionally —
                    // a missing field crashes the driver at run time, so it
                    // fails deterministically here instead.
                    throw MalformedDatabase(tool, $"entry #{entryIndex} has no usable 'directory' path");
                if (directory.Length == 0 || directory.Length > MaxDatabaseFieldChars
                    || directory.Any(char.IsControl))
                    throw MalformedDatabase(
                        tool, $"entry #{entryIndex} has an overlong or control-carrying 'directory' path");

                if (!HasUsableInvocation(entry))
                    throw MalformedDatabase(
                        tool,
                        $"entry #{entryIndex} has no usable 'command' string or 'arguments' string "
                        + "array — iwyu_tool raises on entries it cannot exec, which would crash "
                        + "the driver mid-run");

                var canonical = CanonicalizeEntryFile(file, directory, worktreeRoot);
                if (HostPathPolicy.IsStrictlyWithinDirectory(canonical, worktreeRoot)
                    && canonical.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var repositoryRelative = canonical[prefix.Length..];
                    inScopeFiles.Add(repositoryRelative);
                    AddEntryAnchors(
                        anchors, ambiguousAnchors, file, directory, canonical, worktreeRoot,
                        repositoryRelative);
                }
                else
                {
                    outOfScope++;
                }
            }

            if (inScopeFiles.Count == 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{tool}' compilation database lists no "
                    + "translation units inside the audited worktree — nothing can be analysed, so "
                    + "this is infrastructure, not a pass. Check that the database was generated "
                    + "against this tree.")
                { IsDeterministic = true };

            return new CompilationDatabasePlan(inScopeFiles, outOfScope, anchors);
        }
    }

    // An entry is runnable when the invocation iwyu_tool's
    // Invocation.from_compile_command will actually exec is usable: it
    // prefers 'arguments' whenever the key exists and falls back to
    // 'command' only when it does not — so a present-but-empty 'arguments'
    // masks a valid 'command' and would crash the driver mid-run. Both
    // shapes are bounded here, and control bytes are rejected the same way
    // 'file' and 'directory' are: an arg carrying '\n' is legal execve
    // input yet corrupts the line-oriented report stream the parser
    // consumes.
    private static bool HasUsableInvocation(JsonElement entry)
    {
        if (entry.TryGetProperty("arguments"u8, out var arguments))
        {
            if (arguments.ValueKind != JsonValueKind.Array)
                return false;
            var count = 0;
            foreach (var argument in arguments.EnumerateArray())
            {
                if (++count > MaxCompileArguments
                    || argument.ValueKind != JsonValueKind.String
                    || argument.GetString() is not { Length: > 0 } value
                    || value.Length > MaxCompileCommandChars
                    || value.Any(char.IsControl))
                    return false;
            }
            return count > 0;
        }
        return ExternalToolJsonHelpers.GetString(entry, "command"u8) is { Length: > 0 } command
            && command.Length <= MaxCompileCommandChars
            && !command.Any(char.IsControl);
    }

    /// <summary>
    /// Resolves a database entry's <c>file</c> to its would-be canonical
    /// path — relative <c>file</c> joins <c>directory</c> (which iwyu_tool
    /// requires on every entry), and a relative <c>directory</c> joins the
    /// worktree root the driver runs in. This is a LEXICAL approximation of
    /// iwyu_tool's <c>os.path.realpath</c> fixup
    /// (<c>fixup_compilation_db</c> + <c>is_subpath_of</c>): it collapses dot
    /// segments but cannot resolve symlink components, so a <c>file</c>
    /// traversing an in-tree symlink to outside the worktree counts as
    /// in-scope here while iwyu_tool skips it. The divergence cannot widen
    /// analysis — the tool's own realpath selection is the boundary — and
    /// the report's verdicts are reconciled against the selected files so a
    /// skipped entry surfaces as an infrastructure failure rather than
    /// silent partial coverage.
    /// </summary>
    private static string CanonicalizeEntryFile(string file, string directory, string worktreeRoot)
    {
        var combined = file.StartsWith("/", StringComparison.Ordinal)
            ? file
            : CanonicalizeDirectory(directory, worktreeRoot) + "/" + file;
        return CollapseNormalized(combined);
    }

    // An entry's 'directory' canonicalized under the same join rule the
    // 'file' resolution uses: absolute stays, relative resolves against the
    // worktree root the driver runs in. One helper keeps the entry
    // canonicalization and the anchor computation from diverging.
    private static string CanonicalizeDirectory(string directory, string worktreeRoot)
        => CollapseNormalized(directory.StartsWith("/", StringComparison.Ordinal)
            ? directory
            : worktreeRoot + "/" + directory);

    private static string CollapseNormalized(string path)
        => ExternalToolJsonHelpers.CollapseDotSegments(
            ExternalToolJsonHelpers.NormalizePath(path));

    // Registers the relative spellings under which IWYU can report this
    // entry's file — the 'file' field itself when relative, and the
    // canonical file spelled relative to the entry's 'directory' (the cwd
    // iwyu_tool runs the compile command in, e.g. "-c ../src/a.cc") — each
    // mapped to the file's repository-relative path. A key claimed by two
    // different resolutions is ambiguous and dropped: the parser falls back
    // to the shared reported-path policy for it.
    private static void AddEntryAnchors(
        Dictionary<string, string> anchors,
        HashSet<string> ambiguousAnchors,
        string file,
        string directory,
        string canonicalFile,
        string worktreeRoot,
        string repositoryRelative)
    {
        if (!file.StartsWith("/", StringComparison.Ordinal))
            AddAnchor(anchors, ambiguousAnchors, file, repositoryRelative);
        AddAnchor(
            anchors, ambiguousAnchors,
            RelativeSpelling(CanonicalizeDirectory(directory, worktreeRoot), canonicalFile),
            repositoryRelative);
    }

    private static void AddAnchor(
        Dictionary<string, string> anchors,
        HashSet<string> ambiguousAnchors,
        string spelling,
        string repositoryRelative)
    {
        var key = CollapseNormalized(spelling);
        if (key.Length == 0 || key.StartsWith("/", StringComparison.Ordinal)
            || ambiguousAnchors.Contains(key))
            return;
        if (anchors.TryGetValue(key, out var existing)
            && !string.Equals(existing, repositoryRelative, StringComparison.Ordinal))
        {
            anchors.Remove(key);
            ambiguousAnchors.Add(key);
            return;
        }
        anchors[key] = repositoryRelative;
    }

    // The canonical absolute path `toPath` spelled relative to the canonical
    // absolute directory `fromDir` — the form a compile command's source
    // operand takes when the build spells sources relative to the build
    // directory ("../src/a.cc" from /work/build to /work/src/a.cc).
    private static string RelativeSpelling(string fromDir, string toPath)
    {
        var from = fromDir.Split('/');
        var to = toPath.Split('/');
        var common = 0;
        while (common < from.Length && common < to.Length && from[common] == to[common])
            common++;
        var builder = new StringBuilder();
        for (var i = common; i < from.Length; i++)
        {
            if (from[i].Length > 0)
                builder.Append("../");
        }
        builder.Append(string.Join('/', to.Skip(common)));
        return builder.ToString();
    }

    private static AuditUnavailableException MalformedDatabase(string tool, string detail)
        => new(
            $"could-not-verify: audit tool '{tool}' read a malformed compilation database "
            + $"({detail}). Regenerate it with a conforming build tool.")
        { IsDeterministic = true };
}
