using CodeyBox.Sandbox.Incus;

namespace CodeyBox.Tests;

public sealed class IncusProjectSecurityTests
{
    private static readonly IncusSandboxOptions Options = new()
    {
        ProjectName = "codeybox-security",
    };

    private static readonly IReadOnlyList<string> Roots =
        Array.AsReadOnly(new[] { "/srv/codeybox/repos", "/var/lib/codeybox/incus-staging" });

    [Fact]
    public void ProjectCreate_UsesOneRestrictedDedicatedConfiguration()
    {
        var argv = IncusProjectSecurity.BuildCreateArguments(Options, Roots);

        Assert.Equal("incus", argv[0]);
        Assert.Equal(["project", "create", "codeybox-security"], argv.Skip(1).Take(3));
        Assert.Contains("restricted=true", argv);
        Assert.Contains("restricted.devices.disk=allow", argv);
        Assert.Contains(
            "restricted.devices.disk.paths=/srv/codeybox/repos,/var/lib/codeybox/incus-staging",
            argv);
        Assert.Contains("restricted.devices.nic=allow", argv);
        Assert.Contains("restricted.snapshots=allow", argv);
        Assert.Contains("features.images=false", argv);
        Assert.Contains("features.profiles=true", argv);
        Assert.Contains("user.codeybox.managed=true", argv);
        Assert.Contains("user.codeybox.project-schema=1", argv);
        Assert.Contains("restricted.virtual-machines.lowlevel=block", argv);
    }

    [Fact]
    public void ProjectSet_UpdatesEverySecurityKeyInOneInvocation()
    {
        var argv = IncusProjectSecurity.BuildSetArguments(Options, Roots);

        Assert.Equal(["incus", "project", "set", "codeybox-security"], argv.Take(4));
        Assert.Equal(8, argv.Count - 4);
        Assert.Contains("restricted=true", argv);
        Assert.Contains("restricted.devices.disk=allow", argv);
        Assert.Contains("restricted.devices.nic=allow", argv);
        Assert.Contains("restricted.snapshots=allow", argv);
        Assert.Contains("restricted.virtual-machines.lowlevel=block", argv);
        Assert.Contains("user.codeybox.managed=true", argv);
        Assert.Contains("user.codeybox.project-schema=1", argv);
        Assert.Contains(
            "restricted.devices.disk.paths=/srv/codeybox/repos,/var/lib/codeybox/incus-staging",
            argv);
    }

    [Fact]
    public void ProjectQuery_ParsesWrappedExactConfigurationAndVerifiesReadBack()
    {
        var snapshot = IncusProjectSecurity.ParseProjectQuery(
            ProjectJson(
                "codeybox-security",
                "/var/lib/codeybox/incus-staging,/srv/codeybox/repos"),
            "codeybox-security");

        IncusProjectSecurity.EnsureDedicatedShape(snapshot);
        Assert.True(IncusProjectSecurity.IsCompliant(snapshot, Roots));
        IncusProjectSecurity.EnsureCompliant(snapshot, Roots);
    }

    [Theory]
    [InlineData("restricted", "false")]
    [InlineData("restricted.devices.disk", "managed")]
    [InlineData("restricted.devices.disk.paths", "")]
    [InlineData("restricted.devices.disk.paths", "/srv/codeybox/repos")]
    [InlineData("restricted.devices.nic", "managed")]
    [InlineData("restricted.snapshots", "block")]
    [InlineData("restricted.virtual-machines.lowlevel", "allow")]
    public void ProjectReadBack_RejectsMissingOrWeakenedSecurity(string key, string value)
    {
        var config = ValidConfig();
        config[key] = value;
        var snapshot = new IncusProjectSecuritySnapshot("codeybox-security", config);

        Assert.False(IncusProjectSecurity.IsCompliant(snapshot, Roots));
        Assert.Throws<InvalidOperationException>(() =>
            IncusProjectSecurity.EnsureCompliant(snapshot, Roots));
    }

    [Fact]
    public void ExistingNonDedicatedProject_IsNeverAutomaticallyHardened()
    {
        var config = ValidConfig();
        config[IncusProjectSecurity.FeaturesProfilesKey] = "false";
        var snapshot = new IncusProjectSecuritySnapshot("codeybox-security", config);

        Assert.Throws<InvalidOperationException>(() =>
            IncusProjectSecurity.EnsureDedicatedShape(snapshot));
    }

    [Fact]
    public void ExistingFeaturesDisabledProject_WithoutOwnershipMarkerIsRejected()
    {
        var config = ValidConfig();
        config.Remove(IncusProjectSecurity.ManagedKey);
        var snapshot = new IncusProjectSecuritySnapshot("codeybox-security", config);

        Assert.Throws<InvalidOperationException>(() =>
            IncusProjectSecurity.EnsureDedicatedShape(snapshot));
    }

    [Theory]
    [InlineData("/", typeof(InvalidOperationException))]
    [InlineData("/srv/codeybox,other", typeof(ArgumentException))]
    [InlineData("/srv/codeybox/", typeof(InvalidOperationException))]
    [InlineData("relative", typeof(ArgumentException))]
    public void RestrictedRoots_RejectBroadDelimitedOrNonCanonicalEntries(
        string invalid,
        Type expectedException)
    {
        var exception = Record.Exception(() =>
            IncusProjectSecurity.NormalizeCanonicalRoots([invalid, "/srv/codeybox"]));

        Assert.IsType(expectedException, exception);
    }

    [Theory]
    [InlineData("5.4.0-200-generic", false)]
    [InlineData("5.6.0", true)]
    [InlineData("6.14.0-22-generic", true)]
    [InlineData("7.0", true)]
    [InlineData("not-a-kernel", false)]
    public void KernelVersion_RequiresOpenAt2EraKernel(string version, bool expected) =>
        Assert.Equal(expected, IncusProjectSecurity.KernelSupportsOpenAt2(version));

    [Fact]
    public void ServerCapabilities_RequireVirtiofsRestrictedProjectsAndKernel()
    {
        const string valid = """
            {
              "metadata": {
                "api_extensions": ["disk_io_bus_cache_filesystem", "projects_restrictions"],
                "environment": {"kernel_version": "6.14.0-22-generic"}
              }
            }
            """;

        IncusProjectSecurity.EnsureServerCapabilities(valid);
        Assert.Throws<InvalidOperationException>(() =>
            IncusProjectSecurity.EnsureServerCapabilities(
                valid.Replace("projects_restrictions", "missing", StringComparison.Ordinal)));
        Assert.Throws<InvalidOperationException>(() =>
            IncusProjectSecurity.EnsureServerCapabilities(
                valid.Replace("6.14.0-22-generic", "5.4.0", StringComparison.Ordinal)));
    }

    [Fact]
    public void ProjectQuery_RejectsWrongNameAndAmbiguousConfig()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IncusProjectSecurity.ParseProjectQuery(
                ProjectJson("other-project", string.Join(',', Roots)),
                "codeybox-security"));
        const string duplicate = """
            {
              "metadata": {
                "name": "codeybox-security",
                "config": {"restricted": "true", "restricted": "false"}
              }
            }
            """;
        Assert.Throws<InvalidOperationException>(() =>
            IncusProjectSecurity.ParseProjectQuery(duplicate, "codeybox-security"));
    }

    private static Dictionary<string, string> ValidConfig() => new(StringComparer.Ordinal)
    {
        [IncusProjectSecurity.FeaturesImagesKey] = "false",
        [IncusProjectSecurity.FeaturesProfilesKey] = "true",
        [IncusProjectSecurity.ManagedKey] = "true",
        [IncusProjectSecurity.SchemaKey] = "1",
        [IncusProjectSecurity.RestrictedKey] = "true",
        [IncusProjectSecurity.RestrictedDiskKey] = "allow",
        [IncusProjectSecurity.RestrictedDiskPathsKey] = string.Join(',', Roots),
        [IncusProjectSecurity.RestrictedNicKey] = "allow",
        [IncusProjectSecurity.RestrictedSnapshotsKey] = "allow",
        [IncusProjectSecurity.RestrictedVmLowLevelKey] = "block",
    };

    private static string ProjectJson(string name, string roots)
    {
        var config = ValidConfig();
        config[IncusProjectSecurity.RestrictedDiskPathsKey] = roots;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            metadata = new
            {
                name,
                config,
            },
        });
    }
}
