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
    private const int MaxRawOutputChars = 1_000_000;
    private const string NuGetAuditSource = "https://api.nuget.org/v3/index.json";
    private const string CSharpUnsafeSourceMarker = "CODEYBOX_UNSAFE_NUGET_DEPENDENCY_SOURCE";
    private const string NpmAuditRegistry = "https://registry.npmjs.org/";
    private const string NodeUnsafeNpmConfigMarker = "CODEYBOX_UNSAFE_NPM_AUDIT_CONFIG";
    private const string PythonUnsafeSourceMarker = "CODEYBOX_UNSAFE_PYTHON_DEPENDENCY_SOURCE";
    private const string RustSecAdvisoryDatabaseUrl = "https://github.com/RustSec/advisory-db";
    private const string RustUnsafeCargoAuditConfigMarker = "CODEYBOX_UNSAFE_CARGO_AUDIT_CONFIG";
    private static readonly string CSharpScannerScript = $$"""
        set -eu
        trusted_source='{{NuGetAuditSource}}'
        url_regex="[a-z][a-z0-9+.-]*://[^[:space:]\"'<>;]+"
        blocked="${TMPDIR:-/tmp}/codeybox-unsafe-nuget-sources.$$"
        config="${TMPDIR:-/tmp}/codeybox-nuget.$$".config
        : > "$blocked"
        cleanup() {
            rm -f "$blocked" "$config"
        }
        trap cleanup EXIT HUP INT TERM

        is_trusted_nuget_source() {
            case "$1" in
                https://api.nuget.org/*|https://www.nuget.org/*) return 0 ;;
                *) return 1 ;;
            esac
        }

        record_url_if_untrusted() {
            source_file=$1
            line_number=$2
            url=$3
            if ! is_trusted_nuget_source "$url"; then
                printf '%s:%s: %s\n' "$source_file" "$line_number" "$url" >> "$blocked"
            fi
        }

        scan_all_urls_in_file() {
            source_file=$1
            (grep -nEio "$url_regex" "$source_file" || true) |
            while IFS= read -r hit; do
                line_number=${hit%%:*}
                url=${hit#*:}
                record_url_if_untrusted "$source_file" "$line_number" "$url"
            done
        }

        scan_restore_source_lines() {
            source_file=$1
            (awk '
                {
                    lower = tolower($0)
                    source_token = "restore(sources|additionalprojectsources|fallbackfolders|configfile)|packagesource"
                    starts_source_block = lower ~ ("<[[:space:]]*(" source_token ")[^>]*>")
                    ends_source_block = lower ~ ("</[[:space:]]*(" source_token ")[^>]*>")
                    mentions_source_token = lower ~ source_token
                    if (in_source_block || mentions_source_token || starts_source_block)
                        print NR ":" $0
                    if (starts_source_block && !ends_source_block)
                        in_source_block = 1
                    if (in_source_block && ends_source_block)
                        in_source_block = 0
                }
            ' "$source_file" || true) |
            while IFS= read -r line; do
                line_number=${line%%:*}
                text=${line#*:}
                printf '%s\n' "$text" | (grep -Eio "$url_regex" || true) |
                while IFS= read -r url; do
                    record_url_if_untrusted "$source_file" "$line_number" "$url"
                done
            done
        }

        find_nuget_source_files() {
            dir=$PWD
            while :; do
                for name in NuGet.config nuget.config NuGet.Config Directory.Build.props Directory.Build.targets Directory.Packages.props; do
                    [ -f "$dir/$name" ] && printf '%s\n' "$dir/$name"
                done
                [ "$dir" = "/" ] && break
                [ -d "$dir/.git" ] && break
                parent=$(dirname "$dir")
                [ "$parent" = "$dir" ] && break
                dir=$parent
            done

            find . \( -path './.git' -o -path './bin' -o -path './obj' -o -path './node_modules' \) -prune -o \
                \( -name '*.csproj' -o -name '*.fsproj' -o -name '*.vbproj' -o -name '*.props' -o -name '*.targets' \) \
                -type f -print
        }

        find_nuget_source_files | sort -u |
        while IFS= read -r source_file; do
            [ -f "$source_file" ] || continue
            case "$(basename "$source_file")" in
                NuGet.config|nuget.config|NuGet.Config) scan_all_urls_in_file "$source_file" ;;
                *) scan_restore_source_lines "$source_file" ;;
            esac
        done

        if [ -s "$blocked" ]; then
            echo '{{CSharpUnsafeSourceMarker}}' >&2
            cat "$blocked" >&2
            exit 126
        fi

        cat > "$config" <<EOF
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="nuget.org" value="$trusted_source" protocolVersion="3" />
          </packageSources>
          <disabledPackageSources>
            <clear />
          </disabledPackageSources>
        </configuration>
        EOF

        exec dotnet list package --vulnerable --include-transitive --config "$config" --source "$trusted_source"
        """;
    private static readonly string PythonScannerScript = $$"""
        validate_python_dependency_sources() {
        "$1" - <<'PY'
        import pathlib, re, shlex, sys
        from urllib.parse import urlparse

        allowed_hosts = {'pypi.org', 'files.pythonhosted.org'}
        include_options = {'-r', '--requirement', '-c', '--constraint'}
        manifest_names = {'pyproject.toml', 'setup.py', 'setup.cfg'}
        root = pathlib.Path('.').resolve()
        url_re = re.compile(r'''(?i)(?:https?|ftp|file)://[^\s\]})>,"']+|(?:git|hg|svn|bzr)\+[^\s\]})>,"']+''')
        blocked = []
        unsupported = []
        seen_requirements = set()

        def display(path):
            try:
                rel = path.resolve().relative_to(root)
                return rel.as_posix() or '.'
            except (OSError, ValueError):
                return str(path)

        def scan_urls(path, text):
            for line_number, line in enumerate(text.splitlines(), 1):
                stripped = line.strip()
                if not stripped or stripped.startswith('#'):
                    continue
                for match in url_re.finditer(line):
                    raw_url = match.group(0)
                    parsed_url = raw_url.split('+', 1)[1] if '+' in raw_url and '://' in raw_url.split('+', 1)[1] else raw_url
                    parsed = urlparse(parsed_url)
                    host = (parsed.hostname or '').lower().rstrip('.')
                    if parsed.scheme != 'https' or host not in allowed_hosts:
                        blocked.append(f'{display(path)}:{line_number}: {raw_url}')

        def include_targets(line):
            try:
                tokens = shlex.split(line, comments=True, posix=True)
            except ValueError:
                return []

            targets = []
            i = 0
            while i < len(tokens):
                token = tokens[i]
                if token in include_options:
                    if i + 1 >= len(tokens):
                        targets.append((None, f'{token} without a path'))
                    else:
                        targets.append((tokens[i + 1], None))
                        i += 1
                elif token.startswith('-r') and token != '-r':
                    targets.append((token[2:], None))
                elif token.startswith('--requirement='):
                    targets.append((token.split('=', 1)[1], None))
                elif token.startswith('-c') and token != '-c':
                    targets.append((token[2:], None))
                elif token.startswith('--constraint='):
                    targets.append((token.split('=', 1)[1], None))
                i += 1
            return targets

        def resolve_include(current, target, problem):
            if problem is not None:
                return None, problem
            if not target:
                return None, 'empty include path'
            parsed = urlparse(target)
            if parsed.scheme or re.match(r'(?i)^(git|hg|svn|bzr)\+', target):
                return None, f'URL include path is not supported: {target}'

            target_path = pathlib.Path(target)
            if target_path.is_absolute():
                return None, f'absolute include path is not supported: {target}'

            candidate = (current.parent / target_path).resolve()
            try:
                candidate.relative_to(root)
            except ValueError:
                return None, f'include path escapes the project directory: {target}'
            if not candidate.is_file():
                return None, f'included requirements file was not found: {target}'
            return candidate, None

        def scan_requirements(path, stack):
            try:
                resolved = path.resolve()
            except OSError as exc:
                unsupported.append(f'{display(path)}: {exc}')
                return
            if resolved in stack:
                unsupported.append(f'{display(path)}: recursive requirements include')
                return
            if resolved in seen_requirements:
                return
            seen_requirements.add(resolved)

            try:
                text = resolved.read_text(encoding='utf-8', errors='ignore')
            except OSError as exc:
                unsupported.append(f'{display(path)}: {exc}')
                return

            scan_urls(resolved, text)
            next_stack = stack | {resolved}
            for line_number, line in enumerate(text.splitlines(), 1):
                for target, problem in include_targets(line):
                    included, include_problem = resolve_include(resolved, target, problem)
                    if include_problem is not None:
                        unsupported.append(f'{display(resolved)}:{line_number}: {include_problem}')
                    elif included is not None:
                        scan_requirements(included, next_stack)

        for manifest in pathlib.Path('.').iterdir():
            if not manifest.is_file():
                continue
            if manifest.name in manifest_names:
                try:
                    scan_urls(manifest, manifest.read_text(encoding='utf-8', errors='ignore'))
                except OSError:
                    continue
            elif manifest.name.startswith('requirements') and manifest.suffix == '.txt':
                scan_requirements(manifest, set())

        if blocked or unsupported:
            print('{{PythonUnsafeSourceMarker}}', file=sys.stderr)
            for item in blocked:
                print(item, file=sys.stderr)
            for item in unsupported:
                print(item, file=sys.stderr)
            sys.exit(126)
        PY
        }
        if command -v python3 >/dev/null 2>&1; then validate_python_dependency_sources python3 || exit $?;
        elif command -v python >/dev/null 2>&1; then validate_python_dependency_sources python || exit $?;
        else echo 'python interpreter is not installed in sandbox' >&2; exit 127; fi;
        if [ -f requirements.txt ]; then
        if command -v pip-audit >/dev/null 2>&1; then exec pip-audit -f json -r requirements.txt; fi;
        if command -v safety >/dev/null 2>&1; then exec safety check -r requirements.txt --json; fi;
        else
        if command -v pip-audit >/dev/null 2>&1; then exec pip-audit -f json .; fi;
        if command -v safety >/dev/null 2>&1; then exec safety scan --target . --output json; fi;
        fi;
        echo 'pip-audit or safety is not installed in sandbox' >&2; exit 127
        """;
    private static readonly string NodeScannerScript = $$"""
        set -eu
        blocked="${TMPDIR:-/tmp}/codeybox-unsafe-npm-config.$$"
        : > "$blocked"
        cleanup() {
            rm -f "$blocked"
        }
        trap cleanup EXIT HUP INT TERM

        scan_npmrc() {
            source_file=$1
            awk '
                {
                    line = tolower($0)
                    sub(/^[[:space:]]*/, "", line)
                }
                line ~ /^(proxy|https-proxy|http-proxy)[[:space:]]*=/ {
                    print FILENAME ":" FNR ": " $0
                }
            ' "$source_file" >> "$blocked"
        }

        dir=$PWD
        while :; do
            [ -f "$dir/.npmrc" ] && scan_npmrc "$dir/.npmrc"
            [ "$dir" = "/" ] && break
            [ -d "$dir/.git" ] && break
            parent=$(dirname "$dir")
            [ "$parent" = "$dir" ] && break
            dir=$parent
        done

        if [ -s "$blocked" ]; then
            echo '{{NodeUnsafeNpmConfigMarker}}' >&2
            cat "$blocked" >&2
            exit 126
        fi

        exec npm audit --json --registry '{{NpmAuditRegistry}}' --audit-registry '{{NpmAuditRegistry}}' --proxy=false --https-proxy=false
        """;

    private static readonly string RustScannerScript = $$"""
        set -eu
        blocked="${TMPDIR:-/tmp}/codeybox-unsafe-cargo-audit-config.$$"
        trusted_config="${TMPDIR:-/tmp}/codeybox-cargo-audit.$$".toml
        : > "$blocked"
        cleanup() {
            rm -f "$blocked" "$trusted_config"
        }
        trap cleanup EXIT HUP INT TERM

        cargo_audit_config_has_database_override() {
            awk '
                function trim(s) {
                    sub(/^[ \t\r\n]+/, "", s)
                    sub(/[ \t\r\n]+$/, "", s)
                    return s
                }
                {
                    line = trim($0)
                    if (line == "" || substr(line, 1, 1) == "#")
                        next

                    lower = tolower(line)
                    if (lower ~ /^\[[[:space:]]*database[[:space:]]*\]/) {
                        in_database = 1
                        next
                    }
                    if (lower ~ /^\[/) {
                        in_database = 0
                        next
                    }

                    if (in_database && lower ~ /^(url|path|fetch)[[:space:]]*=/) {
                        print FILENAME ":" FNR ": " line
                        found = 1
                    }
                    if (lower ~ /^database[[:space:]]*\.[[:space:]]*(url|path|fetch)[[:space:]]*=/) {
                        print FILENAME ":" FNR ": " line
                        found = 1
                    }
                    if (lower ~ /^database[[:space:]]*=/ && lower ~ /(url|path|fetch)[[:space:]]*=/) {
                        print FILENAME ":" FNR ": " line
                        found = 1
                    }
                }
                END { exit found ? 0 : 1 }
            ' "$1"
        }

        find_repository_cargo_audit_configs() {
            dir=$PWD
            while :; do
                [ -f "$dir/.cargo/audit.toml" ] && printf '%s\n' "$dir/.cargo/audit.toml"
                [ "$dir" = "/" ] && break
                [ -d "$dir/.git" ] && break
                parent=$(dirname "$dir")
                [ "$parent" = "$dir" ] && break
                dir=$parent
            done

            find "$PWD" \( -path '*/.git' -o -path '*/target' -o -path '*/node_modules' \) -prune -o \
                -path '*/.cargo/audit.toml' -type f -print
        }

        find_repository_cargo_audit_configs | sort -u |
        while IFS= read -r source_file; do
            [ -f "$source_file" ] || continue
            cargo_audit_config_has_database_override "$source_file" >> "$blocked" || true
        done

        if [ -s "$blocked" ]; then
            echo '{{RustUnsafeCargoAuditConfigMarker}}' >&2
            cat "$blocked" >&2
            exit 126
        fi

        cat > "$trusted_config" <<EOF
        [database]
        url = "{{RustSecAdvisoryDatabaseUrl}}"
        fetch = true
        EOF

        exec cargo audit --config "$trusted_config"
        """;

    private static readonly IReadOnlyDictionary<string, string> NpmAuditEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NPM_CONFIG_REGISTRY"] = NpmAuditRegistry,
            ["NPM_CONFIG_AUDIT_REGISTRY"] = NpmAuditRegistry,
            ["NPM_CONFIG_PROXY"] = "false",
            ["NPM_CONFIG_HTTPS_PROXY"] = "false",
            ["NPM_CONFIG_USERCONFIG"] = "/dev/null",
            ["NPM_CONFIG_GLOBALCONFIG"] = "/dev/null",
            ["npm_config_registry"] = NpmAuditRegistry,
            ["npm_config_audit_registry"] = NpmAuditRegistry,
            ["npm_config_proxy"] = "false",
            ["npm_config_https_proxy"] = "false",
            ["npm_config_userconfig"] = "/dev/null",
            ["npm_config_globalconfig"] = "/dev/null",
            ["HTTP_PROXY"] = "",
            ["HTTPS_PROXY"] = "",
            ["http_proxy"] = "",
            ["https_proxy"] = "",
        };

    private static readonly IReadOnlyDictionary<string, string> PythonScannerEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PIP_CONFIG_FILE"] = "/dev/null",
            ["PIP_DISABLE_PIP_VERSION_CHECK"] = "1",
            ["PIP_EXTRA_INDEX_URL"] = "",
            ["PIP_FIND_LINKS"] = "",
            ["PIP_INDEX_URL"] = "https://pypi.org/simple",
            ["PIP_NO_INPUT"] = "1",
            ["PIP_REQUIRE_VIRTUALENV"] = "0",
            ["PIP_TRUSTED_HOST"] = "",
        };

    private static readonly IReadOnlyDictionary<string, string> CSharpScannerEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["NUGET_XMLDOC_MODE"] = "skip",
        };

    private static readonly IReadOnlyDictionary<string, string> GoScannerEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GOINSECURE"] = "",
            ["GONOPROXY"] = "",
            ["GONOSUMDB"] = "",
            ["GOPRIVATE"] = "",
            ["GOPROXY"] = "https://proxy.golang.org",
            ["GOSUMDB"] = "sum.golang.org",
        };

    private static readonly IReadOnlyDictionary<string, Scanner> Scanners =
        new Dictionary<string, Scanner>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = new(
                "csharp",
                LanguageProjectDiscovery.CSharpDiscoveryScript,
                ["sh", "-c", CSharpScannerScript],
                "dotnet SDK not installed in sandbox; CVE scan skipped",
                "Install the .NET SDK in the sandbox image to enable C# CVE scanning.",
                ParseDotnetFindings,
                "'dotnet list package --vulnerable' exited with code {0}. Ensure a valid .NET solution or project file exists in the working directory.",
                CSharpScannerEnvironment),
            ["python"] = new(
                "python",
                LanguageProjectDiscovery.PythonDiscoveryScript,
                ["sh", "-c", PythonScannerScript],
                "pip-audit or safety not installed in sandbox; CVE scan skipped",
                "Install pip-audit or safety in the sandbox image to enable Python CVE scanning.",
                ParsePythonFindings,
                "Python dependency CVE scanner exited with code {0} but no vulnerability records were parsed.",
                PythonScannerEnvironment),
            ["node"] = new(
                "node",
                LanguageProjectDiscovery.NodeDiscoveryScript,
                ["sh", "-c", NodeScannerScript],
                "npm not installed in sandbox; CVE scan skipped",
                "Install Node.js/npm in the sandbox image to enable Node CVE scanning.",
                ParseNpmFindings,
                "npm audit exited with code {0} but no vulnerability records were parsed.",
                NpmAuditEnvironment),
            ["go"] = new(
                "go",
                LanguageProjectDiscovery.GoDiscoveryScript,
                ["govulncheck", "-json", "./..."],
                "govulncheck not installed in sandbox; CVE scan skipped",
                "Install govulncheck in the sandbox image to enable Go CVE scanning.",
                ParseGoFindings,
                "govulncheck exited with code {0} but no vulnerability records were parsed.",
                GoScannerEnvironment),
            ["rust"] = new(
                "rust",
                LanguageProjectDiscovery.RustDiscoveryScript,
                ["sh", "-c", RustScannerScript],
                "cargo-audit not installed in sandbox; CVE scan skipped",
                "Install cargo-audit in the sandbox image to enable Rust CVE scanning.",
                ParseCargoAuditFindings,
                "cargo audit exited with code {0} but no vulnerability records were parsed."),
        };

    public string Name => "deps-cve-scan";
    public string Kind => "shell";
    public AuditCapabilities Required => AuditCapabilities.Network;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        DeepAuditContext context,
        CancellationToken ct = default)
    {
        var languages = ResolveLanguages(context.Languages)
            .Where(ProjectAuditLanguages.IsSupported)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (languages.Count == 0)
            return new AuditResult(true, []);

        var allFindings = new List<AuditFinding>();
        var rawParts = new List<string>();
        var rawOutputChars = 0;
        var rawOutputTruncated = false;

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
            var projectDirectoriesToRun = LanguageProjectDiscovery.SelectProjectDirectoriesToRun(
                language,
                projectDirectories);

            foreach (var projectDirectory in projectDirectoriesToRun)
            {
                var result = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = scanner.Argv,
                    WorkingDirectory = ResolveWorkingDirectory(workingDirectory, projectDirectory),
                    ExtraEnvironment = scanner.ExtraEnvironment,
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

                if (IsUnsafeCSharpDependencySource(scanner, result))
                {
                    allFindings.Add(new AuditFinding(
                        AuditorName: Name,
                        Severity: AuditSeverity.Error,
                        Title: "C# CVE scan blocked repository-controlled NuGet source URLs",
                        Description: "C# dependency metadata contains NuGet package-source URLs outside the allowed public NuGet hosts (api.nuget.org and www.nuget.org). The dependency CVE scanner was not run because those URLs could trigger SSRF if the audit sandbox has egress. Stderr: " + TruncatedOutput(result.Stderr, out _)));
                    continue;
                }

                if (IsUnsafePythonDependencySource(scanner, result))
                {
                    allFindings.Add(new AuditFinding(
                        AuditorName: Name,
                        Severity: AuditSeverity.Error,
                        Title: "Python CVE scan blocked repository-controlled package source URLs",
                        Description: "Python dependency metadata contains direct package URLs or alternate package-source URLs outside the allowed public package hosts (pypi.org and files.pythonhosted.org). The dependency CVE scanner was not run because those URLs could trigger SSRF if the audit sandbox has egress. Stderr: " + TruncatedOutput(result.Stderr, out _)));
                    continue;
                }

                if (IsUnsafeNodeNpmConfig(scanner, result))
                {
                    allFindings.Add(new AuditFinding(
                        AuditorName: Name,
                        Severity: AuditSeverity.Error,
                        Title: "Node CVE scan blocked repository-controlled npm proxy settings",
                        Description: "Node dependency metadata contains .npmrc proxy settings. The dependency CVE scanner was not run because npm could use those repository-controlled proxy URLs to reach internal services if the audit sandbox has egress. Stderr: " + TruncatedOutput(result.Stderr, out _)));
                    continue;
                }

                if (IsUnsafeRustCargoAuditConfig(scanner, result))
                {
                    allFindings.Add(new AuditFinding(
                        AuditorName: Name,
                        Severity: AuditSeverity.Error,
                        Title: "Rust CVE scan blocked repository-controlled cargo-audit database settings",
                        Description: "Rust dependency metadata contains .cargo/audit.toml database settings. The dependency CVE scanner was not run because those repository-controlled settings can replace or disable the trusted RustSec advisory database. Stderr: " + TruncatedOutput(result.Stderr, out _)));
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

    private static IReadOnlyList<string> ResolveLanguages(IReadOnlyList<string>? languages)
        => languages ?? ["csharp"];

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

    private static bool IsUnsafeCSharpDependencySource(Scanner scanner, SandboxExecResult result)
        => scanner.Language.Equals("csharp", StringComparison.OrdinalIgnoreCase) &&
           result.ExitCode == 126 &&
           result.Stderr.Contains(CSharpUnsafeSourceMarker, StringComparison.Ordinal);

    private static bool IsUnsafePythonDependencySource(Scanner scanner, SandboxExecResult result)
        => scanner.Language.Equals("python", StringComparison.OrdinalIgnoreCase) &&
           result.ExitCode == 126 &&
           result.Stderr.Contains(PythonUnsafeSourceMarker, StringComparison.Ordinal);

    private static bool IsUnsafeNodeNpmConfig(Scanner scanner, SandboxExecResult result)
        => scanner.Language.Equals("node", StringComparison.OrdinalIgnoreCase) &&
           result.ExitCode == 126 &&
           result.Stderr.Contains(NodeUnsafeNpmConfigMarker, StringComparison.Ordinal);

    private static bool IsUnsafeRustCargoAuditConfig(Scanner scanner, SandboxExecResult result)
        => scanner.Language.Equals("rust", StringComparison.OrdinalIgnoreCase) &&
           result.ExitCode == 126 &&
           result.Stderr.Contains(RustUnsafeCargoAuditConfigMarker, StringComparison.Ordinal);

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
        string FailureDescription,
        IReadOnlyDictionary<string, string>? ExtraEnvironment = null);

    private sealed record GoFindingRecord(string Package, string Id);
    private sealed record GoOsvRecord(string Id, string? Package, string? Severity);
}
