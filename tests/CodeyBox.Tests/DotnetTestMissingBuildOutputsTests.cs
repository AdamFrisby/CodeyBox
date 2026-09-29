using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// A <c>dotnet test --no-build</c> refusal against a <c>bin/</c> test assembly
/// means the build gate never produced the assemblies under test. The
/// classifier must report it as "build outputs missing" on the infrastructure
/// path (never deterministic/configuration), while genuine invocation faults
/// keep their deterministic configuration routing.
/// </summary>
public sealed class DotnetTestMissingBuildOutputsTests
{
    private static readonly IReadOnlyList<string> NoBuildArgv = ["dotnet", "test", "--no-build"];
    private static readonly IReadOnlyList<string> BuildArgv = ["dotnet", "test"];

    private static AuditResultClassificationContext ContextFor(
        IReadOnlyList<string> argv,
        string output,
        IReadOnlyList<string>? executedArgv = null) =>
        new(
            "csharp:test-pass",
            argv,
            new SandboxExecResult(1, output, string.Empty),
            output,
            new AuditFinding("csharp:test-pass", AuditSeverity.Error, "command failed", "exit 1"),
            executedArgv ?? argv);

    [Fact]
    public void NoBuildBinAssemblyInvalid_ClassifiedAsBuildOutputsMissing()
    {
        var output = "The argument /work/tests/CodeyBox.Tests/bin/Debug/net10.0/CodeyBox.Tests.dll is invalid.\n"
            + "Please use the /help option to check the list of valid arguments.";
        var classifier = new DotnetTestCommandResultClassifier();

        var ex = Assert.Throws<AuditUnavailableException>(
            () => classifier.ClassifyFailedCommand(ContextFor(NoBuildArgv, output)));

        Assert.Contains("build outputs missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ex.IsDeterministic);
    }

    [Fact]
    public void NoBuildBinTestSourceNotFound_ClassifiedAsBuildOutputsMissing()
    {
        var output = "The test source file \"/work/tests/CodeyBox.Tests/bin/Debug/net10.0/CodeyBox.Tests.dll\" was not found.";
        var classifier = new DotnetTestCommandResultClassifier();

        var ex = Assert.Throws<AuditUnavailableException>(
            () => classifier.ClassifyFailedCommand(ContextFor(NoBuildArgv, output)));

        Assert.Contains("build outputs missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ex.IsDeterministic);
    }

    [Fact]
    public void NoBuildNonBinInvocationError_StaysDeterministicConfiguration()
    {
        var output = "MSB1001: Unknown switch.";
        var classifier = new DotnetTestCommandResultClassifier();

        var ex = Assert.Throws<AuditUnavailableException>(
            () => classifier.ClassifyFailedCommand(ContextFor(NoBuildArgv, output)));

        Assert.Contains("test runner invocation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public void BinAssemblyInvalidWithoutNoBuild_StaysDeterministicConfiguration()
    {
        var output = "The argument /work/tests/CodeyBox.Tests/bin/Debug/net10.0/CodeyBox.Tests.dll is invalid.\n"
            + "Please use the /help option to check the list of valid arguments.";
        var classifier = new DotnetTestCommandResultClassifier();

        var ex = Assert.Throws<AuditUnavailableException>(
            () => classifier.ClassifyFailedCommand(ContextFor(BuildArgv, output)));

        Assert.Contains("test runner invocation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public void MissingBuildOutputsHelper_DetectsNoBuildBinRefusal()
    {
        var output = "The argument /work/tests/CodeyBox.Tests/bin/Debug/net10.0/CodeyBox.Tests.dll is invalid.";
        Assert.True(MissingBuildOutputs.IsMissingBuildOutputs(NoBuildArgv, output));
        Assert.True(MissingBuildOutputs.IsMissingBuildOutputsMessage("command: dotnet test --no-build", output));
        Assert.False(MissingBuildOutputs.IsMissingBuildOutputs(BuildArgv, output));
        Assert.False(MissingBuildOutputs.IsMissingBuildOutputs(NoBuildArgv, "MSB1001: Unknown switch."));
        Assert.False(MissingBuildOutputs.IsMissingBuildOutputsMessage("command: dotnet test", output));
    }
}
