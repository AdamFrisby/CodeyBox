using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.DotnetTestRunnerPlugin;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the 2026-09-14 <c>csharp:test-pass</c> incident: the
/// gate's <c>dotnet test</c> invocation reached the runner with a built test
/// assembly as a bare positional argument, VSTest rejected it
/// (<c>The argument ... is invalid</c>, exit 1, zero tests executed), the
/// diagnostic reported only <c>dotnet test --no-build</c> (omitting the failing
/// argument), and the deterministic refusal burned seven recovery attempts
/// before surfacing.
///
/// Locks all three fixes: the gate never constructs the assembly form (guard
/// at the sink), the reported command string is byte-identical to the argument
/// vector actually executed, and an argument-validation refusal is classified
/// deterministic so it surfaces immediately without consuming recovery budget.
/// </summary>
public sealed class DotnetTestInvocationRegressionTests
{
    public static TheoryData<string[], string?> BareAssemblyCases => new()
    {
        // The rejected form: a bare assembly positional flips `dotnet test`
        // into VSTest passthrough, which rejects the source.
        { ["dotnet", "test", "--no-build", "Vec.Tests.dll"], "Vec.Tests.dll" },
        { ["dotnet", "test", "Vec.Tests.dll"], "Vec.Tests.dll" },
        { ["dotnet", "test", "--no-build", "/tmp/vec/Vec.Tests.DLL"], "/tmp/vec/Vec.Tests.DLL" },
        { ["dotnet", "test", "--", "Vec.Tests.dll"], "Vec.Tests.dll" },
        // The intended form: project/solution/discovery targets are allowed.
        { ["dotnet", "test", "--no-build"], null },
        { ["dotnet", "test", "Vec.Tests.csproj", "--no-build"], null },
        { ["dotnet", "test", "CodeyBox.slnx", "--no-build"], null },
        { ["dotnet", "test", "Vec.sln", "--no-build"], null },
        // Option tokens are never positionals, including the `--opt=value` shape.
        { ["dotnet", "test", "--filter=Foo.dll"], null },
        // A `--filter` VALUE may legitimately end in `.dll` (a test in a
        // namespace segment named `dll`); it is not a test source.
        { ["dotnet", "test", "--no-build", "--filter", "FullyQualifiedName=My.dll"], null },
        // Out of scope: not a `dotnet test` command.
        { ["dotnet", "build", "--no-incremental", "Vec.Tests.dll"], null },
        { ["pytest", "tests.dll"], null },
        { ["dotnet"], null },
        { [], null },
    };

    [Theory]
    [MemberData(nameof(BareAssemblyCases))]
    public void FindBareAssemblyPositional_ClassifiesArgvShape(string[] argv, string? expected)
    {
        Assert.Equal(expected, DotnetTestArgv.FindBareAssemblyPositional(argv));
    }

    [Fact]
    public void FindBareAssemblyPositional_NullArgv_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DotnetTestArgv.FindBareAssemblyPositional(null!));
    }

    [Theory]
    [InlineData("Vec.Tests.dll")]
    [InlineData("/work/tests/CodeyBox.Tests/bin/Debug/net10.0/CodeyBox.Tests.dll")]
    public void Ctor_BareAssemblyBaseArgv_ThrowsDeterministicConfigError(string assembly)
    {
        var ex = Assert.Throws<ArgumentException>(() => new DotnetTestAuditor(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = ["dotnet", "test", "--no-build", assembly],
        }));
        Assert.Equal("opts", ex.ParamName);
        Assert.Contains("project/solution", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctor_ProjectFormBaseArgv_IsAllowed()
    {
        // The project/solution form is the form the auditor intends: an
        // explicit project target is valid and must keep working.
        var auditor = new DotnetTestAuditor(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = ["dotnet", "test", "Vec.Tests.csproj", "--no-build"],
        });
        var invocation = auditor.BuildInvocation(TestSelection.All, TestRunOptions.Default);
        Assert.Null(DotnetTestArgv.FindBareAssemblyPositional(invocation));
    }

    [Fact]
    public void BuildInvocation_AllVariants_NeverEmitBareAssemblyPositional()
    {
        // Even a filter entry that itself ends in `.dll` must only ever land
        // in the `--filter` VALUE slot — never as a positional test source.
        var auditor = new DotnetTestAuditor(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = ["dotnet", "test", "--no-build"],
        });
        var variants = new[]
        {
            auditor.BuildInvocation(TestSelection.All, TestRunOptions.Default),
            auditor.BuildInvocation(
                new TestSelection(["Ns.A", "My.dll"]),
                TestRunOptions.Default),
            auditor.BuildInvocation(
                TestSelection.All,
                new TestRunOptions { BlameHangTimeout = TimeSpan.FromMinutes(5) }),
            auditor.TestSuite.EnumerationArgv,
            auditor.Argv,
        };
        foreach (var invocation in variants)
            Assert.Null(DotnetTestArgv.FindBareAssemblyPositional(invocation));
    }

    [Fact]
    public async Task UnwrappedFinding_ReportsExecutedArgvByteIdentical()
    {
        var auditor = GateAuditor(selfHeal: false);
        var sandbox = new CapturingSandbox(_ => new SandboxExecResult(1, "simulated tool failure\n", ""));
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        var expected = string.Join(' ', sandbox.Captured.Single().Argv);
        Assert.Equal("command exited 1", finding.Title);
        Assert.Contains(expected, finding.Description, StringComparison.Ordinal);
        Assert.Equal("dotnet test --no-build", expected);
    }

    [Fact]
    public async Task WrappedFinding_ReportsExecutedArgvByteIdentical()
    {
        // Production shape: the NuGet-home self-heal wraps the invocation, so
        // the executed vector differs from the configured one. The diagnostic
        // must report what the sandbox received, not the pre-wrap argv.
        var auditor = GateAuditor(selfHeal: true);
        var sandbox = new CapturingSandbox(_ => new SandboxExecResult(1, "simulated tool failure\n", ""));
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        var executed = sandbox.Captured.Single().Argv;
        Assert.Equal("sh", executed[0]);
        Assert.NotEqual<string>(["dotnet", "test", "--no-build"], [.. executed]);
        Assert.Equal("command exited 1", finding.Title);
        Assert.Contains(string.Join(' ', executed), finding.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunnerRefusal_ReportsExecutedCommand_AndIsDeterministic(bool selfHeal)
    {
        var auditor = GateAuditor(selfHeal);
        const string output = """
            The following arguments have been ignored : "--no-build"
            VSTest version 18.7.0 (x64)

            Test run for /work/tests/CodeyBox.Tests/bin/Debug/net10.0/CodeyBox.Tests.dll (.NETCoreApp,Version=v10.0)
            The argument /work/tests/CodeyBox.Tests/bin/Debug/net10.0/CodeyBox.Tests.dll is invalid. Please use the /help option to check the list of valid arguments.
            """;
        var sandbox = new CapturingSandbox(_ => new SandboxExecResult(1, output, ""));

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        var executed = string.Join(' ', sandbox.Captured.Single().Argv);
        Assert.EndsWith($"(command: {executed})", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        if (!selfHeal)
            Assert.EndsWith("(command: dotnet test --no-build)", ex.Message, StringComparison.Ordinal);
    }

    private static DotnetTestAuditor GateAuditor(bool selfHeal) =>
        new(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = ["dotnet", "test", "--no-build"],
            Role = AuditorRole.BuildTestGate,
            BuildTestGateEvidence = BuildTestGateEvidence.Test,
            SelfHealNuGetHome = selfHeal,
        });

    private static AuditContext FakeContext() =>
        new(WorkItemId.New(), "feature", "main", 1, "do x");

    private sealed class CapturingSandbox(Func<SandboxExec, SandboxExecResult> onExec) : ISandbox
    {
        private readonly Func<SandboxExec, SandboxExecResult> _onExec = onExec;
        public List<SandboxExec> Captured { get; } = [];
        public string Id => "capturing";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count >= 3
                && exec.Argv[0] == "sh"
                && exec.Argv[1] == "-c"
                && exec.Argv[2].Contains("command -v", StringComparison.Ordinal))
            {
                return Task.FromResult(new SandboxExecResult(0, "/usr/bin/dotnet\n", ""));
            }

            Captured.Add(exec);
            return Task.FromResult(_onExec(exec));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
