using CodeyBox.Audit;

namespace CodeyBox.Tests;

/// <summary>
/// Unit coverage for the Stryker runner's pure cores: options validation,
/// repository-path guards, tool-command validation, and deterministic
/// project selection (ownership, test-dir precedence, multi-test-project
/// support, caps).
/// </summary>
public sealed class StrykerOptionsAndSelectionTests
{
    [Fact]
    public void DefaultOptions_AreValid_AndDisabled()
    {
        var opts = new StrykerMutationRunnerOptions();

        Assert.False(opts.Enabled);
        Assert.Equal(StrykerMutationRunnerOptions.PinnedVersion, opts.ExpectedVersion);
        Assert.Empty(opts.Validate());
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("v4.16.0")]
    public void BadExpectedVersion_IsRejected(string version)
    {
        var errors = new StrykerMutationRunnerOptions { ExpectedVersion = version }.Validate();

        Assert.Contains(errors, e => e.Contains("ExpectedVersion"));
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("Standard")]
    [InlineData("Advanced")]
    [InlineData("Complete")]
    [InlineData("standard")]
    public void KnownMutationLevels_AreAccepted(string level)
    {
        var errors = new StrykerMutationRunnerOptions { MutationLevel = level }.Validate();

        Assert.DoesNotContain(errors, e => e.Contains("MutationLevel"));
    }

    [Fact]
    public void UnknownMutationLevel_IsRejected()
    {
        var errors = new StrykerMutationRunnerOptions { MutationLevel = "Extreme" }.Validate();

        Assert.Contains(errors, e => e.Contains("MutationLevel"));
    }

    [Fact]
    public void OutOfRangeKnobs_AreRejected()
    {
        var errors = new StrykerMutationRunnerOptions
        {
            Concurrency = 65,
            MaxReportBytes = 1024,
            DiscoveryMaxDepth = 0,
            MaxProjectsPerRun = 0,
            ProbeTimeoutSeconds = 5,
        }.Validate();

        Assert.Contains(errors, e => e.Contains("Concurrency"));
        Assert.Contains(errors, e => e.Contains("MaxReportBytes"));
        Assert.Contains(errors, e => e.Contains("DiscoveryMaxDepth"));
        Assert.Contains(errors, e => e.Contains("MaxProjectsPerRun"));
        Assert.Contains(errors, e => e.Contains("ProbeTimeoutSeconds"));
    }

    [Fact]
    public void DuplicateProjectOverrides_AreRejected()
    {
        var errors = new StrykerMutationRunnerOptions
        {
            Projects =
            [
                new StrykerProjectOverride
                {
                    Project = "src/A/A.csproj",
                    TestProjects = ["test/A.Tests/A.Tests.csproj"],
                },
                new StrykerProjectOverride
                {
                    Project = "src/A/A.csproj",
                    TestProjects = ["test/A.Tests/A.Tests.csproj"],
                },
            ],
        }.Validate();

        Assert.Contains(errors, e => e.Contains("duplicated"));
    }

    [Theory]
    [InlineData("dotnet", true)]
    [InlineData("dotnet-stryker", true)]
    [InlineData("/opt/stryker/dotnet-stryker", true)]
    [InlineData("dotnet; rm -rf /", false)]
    [InlineData("dotnet stryker", false)]
    [InlineData("$(evil)", false)]
    [InlineData("../bin/stryker", false)]
    [InlineData("", false)]
    public void ToolCommandElements_Validated(string element, bool expected)
    {
        Assert.Equal(expected, StrykerArgv.IsValidToolCommandElement(element));
    }

    [Theory]
    [InlineData("src/Foo.cs", "src/Foo.cs")]
    [InlineData("./src/Foo.cs", "src/Foo.cs")]
    [InlineData("src/./Foo.cs", "src/Foo.cs")]
    [InlineData("../evil.cs", null)]
    [InlineData("/abs/evil.cs", null)]
    [InlineData("src/../../evil.cs", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("-", null)]
    public void RepoPaths_NormalizedOrRejected(string? input, string? expected)
    {
        Assert.Equal(expected, StrykerPaths.NormalizeRepoPath(input));
    }

    [Fact]
    public void RepoPath_WithControlCharacters_IsRejected()
    {
        Assert.Null(StrykerPaths.NormalizeRepoPath("src/Fo\to.cs"));
        Assert.Null(StrykerPaths.NormalizeRepoPath("src/Foo.cs\n"));
    }

    [Fact]
    public void Selection_MapsChangedFilesToOwningProjects()
    {
        var selection = StrykerProjectSelector.Select(
            ["src/A/A.cs", "src/B/B.cs"],
            ["src/A/A.csproj", "src/B/B.csproj"],
            ["test/A.Tests/A.Tests.csproj"],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["test/A.Tests/A.Tests.csproj"] = ["A.csproj"],
            },
            maxTestProjectsPerProject: 8);

        Assert.Equal(2, selection.Groups.Count);
        var groupA = selection.Groups.Single(g => g.ProjectCsproj == "src/A/A.csproj");
        Assert.Equal("src/A", groupA.ProjectDirectory);
        Assert.Equal("A.csproj", groupA.ProjectFileName);
        Assert.Equal(["test/A.Tests/A.Tests.csproj"], groupA.TestProjects);
        Assert.Equal(["A.cs"], groupA.MutatePatterns);
        var groupB = selection.Groups.Single(g => g.ProjectCsproj == "src/B/B.csproj");
        Assert.Empty(groupB.TestProjects);
        Assert.Empty(selection.UnmappedChangedFiles);
        Assert.Empty(selection.TestOnlyChangedFiles);
    }

    [Fact]
    public void Selection_LongestPrefixWins_OnNestedProjects()
    {
        var selection = StrykerProjectSelector.Select(
            ["src/Sub/F.cs"],
            ["src/App.csproj", "src/Sub/Sub.csproj"],
            [],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            maxTestProjectsPerProject: 8);

        var group = Assert.Single(selection.Groups);
        Assert.Equal("src/Sub/Sub.csproj", group.ProjectCsproj);
        Assert.Equal(["F.cs"], group.MutatePatterns);
    }

    [Fact]
    public void Selection_TestDirectoriesWin_OverProduction()
    {
        var selection = StrykerProjectSelector.Select(
            ["src/App/App.cs", "src/App/Tests/T.cs"],
            ["src/App/App.csproj", "src/App/Tests/App.Tests.csproj"],
            ["src/App/Tests/App.Tests.csproj"],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            maxTestProjectsPerProject: 8);

        var group = Assert.Single(selection.Groups);
        Assert.Equal("src/App/App.csproj", group.ProjectCsproj);
        Assert.Equal(["App.cs"], group.MutatePatterns);
        Assert.Equal(["src/App/Tests/T.cs"], selection.TestOnlyChangedFiles);
    }

    [Fact]
    public void Selection_SupportsMultipleTestProjects_SortedAndCapped()
    {
        var refs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var tests = new List<string>();
        for (var i = 5; i >= 1; i--)
        {
            var name = $"test/T{i}.Tests/T{i}.Tests.csproj";
            tests.Add(name);
            refs[name] = ["A.csproj"];
        }

        var selection = StrykerProjectSelector.Select(
            ["src/A/A.cs"],
            ["src/A/A.csproj"],
            tests,
            refs,
            maxTestProjectsPerProject: 3);

        var group = Assert.Single(selection.Groups);
        Assert.Equal(
            [
                "test/T1.Tests/T1.Tests.csproj",
                "test/T2.Tests/T2.Tests.csproj",
                "test/T3.Tests/T3.Tests.csproj",
            ],
            group.TestProjects);
    }

    [Fact]
    public void Selection_UnmappedFiles_AreReported()
    {
        var selection = StrykerProjectSelector.Select(
            ["docs/readme.cs", "src/A/A.cs"],
            ["src/A/A.csproj"],
            [],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            maxTestProjectsPerProject: 8);

        Assert.Equal(["docs/readme.cs"], selection.UnmappedChangedFiles);
        Assert.Single(selection.Groups);
    }

    [Fact]
    public void Selection_IsDeterministic_RegardlessOfInputOrder()
    {
        IReadOnlyList<string> changedB = ["src/B/B.cs", "src/A/A.cs"];
        IReadOnlyList<string> changedA = ["src/A/A.cs", "src/B/B.cs"];

        var first = StrykerProjectSelector.Select(
            changedA, ["src/B/B.csproj", "src/A/A.csproj"], [],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), 8);
        var second = StrykerProjectSelector.Select(
            changedB, ["src/A/A.csproj", "src/B/B.csproj"], [],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), 8);

        Assert.Equal(
            first.Groups.Select(g => g.ProjectCsproj),
            second.Groups.Select(g => g.ProjectCsproj));
        Assert.Equal(
            first.Groups.SelectMany(g => g.MutatePatterns),
            second.Groups.SelectMany(g => g.MutatePatterns));
    }
}
