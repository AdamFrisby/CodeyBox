using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CodeyBox.Build.MSBuild;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Opted-in MSBuild binary-log capture inside the existing isolated required-build
/// execution path. No second build is ever executed for diagnostics: the same
/// sandbox session that ran the build carries the <c>-bl</c> capture, and the
/// bytes are parsed locally in this process through the capability-based
/// <see cref="IBuildDiagnosticsProducer"/> seam.
///
/// The enrichment never alters the authoritative build outcome. Every capture,
/// transfer, parse, or binding problem yields explicit
/// <see cref="BuildDiagnosticsStatus.InsufficientDiagnostics"/> evidence while
/// the Failed result stands; caller cancellation still propagates.
/// </summary>
internal static class MSBuildDiagnosticsEnrichment
{
    internal const string BinlogDirectoryName = RequiredBuildScript.BinlogDirectoryName;
    internal const string BinlogConfigVariable = RequiredBuildScript.BinlogConfigVariable;

    private const int BinlogFileNameMaxIndex = 9999;

    private static readonly Regex BinlogFileNamePattern = new(
        "^target-([0-9]{1,4})\\.binlog$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CommitShaPattern = new(
        "^[0-9a-f]{40}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Capture variant of the required-build shell: composed by
    // RequiredBuildScript from the same discovery and isolation fragments
    // as the default path, with least-data `-bl` logging added to each
    // `dotnet build` (ProjectImports=None: no embedded imported-project
    // content) into the fixed directory this file's fetch step knows. The
    // build configuration arrives via CODEYBOX_MSBUILD_CONFIG (validated
    // operator config, re-checked by the case guard); binlog file names are
    // index-derived, never taken from branch-controlled paths.
    internal static readonly string BuildScriptWithBinlogCapture =
        RequiredBuildScript.Build(captureBinlogs: true);

    /// <summary>
    /// Reads the live options once per verification (hot-reloadable, no
    /// restart). Fail-safe: if options cannot be read, capture stays off and
    /// the build runs exactly as it does today.
    /// </summary>
    internal static MSBuildDiagnosticsOptions ReadOptions(Func<MSBuildDiagnosticsOptions>? optionsAccessor)
    {
        try
        {
            return optionsAccessor?.Invoke() ?? new MSBuildDiagnosticsOptions();
        }
        catch (Exception)
        {
            return new MSBuildDiagnosticsOptions();
        }
    }

    /// <summary>
    /// Captures the binlogs written by the failed build in this sandbox
    /// session, parses them locally, and returns attempt-bound evidence.
    /// Returns null when enrichment is disabled; otherwise always returns
    /// evidence (Enriched or explicit InsufficientDiagnostics). Only caller
    /// cancellation propagates as an exception. The options are the single
    /// per-verification read taken by the caller, so the capture script,
    /// fetch caps, and evidence binding all agree even if the operator
    /// reloads configuration mid-build.
    /// </summary>
    internal static async Task<BuildDiagnosticsEvidence?> EnrichFailedBuildAsync(
        ISandbox sandbox,
        string workBranch,
        MSBuildDiagnosticsOptions? options,
        IBuildDiagnosticsProducer? producer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(workBranch);

        options ??= new MSBuildDiagnosticsOptions();
        if (!options.Enabled || producer is null || !producer.IsEnabled)
            return null;

        var attempt = Guid.NewGuid().ToString("N");
        BuildDiagnosticsSourceBinding FallbackBinding() => new()
        {
            SourceRef = workBranch,
            Configuration = options.BuildConfiguration,
            Attempt = attempt,
        };

        if (!MSBuildDiagnosticsOptions.IsValidConfiguration(options.BuildConfiguration))
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId,
                FallbackBinding(),
                "invalid-configuration: the configured build configuration is not an allowlisted value");

        try
        {
            var commitSha = await ReadCommitShaAsync(sandbox, ct).ConfigureAwait(false);
            var binding = new BuildDiagnosticsSourceBinding
            {
                SourceRef = commitSha ?? workBranch,
                Configuration = options.BuildConfiguration,
                Attempt = attempt,
            };

            var listing = await ListBinlogNamesAsync(sandbox, options, ct).ConfigureAwait(false);
            if (!listing.Available)
                return BuildDiagnosticsEvidence.Insufficient(
                    MSBuildDiagnosticsOptions.ProviderId,
                    binding,
                    "capture-failure: the failed build's binary logs could not be listed from the sandbox");
            if (listing.Names.Count == 0)
                return BuildDiagnosticsEvidence.Insufficient(
                    MSBuildDiagnosticsOptions.ProviderId,
                    binding,
                    "missing: the failed build wrote no binary log");

            var parts = new List<BuildDiagnosticsEvidence>(listing.Names.Count);
            // Every listed log that yields no usable diagnostics is named
            // here: a merged subset must never masquerade as the whole
            // attempt's evidence.
            var dropped = new List<string>();
            if (listing.Truncated)
                dropped.Add($"listing capped at {options.MaxBinlogsPerBuild} log(s): further logs were not examined");
            foreach (var name in listing.Names)
            {
                var fetch = await FetchAndProduceAsync(sandbox, name, binding, options, producer, ct)
                    .ConfigureAwait(false);
                if (fetch.Evidence is null)
                {
                    dropped.Add($"{name} ({fetch.DropReason})");
                    continue;
                }
                parts.Add(fetch.Evidence);
                if (fetch.Evidence.Status != BuildDiagnosticsStatus.Enriched)
                {
                    var detail = string.IsNullOrWhiteSpace(fetch.Evidence.Reason)
                        ? "no usable diagnostics"
                        : SingleLine(fetch.Evidence.Reason);
                    dropped.Add($"{name} ({BoundDetail(detail)})");
                }
            }

            if (parts.Count == 0)
                return BuildDiagnosticsEvidence.Insufficient(
                    MSBuildDiagnosticsOptions.ProviderId,
                    binding,
                    $"unreadable: {dropped.Count} collected log(s) yielded no usable diagnostics: {string.Join("; ", dropped)}");

            var merged = MSBuildEvidenceMerger.Merge(parts, binding, options);
            if (!merged.SourceBinding.Matches(binding))
                return BuildDiagnosticsEvidence.Insufficient(
                    MSBuildDiagnosticsOptions.ProviderId,
                    binding,
                    "source-mismatch: merged evidence is not bound to this source/configuration/attempt");
            if (dropped.Count > 0)
            {
                var note = $"partial-evidence: {dropped.Count} gap(s) in this attempt's logs: {string.Join("; ", dropped)}";
                if (merged.Status == BuildDiagnosticsStatus.Enriched)
                    return merged with { Truncated = true, Reason = note };
                var mergedReason = string.IsNullOrWhiteSpace(merged.Reason) ? note : $"{merged.Reason}; {note}";
                return merged with { Reason = mergedReason };
            }
            return merged;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Justification: enrichment is best-effort. Any capture/transfer
            // failure (including sandbox transport loss after the build
            // already failed) must not escalate into infrastructure handling
            // or mask the definitive Failed outcome, so it becomes explicit
            // insufficient-diagnostics. Redacted and single-lined.
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId,
                FallbackBinding(),
                $"capture-failure: {SingleLine(RawOutputRedactor.Redact(ex.GetType().Name + ": " + ex.Message))}");
        }
    }

    private static async Task<string?> ReadCommitShaAsync(ISandbox sandbox, CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "rev-parse", "HEAD"],
            WorkingDirectory = SandboxConventions.WorkDir,
            MaxStdoutBytes = 256,
            MaxStderrBytes = 1024,
        }, ct).ConfigureAwait(false);
        if (!result.Success || result.ExecutionUnavailable)
            return null;
        var sha = result.Stdout.Trim();
        return CommitShaPattern.IsMatch(sha) ? sha : null;
    }

    /// <summary>
    /// The outcome of listing the attempt's binlog directory: the
    /// allowlisted file names, whether the listing itself succeeded, and
    /// whether the listing was cut short (transfer cap or per-build file
    /// cap), in which case the caller must not present the collected subset
    /// as the whole attempt.
    /// </summary>
    private sealed record BinlogListing(IReadOnlyList<string> Names, bool Available, bool Truncated);

    private static async Task<BinlogListing> ListBinlogNamesAsync(
        ISandbox sandbox,
        MSBuildDiagnosticsOptions options,
        CancellationToken ct)
    {
        // Fixed directory (no PID interpolation: each exec is a fresh shell
        // with its own $$). Structured argv only; the listing is filtered by
        // an exact file-name allowlist below.
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [
                "sh", "-c",
                "ls -1 -- \"$1\" 2>/dev/null",
                "codeybox-binlog-list",
                "${TMPDIR:-/tmp}/" + BinlogDirectoryName,
            ],
            WorkingDirectory = SandboxConventions.WorkDir,
            MaxStdoutBytes = 4096,
            MaxStderrBytes = 1024,
        }, ct).ConfigureAwait(false);
        if (!result.Success || result.ExecutionUnavailable)
            return new BinlogListing([], Available: false, Truncated: false);

        var names = new List<string>();
        var capped = false;
        foreach (var line in result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = line.Trim();
            var match = BinlogFileNamePattern.Match(name);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[1].Value, out var index)
                || index < 1
                || index > BinlogFileNameMaxIndex)
                continue;
            if (names.Count >= options.MaxBinlogsPerBuild)
            {
                capped = true;
                continue;
            }
            names.Add(name);
        }
        names.Sort(StringComparer.Ordinal);
        return new BinlogListing(names, Available: true, Truncated: capped || result.StdoutLimitExceeded);
    }

    /// <summary>
    /// One binlog fetch-and-produce outcome: usable evidence, or (when null)
    /// a fixed-literal reason naming why this file yielded nothing. Reasons
    /// never echo sandbox output — only the allowlisted file name (known by
    /// the caller) and fixed literals travel into retained evidence.
    /// </summary>
    private sealed record BinlogFetch(BuildDiagnosticsEvidence? Evidence, string? DropReason);

    private static async Task<BinlogFetch> FetchAndProduceAsync(
        ISandbox sandbox,
        string fileName,
        BuildDiagnosticsSourceBinding binding,
        MSBuildDiagnosticsOptions options,
        IBuildDiagnosticsProducer producer,
        CancellationToken ct)
    {
        // One bounded exec prints "<byte-size>\n<base64>". The file name was
        // allowlisted by ListBinlogNamesAsync and travels as "$1" (never
        // interpolated), so branch-controlled content cannot reach a shell.
        var fetchCap = checked((int)Math.Min(
            options.MaxBinlogBytes / 3 * 4 + 128,
            int.MaxValue / 2));
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [
                "sh", "-c",
                "size=$(stat -c %s -- \"$1\" 2>/dev/null) || exit 3; printf '%s\\n' \"$size\"; base64 -w0 -- \"$1\"",
                "codeybox-binlog-fetch",
                "${TMPDIR:-/tmp}/" + BinlogDirectoryName + "/" + fileName,
            ],
            WorkingDirectory = SandboxConventions.WorkDir,
            MaxStdoutBytes = fetchCap,
            MaxStderrBytes = 1024,
        }, ct).ConfigureAwait(false);
        if (!result.Success || result.ExecutionUnavailable)
            return new BinlogFetch(null, "fetch-failed: sandbox transfer was unsuccessful or unavailable");
        if (result.StdoutLimitExceeded)
            return new BinlogFetch(null, "truncated: fetch exceeded the transfer cap");

        var newline = result.Stdout.IndexOf('\n');
        if (newline <= 0)
            return new BinlogFetch(null, "malformed: fetch output is missing size framing");
        if (!long.TryParse(result.Stdout.AsSpan(0, newline).Trim(), out var size) || size <= 0)
            return new BinlogFetch(null, "malformed: fetch output has an invalid size prefix");
        if (size > options.MaxBinlogBytes)
            return new BinlogFetch(null, "oversized: log exceeds the per-file byte bound");

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(result.Stdout[(newline + 1)..].Trim());
        }
        catch (FormatException)
        {
            return new BinlogFetch(null, "malformed: fetch payload is not valid base64");
        }
        if (payload.Length == 0 || payload.Length > options.MaxBinlogBytes)
            return new BinlogFetch(null, "malformed: decoded payload is empty or exceeds the per-file byte bound");

        var evidence = await producer.ProduceAsync(new BuildDiagnosticsProductionRequest
        {
            ExpectedBinding = binding,
            PayloadFormat = MSBuildDiagnosticsOptions.PayloadFormat,
            PayloadBytes = payload,
        }, ct).ConfigureAwait(false);

        using var sha = SHA256.Create();
        return new BinlogFetch(
            evidence with
            {
                ArtifactRefs =
                [
                    new BuildDiagnosticsArtifactRef
                    {
                        Name = fileName,
                        ContentHashSha256 = Convert.ToHexString(sha.ComputeHash(payload)).ToLowerInvariant(),
                        ByteSize = payload.Length,
                    },
                ],
            },
            DropReason: null);
    }

    /// <summary>
    /// Bounds a producer reason before it is embedded in a merged-evidence
    /// note. Producer reasons are short fixed literals; the cap only guards
    /// a future producer that interpolates unbounded text.
    /// </summary>
    private static string BoundDetail(string detail, int maxChars = 200) =>
        detail.Length <= maxChars ? detail : detail[..maxChars] + "...";

    private static string SingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}
