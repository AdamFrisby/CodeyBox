using CodeyBox.Sandbox.Incus;

namespace CodeyBox.Tests;

public sealed class IncusSandboxOptionsTests
{
    [Fact]
    public void Validate_AcceptsDefaults()
    {
        Assert.Empty(IncusSandboxOptions.Validate(new IncusSandboxOptions()));
    }

    [Fact]
    public void Validate_RejectsUnsafeNamesPathsAndLimits()
    {
        var options = new IncusSandboxOptions
        {
            ProjectName = new string('p', 43),
            StoragePoolName = "pool\nname",
            InstanceNamePrefix = "-unsafe",
            BaselineNamePrefix = "unsafe/path",
            StagingDirectory = "relative/staging",
            GuestHome = "/home/../root",
            GuestUserId = 0,
            GuestGroupId = 0,
            OperationTimeout = TimeSpan.Zero,
            ExecTimeout = TimeSpan.Zero,
            ImageProvisioningTimeout = TimeSpan.Zero,
            MaxConcurrentOperations = 0,
            MaxCliStdoutBytes = 1,
            MaxCliStderrBytes = int.MaxValue,
            BaselineCpus = 0,
            BaselineMemoryBytes = 1,
            BaselineDiskBytes = 1,
            MaxSnapshotBytes = 0,
            MaxSnapshotEntries = 0,
            MaxReadinessProbeEntries = 0,
            NetworkProfiles = new Dictionary<string, string>
            {
                ["internet-only"] = "bridge-name-is-too-long",
            },
        };

        var errors = IncusSandboxOptions.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("ProjectName", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("StoragePoolName", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("InstanceNamePrefix", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("BaselineNamePrefix", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("StagingDirectory", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("GuestHome", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("GuestUserId", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("OperationTimeout", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ExecTimeout", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ImageProvisioningTimeout", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("MaxConcurrentOperations", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("MaxCliStdoutBytes", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("MaxCliStderrBytes", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("BaselineCpus", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("BaselineMemoryBytes", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("BaselineDiskBytes", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("MaxSnapshotBytes", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("MaxSnapshotEntries", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("MaxReadinessProbeEntries", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("NetworkProfiles", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("/work/../root")]
    [InlineData("/work/./repo")]
    [InlineData("//etc")]
    [InlineData("/etc//shadow")]
    [InlineData("/work/")]
    public void IsAbsoluteGuestPath_RejectsAmbiguousPaths(string path)
    {
        Assert.False(IncusSandboxOptions.IsAbsoluteGuestPath(path));
    }

    [Fact]
    public void Validate_RejectsExtraCloudInitThatRedefinesGeneratedKeys()
    {
        var errors = IncusSandboxOptions.Validate(new IncusSandboxOptions
        {
            ExtraCloudInit = "packages: []\rwrite_files: []",
        });

        Assert.Contains(errors, error => error.StartsWith("ExtraCloudInit is invalid:", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsDefaultProjectAndUnencodableRestrictedRoots()
    {
        var errors = IncusSandboxOptions.Validate(new IncusSandboxOptions
        {
            ProjectName = "default",
            AllowedHostMountRoots = ["/", "/srv/codeybox,other", "/srv/control\nroot"],
            StagingDirectory = "/var/lib/codeybox,staging",
        });

        Assert.Contains(errors, error =>
            error.Contains("dedicated non-default", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Contains("filesystem root", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.StartsWith("Each AllowedHostMountRoots", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.StartsWith("StagingDirectory", StringComparison.Ordinal));
    }
}
