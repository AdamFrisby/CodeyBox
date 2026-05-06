using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Audit.Shell;

/// <summary>
/// Language-aware deep auditor that runs dependency vulnerability scanners for
/// the languages declared by the project. Unsupported or markerless languages
/// are skipped so a polyglot preset can be enabled without forcing every
/// scanner to run in every repository.
/// </summary>
public sealed partial class DepsCveScanDeepAuditor : IDeepAuditor
{
    private const int MaxScannerOutputChars = 1_000_000;

    private static readonly IReadOnlyDictionary<string, Scanner> Scanners =
        new Dictionary<string, Scanner>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = new(
                "csharp",
                LanguageProjectDiscovery.CSharpDiscoveryScript,
                ["dotnet", "list", "package", "--vulnerable", "--include-transitive"],
                "dotnet SDK not installed in sandbox; CVE scan skipped",
                "Install the .NET SDK in the sandbox image to enable C# CVE scanning.",
                ParseDotnetFindings,
                "'dotnet list package --vulnerable' exited with code {0}. Ensure a valid .NET solution or project file exists in the working directory."),
            ["python"] = new(
                "python",
                LanguageProjectDiscovery.PythonDiscoveryScript,
                ["sh", "-c", "if command -v pip-audit >/dev/null 2>&1; then exec pip-audit -f json; fi; if command -v safety >/dev/null 2>&1; then exec safety check --json; fi; echo 'pip-audit or safety is not installed in sandbox' >&2; exit 127"],
                "pip-audit or safety not installed in sandbox; CVE scan skipped",
                "Install pip-audit or safety in the sandbox image to enable Python CVE scanning.",
                ParsePythonFindings,
                "Python dependency CVE scanner exited with code {0} but no vulnerability records were parsed."),
            ["node"] = new(
                "node",
                LanguageProjectDiscovery.NodeDiscoveryScript,
                ["npm", "audit", "--json"],
                "npm not installed in sandbox; CVE scan skipped",
                "Install Node.js/npm in the sandbox image to enable Node CVE scanning.",
                ParseNpmFindings,
                "npm audit exited with code {0} but no vulnerability records were parsed."),
            ["go"] = new(
                "go",
                LanguageProjectDiscovery.GoDiscoveryScript,
                ["govulncheck", "-json", "./..."],
                "govulncheck not installed in sandbox; CVE scan skipped",
                "Install govulncheck in the sandbox image to enable Go CVE scanning.",
                ParseGoFindings,
                "govulncheck exited with code {0} but no vulnerability records were parsed."),
            ["rust"] = new(
                "rust",
                LanguageProjectDiscovery.RustDiscoveryScript,
                ["cargo", "audit"],
                "cargo-audit not installed in sandbox; CVE scan skipped",
                "Install cargo-audit in the sandbox image to enable Rust CVE scanning.",
                ParseCargoAuditFindings,
                "cargo audit exited with code {0} but no vulnerability records were parsed."),
        };

    public string Name => "deps-cve-scan";
    public string Kind => "shell";
    public AuditCapabilities Required => AuditCapabilities.None;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        DeepAuditContext context,
        CancellationToken ct = default)
    {
        var languages = (context.Languages is null ? ["csharp"] : context.Languages)
            .Where(ProjectAuditLanguages.IsSupported)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (languages.Count == 0)
            return new AuditResult(true, []);

        var allFindings = new List<AuditFinding>();
        var rawParts = new List<string>();

        foreach (var language in languages)
        {
            if (!Scanners.TryGetValue(language, out var scanner))
                continue;

            var discovery = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", scanner.MarkerScript],
                WorkingDirectory = workingDirectory,
            }, ct);

            var projectDirectories = ParseProjectDirectories(discovery.Stdout);
            foreach (var projectDirectory in projectDirectories)
            {
                var result = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = scanner.Argv,
                    WorkingDirectory = ResolveWorkingDirectory(workingDirectory, projectDirectory),
                }, ct);

                var rawOutput = CombinedOutput(result, out var wasTruncated);
                rawParts.Add($"## {language}:{projectDirectory}\n{rawOutput}");

                if (IsMissingScannerTool(scanner, result))
                {
                    allFindings.Add(new AuditFinding(
                        AuditorName: Name,
                        Severity: AuditSeverity.Info,
                        Title: scanner.MissingToolTitle,
                        Description: scanner.MissingToolDescription));
                    continue;
                }

                var findings = scanner.Parse(rawOutput).ToList();
                if (result.ExitCode != 0 && findings.Count == 0)
                {
                    allFindings.Add(new AuditFinding(
                        AuditorName: Name,
                        Severity: AuditSeverity.Error,
                        Title: $"{language} CVE scan command failed; treating as blocking",
                        Description: string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            scanner.FailureDescription,
                            result.ExitCode) +
                            (wasTruncated ? " Scanner output exceeded the parser limit and was truncated." : "") +
                            $" Stderr: {result.Stderr}"));
                    continue;
                }

                allFindings.AddRange(findings);
            }
        }

        var hasError = allFindings.Any(f => f.Severity == AuditSeverity.Error);
        return new AuditResult(!hasError, allFindings, RawOutput: string.Join("\n\n", rawParts));
    }

    private static IEnumerable<AuditFinding> ParseDotnetFindings(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) yield break;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("> ", StringComparison.Ordinal)) continue;

            var parts = trimmed[2..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            var package = parts[0];
            var resolved = parts.Length >= 3 ? parts[2] : "?";
            var severityText = parts.Length >= 4 ? parts[3] : "Unknown";
            var advisoryUrl = parts.Length >= 5 ? parts[4] : null;

            yield return Finding(package, resolved, severityText, advisoryUrl);
        }
    }

    private static IEnumerable<AuditFinding> ParsePythonFindings(string output)
    {
        using var doc = TryParseJson(output);
        if (doc is null) yield break;

        foreach (var dep in EnumerateArraysNamed(doc.RootElement, "dependencies"))
        {
            foreach (var item in dep.EnumerateArray())
            {
                var package = GetString(item, "name") ?? "unknown";
                var version = GetString(item, "version") ?? "?";
                if (!item.TryGetProperty("vulns", out var vulns) && !item.TryGetProperty("vulnerabilities", out vulns))
                    continue;
                if (vulns.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var vuln in vulns.EnumerateArray())
                {
                    var id = GetString(vuln, "id") ?? GetString(vuln, "vulnerability_id") ?? "vulnerability";
                    var severity = GetString(vuln, "severity") ?? "Unknown";
                    yield return Finding(package, version, severity, id);
                }
            }
        }

        foreach (var vuln in EnumerateArraysNamed(doc.RootElement, "vulnerabilities").SelectMany(a => a.EnumerateArray()))
        {
            var package = GetString(vuln, "package_name") ?? GetString(vuln, "package") ?? "unknown";
            var version = GetString(vuln, "analyzed_version") ?? GetString(vuln, "version") ?? "?";
            var id = GetString(vuln, "vulnerability_id") ?? GetString(vuln, "id") ?? "vulnerability";
            var severity = GetString(vuln, "severity") ?? "Unknown";
            yield return Finding(package, version, severity, id);
        }
    }

    private static IEnumerable<AuditFinding> ParseNpmFindings(string output)
    {
        using var doc = TryParseJson(output);
        if (doc is null) yield break;
        if (!doc.RootElement.TryGetProperty("vulnerabilities", out var vulnerabilities) ||
            vulnerabilities.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var property in vulnerabilities.EnumerateObject())
        {
            var vuln = property.Value;
            var severity = GetString(vuln, "severity") ?? "Unknown";
            var version = GetString(vuln, "range") ?? "?";
            var advisory = FirstNpmAdvisory(vuln);
            yield return Finding(property.Name, version, severity, advisory);
        }
    }

    private static IEnumerable<AuditFinding> ParseGoFindings(string output)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parsed = TryParseGoJsonFinding(line);
            if (parsed is null)
                continue;

            var (package, id) = parsed.Value;
            if (seen.Add(package + "\0" + id))
                yield return Finding(package, "?", "High", id);
        }
    }

    private static IEnumerable<AuditFinding> ParseCargoAuditFindings(string output)
    {
        foreach (var block in output.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var crate = CargoCrateRegex().Match(block);
            if (!crate.Success) continue;
            var id = CargoIdRegex().Match(block);
            var severity = CargoSeverityRegex().Match(block);
            yield return Finding(
                crate.Groups["crate"].Value,
                "?",
                severity.Success ? severity.Groups["severity"].Value : "Unknown",
                id.Success ? id.Groups["id"].Value : null);
        }
    }

    private static AuditFinding Finding(string package, string version, string severityText, string? advisory)
    {
        var severity = severityText.ToLowerInvariant() switch
        {
            "critical" or "high" => AuditSeverity.Error,
            "moderate" or "medium" => AuditSeverity.Warning,
            _ => AuditSeverity.Info,
        };

        var description = $"Package {package} version {version} has a known {severityText} severity vulnerability.";
        if (!string.IsNullOrWhiteSpace(advisory))
            description += $" Advisory: {advisory}.";
        description += " Upgrade to a patched version.";

        return new AuditFinding(
            AuditorName: "deps-cve-scan",
            Severity: severity,
            Title: $"CVE in {package} ({severityText})",
            Description: description);
    }

    private static JsonDocument? TryParseJson(string output)
    {
        var start = output.IndexOfAny(['{', '[']);
        if (start < 0) return null;
        try { return JsonDocument.Parse(output[start..]); }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<JsonElement> EnumerateArraysNamed(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.Array)
                    yield return property.Value;
                foreach (var child in EnumerateArraysNamed(property.Value, name))
                    yield return child;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var child in EnumerateArraysNamed(item, name))
                    yield return child;
            }
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? FirstNpmAdvisory(JsonElement vulnerability)
    {
        if (!vulnerability.TryGetProperty("via", out var via) || via.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in via.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                return item.GetString();
            if (item.ValueKind == JsonValueKind.Object)
                return GetString(item, "url") ?? GetString(item, "source") ?? GetString(item, "title");
        }
        return null;
    }

    private static (string Package, string Id)? TryParseGoJsonFinding(string line)
    {
        if (!line.StartsWith('{'))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("finding", out var finding) &&
                finding.ValueKind == JsonValueKind.Object)
            {
                var id = GetString(finding, "osv") ?? GetString(finding, "id");
                if (!string.IsNullOrWhiteSpace(id))
                    return (FirstGoTracePackage(finding) ?? "unknown", id);
            }

            if (root.TryGetProperty("osv", out var osv) &&
                osv.ValueKind == JsonValueKind.Object)
            {
                var id = GetString(osv, "id");
                if (!string.IsNullOrWhiteSpace(id))
                    return (FirstGoAffectedPackage(osv) ?? "unknown", id);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? FirstGoTracePackage(JsonElement finding)
    {
        if (!finding.TryGetProperty("trace", out var trace) ||
            trace.ValueKind != JsonValueKind.Array)
            return GetString(finding, "package");

        foreach (var frame in trace.EnumerateArray())
        {
            var package = GetString(frame, "package");
            if (!string.IsNullOrWhiteSpace(package))
                return package;
        }

        return null;
    }

    private static string? FirstGoAffectedPackage(JsonElement osv)
    {
        if (!osv.TryGetProperty("affected", out var affected) ||
            affected.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in affected.EnumerateArray())
        {
            if (!item.TryGetProperty("package", out var package) ||
                package.ValueKind != JsonValueKind.Object)
                continue;

            var name = GetString(package, "name");
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return null;
    }

    private static IReadOnlyList<string> ParseProjectDirectories(string output)
        => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string ResolveWorkingDirectory(string workingDirectory, string projectDirectory)
    {
        if (projectDirectory == ".")
            return workingDirectory;

        return workingDirectory.TrimEnd('/') + "/" + projectDirectory.TrimStart('.', '/');
    }

    private static string CombinedOutput(SandboxExecResult result, out bool wasTruncated)
    {
        var output = result.Stdout + (string.IsNullOrWhiteSpace(result.Stderr) ? "" : "\n" + result.Stderr);
        wasTruncated = output.Length > MaxScannerOutputChars;
        return wasTruncated ? output[..MaxScannerOutputChars] : output;
    }

    private static bool IsMissingScannerTool(Scanner scanner, SandboxExecResult result)
    {
        if (result.ExitCode == 127)
            return true;

        return scanner.Language.Equals("rust", StringComparison.OrdinalIgnoreCase) &&
               result.ExitCode != 0 &&
               result.Stderr.Contains("no such command", StringComparison.OrdinalIgnoreCase) &&
               result.Stderr.Contains("audit", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"Crate:\s+(?<crate>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CargoCrateRegex();

    [GeneratedRegex(@"ID:\s+(?<id>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CargoIdRegex();

    [GeneratedRegex(@"Severity:\s+(?<severity>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CargoSeverityRegex();

    private sealed record Scanner(
        string Language,
        string MarkerScript,
        IReadOnlyList<string> Argv,
        string MissingToolTitle,
        string MissingToolDescription,
        Func<string, IEnumerable<AuditFinding>> Parse,
        string FailureDescription);
}
