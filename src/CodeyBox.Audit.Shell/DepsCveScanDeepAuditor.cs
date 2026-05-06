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
    private const int MaxProjectDirectories = 25;
    private const int MaxRawOutputChars = 1_000_000;

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
                ["sh", "-c", "if command -v pip-audit >/dev/null 2>&1; then if [ -f requirements.txt ]; then exec pip-audit -f json -r requirements.txt; fi; exec pip-audit -f json .; fi; if command -v safety >/dev/null 2>&1; then if [ -f requirements.txt ]; then exec safety check -r requirements.txt --json; fi; exec safety scan --output json; fi; echo 'pip-audit or safety is not installed in sandbox' >&2; exit 127"],
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
        var languages = (context.Languages ?? ProjectAuditLanguages.Default)
            .Where(ProjectAuditLanguages.IsSupported)
            .Select(NormalizeScannerLanguage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (languages.Count == 0)
            return new AuditResult(true, []);

        var allFindings = new List<AuditFinding>();
        var rawParts = new List<string>();
        var rawOutputChars = 0;
        var rawOutputTruncated = false;
        var remainingProjectDirectories = MaxProjectDirectories;

        foreach (var language in languages)
        {
            if (!Scanners.TryGetValue(language, out var scanner))
                continue;

            var discovery = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", scanner.MarkerScript],
                WorkingDirectory = workingDirectory,
            }, ct);

            if (discovery.ExitCode != 0)
            {
                allFindings.Add(new AuditFinding(
                    AuditorName: Name,
                    Severity: AuditSeverity.Error,
                    Title: $"{language} CVE scan discovery failed; treating as blocking",
                    Description: $"The {language} CVE scanner could not discover dependency project marker files. Discovery exited with code {discovery.ExitCode}. Stderr: {discovery.Stderr}"));
                continue;
            }

            var projectDirectories = ParseProjectDirectories(discovery.Stdout);
            var projectDirectoriesToRun = TakeProjectDirectoriesWithinBudget(
                language,
                projectDirectories,
                ref remainingProjectDirectories,
                allFindings);

            foreach (var projectDirectory in projectDirectoriesToRun)
            {
                var result = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = scanner.Argv,
                    WorkingDirectory = ResolveWorkingDirectory(workingDirectory, projectDirectory),
                }, ct);

                var rawOutput = CombinedOutput(result, out _);
                var parserInput = TruncatedOutput(result.Stdout, out var parserInputWasTruncated);
                AppendRawPart(rawParts, $"## {language}:{projectDirectory}\n{rawOutput}", ref rawOutputChars, ref rawOutputTruncated);

                if (IsMissingScannerTool(scanner, result))
                {
                    allFindings.Add(new AuditFinding(
                        AuditorName: Name,
                        Severity: AuditSeverity.Info,
                        Title: scanner.MissingToolTitle,
                        Description: scanner.MissingToolDescription));
                    continue;
                }

                var findings = scanner.Parse(parserInput).ToList();
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
                            (parserInputWasTruncated ? " Scanner stdout exceeded the parser limit and was truncated." : "") +
                            $" Stderr: {result.Stderr}"));
                    continue;
                }

                allFindings.AddRange(findings);
            }
        }

        if (rawOutputTruncated)
            allFindings.Add(new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Info,
                Title: "CVE scanner raw output truncated",
                Description: $"Combined CVE scanner raw output exceeded {MaxRawOutputChars} characters and was truncated before storage."));

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

    private static string NormalizeScannerLanguage(string language)
        => language.ToLowerInvariant() switch
        {
            "javascript" or "typescript" => "node",
            _ => language,
        };

    private static IEnumerable<AuditFinding> ParsePythonFindings(string output)
    {
        using var doc = TryParseJson(output);
        if (doc is null) yield break;

        foreach (var item in EnumeratePythonDependencyItems(doc.RootElement))
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
                var severity = GetSeverity(vuln, "severity") ?? "Unknown";
                yield return Finding(package, version, severity, id);
            }
        }

        foreach (var vuln in EnumerateArraysNamed(doc.RootElement, "vulnerabilities").SelectMany(a => a.EnumerateArray()))
        {
            var package = GetString(vuln, "package_name") ?? GetString(vuln, "package") ?? "unknown";
            var version = GetString(vuln, "analyzed_version") ?? GetString(vuln, "version") ?? "?";
            var id = GetString(vuln, "vulnerability_id") ?? GetString(vuln, "id") ?? "vulnerability";
            var severity = GetSeverity(vuln, "severity") ?? "Unknown";
            yield return Finding(package, version, severity, id);
        }
    }

    private static IEnumerable<JsonElement> EnumeratePythonDependencyItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
        }

        foreach (var dependencies in EnumerateArraysNamed(root, "dependencies"))
        {
            foreach (var item in dependencies.EnumerateArray())
                yield return item;
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
            var severity = GetSeverity(vuln, "severity") ?? "Unknown";
            var version = GetString(vuln, "range") ?? "?";
            var advisory = FirstNpmAdvisory(vuln);
            yield return Finding(property.Name, version, severity, advisory);
        }
    }

    private static IEnumerable<AuditFinding> ParseGoFindings(string output)
    {
        var osvById = new Dictionary<string, GoOsvRecord>(StringComparer.OrdinalIgnoreCase);
        var findings = new List<GoFindingRecord>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (TryParseGoOsvRecord(root) is { } osv)
                    osvById[osv.Id] = osv;

                if (TryParseGoFindingRecord(root) is { } finding)
                    findings.Add(finding);
            }
            catch (JsonException)
            {
                continue;
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var finding in findings)
        {
            var package = finding.Package;
            var severity = "Unknown";
            if (osvById.TryGetValue(finding.Id, out var osv))
            {
                severity = osv.Severity ?? severity;
                if (string.Equals(package, "unknown", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(osv.Package))
                    package = osv.Package;
            }

            if (seen.Add(package + "\0" + finding.Id))
                yield return Finding(package, "?", severity, finding.Id);

            seenIds.Add(finding.Id);
        }

        foreach (var osv in osvById.Values)
        {
            if (!seenIds.Add(osv.Id))
                continue;

            var package = osv.Package ?? "unknown";
            var severity = osv.Severity ?? "Unknown";
            if (seen.Add(package + "\0" + osv.Id))
                yield return Finding(package, "?", severity, osv.Id);
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
        severityText = NormalizeSeverityText(severityText);
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

    private static string NormalizeSeverityText(string severityText)
    {
        var trimmed = severityText.Trim();
        if (double.TryParse(
                trimmed,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var score))
            return SeverityFromCvss(score);

        if (TryGetCvssVectorScore(trimmed) is { } vectorScore)
            return SeverityFromCvss(vectorScore);

        return trimmed.ToLowerInvariant() switch
        {
            "critical" => "Critical",
            "high" => "High",
            "medium" or "moderate" => "Medium",
            "low" => "Low",
            _ => trimmed,
        };
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

    private static string? GetSeverity(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.String)
            return NormalizeSeverityText(property.GetString() ?? "Unknown");

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var score))
            return SeverityFromCvss(score);

        if (property.ValueKind == JsonValueKind.Array)
        {
            var severities = new List<string>();
            foreach (var item in property.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    AddSeverity(severities, NormalizeSeverityText(item.GetString() ?? "Unknown"));
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                AddSeverity(severities, GetSeverity(item, "severity"));
                AddSeverity(severities, GetSeverity(item, "level"));
                AddSeverity(severities, GetSeverity(item, "score"));
            }

            return severities
                .OrderByDescending(SeverityRank)
                .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        if (property.ValueKind == JsonValueKind.Object)
        {
            var label = GetString(property, "severity") ?? GetString(property, "level");
            if (!string.IsNullOrWhiteSpace(label))
                return NormalizeSeverityText(label);

            if (property.TryGetProperty("score", out var scoreProperty) &&
                scoreProperty.ValueKind == JsonValueKind.Number &&
                scoreProperty.TryGetDouble(out var nestedScore))
                return SeverityFromCvss(nestedScore);

            if (property.TryGetProperty("score", out scoreProperty) &&
                scoreProperty.ValueKind == JsonValueKind.String)
                return NormalizeSeverityText(scoreProperty.GetString() ?? "Unknown");
        }

        return null;
    }

    private static string SeverityFromCvss(double score) => score switch
    {
        >= 9.0 => "Critical",
        >= 7.0 => "High",
        >= 4.0 => "Medium",
        > 0.0 => "Low",
        _ => "Unknown",
    };

    private static int SeverityRank(string severityText)
        => NormalizeSeverityText(severityText) switch
        {
            "Critical" => 4,
            "High" => 3,
            "Medium" => 2,
            "Low" => 1,
            _ => 0,
        };

    private static void AddSeverity(List<string> severities, string? severity)
    {
        if (!string.IsNullOrWhiteSpace(severity))
            severities.Add(severity);
    }

    private static double? TryGetCvssVectorScore(string value)
    {
        if (value.StartsWith("CVSS:3.", StringComparison.OrdinalIgnoreCase))
            return TryGetCvssV3Score(value);

        if (value.Contains("/Au:", StringComparison.OrdinalIgnoreCase))
            return TryGetCvssV2Score(value);

        return null;
    }

    private static double? TryGetCvssV3Score(string vector)
    {
        var metrics = ParseCvssVector(vector);
        if (!metrics.TryGetValue("AV", out var avText) ||
            !metrics.TryGetValue("AC", out var acText) ||
            !metrics.TryGetValue("PR", out var prText) ||
            !metrics.TryGetValue("UI", out var uiText) ||
            !metrics.TryGetValue("S", out var scope) ||
            !metrics.TryGetValue("C", out var cText) ||
            !metrics.TryGetValue("I", out var iText) ||
            !metrics.TryGetValue("A", out var aText))
            return null;

        var av = CvssMetric(avText, ("N", 0.85), ("A", 0.62), ("L", 0.55), ("P", 0.2));
        var ac = CvssMetric(acText, ("L", 0.77), ("H", 0.44));
        var ui = CvssMetric(uiText, ("N", 0.85), ("R", 0.62));
        var c = CvssMetric(cText, ("H", 0.56), ("L", 0.22), ("N", 0.0));
        var i = CvssMetric(iText, ("H", 0.56), ("L", 0.22), ("N", 0.0));
        var a = CvssMetric(aText, ("H", 0.56), ("L", 0.22), ("N", 0.0));
        if (av is null || ac is null || ui is null || c is null || i is null || a is null)
            return null;

        var scopeChanged = scope.Equals("C", StringComparison.OrdinalIgnoreCase);
        var pr = scopeChanged
            ? CvssMetric(prText, ("N", 0.85), ("L", 0.68), ("H", 0.5))
            : CvssMetric(prText, ("N", 0.85), ("L", 0.62), ("H", 0.27));
        if (pr is null)
            return null;

        var impact = 1 - ((1 - c.Value) * (1 - i.Value) * (1 - a.Value));
        if (impact <= 0)
            return 0;

        var impactSubScore = scopeChanged
            ? 7.52 * (impact - 0.029) - 3.25 * Math.Pow(impact - 0.02, 15)
            : 6.42 * impact;
        var exploitability = 8.22 * av.Value * ac.Value * pr.Value * ui.Value;
        var baseScore = scopeChanged
            ? Math.Min(1.08 * (impactSubScore + exploitability), 10)
            : Math.Min(impactSubScore + exploitability, 10);
        return RoundUpCvssV3(baseScore);
    }

    private static double? TryGetCvssV2Score(string vector)
    {
        var metrics = ParseCvssVector(vector);
        if (!metrics.TryGetValue("AV", out var avText) ||
            !metrics.TryGetValue("AC", out var acText) ||
            !metrics.TryGetValue("Au", out var auText) ||
            !metrics.TryGetValue("C", out var cText) ||
            !metrics.TryGetValue("I", out var iText) ||
            !metrics.TryGetValue("A", out var aText))
            return null;

        var av = CvssMetric(avText, ("N", 1.0), ("A", 0.646), ("L", 0.395));
        var ac = CvssMetric(acText, ("L", 0.71), ("M", 0.61), ("H", 0.35));
        var au = CvssMetric(auText, ("N", 0.704), ("S", 0.56), ("M", 0.45));
        var c = CvssMetric(cText, ("C", 0.66), ("P", 0.275), ("N", 0.0));
        var i = CvssMetric(iText, ("C", 0.66), ("P", 0.275), ("N", 0.0));
        var a = CvssMetric(aText, ("C", 0.66), ("P", 0.275), ("N", 0.0));
        if (av is null || ac is null || au is null || c is null || i is null || a is null)
            return null;

        var impact = 10.41 * (1 - ((1 - c.Value) * (1 - i.Value) * (1 - a.Value)));
        if (impact <= 0)
            return 0;

        var exploitability = 20 * av.Value * ac.Value * au.Value;
        return Math.Round(((0.6 * impact) + (0.4 * exploitability) - 1.5) * 1.176, 1, MidpointRounding.AwayFromZero);
    }

    private static Dictionary<string, string> ParseCvssVector(string vector)
    {
        var metrics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in vector.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf(':');
            if (separator <= 0 || separator == part.Length - 1)
                continue;

            metrics[part[..separator]] = part[(separator + 1)..];
        }

        return metrics;
    }

    private static double? CvssMetric(string value, params (string Key, double Value)[] options)
    {
        foreach (var option in options)
        {
            if (value.Equals(option.Key, StringComparison.OrdinalIgnoreCase))
                return option.Value;
        }

        return null;
    }

    private static double RoundUpCvssV3(double value)
        => Math.Ceiling((value - 0.000001) * 10.0) / 10.0;

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

    private static GoOsvRecord? TryParseGoOsvRecord(JsonElement root)
    {
        JsonElement osv;
        if (root.TryGetProperty("osv", out var wrappedOsv) &&
            wrappedOsv.ValueKind == JsonValueKind.Object)
        {
            osv = wrappedOsv;
        }
        else if (root.TryGetProperty("id", out _) &&
                 root.TryGetProperty("affected", out _))
        {
            osv = root;
        }
        else
        {
            return null;
        }

        var id = GetString(osv, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return new GoOsvRecord(
            id,
            FirstGoAffectedPackage(osv),
            GetOsvSeverity(osv));
    }

    private static GoFindingRecord? TryParseGoFindingRecord(JsonElement root)
    {
        if (!root.TryGetProperty("finding", out var finding) ||
            finding.ValueKind != JsonValueKind.Object)
            return null;

        var id = GetString(finding, "osv") ?? GetString(finding, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return new GoFindingRecord(FirstGoTracePackage(finding) ?? "unknown", id);
    }

    private static string? GetOsvSeverity(JsonElement osv)
    {
        var severities = new List<string>();

        AddSeverity(severities, GetSeverity(osv, "severity"));

        if (osv.TryGetProperty("database_specific", out var databaseSpecific) &&
            databaseSpecific.ValueKind == JsonValueKind.Object)
        {
            AddSeverity(severities, GetSeverity(databaseSpecific, "severity"));
            AddSeverity(severities, GetSeverity(databaseSpecific, "level"));
        }

        if (osv.TryGetProperty("affected", out var affected) &&
            affected.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in affected.EnumerateArray())
            {
                AddSeverity(severities, GetSeverity(item, "severity"));

                if (item.TryGetProperty("database_specific", out var affectedDatabaseSpecific) &&
                    affectedDatabaseSpecific.ValueKind == JsonValueKind.Object)
                {
                    AddSeverity(severities, GetSeverity(affectedDatabaseSpecific, "severity"));
                    AddSeverity(severities, GetSeverity(affectedDatabaseSpecific, "level"));
                }

                if (item.TryGetProperty("ecosystem_specific", out var ecosystemSpecific) &&
                    ecosystemSpecific.ValueKind == JsonValueKind.Object)
                {
                    AddSeverity(severities, GetSeverity(ecosystemSpecific, "severity"));
                    AddSeverity(severities, GetSeverity(ecosystemSpecific, "level"));
                }
            }
        }

        return severities
            .OrderByDescending(SeverityRank)
            .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
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

    private static IReadOnlyList<string> TakeProjectDirectoriesWithinBudget(
        string language,
        IReadOnlyList<string> projectDirectories,
        ref int remainingProjectDirectories,
        List<AuditFinding> findings)
    {
        if (projectDirectories.Count == 0)
            return [];

        if (remainingProjectDirectories <= 0)
        {
            findings.Add(ProjectDirectoryLimitFinding(language, projectDirectories.Count, 0));
            return [];
        }

        if (projectDirectories.Count <= remainingProjectDirectories)
        {
            remainingProjectDirectories -= projectDirectories.Count;
            return projectDirectories;
        }

        var allowed = remainingProjectDirectories;
        remainingProjectDirectories = 0;
        findings.Add(ProjectDirectoryLimitFinding(language, projectDirectories.Count, allowed));
        return projectDirectories.Take(allowed).ToList();
    }

    private static AuditFinding ProjectDirectoryLimitFinding(string language, int discoveredCount, int auditedCount)
        => new(
            AuditorName: "deps-cve-scan",
            Severity: AuditSeverity.Error,
            Title: $"{language} CVE scan discovered too many project directories",
            Description: $"The {language} CVE scanner found {discoveredCount} project directories after the global CVE scanner budget was reached. Audited {auditedCount} of those directories and stopped to prevent repository-controlled scanner fan-out. The global maximum is {MaxProjectDirectories} project directories per run.");

    private static void AppendRawPart(
        List<string> rawParts,
        string rawPart,
        ref int rawOutputChars,
        ref bool rawOutputTruncated)
    {
        if (rawOutputTruncated)
            return;

        var remaining = MaxRawOutputChars - rawOutputChars;
        if (remaining <= 0)
        {
            rawOutputTruncated = true;
            return;
        }

        if (rawPart.Length > remaining)
        {
            rawParts.Add(rawPart[..remaining]);
            rawOutputChars += remaining;
            rawOutputTruncated = true;
            return;
        }

        rawParts.Add(rawPart);
        rawOutputChars += rawPart.Length;
    }

    private static string ResolveWorkingDirectory(string workingDirectory, string projectDirectory)
    {
        if (projectDirectory == ".")
            return workingDirectory;

        return workingDirectory.TrimEnd('/') + "/" + projectDirectory.TrimStart('.', '/');
    }

    private static string CombinedOutput(SandboxExecResult result, out bool wasTruncated)
    {
        var output = result.Stdout + (string.IsNullOrWhiteSpace(result.Stderr) ? "" : "\n" + result.Stderr);
        return TruncatedOutput(output, out wasTruncated);
    }

    private static string TruncatedOutput(string output, out bool wasTruncated)
    {
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

    [GeneratedRegex(@"Severity:\s+(?:\S+\s+\((?<severity>[^)]+)\)|(?<severity>\S+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CargoSeverityRegex();

    private sealed record Scanner(
        string Language,
        string MarkerScript,
        IReadOnlyList<string> Argv,
        string MissingToolTitle,
        string MissingToolDescription,
        Func<string, IEnumerable<AuditFinding>> Parse,
        string FailureDescription);

    private sealed record GoFindingRecord(string Package, string Id);
    private sealed record GoOsvRecord(string Id, string? Package, string? Severity);
}
