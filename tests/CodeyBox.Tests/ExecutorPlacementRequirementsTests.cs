using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// The placement-requirements assembler is shared: executor dispatch
/// (<c>FromRequest</c>) and sandbox acquisition (<c>FromValues</c>) run the
/// same normalisation and bounds, so the two placement paths cannot drift.
/// </summary>
public sealed class ExecutorPlacementRequirementsTests
{
    [Fact]
    public void FromValues_EquivalentToFromRequest()
    {
        var request = new ExecutorPhaseRequest
        {
            WorkItemId = WorkItemId.New().ToString(),
            Phase = "work",
            Attempt = 0,
            RepositoryId = WorkItemId.New().ToString(),
            PayloadJson = "{}",
            RequiredCredential = "  claude ",
            RequiredNetworkProfile = " work ",
            RequiredCapabilities = [" gpu ", "", "  "],
        };

        var fromRequest = ExecutorPlacementRequirements.FromRequest(request);
        var fromValues = ExecutorPlacementRequirements.FromValues(
            "  claude ", " work ", [" gpu ", "", "  "]);

        Assert.Equal(fromRequest.RequiredCredential, fromValues.RequiredCredential);
        Assert.Equal(fromRequest.RequiredNetworkProfile, fromValues.RequiredNetworkProfile);
        Assert.Equal(fromRequest.RequiredCapabilities.ToList(), fromValues.RequiredCapabilities.ToList());
        Assert.Equal("claude", fromValues.RequiredCredential);
        Assert.Equal("work", fromValues.RequiredNetworkProfile);
        Assert.Equal(["gpu"], fromValues.RequiredCapabilities.ToList());
    }

    [Fact]
    public void FromValues_BlanksBecomeNullAndEmpty()
    {
        var requirements = ExecutorPlacementRequirements.FromValues("  ", null, []);

        Assert.Null(requirements.RequiredCredential);
        Assert.Null(requirements.RequiredNetworkProfile);
        Assert.Empty(requirements.RequiredCapabilities);
    }

    [Fact]
    public void FromValues_BoundViolation_NamesValueParameter()
    {
        var tooLong = new string('x', ExecutorPlacementRequirements.MaxEntryLength + 1);

        Assert.Equal(
            "requiredCredential",
            Assert.Throws<ArgumentException>(() => ExecutorPlacementRequirements.FromValues(tooLong, null, [])).ParamName);
        Assert.Equal(
            "requiredNetworkProfile",
            Assert.Throws<ArgumentException>(() => ExecutorPlacementRequirements.FromValues(null, tooLong, [])).ParamName);
        Assert.Equal(
            "requiredCapabilities",
            Assert.Throws<ArgumentException>(() => ExecutorPlacementRequirements.FromValues(null, null, [tooLong])).ParamName);
    }

    [Fact]
    public void FromRequest_BoundViolation_KeepsRequestParameter()
    {
        var tooLong = new string('x', ExecutorPlacementRequirements.MaxEntryLength + 1);
        var request = new ExecutorPhaseRequest
        {
            WorkItemId = WorkItemId.New().ToString(),
            Phase = "work",
            Attempt = 0,
            RepositoryId = WorkItemId.New().ToString(),
            PayloadJson = "{}",
            RequiredCredential = tooLong,
        };

        Assert.Equal(
            "request",
            Assert.Throws<ArgumentException>(() => ExecutorPlacementRequirements.FromRequest(request)).ParamName);
    }
}
