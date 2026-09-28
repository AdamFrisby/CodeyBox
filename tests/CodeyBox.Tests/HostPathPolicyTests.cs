using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Host paths must be OS-neutral (<see cref="Path.Combine"/>, no hardcoded
/// separators); guest paths inside a Linux sandbox stay forward-slash.
/// </summary>
public sealed class HostPathPolicyTests
{
    [Fact]
    public void DefaultGitRoot_IsAbsoluteOnCurrentHost()
    {
        Assert.True(HostPathPolicy.IsAbsoluteHostPath(HostPathPolicy.DefaultGitRootDirectory()));
    }

    [Fact]
    public void DefaultStateDb_IsAbsoluteOnCurrentHost()
    {
        Assert.True(HostPathPolicy.IsAbsoluteHostPath(HostPathPolicy.DefaultStateDatabasePath()));
    }

    [Fact]
    public void Defaults_UseBackslashFreeJoin_OnUnix_AndDriveOrUnc_OnWindows()
    {
        var gitRoot = HostPathPolicy.DefaultGitRootDirectory();
        if (OperatingSystem.IsWindows())
        {
            Assert.True(gitRoot.Contains('\\', StringComparison.Ordinal));
            Assert.Contains("CodeyBox", gitRoot, StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith("/", gitRoot, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", gitRoot, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExpandHomeDirectory_ExpandsTilde_ToUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return;
        Assert.Equal(home, HostPathPolicy.ExpandHomeDirectory("~"));
        Assert.Equal(
            Path.Combine(home, ".ssh", "id_ed25519"),
            HostPathPolicy.ExpandHomeDirectory("~/.ssh/id_ed25519"));
    }

    [Fact]
    public void ExpandHomeDirectory_LeavesPlainPathsUntouched()
    {
        Assert.Equal("/etc/codeybox/networks.conf", HostPathPolicy.ExpandHomeDirectory("/etc/codeybox/networks.conf"));
        Assert.Equal(string.Empty, HostPathPolicy.ExpandHomeDirectory(null));
        Assert.Equal(string.Empty, HostPathPolicy.ExpandHomeDirectory(""));
        Assert.Equal("~other/file", HostPathPolicy.ExpandHomeDirectory("~other/file"));
    }

    [Fact]
    public void CombineHostPath_UsesPlatformSeparator()
    {
        var combined = HostPathPolicy.CombineHostPath("a", "b", "c");
        Assert.Equal(Path.Combine("a", "b", "c"), combined);
        Assert.Throws<ArgumentException>(() => HostPathPolicy.CombineHostPath());
    }

    [Fact]
    public void NormalizeSeparators_NeverReturnsNull()
    {
        Assert.Equal(string.Empty, HostPathPolicy.NormalizeSeparators(null));
        var normalized = HostPathPolicy.NormalizeSeparators("a/b\\c");
        Assert.DoesNotContain(
            Path.DirectorySeparatorChar == '/' ? "\\" : "/",
            normalized,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HostPathsEqual_RespectsCaseSensitivityRules()
    {
        var left = HostPathPolicy.NormalizeSeparators("/VAR/LIB/CodeyBox/repos");
        var right = HostPathPolicy.NormalizeSeparators("/var/lib/codeybox/repos");
        Assert.Equal(
            !OperatingSystem.IsLinux(),
            HostPathPolicy.HostPathsEqual(left, right));
    }

    [Fact]
    public void IsWithinDirectory_ContainmentCases()
    {
        var root = OperatingSystem.IsWindows() ? "C:\\work" : "/work";
        var sep = Path.DirectorySeparatorChar;
        Assert.True(HostPathPolicy.IsWithinDirectory(root + sep + "a" + sep + "b.txt", root));
        Assert.True(HostPathPolicy.IsWithinDirectory(root + sep + "sub" + sep + ".." + sep + "b.txt", root));
        Assert.True(HostPathPolicy.IsWithinDirectory(root, root));
        Assert.False(HostPathPolicy.IsWithinDirectory(root + sep + ".." + sep + "escape", root));
        // A sibling sharing the root's string prefix is not a child.
        Assert.False(HostPathPolicy.IsWithinDirectory(root + "other" + sep + "x", root));
        Assert.False(HostPathPolicy.IsWithinDirectory(root + sep + "a" + sep + ".." + sep + ".." + sep + "x", root));
    }

    [Fact]
    public void IsWithinDirectory_DotDotLeadingLeafName_IsInside_NotAnEscape()
    {
        // Path.GetRelativePath returns "..evil" for /work/..evil — a bare
        // StartsWith("..") check fails open on committed files whose names
        // merely begin with "..". The segment-aware check keeps them within.
        var root = OperatingSystem.IsWindows() ? "C:\\work" : "/work";
        var leaf = root + Path.DirectorySeparatorChar + "..evil";
        Assert.True(HostPathPolicy.IsWithinDirectory(leaf, root));
        Assert.True(HostPathPolicy.IsStrictlyWithinDirectory(leaf, root));
    }

    [Fact]
    public void IsWithinDirectory_RejectsNonRootedInputs_RatherThanResolvingAgainstCwd()
    {
        var root = OperatingSystem.IsWindows() ? "C:\\work" : "/work";
        Assert.False(HostPathPolicy.IsWithinDirectory("relative/child", root));
        Assert.False(HostPathPolicy.IsWithinDirectory(root + Path.DirectorySeparatorChar + "child", "relative"));
        Assert.False(HostPathPolicy.IsWithinDirectory("", root));
    }

    [Fact]
    public void IsStrictlyWithinDirectory_ExcludesTheDirectoryItself()
    {
        var root = OperatingSystem.IsWindows() ? "C:\\work" : "/work";
        var sep = Path.DirectorySeparatorChar;
        Assert.True(HostPathPolicy.IsStrictlyWithinDirectory(root + sep + "child", root));
        Assert.False(HostPathPolicy.IsStrictlyWithinDirectory(root, root));
        Assert.False(HostPathPolicy.IsStrictlyWithinDirectory(root + sep + ".." + sep + "escape", root));
    }

    [Fact]
    public void GuestPaths_StayForwardSlash_RegardlessOfHost()
    {
        Assert.Equal("/work", CodeyBox.Sandbox.SandboxConventions.WorkDir);
        Assert.Equal("/run/codeybox/creds", CodeyBox.Sandbox.SandboxConventions.CredentialsDir);
        Assert.DoesNotContain("\\", CodeyBox.Sandbox.SandboxConventions.WorkDir, StringComparison.Ordinal);
    }
}
