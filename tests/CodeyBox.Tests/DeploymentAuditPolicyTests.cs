using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pure-policy tests for the deployment stage of the cost-ordered audit
/// ladder. Every leg of <see cref="DeploymentAuditPolicy.ShouldProvision"/>
/// is a cheap-fail gate: a blocking code stage must provision ZERO
/// deployments, and the phase must not exist without the toggle, a recipe,
/// and at least one deployment-targeted auditor.
/// </summary>
public sealed class DeploymentAuditPolicyTests
{
    private static readonly DeploymentRecipe Recipe = new()
    {
        Kind = DeploymentKinds.WebApp,
        ImageReference = "ubuntu-22.04",
    };

    [Fact]
    public void ShouldProvision_AllLegsSatisfied_Provisions()
    {
        var decision = DeploymentAuditPolicy.ShouldProvision(
            deploymentAuditEnabled: true, recipe: Recipe, hasDeploymentAuditors: true, codeStageClean: true);

        Assert.True(decision.Provision);
        Assert.Equal("provision", decision.Reason);
    }

    [Fact]
    public void ShouldProvision_BlockingCodeStage_ProvisionsNothing()
    {
        var decision = DeploymentAuditPolicy.ShouldProvision(
            deploymentAuditEnabled: true, recipe: Recipe, hasDeploymentAuditors: true, codeStageClean: false);

        Assert.False(decision.Provision);
        Assert.Equal("code-stage-blocking", decision.Reason);
    }

    [Fact]
    public void ShouldProvision_ToggleOff_ProvisionsNothing()
    {
        var decision = DeploymentAuditPolicy.ShouldProvision(
            deploymentAuditEnabled: false, recipe: Recipe, hasDeploymentAuditors: true, codeStageClean: true);

        Assert.False(decision.Provision);
        Assert.Equal("deployment-auditing-disabled", decision.Reason);
    }

    [Fact]
    public void ShouldProvision_NoRecipe_ProvisionsNothing()
    {
        var decision = DeploymentAuditPolicy.ShouldProvision(
            deploymentAuditEnabled: true, recipe: null, hasDeploymentAuditors: true, codeStageClean: true);

        Assert.False(decision.Provision);
        Assert.Equal("no-deployment-recipe", decision.Reason);
    }

    [Fact]
    public void ShouldProvision_NoDeploymentAuditors_PhaseDoesNotExist()
    {
        var decision = DeploymentAuditPolicy.ShouldProvision(
            deploymentAuditEnabled: true, recipe: Recipe, hasDeploymentAuditors: false, codeStageClean: true);

        Assert.False(decision.Provision);
        Assert.Equal("no-deployment-auditors", decision.Reason);
    }

    [Fact]
    public void EffectiveLifetime_PositiveRecipeValue_PassesThrough()
    {
        var recipe = Recipe with { MaxLifetime = TimeSpan.FromMinutes(17) };

        Assert.Equal(TimeSpan.FromMinutes(17), DeploymentAuditPolicy.EffectiveLifetime(recipe));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void EffectiveLifetime_NonPositiveRecipeValue_FallsBackToBoundedDefault(int minutes)
    {
        var recipe = Recipe with { MaxLifetime = TimeSpan.FromMinutes(minutes) };

        var lifetime = DeploymentAuditPolicy.EffectiveLifetime(recipe);

        Assert.Equal(DeploymentAuditPolicy.FallbackMaxLifetime, lifetime);
        Assert.True(lifetime > TimeSpan.Zero);
    }

    [Fact]
    public void DeadlineFor_IsStartPlusEffectiveLifetime()
    {
        var start = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        var recipe = Recipe with { MaxLifetime = TimeSpan.FromMinutes(9) };

        Assert.Equal(start + TimeSpan.FromMinutes(9), DeploymentAuditPolicy.DeadlineFor(recipe, start));
    }
}
