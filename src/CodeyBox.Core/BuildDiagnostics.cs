namespace CodeyBox.Core;

/// <summary>
/// Severity of one structured build diagnostic. Language- and
/// toolchain-neutral: producers map their native severities onto these three.
/// </summary>
public enum BuildDiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// Exact source/configuration/attempt identity a structured build-diagnostics
/// payload was captured from. Diagnostics evidence is only attachable to the
/// verification attempt whose binding it carries; any mismatch (stale log from
/// another commit, another configuration, or another attempt) must be
/// rejected as substituted evidence rather than attached.
/// </summary>
public sealed record BuildDiagnosticsSourceBinding
{
    public required string SourceRef { get; init; }
    public string? Configuration { get; init; }
    public required string Attempt { get; init; }

    /// <summary>
    /// Exact-match binding comparison. Ordinal, case-sensitive: branch names,
    /// commit SHAs, configuration names, and attempt ids compare by exact
    /// equality, never by substring or prefix.
    /// </summary>
    public bool Matches(BuildDiagnosticsSourceBinding? other) =>
        other is not null
        && string.Equals(SourceRef, other.SourceRef, StringComparison.Ordinal)
        && string.Equals(Configuration ?? string.Empty, other.Configuration ?? string.Empty, StringComparison.Ordinal)
        && string.Equals(Attempt, other.Attempt, StringComparison.Ordinal);

    public string Describe() =>
        string.IsNullOrEmpty(Configuration)
            ? $"{SourceRef}@{Attempt}"
            : $"{SourceRef}/{Configuration}@{Attempt}";
}

/// <summary>
/// Optional source location of one diagnostic. Line/column are 1-based when
/// present; all fields are producer-truncated and redacted before retention.
/// </summary>
public sealed record BuildDiagnosticLocation
{
    public string? Path { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
}

/// <summary>
/// One structured build diagnostic: a redacted, bounded, attributable build
/// failure or warning. Carries provider/tool identity (<see cref="ProviderId"/>),
/// severity, optional toolchain code, optional source location, optional
/// project/target context, and causal edges (<see cref="CausedByIds"/>) that
/// point at the diagnostics believed to have caused this one.
/// </summary>
public sealed record BuildDiagnostic
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public BuildDiagnosticSeverity Severity { get; init; }
    public string? Code { get; init; }
    public required string Message { get; init; }
    public BuildDiagnosticLocation? Location { get; init; }
    public string? Project { get; init; }
    public string? Target { get; init; }
    public IReadOnlyList<string> CausedByIds { get; init; } = [];
    public bool IsRootCause { get; init; }
}

/// <summary>Outcome of a structured build-diagnostics production attempt.</summary>
public enum BuildDiagnosticsStatus
{
    /// <summary>Producer is disabled; no diagnostics were attempted.</summary>
    Disabled = 0,
    /// <summary>Producer does not apply (e.g. non-matching toolchain); no gate is added.</summary>
    NotApplicable = 1,
    /// <summary>Actionable diagnostics were extracted.</summary>
    Enriched = 2,
    /// <summary>
    /// No usable diagnostics could be extracted (missing, malformed,
    /// truncated, oversized, unsupported, timed-out, or source-mismatched
    /// input). Never a pass: the authoritative build outcome stands and the
    /// <see cref="BuildDiagnosticsEvidence.Reason"/> explains the gap.
    /// </summary>
    InsufficientDiagnostics = 3,
}

/// <summary>
/// Reference to a retained diagnostics artifact. Carries identity only
/// (name, content hash, size) — never raw content. Raw toolchain logs are
/// never retained or uploaded; only redacted <see cref="BuildDiagnostic"/>
/// records flow into audit evidence.
/// </summary>
public sealed record BuildDiagnosticsArtifactRef
{
    public required string Name { get; init; }
    public required string ContentHashSha256 { get; init; }
    public required long ByteSize { get; init; }
}

/// <summary>
/// Language-neutral structured build-diagnostics evidence attached to a build
/// verification result. Producers fill it; the repair loop reads the root
/// causes plus project/target context. Bounded: the producer caps diagnostic
/// and artifact-ref counts and sets <see cref="Truncated"/> when a bound bit.
/// </summary>
public sealed record BuildDiagnosticsEvidence
{
    public required string ProviderId { get; init; }
    public required BuildDiagnosticsSourceBinding SourceBinding { get; init; }
    public required BuildDiagnosticsStatus Status { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<BuildDiagnostic> Diagnostics { get; init; } = [];
    public IReadOnlyList<string> RootCauseIds { get; init; } = [];
    public int TotalErrorCount { get; init; }
    public int TotalWarningCount { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<BuildDiagnosticsArtifactRef> ArtifactRefs { get; init; } = [];

    public bool IsInsufficient => Status == BuildDiagnosticsStatus.InsufficientDiagnostics;

    public static BuildDiagnosticsEvidence Disabled(string providerId, BuildDiagnosticsSourceBinding binding) =>
        new() { ProviderId = providerId, SourceBinding = binding, Status = BuildDiagnosticsStatus.Disabled };

    public static BuildDiagnosticsEvidence NotApplicable(string providerId, BuildDiagnosticsSourceBinding binding) =>
        new() { ProviderId = providerId, SourceBinding = binding, Status = BuildDiagnosticsStatus.NotApplicable };

    public static BuildDiagnosticsEvidence Insufficient(
        string providerId,
        BuildDiagnosticsSourceBinding binding,
        string reason) =>
        new()
        {
            ProviderId = providerId,
            SourceBinding = binding,
            Status = BuildDiagnosticsStatus.InsufficientDiagnostics,
            Reason = reason,
        };
}

/// <summary>
/// Neutral production request handed to an <see cref="IBuildDiagnosticsProducer"/>.
/// The payload format id is matched exactly by the producer; the payload bytes
/// are re-bounded by the producer at its own sink. The expected binding is
/// echoed into the returned evidence so the caller can reject stale input.
/// </summary>
public sealed record BuildDiagnosticsProductionRequest
{
    public required BuildDiagnosticsSourceBinding ExpectedBinding { get; init; }
    public required string PayloadFormat { get; init; }
    public byte[]? PayloadBytes { get; init; }
}

/// <summary>
/// Capability-based contract for structured build-diagnostics producers.
/// Toolchain-specific parsing lives behind this neutral seam so the build
/// lifecycle never depends on a concrete log format: each adapter declares a
/// stable <see cref="ProviderId"/>, an <see cref="IsEnabled"/> switch (off by
/// default), and produces <see cref="BuildDiagnosticsEvidence"/> from an
/// opaque payload. Producers must return
/// <see cref="BuildDiagnosticsStatus.InsufficientDiagnostics"/> for bad input
/// instead of throwing; <see cref="OperationCanceledException"/> for caller
/// cancellation propagates.
/// </summary>
public interface IBuildDiagnosticsProducer
{
    string ProviderId { get; }
    bool IsEnabled { get; }
    Task<BuildDiagnosticsEvidence> ProduceAsync(
        BuildDiagnosticsProductionRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Capability registry for build-diagnostics producers. Routes by exact
/// provider id; unknown or disabled providers yield an explicit
/// <see cref="BuildDiagnosticsStatus.Disabled"/> evidence value — never a
/// pass, never an exception, never an implicit gate.
/// </summary>
public sealed class BuildDiagnosticsProducerRegistry
{
    private readonly IReadOnlyDictionary<string, IBuildDiagnosticsProducer> _producers;

    public BuildDiagnosticsProducerRegistry(IEnumerable<IBuildDiagnosticsProducer> producers)
    {
        ArgumentNullException.ThrowIfNull(producers);
        _producers = producers.ToDictionary(
            static p => p.ProviderId,
            static p => p,
            StringComparer.Ordinal);
    }

    public bool IsProviderEnabled(string providerId) =>
        _producers.TryGetValue(providerId, out var producer) && producer.IsEnabled;

    public Task<BuildDiagnosticsEvidence> ProduceAsync(
        string providerId,
        BuildDiagnosticsProductionRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(request);
        if (!_producers.TryGetValue(providerId, out var producer))
            return Task.FromResult(BuildDiagnosticsEvidence.Disabled(providerId, request.ExpectedBinding));
        if (!producer.IsEnabled)
            return Task.FromResult(BuildDiagnosticsEvidence.Disabled(providerId, request.ExpectedBinding));
        return producer.ProduceAsync(request, ct);
    }
}

/// <summary>
/// Pure renderer for build-diagnostics evidence into human-readable finding
/// text. Used by the build gate so the repair loop receives root-cause
/// failures plus project/target context, or an explicit
/// insufficient-diagnostics note. Output is bounded by
/// <paramref name="maxChars"/> and never empty for non-disabled evidence.
/// </summary>
public static class BuildDiagnosticsFormatter
{
    public static string Describe(BuildDiagnosticsEvidence evidence, int maxChars = 2000)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (maxChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChars));

        var writer = new System.Text.StringBuilder();
        switch (evidence.Status)
        {
            case BuildDiagnosticsStatus.Disabled:
            case BuildDiagnosticsStatus.NotApplicable:
                return string.Empty;
            case BuildDiagnosticsStatus.InsufficientDiagnostics:
                writer.Append("structured diagnostics unavailable");
                writer.Append($" ({evidence.ProviderId}): ");
                writer.Append(string.IsNullOrWhiteSpace(evidence.Reason) ? "no reason recorded" : evidence.Reason.Trim());
                break;
            case BuildDiagnosticsStatus.Enriched:
                writer.Append($"structured diagnostics ({evidence.ProviderId}): ");
                writer.Append($"{evidence.TotalErrorCount} error(s), {evidence.TotalWarningCount} warning(s)");
                if (evidence.Truncated) writer.Append(" [truncated to producer bounds]");
                writer.Append('.');
                foreach (var diagnostic in evidence.Diagnostics.Take(8))
                {
                    writer.AppendLine();
                    writer.Append(" - ");
                    writer.Append(RenderDiagnostic(diagnostic));
                }
                break;
            default:
                writer.Append($"structured diagnostics ({evidence.ProviderId}): unknown status.");
                break;
        }

        var text = writer.ToString();
        if (text.Length <= maxChars) return text;
        return text[..Math.Max(0, maxChars - 15)] + " [...truncated]";
    }

    private static string RenderDiagnostic(BuildDiagnostic diagnostic)
    {
        var writer = new System.Text.StringBuilder();
        writer.Append(diagnostic.Severity switch
        {
            BuildDiagnosticSeverity.Error => "error",
            BuildDiagnosticSeverity.Warning => "warning",
            _ => "info",
        });
        if (!string.IsNullOrWhiteSpace(diagnostic.Code))
        {
            writer.Append(' ');
            writer.Append(diagnostic.Code.Trim());
        }
        if (!string.IsNullOrWhiteSpace(diagnostic.Project))
        {
            writer.Append(" in ");
            writer.Append(Shorten(diagnostic.Project.Trim(), 120));
        }
        if (!string.IsNullOrWhiteSpace(diagnostic.Target))
        {
            writer.Append(" target ");
            writer.Append(Shorten(diagnostic.Target.Trim(), 80));
        }
        var location = RenderLocation(diagnostic.Location);
        if (location.Length > 0)
        {
            writer.Append(" (");
            writer.Append(location);
            writer.Append(')');
        }
        writer.Append(": ");
        writer.Append(Shorten(SingleLine(diagnostic.Message), 400));
        if (diagnostic.IsRootCause) writer.Append(" [root cause]");
        return writer.ToString();
    }

    private static string RenderLocation(BuildDiagnosticLocation? location)
    {
        if (location is null || string.IsNullOrWhiteSpace(location.Path)) return string.Empty;
        var path = Shorten(location.Path.Trim(), 160);
        if (location.Line is int line && line > 0)
            return location.Column is int column && column > 0
                ? $"{path}:{line}:{column}"
                : $"{path}:{line}";
        return path;
    }

    private static string SingleLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Shorten(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..Math.Max(0, maxChars - 3)] + "...";
}
