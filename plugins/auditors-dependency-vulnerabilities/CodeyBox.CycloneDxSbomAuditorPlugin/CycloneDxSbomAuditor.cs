using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.CycloneDxSbomAuditorPlugin;

/// <summary>
/// Producer-neutral CycloneDX SBOM baseline-comparison auditor. Imports existing
/// CycloneDX evidence from any producer/ecosystem (NuGet, npm, Maven, native)
/// through the SAME Core import/validate/diff path, compares the candidate
/// against an operator-approved immutable baseline owned by the same
/// project/configuration, and reports additions, removals, and version and
/// relationship changes as actionable findings under explicit policy semantics.
///
/// <para><b>Not a vulnerability scanner.</b> A passing validation means the
/// inventory was well-formed and matches the approved baseline — it never
/// means the components are vulnerability-free. Vulnerability scanning stays
/// with the dedicated dependency-vulnerability auditors.</para>
///
/// <para><b>Default OFF.</b> The auditor runs only when the operator adds
/// <c>codeybox.sbom-cyclonedx</c> to <c>Plugins:Enabled</c> and sets
/// <c>CodeyBox:Plugins:codeybox.sbom-cyclonedx:Enabled=true</c> with an
/// approved baseline (<c>BaselinePath</c> plus its <c>BaselineDigest</c> pin).
/// Absent baselines, incomplete coverage, and unavailable generation/import
/// never pass: they fail with explicit findings or report unavailable.</para>
///
/// <para><b>Trust.</b> Repository-controlled files are validated, never
/// trusted: the baseline is accepted only when its bytes match the
/// operator-pinned digest, and embedded producer assertions never override
/// policy. Baseline promotion is an explicit operator-owned step outside this
/// auditor (update the baseline file and its digest pin). No referenced URL
/// is ever fetched.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: CycloneDX SBOM Baseline Comparison",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "cdxgen",
    InstallHint = "optional: provision a pinned cdxgen release (see GeneratorExpectedVersion, default "
        + CdxgenGeneratorAdapter.DefaultExpectedVersion + ") into the sandbox baseline only when SBOM "
        + "generation is selected (CodeyBox:Plugins:codeybox.sbom-cyclonedx:Generator=cdxgen) — "
        + "download the versioned upstream release (https://github.com/cdxgen/cdxgen) via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions. "
        + "Import/validation of existing producer evidence needs no generator.")]
public sealed class CycloneDxSbomAuditor : IAuditor, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.sbom-cyclonedx";

    /// <summary>Stable auditor name for logs and findings.</summary>
    public const string AuditorName = "codeybox:sbom-cyclonedx";

    /// <summary>Scoped-config key for the master switch.</summary>
    public const string EnabledKey = "Enabled";

    /// <summary>Well-known producer outputs reused before any generation is attempted.</summary>
    internal static readonly IReadOnlyList<string> WellKnownCandidatePaths =
    [
        "bom.json",
        "sbom.json",
        "cyclonedx.json",
        "sbom.cdx.json",
        ".cyclonedx/bom.json",
        "bom.xml",
        "sbom.xml",
    ];

    private const int RawOutputMaxChars = 64 * 1024;

    private Func<CycloneDxSbomAuditorOptions> _optionsAccessor = static () => new CycloneDxSbomAuditorOptions();

    /// <inheritdoc />
    public string Name => AuditorName;

    /// <inheritdoc />
    public string Kind => "tool";

    /// <inheritdoc />
    public AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => BindOptions(scoped);
        context.Logger.LogInformation("CycloneDxSbomAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    internal static CycloneDxSbomAuditorOptions BindOptions(Microsoft.Extensions.Configuration.IConfigurationSection scoped)
    {
        return new CycloneDxSbomAuditorOptions
        {
            Enabled = bool.TryParse(scoped[EnabledKey], out var enabled) && enabled,
            PolicyMode = string.IsNullOrWhiteSpace(scoped["PolicyMode"]) ? nameof(SbomPolicyMode.FailOnAnyChange) : scoped["PolicyMode"]!.Trim(),
            CandidatePath = scoped["CandidatePath"]?.Trim() ?? string.Empty,
            BaselinePath = scoped["BaselinePath"]?.Trim() ?? string.Empty,
            BaselineDigest = scoped["BaselineDigest"]?.Trim().ToLowerInvariant() ?? string.Empty,
            BaselineProjectId = scoped["BaselineProjectId"]?.Trim() ?? string.Empty,
            BaselineConfigDigest = scoped["BaselineConfigDigest"]?.Trim().ToLowerInvariant() ?? string.Empty,
            Generator = scoped["Generator"]?.Trim() ?? string.Empty,
            GeneratorExpectedVersion = scoped["GeneratorExpectedVersion"]?.Trim() ?? string.Empty,
            EcosystemTags = scoped["EcosystemTags"]?.Trim() ?? string.Empty,
            ValidatorTool = scoped["ValidatorTool"]?.Trim() ?? string.Empty,
            ValidatorExpectedVersion = scoped["ValidatorExpectedVersion"]?.Trim() ?? string.Empty,
            MaxSbomBytes = ReadInt(scoped["MaxSbomBytes"], 5 * 1024 * 1024),
            MaxComponents = ReadInt(scoped["MaxComponents"], 20_000),
            MaxDependencies = ReadInt(scoped["MaxDependencies"], 60_000),
            SupportedSpecVersions = string.IsNullOrWhiteSpace(scoped["SupportedSpecVersions"]) ? "1.4,1.5,1.6" : scoped["SupportedSpecVersions"]!,
            OperationTimeoutSeconds = ReadInt(scoped["OperationTimeoutSeconds"], 120),
        };
    }

    private static int ReadInt(string? value, int fallback) =>
        int.TryParse(value?.Trim(), out var parsed) && parsed > 0 ? parsed : fallback;

    /// <inheritdoc />
    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        var options = _optionsAccessor();
        if (!options.Enabled)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{AuditorName}' is disabled by configuration (default OFF).")
            { IsDeterministic = true };

        var coreOptions = options.ToCoreOptions();
        if (!SbomCycloneDxOptions.IsValid(coreOptions))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{AuditorName}' has invalid SBOM options (bounds, spec versions, or policy mode).")
            { IsDeterministic = true };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.OperationTimeoutSeconds, 5, 3600)));
        var timeout = timeoutCts.Token;

        var (candidateBytes, candidateSource) = await AcquireCandidateAsync(sandbox, workingDirectory, options, timeout).ConfigureAwait(false);
        var candidateFormat = DetectFormat(candidateSource, candidateBytes);
        var candidateImport = SbomCycloneDxImport.Import(candidateBytes, candidateFormat, coreOptions, timeout);
        var candidateDigest = SbomCycloneDxImport.DigestBytes(candidateBytes);

        if (!candidateImport.Ok || candidateImport.Document is null)
        {
            var importFindings = candidateImport.Issues.Select(i => new AuditFinding(
                Name, AuditSeverity.Error,
                $"SBOM evidence rejected ({i.Code})",
                SbomCycloneDxDiff.Redact(i.Message) + " Supply well-formed CycloneDX evidence from any producer (cdxgen, cyclonedx-cli, cyclonedx-npm, syft) and re-run; rejected evidence never passes.")).ToList();
            return Fail(importFindings, Report(candidateDigest, candidateSource, candidateImport.Issues, null, null));
        }

        if (!string.IsNullOrEmpty(options.ValidatorTool))
        {
            var validatorError = await CycloneDxCliValidator.ValidateAsync(
                sandbox, candidateBytes, candidateFormat, options, timeout).ConfigureAwait(false);
            if (validatorError is not null && !validatorError.StartsWith("validator not selected", StringComparison.Ordinal))
            {
                return Fail(
                    [new AuditFinding(Name, AuditSeverity.Error, "SBOM evidence rejected (trusted validator)",
                        SbomCycloneDxDiff.Redact(validatorError) + " Supply evidence the pinned validator accepts and re-run.")],
                    Report(candidateDigest, candidateSource, candidateImport.Issues, null, null));
            }
        }

        if (string.IsNullOrWhiteSpace(options.BaselineDigest) || string.IsNullOrWhiteSpace(options.BaselinePath))
        {
            return Fail(
                [new AuditFinding(Name, AuditSeverity.Error, "No approved SBOM baseline",
                    "No operator-approved immutable baseline is configured (BaselinePath plus its BaselineDigest pin). " +
                    "An absent baseline never passes: approve one through the explicit operator-owned promotion step " +
                    "(store the approved SBOM bytes and pin their SHA-256 digest in BaselineDigest), then re-run.")],
                Report(candidateDigest, candidateSource, [], candidateImport.Document, null));
        }

        var baselineBytes = await ReadWorktreeFileAsync(sandbox, workingDirectory, options.BaselinePath, options, timeout).ConfigureAwait(false);
        if (baselineBytes is null)
        {
            return Fail(
                [new AuditFinding(Name, AuditSeverity.Error, "Approved SBOM baseline unavailable",
                    $"The configured baseline '{options.BaselinePath}' could not be read. An unavailable baseline never passes.")],
                Report(candidateDigest, candidateSource, [], candidateImport.Document, null));
        }
        var baselineDigest = SbomCycloneDxImport.DigestBytes(baselineBytes);
        if (!string.Equals(baselineDigest, options.BaselineDigest.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            return Fail(
                [new AuditFinding(Name, AuditSeverity.Error, "Approved SBOM baseline mismatch",
                    "The baseline bytes do not match the operator-pinned BaselineDigest. The repository-controlled " +
                    "baseline choice is never trusted without verification: re-approve through the explicit " +
                    "operator-owned promotion step (update the baseline file and its digest pin), then re-run.")],
                Report(candidateDigest, candidateSource, [], candidateImport.Document, null));
        }

        var baselineFormat = DetectFormat(options.BaselinePath, baselineBytes);
        var baselineImport = SbomCycloneDxImport.Import(baselineBytes, baselineFormat, coreOptions, timeout);
        if (!baselineImport.Ok || baselineImport.Document is null)
        {
            return Fail(
                baselineImport.Issues.Select(i => new AuditFinding(
                    Name, AuditSeverity.Error,
                    $"Approved baseline rejected ({i.Code})",
                    SbomCycloneDxDiff.Redact(i.Message) + " The approved baseline itself must be well-formed; re-approve valid evidence.")).ToList(),
                Report(candidateDigest, candidateSource, [], candidateImport.Document, null));
        }

        var baseline = new SbomBaseline
        {
            ProjectId = string.IsNullOrWhiteSpace(options.BaselineProjectId) ? context.ProjectId ?? string.Empty : options.BaselineProjectId,
            ConfigDigest = string.IsNullOrWhiteSpace(options.BaselineConfigDigest) ? CurrentConfigDigest(options) : options.BaselineConfigDigest,
            ContentDigest = baselineDigest,
            Document = baselineImport.Document,
        };
        var candidateBinding = new SbomCandidateBinding
        {
            ProjectId = context.ProjectId ?? string.Empty,
            ResolvedSha = context.WorkBranch,
            ConfigDigest = CurrentConfigDigest(options),
            ContentDigest = candidateDigest,
        };
        if (!string.IsNullOrWhiteSpace(options.BaselineProjectId)
            && !string.IsNullOrWhiteSpace(context.ProjectId)
            && !string.Equals(baseline.ProjectId, candidateBinding.ProjectId, StringComparison.Ordinal))
        {
            return Fail(
                [new AuditFinding(Name, AuditSeverity.Error, "SBOM baseline project mismatch",
                    $"The approved baseline belongs to project '{baseline.ProjectId}' but the candidate belongs to '{candidateBinding.ProjectId}'. Baselines never cross projects.")],
                Report(candidateDigest, candidateSource, [], candidateImport.Document, baseline));
        }
        if (!string.IsNullOrWhiteSpace(options.BaselineConfigDigest)
            && !string.Equals(baseline.ConfigDigest, candidateBinding.ConfigDigest, StringComparison.Ordinal))
        {
            return Fail(
                [new AuditFinding(Name, AuditSeverity.Error, "SBOM baseline configuration mismatch",
                    "The approved baseline was pinned under a different configuration digest. Re-approve under the current configuration through the explicit operator-owned promotion step.")],
                Report(candidateDigest, candidateSource, [], candidateImport.Document, baseline));
        }
        var bindingError = SbomCycloneDxDiff.VerifyBinding(
            baseline with { ProjectId = candidateBinding.ProjectId, ConfigDigest = candidateBinding.ConfigDigest },
            candidateBinding);
        if (bindingError is not null)
        {
            return Fail(
                [new AuditFinding(Name, AuditSeverity.Error, "SBOM candidate binding failed",
                    SbomCycloneDxDiff.Redact(bindingError))],
                Report(candidateDigest, candidateSource, [], candidateImport.Document, baseline));
        }

        if (!Enum.TryParse<SbomPolicyMode>(options.PolicyMode, ignoreCase: false, out var policy))
        {
            return Fail(
                [new AuditFinding(Name, AuditSeverity.Error, "SBOM policy misconfigured",
                    $"PolicyMode '{options.PolicyMode}' is unknown. Use FailOnAnyChange, FailOnAddedOrVersionChanged, or AdvisoryOnly.")],
                Report(candidateDigest, candidateSource, [], candidateImport.Document, baseline));
        }

        var diff = SbomCycloneDxDiff.Compare(baseline.Document, candidateImport.Document, timeout);
        var verdict = SbomCycloneDxDiff.Evaluate(diff, policy, candidateImport.Document.InventoryComplete);
        var findings = verdict.Findings.ToList();
        if (verdict is { Passed: true, EvidenceInsufficient: false } && diff.IsEmpty)
        {
            findings.Add(new AuditFinding(Name, AuditSeverity.Info, "SBOM matches the approved baseline",
                "The candidate inventory is well-formed and matches the operator-approved baseline under the configured policy. " +
                "Validation confirms inventory integrity and baseline agreement — it does not mean the components are vulnerability-free."));
        }
        return new AuditResult(verdict.Passed && !verdict.EvidenceInsufficient, findings,
            RawOutput: SbomCycloneDxDiff.Redact(Report(candidateDigest, candidateSource, [], candidateImport.Document, baseline, diff)));
    }

    internal async Task<(byte[] Content, string Source)> AcquireCandidateAsync(
        ISandbox sandbox,
        string workingDirectory,
        CycloneDxSbomAuditorOptions options,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.CandidatePath))
        {
            var explicitBytes = await ReadWorktreeFileAsync(sandbox, workingDirectory, options.CandidatePath, options, ct).ConfigureAwait(false);
            if (explicitBytes is null)
                throw new AuditUnavailableException(
                    $"could-not-verify: configured SBOM candidate '{options.CandidatePath}' could not be read.");
            return (explicitBytes, options.CandidatePath);
        }

        foreach (var wellKnown in WellKnownCandidatePaths)
        {
            var bytes = await ReadWorktreeFileAsync(sandbox, workingDirectory, wellKnown, options, ct).ConfigureAwait(false);
            if (bytes is not null)
                return (bytes, wellKnown);
        }

        if (!string.IsNullOrWhiteSpace(options.Generator))
        {
            if (!string.Equals(options.Generator, "cdxgen", StringComparison.Ordinal))
                throw new AuditUnavailableException(
                    $"could-not-verify: SBOM generator '{options.Generator}' is unknown (supported: 'cdxgen' when explicitly selected).")
                { IsDeterministic = true };
            if (string.IsNullOrWhiteSpace(options.EcosystemTags))
                throw new AuditUnavailableException(
                    "could-not-verify: SBOM generation needs explicit project capabilities (EcosystemTags, e.g. 'npm,nuget,maven,native').")
                { IsDeterministic = true };
            var generator = new CdxgenGeneratorAdapter();
            var generated = await generator.GenerateAsync(sandbox, workingDirectory, options, ct).ConfigureAwait(false);
            return (generated, "generated:cdxgen");
        }

        throw new AuditUnavailableException(
            "could-not-verify: no SBOM candidate found (checked well-known producer outputs; no generator selected). Supply CycloneDX evidence or select a generator explicitly.");
    }

    internal static async Task<byte[]?> ReadWorktreeFileAsync(
        ISandbox sandbox,
        string workingDirectory,
        string relativePath,
        CycloneDxSbomAuditorOptions options,
        CancellationToken ct)
    {
        ValidateWorktreeRelativePath(relativePath);
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["cat", "--", relativePath.Trim()],
            WorkingDirectory = workingDirectory,
            MaxStdoutBytes = options.MaxSbomBytes + 1,
            MaxStderrBytes = 4096,
        }, ct).ConfigureAwait(false);
        if (!result.Success || result.StdoutLimitExceeded)
            return null;
        var bytes = Encoding.UTF8.GetBytes(result.Stdout ?? string.Empty);
        if (bytes.Length == 0)
            return null;
        if (bytes.Length > options.MaxSbomBytes)
            throw new AuditUnavailableException(
                $"could-not-verify: SBOM file '{relativePath}' exceeds the {options.MaxSbomBytes}-byte cap.");
        return bytes;
    }

    internal static void ValidateWorktreeRelativePath(string path)
    {
        var trimmed = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 512)
            throw new AuditUnavailableException($"could-not-verify: SBOM path '{path}' is invalid.")
            { IsDeterministic = true };
        if (Path.IsPathRooted(trimmed) || trimmed.StartsWith("~", StringComparison.Ordinal)
            || trimmed.Split('/').Any(static segment => string.Equals(segment, "..", StringComparison.Ordinal))
            || trimmed.Any(static c => c is ';' or '&' or '|' or '$' or '`' or '\'' || char.IsControl(c)))
            throw new AuditUnavailableException($"could-not-verify: SBOM path '{path}' escapes the worktree or carries shell metacharacters.")
            { IsDeterministic = true };
    }

    internal static string DetectFormat(string source, byte[] content)
    {
        if (source.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return "xml";
        var text = Encoding.UTF8.GetString(content, 0, Math.Min(content.Length, 512)).TrimStart();
        return text.StartsWith('<') ? "xml" : "json";
    }

    internal static string CurrentConfigDigest(CycloneDxSbomAuditorOptions options)
    {
        var payload = JsonSerializer.Serialize(new
        {
            options.PolicyMode,
            options.SupportedSpecVersions,
            options.MaxSbomBytes,
            options.MaxComponents,
            options.MaxDependencies,
            options.Generator,
            options.EcosystemTags,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16].ToLowerInvariant();
    }

    private AuditResult Fail(IReadOnlyList<AuditFinding> findings, string report) =>
        new(false, findings, RawOutput: SbomCycloneDxDiff.Redact(report));

    private string Report(
        string candidateDigest,
        string candidateSource,
        IReadOnlyList<SbomValidationIssue> issues,
        SbomDocument? candidate,
        SbomBaseline? baseline,
        SbomDiff? diff = null)
    {
        var payload = new
        {
            auditor = AuditorName,
            candidateDigest,
            candidateSource,
            candidateSpec = candidate?.SpecVersion,
            candidateFormat = candidate?.Format,
            candidateComponents = candidate?.Components.Count,
            producer = candidate?.Producer.Producer,
            tool = candidate?.Producer.ToolName,
            toolVersion = candidate?.Producer.ToolVersion,
            baselineDigest = baseline?.ContentDigest,
            validationIssues = issues.Select(i => new { i.Code, i.Message }).ToArray(),
            diff = diff is null
                ? null
                : (object?)JsonSerializer.Deserialize<JsonElement>(SbomCycloneDxDiff.SerializeDiff(diff, candidate!, baseline)),
            note = "SBOM validation confirms inventory integrity and baseline agreement; it is not a vulnerability verdict.",
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return json.Length <= RawOutputMaxChars ? json : json[..RawOutputMaxChars] + "[...truncated]";
    }
}
