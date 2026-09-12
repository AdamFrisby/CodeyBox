using CodeyBox.Audit;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AuditPhaseLadder"/>: phase membership derives from
/// each auditor's declared <see cref="IAuditor.Targets"/>, and cost class
/// derives from declared capabilities (<see cref="IAuditor.Role"/>,
/// <see cref="IAuditor.Required"/>, <see cref="IAuditor.Kind"/>) — never
/// from concrete auditor types.
/// </summary>
public sealed class AuditPhaseLadderTests
{
    private sealed class DeclaredAuditor(
        string name,
        IReadOnlySet<AuditTarget> targets,
        string kind = "tool",
        AuditCapabilities required = AuditCapabilities.None,
        AuditorRole role = AuditorRole.None) : IAuditor
    {
        public string Name { get; } = name;
        public string Kind { get; } = kind;
        public AuditCapabilities Required { get; } = required;
        public AuditorRole Role { get; } = role;
        public IReadOnlySet<AuditTarget> Targets { get; } = targets;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    [Fact]
    public void SelectCodeStage_OnlyTakesCodeDeclaringAuditors()
    {
        var auditors = new IAuditor[]
        {
            new DeclaredAuditor("code", AuditTargets.CodeOnly),
            new DeclaredAuditor("plan", AuditTargets.PlanOnly),
            new DeclaredAuditor("deploy", AuditTargets.DeploymentOnly),
            new DeclaredAuditor("both", AuditTargets.CodeAndDeployment),
        };

        var code = AuditPhaseLadder.SelectCodeStage(auditors);

        Assert.Equal(["code", "both"], code.Select(a => a.Name));
    }

    [Fact]
    public void SelectDeploymentStage_OnlyTakesDeploymentDeclaringAuditors()
    {
        var auditors = new IAuditor[]
        {
            new DeclaredAuditor("code", AuditTargets.CodeOnly),
            new DeclaredAuditor("plan", AuditTargets.PlanOnly),
            new DeclaredAuditor("deploy", AuditTargets.DeploymentOnly),
            new DeclaredAuditor("both", AuditTargets.CodeAndDeployment),
        };

        var deployment = AuditPhaseLadder.SelectDeploymentStage(auditors);

        Assert.Equal(["deploy", "both"], deployment.Select(a => a.Name));
    }

    [Fact]
    public void RunsInStages_DefaultAuditor_IsCodeOnly()
    {
        var auditor = new DeclaredAuditor("plain", AuditTargets.CodeOnly);

        Assert.True(AuditPhaseLadder.RunsInCodeStage(auditor));
        Assert.False(AuditPhaseLadder.RunsInDeploymentStage(auditor));
    }

    [Theory]
    [InlineData(AuditorRole.BuildTestGate, "tool", (int)AuditCapabilities.None, AuditCostClass.MechanicalGate)]
    [InlineData(AuditorRole.None, "tool", (int)AuditCapabilities.None, AuditCostClass.Tool)]
    [InlineData(AuditorRole.None, "llm", (int)AuditCapabilities.None, AuditCostClass.Reviewer)]
    [InlineData(AuditorRole.None, "LLM", (int)AuditCapabilities.None, AuditCostClass.Reviewer)]
    [InlineData(AuditorRole.None, "tool", (int)AuditCapabilities.AgentCredentials, AuditCostClass.Reviewer)]
    [InlineData(AuditorRole.None, "tool", (int)(AuditCapabilities.AgentCredentials | AuditCapabilities.Network), AuditCostClass.Reviewer)]
    public void CostClassOf_DerivesFromDeclaredCapabilitiesOnly(
        AuditorRole role, string kind, int required, AuditCostClass expected)
    {
        var auditor = new DeclaredAuditor(
            "x", AuditTargets.CodeOnly, kind, (AuditCapabilities)required, role);

        Assert.Equal(expected, AuditPhaseLadder.CostClassOf(auditor));
    }

    [Fact]
    public void OrderDeploymentStage_RunsSmokeProbesBeforeExploration()
    {
        var auditors = new IAuditor[]
        {
            new DeclaredAuditor("cua-explore", AuditTargets.DeploymentOnly, kind: "llm", required: AuditCapabilities.AgentCredentials),
            new DeclaredAuditor("smoke", AuditTargets.DeploymentOnly, kind: "tool"),
            new DeclaredAuditor("build-gate", AuditTargets.DeploymentOnly, kind: "tool", role: AuditorRole.BuildTestGate),
        };

        var ordered = AuditPhaseLadder.OrderDeploymentStage(auditors);

        Assert.Equal(["build-gate", "smoke", "cua-explore"], ordered.Select(a => a.Name));
    }

    [Fact]
    public void OrderDeploymentStage_IsStableWithinATier()
    {
        var auditors = new IAuditor[]
        {
            new DeclaredAuditor("b-tool", AuditTargets.DeploymentOnly),
            new DeclaredAuditor("a-tool", AuditTargets.DeploymentOnly),
        };

        var ordered = AuditPhaseLadder.OrderDeploymentStage(auditors);

        Assert.Equal(["b-tool", "a-tool"], ordered.Select(a => a.Name));
    }

    [Fact]
    public void DeploymentTarget_RoundTripsThroughParseOrCodeOnly()
    {
        var targets = AuditTargets.ParseOrCodeOnly(["deployment"]);

        Assert.True(targets.Contains(AuditTarget.Deployment));
        Assert.True(AuditTargetSemantics.IsDeploymentReview(AuditTarget.Deployment));
        Assert.False(AuditTargetSemantics.IsCodeReview(AuditTarget.Deployment));
    }
}
