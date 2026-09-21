using System.Xml.Linq;

namespace CodeyBox.Tests;

/// <summary>
/// Enforces the in-repo plugin layout documented in <c>plugins/README.md</c>:
/// every plugin project lives at <c>plugins/&lt;group&gt;/&lt;ProjectName&gt;</c>,
/// the group is recognised, the directory and project file share one
/// <c>CodeyBox.*</c> name, the project file targets the SDK surface, and both
/// registration lines (solution + test project) exist. The canonical group
/// list lives here; <c>plugins/README.md</c> mirrors it.
/// </summary>
public sealed class PluginLayoutConventionTests
{
    private static readonly HashSet<string> RecognisedGroups = new(StringComparer.Ordinal)
    {
        "auditors-api-compatibility",
        "auditors-architecture",
        "auditors-dependency-vulnerabilities",
        "auditors-documentation",
        "auditors-infrastructure",
        "auditors-licensing",
        "auditors-linting",
        "auditors-schema",
        "auditors-scripting",
        "auditors-secrets",
        "auditors-static-analysis",
        "credentials",
        "quota",
        "telemetry",
        "test-runners",
        "upstream",
        "work-sync",
    };

    private static readonly HashSet<string> GrandfatheredNames = new(StringComparer.Ordinal)
    {
        "CodeyBox.QuotaResetNotifier",
    };

    private sealed record PluginProject(string Group, string Name, string CsprojPath);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodeyBox.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no CodeyBox.slnx found walking up from "
            + AppContext.BaseDirectory + ").");
    }

    private static IReadOnlyList<PluginProject> DiscoverPluginProjects(string root)
    {
        var pluginsDir = Path.Combine(root, "plugins");
        var projects = Directory
            .EnumerateFiles(pluginsDir, "*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(pluginsDir, path))
            .Select(relative => new PluginProject(
                Group: relative.Split(Path.DirectorySeparatorChar)[0],
                Name: Path.GetFileNameWithoutExtension(relative),
                CsprojPath: relative))
            .OrderBy(p => p.CsprojPath, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(projects);
        return projects;
    }

    [Fact]
    public void Every_Plugin_Project_Sits_Exactly_Two_Levels_Under_Plugins()
    {
        var root = RepositoryRoot();
        var pluginsDir = Path.Combine(root, "plugins");

        var misplaced = Directory
            .EnumerateFiles(pluginsDir, "*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(pluginsDir, path))
            .Where(relative => relative.Split(Path.DirectorySeparatorChar).Length != 3)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(misplaced.Count == 0,
            "Plugin projects must live at plugins/<group>/<ProjectName>/<ProjectName>.csproj. Misplaced: "
            + string.Join(", ", misplaced));
    }

    [Fact]
    public void Every_Plugin_Project_Lives_In_A_Recognised_Group()
    {
        var projects = DiscoverPluginProjects(RepositoryRoot());

        var unknown = projects
            .Where(p => !RecognisedGroups.Contains(p.Group))
            .Select(p => p.CsprojPath)
            .ToList();

        Assert.True(unknown.Count == 0,
            "Unknown plugin group(s). Add the group to RecognisedGroups and plugins/README.md, or move the project. Offenders: "
            + string.Join(", ", unknown));
    }

    [Fact]
    public void Every_Plugin_Project_Follows_The_Naming_Rule()
    {
        var projects = DiscoverPluginProjects(RepositoryRoot());
        var violations = new List<string>();

        foreach (var project in projects)
        {
            var directoryName = Path.GetFileName(Path.GetDirectoryName(
                Path.Combine(RepositoryRoot(), "plugins", project.CsprojPath)));
            if (!StringComparer.Ordinal.Equals(directoryName, project.Name))
            {
                violations.Add($"{project.CsprojPath}: directory '{directoryName}' does not match project file '{project.Name}'");
            }

            if (!project.Name.StartsWith("CodeyBox.", StringComparison.Ordinal))
            {
                violations.Add($"{project.CsprojPath}: project name must start with 'CodeyBox.'");
            }

            if (!project.Name.EndsWith("Plugin", StringComparison.Ordinal)
                && !GrandfatheredNames.Contains(project.Name))
            {
                violations.Add($"{project.CsprojPath}: project name must end with 'Plugin'");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Every_Plugin_Project_File_Targets_The_Sdk_Surface()
    {
        var root = RepositoryRoot();
        var projects = DiscoverPluginProjects(root);
        var violations = new List<string>();

        foreach (var project in projects)
        {
            var document = XDocument.Load(Path.Combine(root, "plugins", project.CsprojPath));
            var targetFrameworks = document
                .Descendants("TargetFramework")
                .Select(e => e.Value.Trim())
                .ToList();
            if (targetFrameworks is not ["net10.0"])
            {
                violations.Add($"{project.CsprojPath}: TargetFramework must be exactly 'net10.0'");
            }

            var projectReferences = document
                .Descendants("ProjectReference")
                .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
                .ToList();
            var packageReferences = document
                .Descendants("PackageReference")
                .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
                .ToList();
            var allReferences = projectReferences.Concat(packageReferences).ToList();

            if (!allReferences.Any(r => r.EndsWith("CodeyBox.Core.csproj", StringComparison.Ordinal)
                || StringComparer.Ordinal.Equals(r, "CodeyBox.Core")))
            {
                violations.Add($"{project.CsprojPath}: must reference CodeyBox.Core");
            }

            if (!allReferences.Any(r => r.EndsWith("CodeyBox.PluginSdk.csproj", StringComparison.Ordinal)
                || StringComparer.Ordinal.Equals(r, "CodeyBox.PluginSdk")))
            {
                violations.Add($"{project.CsprojPath}: must reference CodeyBox.PluginSdk");
            }

            var forbidden = allReferences
                .Where(r => r.Contains("CodeyBox.Orchestrator", StringComparison.Ordinal)
                    || r.Contains("CodeyBox.Api", StringComparison.Ordinal))
                .ToList();
            if (forbidden.Count != 0)
            {
                violations.Add($"{project.CsprojPath}: must not reference internal host packages: {string.Join(", ", forbidden)}");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Every_Plugin_Project_Is_Registered_In_The_Solution_And_Test_Project()
    {
        var root = RepositoryRoot();
        var projects = DiscoverPluginProjects(root);
        var solution = File.ReadAllText(Path.Combine(root, "CodeyBox.slnx"));
        var testsProject = File.ReadAllText(Path.Combine(root, "tests", "CodeyBox.Tests", "CodeyBox.Tests.csproj"));
        var violations = new List<string>();

        foreach (var project in projects)
        {
            var solutionPath = $"plugins/{project.Group}/{project.Name}/{project.Name}.csproj";
            if (!solution.Contains(solutionPath, StringComparison.Ordinal))
            {
                violations.Add($"{project.CsprojPath}: missing '<Project Path=\"{solutionPath}\" />' in CodeyBox.slnx");
            }

            var testReference = $"plugins\\{project.Group}\\{project.Name}\\{project.Name}.csproj";
            if (!testsProject.Contains(testReference, StringComparison.Ordinal))
            {
                violations.Add($"{project.CsprojPath}: missing ProjectReference '{testReference}' in CodeyBox.Tests.csproj");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Every_Registered_Plugin_Path_Exists_On_Disk()
    {
        var root = RepositoryRoot();
        var solution = File.ReadAllText(Path.Combine(root, "CodeyBox.slnx"));
        var violations = new List<string>();

        foreach (var line in solution.Split('\n'))
        {
            var marker = "plugins/";
            var start = line.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var end = line.IndexOf(".csproj", start, StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            var relative = line.Substring(start, end - start + ".csproj".Length).Replace('/', Path.DirectorySeparatorChar);
            if (!File.Exists(Path.Combine(root, relative)))
            {
                violations.Add($"CodeyBox.slnx references missing project '{relative}'");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }
}
