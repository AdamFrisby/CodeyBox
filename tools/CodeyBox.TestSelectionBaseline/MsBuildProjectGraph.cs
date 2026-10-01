using System.Text.Json;
using System.Xml.Linq;
using CodeyBox.Core;

namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// Walks a checkout's solution / project-reference graph and the files each
/// MSBuild project compiles. File ownership comes from
/// <c>dotnet msbuild -getItem:Compile</c> — the evaluated truth, so default
/// globs, <c>&lt;Compile Remove&gt;</c> and explicit includes are honoured
/// exactly rather than re-implemented. Untrusted repo XML is loaded with DTD
/// prohibited. Paths are canonicalized and must stay inside the repository
/// root.
/// </summary>
public static class MsBuildProjectGraph
{
    public const int MaxProjectXmlBytes = 4 * 1024 * 1024;
    public const int MaxWalkDepth = 16;

    public sealed record Graph(
        IReadOnlyList<string> Projects,
        IReadOnlyList<string> TestProjects,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ProjectReferences,
        IReadOnlyDictionary<string, string> FileProject);

    public static async Task<Graph> LoadAsync(
        string repoRoot,
        string? solutionPath,
        TestSelectionProducerOptions options,
        IHostCommandRunner runner,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);

        var root = Path.GetFullPath(repoRoot);
        var projects = await DiscoverProjectsAsync(root, solutionPath, options, runner, ct)
            .ConfigureAwait(false);
        if (projects.Count == 0)
            throw new TestSelectionBaselineProduceException("No projects were found in the checkout.");
        if (projects.Count > options.MaxProjectFiles)
        {
            throw new TestSelectionBaselineProduceException(
                $"Checkout has {projects.Count} projects, above the cap of {options.MaxProjectFiles}.");
        }

        var references = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var fileProject = new Dictionary<string, string>(StringComparer.Ordinal);
        var testProjects = new List<string>();
        var sourceFiles = 0;

        foreach (var project in projects)
        {
            var fullProject = CombineUnderRepo(root, project);
            var document = SafeXml.LoadFile(fullProject, MaxProjectXmlBytes);
            var isTest = IsTestProject(document);
            if (isTest)
                testProjects.Add(project);

            references[project] = ReadProjectReferences(document, root, fullProject);
            foreach (var file in await ReadCompileItemsAsync(
                         root, project, options, runner, ct).ConfigureAwait(false))
            {
                sourceFiles++;
                if (sourceFiles > options.MaxSourceFiles)
                {
                    throw new TestSelectionBaselineProduceException(
                        $"Checkout exceeds the {options.MaxSourceFiles} source-file cap.");
                }

                // The baseline format maps one file to one project; a file
                // genuinely compiled into two projects has no honest answer —
                // fail loudly rather than silently under-attribute it.
                if (fileProject.TryGetValue(file, out var existing)
                    && !string.Equals(existing, project, StringComparison.Ordinal))
                {
                    throw new TestSelectionBaselineProduceException(
                        $"Source file '{file}' is compiled by both '{existing}' and '{project}'.");
                }

                fileProject[file] = project;
            }
        }

        testProjects.Sort(StringComparer.Ordinal);
        return new Graph(projects, testProjects, references, fileProject);
    }

    public static async Task<IReadOnlyList<string>> DiscoverProjectsAsync(
        string repoRoot,
        string? solutionPath,
        TestSelectionProducerOptions options,
        IHostCommandRunner runner,
        CancellationToken ct)
    {
        var root = Path.GetFullPath(repoRoot);
        var solution = solutionPath is null
            ? FindSolutionFile(root)
            : RequireUnderRepo(root, Path.GetFullPath(solutionPath));

        if (solution is not null)
        {
            var listed = await TryListSolutionAsync(root, solution, options, runner, ct)
                .ConfigureAwait(false);
            if (listed.Count > 0)
                return listed;
        }

        return EnumerateCsprojFiles(root, options.MaxProjectFiles);
    }

    public static string? FindSolutionFile(string repoRoot)
    {
        var root = Path.GetFullPath(repoRoot);
        var preferred = Path.Combine(root, "CodeyBox.slnx");
        if (File.Exists(preferred))
            return preferred;

        var slnx = Directory.GetFiles(root, "*.slnx", SearchOption.TopDirectoryOnly);
        if (slnx.Length == 1)
            return slnx[0];
        if (slnx.Length > 1)
        {
            throw new TestSelectionBaselineProduceException(
                "Multiple .slnx files in the repository root; pass --solution.");
        }

        var sln = Directory.GetFiles(root, "*.sln", SearchOption.TopDirectoryOnly);
        if (sln.Length == 1)
            return sln[0];
        if (sln.Length > 1)
        {
            throw new TestSelectionBaselineProduceException(
                "Multiple .sln files in the repository root; pass --solution.");
        }

        return null;
    }

    private static async Task<IReadOnlyList<string>> TryListSolutionAsync(
        string repoRoot,
        string solutionPath,
        TestSelectionProducerOptions options,
        IHostCommandRunner runner,
        CancellationToken ct)
    {
        // A .slnx is plain XML listing every project — parse it locally and
        // skip the 'dotnet sln' spawn entirely. The binary-era .sln format
        // still goes through 'dotnet sln list'.
        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var fromSlnx = ParseSlnx(solutionPath, repoRoot, options.MaxProjectFiles);
            if (fromSlnx.Count > 0)
                return fromSlnx;
        }

        var relativeSolution = ToRepoRelative(repoRoot, solutionPath);
        var result = await HostCommandRun.CappedAsync(
            runner,
            [options.DotnetExecutable, "sln", relativeSolution, "list"],
            repoRoot,
            options.MaxCommandStdoutChars,
            options.MaxCommandStdoutChars,
            options.CommandTimeout,
            $"dotnet sln {relativeSolution} list",
            ct).ConfigureAwait(false);

        if (result.Success)
        {
            var fromDotnet = ParseSlnList(result.Stdout, repoRoot, options.MaxProjectFiles);
            if (fromDotnet.Count > 0)
                return fromDotnet;
        }

        return [];
    }

    public static IReadOnlyList<string> ParseSlnList(string stdout, string repoRoot, int maxProjects)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        var projects = new SortedSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(stdout);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0
                || line.Equals("Project(s)", StringComparison.Ordinal)
                || line.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (!(line.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                  || line.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                  || line.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var relative = ToRepoRelativeOrNull(repoRoot, line);
            if (relative is null)
            {
                throw new TestSelectionBaselineProduceException(
                    $"dotnet sln list emitted a project path outside the repository: '{line}'.");
            }

            if (projects.Count >= maxProjects)
            {
                throw new TestSelectionBaselineProduceException(
                    $"Solution lists more than {maxProjects} projects.");
            }

            projects.Add(relative);
        }

        return [.. projects];
    }

    public static IReadOnlyList<string> ParseSlnx(string slnxPath, string repoRoot, int maxProjects)
    {
        var document = SafeXml.LoadFile(slnxPath, MaxProjectXmlBytes);
        var projects = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var element in document.Descendants("Project"))
        {
            var include = (string?)element.Attribute("Path");
            if (string.IsNullOrWhiteSpace(include))
                continue;
            if (!include.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && !include.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                && !include.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = ToRepoRelativeOrNull(repoRoot, Path.Combine(repoRoot, include));
            if (relative is null)
            {
                throw new TestSelectionBaselineProduceException(
                    $"Solution project '{include}' escapes the repository.");
            }

            if (projects.Count >= maxProjects)
            {
                throw new TestSelectionBaselineProduceException(
                    $"Solution lists more than {maxProjects} projects.");
            }

            projects.Add(relative);
        }

        return [.. projects];
    }

    public static bool IsTestProject(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var flag in document.Descendants("IsTestProject"))
        {
            if (string.Equals(flag.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var package in document.Descendants("PackageReference"))
        {
            var include = (string?)package.Attribute("Include");
            if (string.Equals(include, "Microsoft.NET.Test.Sdk", StringComparison.Ordinal)
                || string.Equals(include, "coverlet.collector", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ReadProjectReferences(
        XDocument document,
        string repoRoot,
        string projectFullPath)
    {
        var projectDir = Path.GetDirectoryName(projectFullPath)
            ?? throw new TestSelectionBaselineProduceException($"Project '{projectFullPath}' has no directory.");
        var refs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var reference in document.Descendants("ProjectReference"))
        {
            var include = (string?)reference.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
                continue;
            var combined = Path.GetFullPath(Path.Combine(projectDir, include));
            var relative = ToRepoRelativeOrNull(repoRoot, combined);
            if (relative is null)
            {
                throw new TestSelectionBaselineProduceException(
                    $"ProjectReference '{include}' in '{projectFullPath}' escapes the repository.");
            }

            refs.Add(relative);
        }

        return [.. refs];
    }

    /// <summary>
    /// The repository-relative paths of a project's evaluated
    /// <c>Compile</c> items, per <c>dotnet msbuild -getItem:Compile</c> JSON.
    /// Items outside the repository (e.g. sources linked from elsewhere) are
    /// skipped — a repo diff can never reference them.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadCompileItemsAsync(
        string repoRoot,
        string projectRelative,
        TestSelectionProducerOptions options,
        IHostCommandRunner runner,
        CancellationToken ct)
    {
        var label = $"dotnet msbuild -getItem:Compile {projectRelative}";
        var result = await HostCommandRun.CappedAsync(
            runner,
            // -nr:false: never leave a node-reuse MSBuild server running —
            // it outlives the CLI process while still holding the output
            // pipes open.
            [options.DotnetExecutable, "msbuild", projectRelative, "-nologo", "-nr:false", "-getItem:Compile"],
            repoRoot,
            options.MaxCommandStdoutChars,
            options.MaxCommandStdoutChars,
            options.CommandTimeout,
            label,
            ct).ConfigureAwait(false);
        if (result.StdoutLimitExceeded || result.StderrLimitExceeded)
            throw new TestSelectionBaselineProduceException($"{label} exceeded an output cap.");
        if (!result.Success)
        {
            throw new TestSelectionBaselineProduceException(
                $"{label} exited {result.ExitCode}: {HostCommandRun.Tail(result.Stderr)}");
        }

        using var document = ParseJson(result.Stdout, label);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        if (!document.RootElement.TryGetProperty("Items", out var items)
            || items.ValueKind != JsonValueKind.Object
            || !items.TryGetProperty("Compile", out var compile)
            || compile.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var item in compile.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("FullPath", out var fullPath)
                || fullPath.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var relative = ToRepoRelativeOrNull(repoRoot, fullPath.GetString() ?? "");
            if (relative is not null)
                files.Add(relative);
        }

        return [.. files];
    }

    private static JsonDocument ParseJson(string stdout, string label)
    {
        try
        {
            return JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            throw new TestSelectionBaselineProduceException(
                $"{label} returned malformed JSON.", ex);
        }
    }

    private static IReadOnlyList<string> EnumerateCsprojFiles(string repoRoot, int maxProjects)
    {
        var projects = new SortedSet<string>(StringComparer.Ordinal);
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((repoRoot, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            if (depth > MaxWalkDepth)
                continue;
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(dir, "*.csproj");
                children = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var relative = ToRepoRelativeOrNull(repoRoot, file);
                if (relative is null)
                    continue;
                if (projects.Count >= maxProjects)
                {
                    throw new TestSelectionBaselineProduceException(
                        $"Checkout has more than {maxProjects} projects.");
                }

                projects.Add(relative);
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name is "bin" or "obj" or ".git")
                    continue;
                stack.Push((child, depth + 1));
            }
        }

        return [.. projects];
    }

    internal static string ToRepoRelative(string repoRoot, string fullPath)
    {
        var relative = ToRepoRelativeOrNull(repoRoot, fullPath);
        if (relative is null)
        {
            throw new TestSelectionBaselineProduceException(
                $"Path '{fullPath}' is outside the repository '{repoRoot}'.");
        }

        return relative;
    }

    internal static string? ToRepoRelativeOrNull(string repoRoot, string path)
    {
        var root = Path.GetFullPath(repoRoot);
        var candidate = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));
        if (!HostPathPolicy.IsWithinDirectory(candidate, root))
            return null;
        var relative = Path.GetRelativePath(root, candidate);
        return TestSelectionPaths.Normalize(relative);
    }

    private static string CombineUnderRepo(string repoRoot, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(repoRoot, relative));
        if (!HostPathPolicy.IsWithinDirectory(full, repoRoot))
        {
            throw new TestSelectionBaselineProduceException(
                $"Project path '{relative}' escapes the repository.");
        }

        return full;
    }

    private static string RequireUnderRepo(string repoRoot, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        if (!HostPathPolicy.IsWithinDirectory(full, repoRoot))
        {
            throw new TestSelectionBaselineProduceException(
                $"Solution path '{fullPath}' is outside the repository.");
        }

        if (!File.Exists(full))
            throw new FileNotFoundException($"Solution '{full}' does not exist.", full);
        return full;
    }
}
