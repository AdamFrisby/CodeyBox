using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Audit.Shell;

/// <summary>
/// Deep auditor that runs <c>dotnet list package --vulnerable --include-transitive</c>
/// against every project file in the working tree and surfaces known CVEs as
/// structured audit findings. Critical and High severity CVEs produce Error
/// findings; Moderate produces Warning; Low produces Info.
///
/// Requires .NET SDK in the sandbox. When the SDK is absent the exit code is
/// 127 and a single Info finding is emitted instead of failing the release.
/// </summary>
public sealed class DepsCveScanDeepAuditor : IDeepAuditor
{
    public string Name => "deps-cve-scan";
    public string Kind => "shell";
    public AuditCapabilities Required => AuditCapabilities.None;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        DeepAuditContext context,
        CancellationToken ct = default)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["dotnet", "list", "package", "--vulnerable", "--include-transitive"],
            WorkingDirectory = workingDirectory,
        }, ct);

        // dotnet CLI not installed in the sandbox — skip with Info.
        if (result.ExitCode == 127)
            return new AuditResult(true, [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Info,
                Title: "dotnet SDK not installed in sandbox; CVE scan skipped",
                Description: "Install the .NET SDK in the sandbox image to enable CVE scanning.")],
                RawOutput: result.Stdout + result.Stderr);

        // Any other non-zero exit means the scan itself failed (no solution file, runtime error, etc.).
        // Treat as a blocking finding so the gate is not silently bypassed.
        if (result.ExitCode != 0)
            return new AuditResult(false, [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: "CVE scan command failed; treating as blocking",
                Description: $"'dotnet list package --vulnerable' exited with code {result.ExitCode}. " +
                             "Ensure a valid .NET solution or project file exists in the working directory. " +
                             $"Stderr: {result.Stderr}")],
                RawOutput: result.Stdout + result.Stderr);

        var rawOutput = result.Stdout + (string.IsNullOrWhiteSpace(result.Stderr) ? "" : "\n" + result.Stderr);
        var findings = ParseFindings(result.Stdout).ToList();
        var hasError = findings.Any(f => f.Severity == AuditSeverity.Error);
        return new AuditResult(!hasError, findings, RawOutput: rawOutput);
    }

    private IEnumerable<AuditFinding> ParseFindings(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) yield break;

        // `dotnet list package --vulnerable` output per vulnerable package line:
        //   > PackageName    Requested  Resolved  Severity  Advisory URL
        // The '>' prefix marks a vulnerable package.
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("> ", StringComparison.Ordinal)) continue;

            // Tokenise: package name, requested version, resolved version, severity, url
            var parts = trimmed[2..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            var package = parts[0];
            var resolved = parts.Length >= 3 ? parts[2] : "?";
            var severityText = parts.Length >= 4 ? parts[3] : "Unknown";
            var advisoryUrl = parts.Length >= 5 ? parts[4] : null;

            var severity = severityText.ToLowerInvariant() switch
            {
                "critical" or "high" => AuditSeverity.Error,
                "moderate" or "medium" => AuditSeverity.Warning,
                _ => AuditSeverity.Info,
            };

            var description = $"Package {package} version {resolved} has a known {severityText} severity CVE.";
            if (advisoryUrl is not null) description += $" Advisory: {advisoryUrl}";
            description += " Upgrade to a patched version.";

            yield return new AuditFinding(
                AuditorName: Name,
                Severity: severity,
                Title: $"CVE in {package} ({severityText})",
                Description: description,
                Location: null);
        }
    }
}
