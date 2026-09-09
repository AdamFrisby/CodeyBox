using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class SandboxExecEnvironmentRemovalTests
{
    [Fact]
    public void EnvironmentVariablesToUnset_SnapshotsAndAppliesUniqueValidatedNames()
    {
        var requested = new List<string> { "CANDIDATE_TOKEN", "_SECOND" };
        var exec = new SandboxExec
        {
            Argv = ["true"],
            EnvironmentVariablesToUnset = requested,
        };
        requested[0] = "MUTATED_AFTER_CONSTRUCTION";
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CANDIDATE_TOKEN"] = "secret",
            ["_SECOND"] = "secret-two",
            ["KEEP"] = "visible",
        };

        exec.ApplyEnvironmentRemovals(name => environment.Remove(name));

        Assert.Equal(["CANDIDATE_TOKEN", "_SECOND"], exec.EnvironmentVariablesToUnset);
        Assert.Equal(new Dictionary<string, string> { ["KEEP"] = "visible" }, environment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1INVALID")]
    [InlineData("INVALID-NAME")]
    [InlineData("NAME=value")]
    public void EnvironmentVariablesToUnset_RejectsInvalidNames(string name)
    {
        Assert.Throws<ArgumentException>(() => new SandboxExec
        {
            Argv = ["true"],
            EnvironmentVariablesToUnset = [name],
        });
    }

    [Fact]
    public void EnvironmentVariablesToUnset_RejectsDuplicatesAndOverLimitInputs()
    {
        Assert.Throws<ArgumentException>(() => new SandboxExec
        {
            Argv = ["true"],
            EnvironmentVariablesToUnset = ["DUPLICATE", "DUPLICATE"],
        });
        Assert.Throws<ArgumentException>(() => new SandboxExec
        {
            Argv = ["true"],
            EnvironmentVariablesToUnset = Enumerable.Range(
                    0,
                    SandboxExec.MaximumEnvironmentVariablesToUnset + 1)
                .Select(static index => $"REMOVE_{index}")
                .ToArray(),
        });
    }

    [Fact]
    public void EnvironmentRemoval_AppliedAfterExtraMergeMakesUnsetWin()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FROM_SPEC"] = "spec-value",
        };
        var exec = new SandboxExec
        {
            Argv = ["true"],
            ExtraEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["FROM_SPEC"] = "exec-value",
            },
            EnvironmentVariablesToUnset = ["FROM_SPEC"],
        };
        foreach (var (name, value) in exec.ExtraEnvironment)
            environment[name] = value;

        exec.ApplyEnvironmentRemovals(name => environment.Remove(name));

        Assert.False(environment.ContainsKey("FROM_SPEC"));
    }
}
