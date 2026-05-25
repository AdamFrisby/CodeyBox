using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public static class QuotaRouter
{
    public static bool WouldAllow(
        double availablePct,
        bool recentFailure,
        QuotaRouterOptions options,
        double? estimatedIterPctCost = null)
    {
        if (recentFailure)
            return false;

        if (availablePct >= 0)
        {
            if (availablePct < options.MinQuotaPct)
                return false;

            if (estimatedIterPctCost is { } estimate
                && estimate > 0
                && availablePct - estimate < options.MinQuotaPct)
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
