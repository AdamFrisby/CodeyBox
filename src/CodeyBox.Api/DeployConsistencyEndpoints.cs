using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CodeyBox.Api;

/// <summary>
/// Operator surface for deploy consistency: reports the revision baked into
/// the running binaries, the revision currently checked out on disk, and
/// whether they agree — without restarting anything. The same payload is
/// embedded in <c>/healthz</c> for anonymous load-balancer-style checks; this
/// endpoint sits behind API-key auth like the other <c>/admin/*</c> routes.
/// </summary>
internal static class DeployConsistencyEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/admin/deploy-consistency", GetAsync);
    }

    private static IResult GetAsync(DeployConsistencyService service)
    {
        var report = service.Refresh();
        return Results.Ok(BuildPayload(report));
    }

    internal static object BuildPayload(DeployConsistencyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        bool? consistent = report.Status switch
        {
            DeployConsistencyStatus.Consistent => true,
            DeployConsistencyStatus.Diverged => false,
            _ => null,
        };
        return new
        {
            builtRevision = report.BuiltRevision,
            checkoutRevision = report.CheckoutRevision,
            consistent,
            status = report.Status.ToString(),
        };
    }
}
