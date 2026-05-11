using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public static class QuotaRouter
{
    public static bool WouldAllow(double availablePct, bool recentFailure, QuotaRouterOptions options)
    {
        if (recentFailure)
            return false;
        if (availablePct >= options.MinQuotaPct)
            return true;
        if (availablePct >= 0)
            return false;

        return options.UnknownPolicy switch
        {
            QuotaUnknownPolicy.FailOpen => true,
            QuotaUnknownPolicy.FailCautious => false,
            _ => !recentFailure,
        };
    }
}
