using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public static class QuotaRouter
{
    public static bool WouldAllow(
        double availablePct,
        bool recentFailure,
        QuotaRouterOptions options,
        double? estimatedIterPctCost = null,
        double reservedQuotaPct = 0)
    {
        if (recentFailure)
            return false;

        if (availablePct >= 0)
        {
            var effectiveAvailablePct = availablePct - Math.Max(0, reservedQuotaPct);
            if (effectiveAvailablePct < options.MinQuotaPct)
                return false;

            if (estimatedIterPctCost is { } estimate
                && estimate > 0
                && effectiveAvailablePct - estimate < options.MinQuotaPct)
            {
                return false;
            }

            return true;
        }

        return options.UnknownPolicy switch
        {
            QuotaUnknownPolicy.FailOpen => true,
            QuotaUnknownPolicy.FailCautious => false,
            _ => !recentFailure,
        };
    }
}
