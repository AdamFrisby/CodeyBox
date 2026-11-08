using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="BaselineStalePinEvaluator"/> — the pickup-time
/// policy that clears a work item's baseline pin when its baked toolchain no
/// longer matches live config. The motivating incident: an agent CLI upgrade
/// paired with a model switch left items pinned to the old CLI running a
/// model the old CLI does not list, failing the dispatch as a generic exit 1.
/// </summary>
public sealed class BaselineStalePinEvaluatorTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Devin = AgentKind.Devin;

    private const BaselineStalePinMode DefaultMode = BaselineStalePinMode.MigrateOnToolchainAndModelChange;

    private static string ScopedPin(string toolchainHash, string providerRef = "vm-1") =>
        BaselinePin.FormatScopedPin("incus", toolchainHash, providerRef);

    private const string OldToolchain = "aaaaaaaaaaaa";
    private const string NewToolchain = "bbbbbbbbbbbb";

    [Fact]
    public void NoPin_Keeps()
    {
        var decision = BaselineStalePinEvaluator.Evaluate(
            null, Claude, "m1", ScopedPin(NewToolchain), Claude, "m1", DefaultMode);
        Assert.False(decision.ShouldMigrate);
        Assert.False(decision.RefreshModelAttribution);
        Assert.Equal(BaselineStalePinEvaluator.ReasonNoPin, decision.Reason);
    }

    [Fact]
    public void DisabledMode_KeepsEvenWhenStale()
    {
        var decision = BaselineStalePinEvaluator.Evaluate(
            ScopedPin(OldToolchain), Claude, "old-model",
            ScopedPin(NewToolchain), Claude, "new-model",
            BaselineStalePinMode.Disabled);
        Assert.False(decision.ShouldMigrate);
        Assert.Equal(BaselineStalePinEvaluator.ReasonDisabled, decision.Reason);
    }

    [Fact]
    public void AgentChanged_Migrates()
    {
        var decision = BaselineStalePinEvaluator.Evaluate(
            ScopedPin(OldToolchain), Claude, "m1",
            ScopedPin(OldToolchain), Devin, "m1", DefaultMode);
        Assert.True(decision.ShouldMigrate);
        Assert.Equal(BaselineStalePinEvaluator.ReasonAgentChanged, decision.Reason);
    }

    [Fact]
    public void UnattributedPin_Migrates()
    {
        var decision = BaselineStalePinEvaluator.Evaluate(
            ScopedPin(OldToolchain), null, "m1",
            ScopedPin(OldToolchain), Claude, "m1", DefaultMode);
        Assert.True(decision.ShouldMigrate);
        Assert.Equal(BaselineStalePinEvaluator.ReasonUnattributed, decision.Reason);
    }

    [Fact]
    public void SameRef_Keeps_AndRefreshesModelWhenChanged()
    {
        var pin = ScopedPin(NewToolchain);
        var kept = BaselineStalePinEvaluator.Evaluate(pin, Claude, "m1", pin, Claude, "m1", DefaultMode);
        Assert.False(kept.ShouldMigrate);
        Assert.False(kept.RefreshModelAttribution);

        var refreshed = BaselineStalePinEvaluator.Evaluate(pin, Claude, "m1", pin, Claude, "m2", DefaultMode);
        Assert.False(refreshed.ShouldMigrate);
        Assert.True(refreshed.RefreshModelAttribution);
    }

    [Fact]
    public void ToolchainChanged_AndModelChanged_Migrates()
    {
        var decision = BaselineStalePinEvaluator.Evaluate(
            ScopedPin(OldToolchain), Claude, "old-model",
            ScopedPin(NewToolchain), Claude, "new-model", DefaultMode);
        Assert.True(decision.ShouldMigrate);
        Assert.Equal(BaselineStalePinEvaluator.ReasonToolchainChanged, decision.Reason);
    }

    [Fact]
    public void ToolchainChanged_ModelUnchanged_KeepsPin()
    {
        // CLI-only upgrade: the old CLI still serves the pinned model, so no
        // re-bake is forced. Only the incident combination migrates by default.
        var decision = BaselineStalePinEvaluator.Evaluate(
            ScopedPin(OldToolchain), Claude, "same-model",
            ScopedPin(NewToolchain), Claude, "same-model", DefaultMode);
        Assert.False(decision.ShouldMigrate);
        Assert.Equal(BaselineStalePinEvaluator.ReasonToolchainChangedModelKept, decision.Reason);
    }

    [Fact]
    public void ToolchainChanged_AlwaysMigrateMode_IgnoresModel()
    {
        var decision = BaselineStalePinEvaluator.Evaluate(
            ScopedPin(OldToolchain), Claude, "same-model",
            ScopedPin(NewToolchain), Claude, "same-model",
            BaselineStalePinMode.MigrateOnToolchainChange);
        Assert.True(decision.ShouldMigrate);
    }

    [Fact]
    public void ToolchainChanged_UnattributedModel_Migrates()
    {
        // A pin predating model attribution cannot prove it still serves its
        // model — fail safe toward migrating.
        var decision = BaselineStalePinEvaluator.Evaluate(
            ScopedPin(OldToolchain), Claude, null,
            ScopedPin(NewToolchain), Claude, "any-model", DefaultMode);
        Assert.True(decision.ShouldMigrate);
    }

    [Fact]
    public void SameToolchain_DifferentProviderRef_Keeps()
    {
        // Non-toolchain config edits (extra runcmd, profiles) change the ref
        // without changing the CLI: the pin still serves.
        var decision = BaselineStalePinEvaluator.Evaluate(
            ScopedPin(NewToolchain, "vm-1"), Claude, "m1",
            ScopedPin(NewToolchain, "vm-2"), Claude, "m2", DefaultMode);
        Assert.False(decision.ShouldMigrate);
        Assert.True(decision.RefreshModelAttribution);
    }

    [Fact]
    public void LegacyRefChanged_ModelChanged_Migrates()
    {
        var decision = BaselineStalePinEvaluator.Evaluate(
            "cb-old-baseline", Claude, "old-model",
            "cb-new-baseline", Claude, "new-model", DefaultMode);
        Assert.True(decision.ShouldMigrate);
        Assert.Equal(BaselineStalePinEvaluator.ReasonLegacyRefChanged, decision.Reason);
    }

    [Fact]
    public void LegacyRefChanged_ModelUnchanged_Keeps()
    {
        var decision = BaselineStalePinEvaluator.Evaluate(
            "cb-old-baseline", Claude, "same-model",
            "cb-new-baseline", Claude, "same-model", DefaultMode);
        Assert.False(decision.ShouldMigrate);
        Assert.Equal(BaselineStalePinEvaluator.ReasonLegacyRefChangedModelKept, decision.Reason);
    }

    [Fact]
    public void NoLiveBaseline_Migrates()
    {
        var decision = BaselineStalePinEvaluator.Evaluate(
            ScopedPin(OldToolchain), Claude, "m1", null, Claude, "m1", DefaultMode);
        Assert.True(decision.ShouldMigrate);
        Assert.Equal(BaselineStalePinEvaluator.ReasonNoLiveBaseline, decision.Reason);
    }

    [Theory]
    [InlineData(null, "m1", true)]   // legacy row: unverifiable, counts as changed
    [InlineData("m1", "m1", false)]  // exact match only: ids are opaque CLI tokens
    [InlineData("m1", "M1", true)]
    [InlineData("m1", null, true)]
    [InlineData(null, null, true)]
    public void ModelChanged_ComparesExactly(string? pinned, string? dispatch, bool expected) =>
        Assert.Equal(expected, BaselineStalePinEvaluator.ModelChanged(pinned, dispatch));
}
