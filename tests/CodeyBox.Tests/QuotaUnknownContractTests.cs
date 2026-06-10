using System.Net;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the de-magicked quota-unknown contract: a real reading is
/// <see cref="AgentQuotaSnapshot.IsKnown"/>; an unknown carries an explicit
/// <see cref="QuotaUnknownReason"/>; a negative percentage is never "known"
/// even if a reason was never set; and HTTP status maps to transient/permanent.
/// </summary>
public sealed class QuotaUnknownContractTests
{
    [Fact]
    public void RealReading_IsKnown()
    {
        var s = new AgentQuotaSnapshot { AvailablePct = 42 };
        Assert.True(s.IsKnown);
        Assert.Null(s.Unknown);
    }

    [Fact]
    public void UnknownSnapshot_IsNotKnown_AndCarriesReason()
    {
        var s = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Permanent, "403");
        Assert.False(s.IsKnown);
        Assert.Equal(QuotaUnknownReason.Permanent, s.Unknown);
    }

    [Fact]
    public void NegativePct_WithoutReason_IsTreatedAsUnknown()
    {
        // Defensive invariant: a percentage is never negative, so a bare -1 is
        // not a reading even if a probe forgot to set the reason.
        var s = new AgentQuotaSnapshot { AvailablePct = -1 };
        Assert.False(s.IsKnown);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, QuotaUnknownReason.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, QuotaUnknownReason.Transient)]
    [InlineData(HttpStatusCode.RequestTimeout, QuotaUnknownReason.Transient)]
    [InlineData(HttpStatusCode.Unauthorized, QuotaUnknownReason.Permanent)]
    [InlineData(HttpStatusCode.Forbidden, QuotaUnknownReason.Permanent)]
    [InlineData(HttpStatusCode.NotFound, QuotaUnknownReason.Permanent)]
    [InlineData(HttpStatusCode.BadRequest, QuotaUnknownReason.Permanent)]
    public void FromHttpStatus_ClassifiesTransientVsPermanent(HttpStatusCode status, QuotaUnknownReason expected)
    {
        Assert.Equal(expected, QuotaUnknownReasons.FromHttpStatus(status));
    }
}
