using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class DepsCveScanLanguageDispatchTests
{
    [Fact]
    public void DeclaresNetworkCapability()
    {
        var auditor = new DepsCveScanDeepAuditor();

        Assert.True(auditor.Required.HasFlag(AuditCapabilities.Network));
        Assert.False(auditor.Required.HasFlag(AuditCapabilities.AgentCredentials));
    }

    [Fact]
    public async Task RunsScannerForEachDeclaredLanguageWithMarker()
    {
        var sandbox = new DispatchSandbox(markerPresent: true);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["csharp", "python", "node"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        Assert.Contains(sandbox.Commands, c =>
            c.Contains("dotnet list package --vulnerable --include-transitive", StringComparison.Ordinal) &&
            c.Contains("--config \"$config\"", StringComparison.Ordinal) &&
            c.Contains("--source \"$trusted_source\"", StringComparison.Ordinal));
        Assert.Contains(sandbox.ExtraEnvironments, e =>
            e.TryGetValue("DOTNET_CLI_TELEMETRY_OPTOUT", out var telemetry) &&
            telemetry == "1");
        Assert.Contains(sandbox.Commands, c => c.Contains("pip-audit -f json -r requirements.txt", StringComparison.Ordinal));
        Assert.Contains(sandbox.Commands, c => c.Contains("pip-audit -f json .", StringComparison.Ordinal));
        Assert.Contains(sandbox.Commands, c => c.Contains("safety scan --target . --output json", StringComparison.Ordinal));
        Assert.Contains(sandbox.ExtraEnvironments, e =>
            e.TryGetValue("PIP_INDEX_URL", out var indexUrl) &&
            indexUrl == "https://pypi.org/simple" &&
            e.TryGetValue("PIP_CONFIG_FILE", out var pipConfigFile) &&
            pipConfigFile == "/dev/null");
        Assert.Contains(sandbox.Commands, c => c == "npm audit --json --registry https://registry.npmjs.org/");
        Assert.Contains(sandbox.ExtraEnvironments, e =>
            e.TryGetValue("NPM_CONFIG_REGISTRY", out var registry) &&
            registry == "https://registry.npmjs.org/");
        Assert.Contains(sandbox.WorkingDirectories, d => d == "/repo/csharp");
        Assert.Contains(sandbox.WorkingDirectories, d => d == "/repo/python");
        Assert.Contains(sandbox.WorkingDirectories, d => d == "/repo/node");
    }

    [Fact]
    public async Task SkipsDeclaredLanguageWhenMarkerIsMissing()
    {
        var sandbox = new DispatchSandbox(markerPresent: false);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["go"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.DoesNotContain(sandbox.Commands, c => c.StartsWith("govulncheck", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsetLanguagesRunNoLanguageSpecificScanner()
    {
        var sandbox = new DispatchSandbox(markerPresent: true);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        Assert.Empty(sandbox.Commands);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExplicitEmptyLanguagesRunNoLanguageSpecificScanner()
    {
        var sandbox = new DispatchSandbox(markerPresent: true);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: []);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        Assert.Empty(sandbox.Commands);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task JavaScriptAndTypeScriptAreSkippedAsUnsupported()
    {
        var sandbox = new DispatchSandbox(markerPresent: true);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["javascript", "typescript"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        Assert.Empty(sandbox.Commands);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task DiscoveryFailureReportsErrorAndDoesNotRunScanner()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, discoveryExitCode: 2);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["node"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("discovery failed", finding.Title);
        Assert.DoesNotContain(sandbox.Commands, c => c.StartsWith("npm audit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingScannerToolReportsInfo()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, missingTool: true);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["rust"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("cargo-audit not installed", finding.Title);
    }

    [Fact]
    public async Task MissingCargoAuditSubcommandReportsInfo()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, missingCargoAuditSubcommand: true);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["rust"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("cargo-audit not installed", finding.Title);
    }

    [Fact]
    public async Task GoScannerUsesOsvSeverityFromStructuredJsonOutput()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, scannerStdout: """
            {"osv":{"id":"GO-2024-0001","database_specific":{"severity":"MODERATE"},"affected":[{"package":{"ecosystem":"Go","name":"example.com/module"}}]}}
            {"finding":{"osv":"GO-2024-0001","trace":[{"package":"example.com/app"}]}}
            """);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["go"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        Assert.Contains(sandbox.Commands, c => c == "govulncheck -json ./...");
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("example.com/app", finding.Title);
        Assert.Contains("Medium", finding.Title);
    }

    [Fact]
    public async Task GoScannerNormalizesOsvCvssVectorSeverity()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, scannerStdout: """
            {"osv":{"id":"GO-2024-0002","severity":[{"type":"CVSS_V3","score":"CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H"}],"affected":[{"package":{"ecosystem":"Go","name":"example.com/module"}}]}}
            {"finding":{"osv":"GO-2024-0002","trace":[{"package":"example.com/app"}]}}
            """);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["go"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("Critical", finding.Title);
    }

    [Fact]
    public async Task GoScannerDoesNotInventSeverityWhenOsvHasNoSeverity()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, scannerStdout: """
            {"finding":{"osv":"GO-2024-0003","trace":[{"package":"example.com/app"}]}}
            """);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["go"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("Unknown", finding.Title);
    }

    [Fact]
    public async Task PythonScannerParsesPipAuditJsonOutput()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, scannerStdout: """
            [
              {
                "name": "django",
                "version": "1.2",
                "vulns": [
                  { "id": "PYSEC-2019-13", "severity": "high" }
                ]
              }
            ]
            """);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["python"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("django", finding.Title);
        Assert.Contains("PYSEC-2019-13", finding.Description);
    }

    [Fact]
    public async Task PythonScannerBlocksRepositoryControlledPackageSourceUrls()
    {
        var sandbox = new DispatchSandbox(
            markerPresent: true,
            scannerExitCode: 126,
            scannerStderr: """
                CODEYBOX_UNSAFE_PYTHON_DEPENDENCY_SOURCE
                requirements.txt:1: http://169.254.169.254/latest/meta-data
                pyproject.toml:3: git+ssh://internal.example/pkg.git
                """);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["python"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("blocked repository-controlled package source URLs", finding.Title);
        Assert.Contains("169.254.169.254", finding.Description);
    }

    [Fact]
    public async Task PythonPreflightRecursesRequirementIncludesBeforeScannerRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cb-python-cve-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "requirements.txt"), "-r requirements-extra.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, "requirements-extra.txt"), "evil @ http://169.254.169.254/latest/meta-data\n");

            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sh",
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(PythonScannerScript());

            process.Start();
            await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(126, process.ExitCode);
            Assert.Contains("CODEYBOX_UNSAFE_PYTHON_DEPENDENCY_SOURCE", stderr);
            Assert.Contains("requirements-extra.txt:1", stderr);
            Assert.Contains("169.254.169.254", stderr);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PythonScannerParsesWrappedPipAuditJsonOutput()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, scannerStdout: """
            {
              "dependencies": [
                {
                  "name": "django",
                  "version": "1.2",
                  "vulns": [
                    { "id": "PYSEC-2019-13", "severity": "high" }
                  ]
                }
              ],
              "fixes": []
            }
            """);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["python"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("django", finding.Title);
        Assert.Contains("PYSEC-2019-13", finding.Description);
    }

    [Fact]
    public async Task ManyProjectDirectories_AreAllScanned()
    {
        var discoveryStdout = string.Join('\n', Enumerable.Range(0, 40).Select(i => $"./python-{i}")) + "\n";
        var sandbox = new DispatchSandbox(markerPresent: true, discoveryStdout: discoveryStdout);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["python"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        Assert.DoesNotContain(result.Findings, f => f.Severity == AuditSeverity.Error);
        Assert.Equal(40, sandbox.Commands.Count(c => c.Contains("pip-audit -f json -r requirements.txt", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CSharpManyProjectDirectories_AreAllScanned()
    {
        var discoveryStdout = string.Join('\n', Enumerable.Range(0, 40).Select(i => $"./csharp-{i}")) + "\n";
        var sandbox = new DispatchSandbox(markerPresent: true, discoveryStdout: discoveryStdout);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["csharp"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        Assert.DoesNotContain(result.Findings, f => f.Severity == AuditSeverity.Error);
        Assert.Equal(40, sandbox.Commands.Count(c =>
            c.Contains("dotnet list package --vulnerable --include-transitive", StringComparison.Ordinal)));
        Assert.Contains("/repo/csharp-0", sandbox.WorkingDirectories);
        Assert.Contains("/repo/csharp-39", sandbox.WorkingDirectories);
    }

    [Fact]
    public async Task CSharpRootMarker_RunsSingleRootCveScan()
    {
        var discoveryStdout = ".\n" + string.Join('\n', Enumerable.Range(0, 40).Select(i => $"./csharp-{i}")) + "\n";
        var sandbox = new DispatchSandbox(markerPresent: true, discoveryStdout: discoveryStdout);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["csharp"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        Assert.Equal(1, sandbox.Commands.Count(c =>
            c.Contains("dotnet list package --vulnerable --include-transitive", StringComparison.Ordinal)));
        Assert.Contains("/repo", sandbox.WorkingDirectories);
        Assert.DoesNotContain("/repo/csharp-0", sandbox.WorkingDirectories);
    }

    [Fact]
    public async Task CSharpScannerBlocksRepositoryControlledNuGetSourceUrls()
    {
        var sandbox = new DispatchSandbox(
            markerPresent: true,
            scannerExitCode: 126,
            scannerStderr: """
                CODEYBOX_UNSAFE_NUGET_DEPENDENCY_SOURCE
                /repo/NuGet.config:4: http://169.254.169.254/nuget
                ./Directory.Build.props:7: https://internal.example/nuget
                """);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["csharp"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("blocked repository-controlled NuGet source URLs", finding.Title);
        Assert.Contains("169.254.169.254", finding.Description);
        Assert.Contains("internal.example", finding.Description);
    }

    [Fact]
    public async Task PythonScannerParsesJsonStdoutWhenScannerWritesWarningsToStderr()
    {
        var sandbox = new DispatchSandbox(
            markerPresent: true,
            scannerStdout: """
                {
                  "dependencies": [
                    {
                      "name": "requests",
                      "version": "2.19.0",
                      "vulns": [
                        { "id": "PYSEC-2018-28", "severity": "moderate" }
                      ]
                    }
                  ]
                }
                """,
            scannerStderr: "WARNING: pip-audit version check failed",
            scannerExitCode: 1);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["python"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("requests", finding.Title);
        Assert.Contains("PYSEC-2018-28", finding.Description);
        Assert.Contains("pip-audit version check failed", result.RawOutput);
    }

    [Fact]
    public async Task PythonScannerParsesSafetyJsonOutput()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, scannerStdout: """
            {
              "vulnerabilities": [
                {
                  "package_name": "jinja2",
                  "analyzed_version": "2.10",
                  "vulnerability_id": "CVE-2019-10906",
                  "severity": { "score": 9.8 }
                }
              ]
            }
            """);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["python"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("jinja2", finding.Title);
        Assert.Contains("CVE-2019-10906", finding.Description);
    }

    [Fact]
    public async Task NodeScannerParsesNpmAuditJsonOutput()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, scannerStdout: """
            {
              "auditReportVersion": 2,
              "vulnerabilities": {
                "minimist": {
                  "name": "minimist",
                  "severity": "moderate",
                  "range": "<0.2.1",
                  "via": [
                    {
                      "source": 1096466,
                      "title": "Prototype Pollution",
                      "url": "https://github.com/advisories/GHSA-vh95-rmgr-6w4m",
                      "severity": "moderate",
                      "range": "<0.2.1"
                    }
                  ]
                }
              },
              "metadata": {}
            }
            """);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["node"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("minimist", finding.Title);
        Assert.Contains("GHSA-vh95-rmgr-6w4m", finding.Description);
    }

    [Fact]
    public async Task NodeScannerParsesJsonStdoutWhenNpmWritesNoticesToStderr()
    {
        var sandbox = new DispatchSandbox(
            markerPresent: true,
            scannerStdout: """
                {
                  "auditReportVersion": 2,
                  "vulnerabilities": {
                    "lodash": {
                      "name": "lodash",
                      "severity": "low",
                      "range": "<4.17.21",
                      "via": [
                        {
                          "source": 1106913,
                          "title": "Command Injection",
                          "url": "https://github.com/advisories/GHSA-35jh-r3h4-6jhm",
                          "severity": "low",
                          "range": "<4.17.21"
                        }
                      ]
                    }
                  }
                }
                """,
            scannerStderr: "npm notice New minor version of npm available",
            scannerExitCode: 1);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["node"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("lodash", finding.Title);
        Assert.Contains("GHSA-35jh-r3h4-6jhm", finding.Description);
        Assert.Contains("npm notice", result.RawOutput);
    }

    [Fact]
    public async Task RustScannerParsesCargoAuditTextOutput()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, scannerStdout: """
            Crate:     atty
            Version:   0.2.14
            Title:     Potential unaligned read
            Date:      2021-08-18
            ID:        RUSTSEC-2021-0145
            URL:       https://rustsec.org/advisories/RUSTSEC-2021-0145
            Severity:  7.0 (high)
            Solution:  Upgrade to >=0.2.15
            """);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["rust"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("atty", finding.Title);
        Assert.Contains("RUSTSEC-2021-0145", finding.Description);
    }

    private sealed class DispatchSandbox : ISandbox
    {
        private readonly bool _markerPresent;
        private readonly bool _missingTool;
        private readonly bool _missingCargoAuditSubcommand;
        private readonly string _scannerStdout;
        private readonly string _scannerStderr;
        private readonly int _scannerExitCode;
        private readonly int _discoveryExitCode;
        private readonly string? _discoveryStdout;

        public DispatchSandbox(
            bool markerPresent,
            bool missingTool = false,
            bool missingCargoAuditSubcommand = false,
            string scannerStdout = "{}",
            string scannerStderr = "",
            int scannerExitCode = 0,
            int discoveryExitCode = 0,
            string? discoveryStdout = null)
        {
            _markerPresent = markerPresent;
            _missingTool = missingTool;
            _missingCargoAuditSubcommand = missingCargoAuditSubcommand;
            _scannerStdout = scannerStdout;
            _scannerStderr = scannerStderr;
            _scannerExitCode = scannerExitCode;
            _discoveryExitCode = discoveryExitCode;
            _discoveryStdout = discoveryStdout;
        }

        public List<string> Commands { get; } = [];
        public List<string> WorkingDirectories { get; } = [];
        public List<IReadOnlyDictionary<string, string>> ExtraEnvironments { get; } = [];
        public string Id => "dispatch";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var command = string.Join(' ', exec.Argv);
            Commands.Add(command);
            if (exec.WorkingDirectory is not null)
                WorkingDirectories.Add(exec.WorkingDirectory);
            if (exec.ExtraEnvironment is not null)
                ExtraEnvironments.Add(exec.ExtraEnvironment);

            if (exec.Argv.Count >= 2 && exec.Argv[0] == "sh" && exec.Argv[1] == "-c")
            {
                var script = exec.Argv[2];
                if (script.Contains("find .", StringComparison.Ordinal) &&
                    !script.Contains("dotnet list package", StringComparison.Ordinal))
                    return Task.FromResult(new SandboxExecResult(
                        _discoveryExitCode,
                        _markerPresent ? _discoveryStdout ?? DiscoveryOutput(script) : "",
                        _discoveryExitCode == 0 ? "" : "find failed"));
            }

            if (_missingTool)
                return Task.FromResult(new SandboxExecResult(127, "", "missing"));

            if (_missingCargoAuditSubcommand)
                return Task.FromResult(new SandboxExecResult(101, "", "error: no such command: `audit`"));

            return Task.FromResult(new SandboxExecResult(_scannerExitCode, _scannerStdout, _scannerStderr));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static string DiscoveryOutput(string script)
        {
            if (script.Contains("*.csproj", StringComparison.Ordinal))
                return "./csharp\n";
            if (script.Contains("pyproject.toml", StringComparison.Ordinal))
                return "./python\n";
            if (script.Contains("package.json", StringComparison.Ordinal))
                return "./node\n";
            if (script.Contains("go.mod", StringComparison.Ordinal))
                return "./go\n";
            if (script.Contains("Cargo.toml", StringComparison.Ordinal))
                return "./rust\n";
            return ".\n";
        }
    }

    private static string PythonScannerScript()
        => (string)typeof(DepsCveScanDeepAuditor)
            .GetField("PythonScannerScript", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(null)!;
}
