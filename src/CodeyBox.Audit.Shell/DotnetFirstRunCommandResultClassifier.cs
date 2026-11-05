using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

/// <summary>
/// Result classifier for <c>dotnet</c> shell auditors that have no richer
/// command-specific classifier. A plain non-zero exit keeps the generic
/// command-failure result; the classifier exists to recognize .NET CLI
/// first-run infrastructure failures — the <c>NuGet-Migrations</c>
/// named-mutex crash and the bare first-run banner — which fire inside
/// <c>DotnetFirstTimeUseConfigurer</c> before the command evaluates the
/// tree. Those route to <see cref="AuditUnavailableException"/>
/// (infrastructure, retried without consuming a rework iteration) rather
/// than a blocking finding against the diff. See
/// <see cref="DotnetFirstRunFailure"/> for the signatures and the
/// code-failure guard.
/// </summary>
public sealed class DotnetFirstRunCommandResultClassifier : IAuditResultClassifier
{
    public AuditResult? ClassifyFailedCommand(AuditResultClassificationContext context)
    {
        DotnetFirstRunFailure.ThrowIfInfrastructureFailure(context);
        return null;
    }
}
