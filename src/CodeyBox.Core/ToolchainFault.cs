using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Disposition for a classified subprocess (build / test / lint gate) result.
/// Distinct from <see cref="TestFailureAttribution"/>: a toolchain fault means
/// re-run the same commit because the tool itself failed, while a
/// non-diff test failure consults the base branch. The two classifications
/// must never share a disposition.
/// </summary>
public enum ToolchainFaultDisposition
{
    None = 0,
    Retry = 1,
    Fail = 2,
    Escalate = 3,
}

/// <summary>
/// Process result handed to <see cref="IToolchainFaultClassifier"/>.
/// Deliberately language-agnostic: no agent kind, language, or project type.
/// </summary>
public sealed record SubprocessResult(
    string Command,
    int ExitCode,
    string? Stdout = null,
    string? Stderr = null);

/// <summary>
/// Classification returned by <see cref="IToolchainFaultClassifier"/>.
/// <see cref="MatchedSignature"/> names the signature that matched (a
/// configured signature name or a <c>builtin:</c>-prefixed default) so every
/// classification is auditable.
/// </summary>
public sealed record ToolchainFaultClassification(
    ToolchainFaultDisposition Disposition,
    string? FaultClass,
    string? MatchedSignature)
{
    public static readonly ToolchainFaultClassification None =
        new(ToolchainFaultDisposition.None, FaultClass: null, MatchedSignature: null);
}

/// <summary>
/// Classification-only contract over subprocess results. Follows the shape of
/// <see cref="IQuotaFailureClassifier"/> but takes only the process result —
/// never an agent kind, language, or project type — so any gate that runs a
/// subprocess can use it. Implementations must never throw; they return
/// <see cref="ToolchainFaultClassification.None"/> when nothing matches.
/// </summary>
public interface IToolchainFaultClassifier
{
    ToolchainFaultClassification Classify(SubprocessResult result);
}

/// <summary>
/// A single toolchain-fault signature. Declared in configuration
/// (<c>CodeyBox:ToolchainFaults:&lt;name&gt;</c>), not in source, so a new
/// signature for a language the repository has never built requires no code
/// change. Each signature declares its match, the fault class it denotes,
/// and its disposition.
/// </summary>
public sealed class ToolchainFaultSignatureOptions
{
    /// <summary>Fault class this signature denotes (e.g. <c>oom-killed</c>). Required.</summary>
    public string? FaultClass { get; set; }

    /// <summary>What to do when this signature matches. Defaults to <see cref="ToolchainFaultDisposition.Retry"/>.</summary>
    public ToolchainFaultDisposition Disposition { get; set; } = ToolchainFaultDisposition.Retry;

    /// <summary>Match when the exit code is in this set. Null/empty means no exit-code-list constraint.</summary>
    public List<int>? ExitCodes { get; set; }

    /// <summary>Match when the exit code is strictly above this value (e.g. 128 for signal termination). Null means no constraint.</summary>
    public int? ExitCodeAbove { get; set; }

    /// <summary>Case-insensitive substring that must appear in stdout. Null/empty means no constraint.</summary>
    public string? StdoutContains { get; set; }

    /// <summary>Case-insensitive substring that must appear in stderr. Null/empty means no constraint.</summary>
    public string? StderrContains { get; set; }

    /// <summary>Case-insensitive substring that must appear in stdout or stderr. Null/empty means no constraint.</summary>
    public string? OutputContains { get; set; }

    /// <summary>Regex (case-insensitive, 100ms timeout) matched against stdout. Null/empty means no constraint.</summary>
    public string? StdoutRegex { get; set; }

    /// <summary>Regex (case-insensitive, 100ms timeout) matched against stderr. Null/empty means no constraint.</summary>
    public string? StderrRegex { get; set; }

    /// <summary>Regex (case-insensitive, 100ms timeout) matched against stdout or stderr. Null/empty means no constraint.</summary>
    public string? OutputRegex { get; set; }

    public ToolchainFaultSignatureOptions Clone() => new()
    {
        FaultClass = FaultClass,
        Disposition = Disposition,
        ExitCodes = ExitCodes is null ? null : new List<int>(ExitCodes),
        ExitCodeAbove = ExitCodeAbove,
        StdoutContains = StdoutContains,
        StderrContains = StderrContains,
        OutputContains = OutputContains,
        StdoutRegex = StdoutRegex,
        StderrRegex = StderrRegex,
        OutputRegex = OutputRegex,
    };

    internal void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(FaultClass))
            throw new InvalidOperationException(
                $"CodeyBox:ToolchainFaults:{name}: FaultClass is required.");
        if (!Enum.IsDefined(typeof(ToolchainFaultDisposition), Disposition) || Disposition == ToolchainFaultDisposition.None)
            throw new InvalidOperationException(
                $"CodeyBox:ToolchainFaults:{name}: Disposition must be Retry, Fail, or Escalate.");
        if ((ExitCodes is null || ExitCodes.Count == 0)
            && ExitCodeAbove is null
            && string.IsNullOrEmpty(StdoutContains)
            && string.IsNullOrEmpty(StderrContains)
            && string.IsNullOrEmpty(OutputContains)
            && string.IsNullOrEmpty(StdoutRegex)
            && string.IsNullOrEmpty(StderrRegex)
            && string.IsNullOrEmpty(OutputRegex))
            throw new InvalidOperationException(
                $"CodeyBox:ToolchainFaults:{name}: at least one match criterion is required.");
        foreach (var pattern in new[] { StdoutRegex, StderrRegex, OutputRegex })
        {
            if (string.IsNullOrEmpty(pattern))
                continue;
            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"CodeyBox:ToolchainFaults:{name}: invalid regex '{pattern}'.", ex);
            }
        }
    }

    internal bool Matches(int exitCode, string stdout, string stderr)
    {
        var hasExitConstraint = (ExitCodes is { Count: > 0 }) || ExitCodeAbove is not null;
        if (hasExitConstraint)
        {
            var exitMatches = (ExitCodes?.Contains(exitCode) ?? false)
                || (ExitCodeAbove is { } above && exitCode > above);
            if (!exitMatches)
                return false;
        }

        if (!HasOutputConstraint)
            return true;

        return (!string.IsNullOrEmpty(StdoutContains) && Contains(stdout, StdoutContains))
            || (!string.IsNullOrEmpty(StderrContains) && Contains(stderr, StderrContains))
            || (!string.IsNullOrEmpty(OutputContains) && (Contains(stdout, OutputContains) || Contains(stderr, OutputContains)))
            || (!string.IsNullOrEmpty(StdoutRegex) && RegexMatches(stdout, StdoutRegex))
            || (!string.IsNullOrEmpty(StderrRegex) && RegexMatches(stderr, StderrRegex))
            || (!string.IsNullOrEmpty(OutputRegex) && (RegexMatches(stdout, OutputRegex) || RegexMatches(stderr, OutputRegex)));
    }

    private bool HasOutputConstraint =>
        !string.IsNullOrEmpty(StdoutContains)
        || !string.IsNullOrEmpty(StderrContains)
        || !string.IsNullOrEmpty(OutputContains)
        || !string.IsNullOrEmpty(StdoutRegex)
        || !string.IsNullOrEmpty(StderrRegex)
        || !string.IsNullOrEmpty(OutputRegex);

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool RegexMatches(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(
                input, pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

/// <summary>
/// Platform-agnostic built-in signatures. These hold regardless of language
/// and always apply, even with no configured signatures: termination by
/// signal (exit code above 128), out-of-memory kill (137), disk-exhaustion
/// errors, and the observed .NET runtime crash (Fatal error + Internal CLR
/// error 0x80131506) as one entry among these, not as a special case.
/// </summary>
public static class ToolchainFaultDefaults
{
    public static IReadOnlyList<KeyValuePair<string, ToolchainFaultSignatureOptions>> DefaultSignatures() =>
    [
        // Specific output-based signatures precede generic exit-code ones so
        // a runtime crash that also exits above 128 keeps its precise class.
        new("builtin:dotnet-runtime-crash", new ToolchainFaultSignatureOptions
        {
            FaultClass = "dotnet-runtime-crash",
            Disposition = ToolchainFaultDisposition.Retry,
            OutputRegex = "Internal CLR error.*0x80131506|0x80131506.*Internal CLR error",
        }),
        new("builtin:disk-exhaustion", new ToolchainFaultSignatureOptions
        {
            FaultClass = "disk-exhaustion",
            Disposition = ToolchainFaultDisposition.Retry,
            OutputRegex = "No space left on device|ENOSPC|disk quota exceeded|There is not enough space on the disk",
        }),
        new("builtin:oom-killed", new ToolchainFaultSignatureOptions
        {
            FaultClass = "oom-killed",
            Disposition = ToolchainFaultDisposition.Retry,
            ExitCodes = [137],
        }),
        new("builtin:signal-termination", new ToolchainFaultSignatureOptions
        {
            FaultClass = "signal-termination",
            Disposition = ToolchainFaultDisposition.Retry,
            ExitCodeAbove = 128,
        }),
    ];
}

/// <summary>
/// Shared, swappable holder for the current keyed toolchain-fault signatures.
/// Registered as a DI singleton so every gate reads through the same
/// reference. The hot-reload coordinator updates this holder, and subsequent
/// gate runs pick up the new signatures without a process restart. Follows
/// the established <see cref="AgentNetworkToleranceSnapshot"/> shape.
/// </summary>
public sealed class ToolchainFaultSnapshot
{
    private IReadOnlyDictionary<string, ToolchainFaultSignatureOptions> _current;

    public ToolchainFaultSnapshot(IReadOnlyDictionary<string, ToolchainFaultSignatureOptions?>? initial)
    {
        _current = CopySignatures(initial);
    }

    public IReadOnlyDictionary<string, ToolchainFaultSignatureOptions> Current => Volatile.Read(ref _current);

    public void Replace(IReadOnlyDictionary<string, ToolchainFaultSignatureOptions?>? next)
    {
        Volatile.Write(ref _current, CopySignatures(next));
    }

    private static Dictionary<string, ToolchainFaultSignatureOptions> CopySignatures(
        IReadOnlyDictionary<string, ToolchainFaultSignatureOptions?>? source)
    {
        var copy = new Dictionary<string, ToolchainFaultSignatureOptions>(StringComparer.OrdinalIgnoreCase);
        if (source is not null)
        {
            foreach (var kvp in source)
            {
                if (kvp.Value is null)
                    continue;
                kvp.Value.Validate(kvp.Key);
                copy[kvp.Key] = kvp.Value.Clone();
            }
        }
        return copy;
    }
}

/// <summary>
/// Default <see cref="IToolchainFaultClassifier"/>: evaluates configured
/// signatures first (so operators can override a fault class disposition),
/// then the platform-agnostic <see cref="ToolchainFaultDefaults"/>. Matching
/// is conservative — a signature with both exit-code and output criteria
/// requires both — so genuine compilation errors never match a toolchain
/// signature. Never throws; returns
/// <see cref="ToolchainFaultClassification.None"/> on any error.
/// </summary>
public sealed class ToolchainFaultClassifier : IToolchainFaultClassifier
{
    private const int MaxStreamChars = 64 * 1024;

    private readonly ToolchainFaultSnapshot? _snapshot;

    public ToolchainFaultClassifier(ToolchainFaultSnapshot? snapshot = null)
    {
        _snapshot = snapshot;
    }

    public ToolchainFaultClassification Classify(SubprocessResult result)
    {
        try
        {
            if (result is null || string.IsNullOrWhiteSpace(result.Command))
                return ToolchainFaultClassification.None;

            var stdout = Truncate(result.Stdout);
            var stderr = Truncate(result.Stderr);

            if (_snapshot is not null)
            {
                foreach (var kvp in _snapshot.Current)
                {
                    if (MatchesSafe(kvp.Value, result.ExitCode, stdout, stderr))
                        return new ToolchainFaultClassification(
                            kvp.Value.Disposition, kvp.Value.FaultClass, kvp.Key);
                }
            }

            foreach (var kvp in ToolchainFaultDefaults.DefaultSignatures())
            {
                if (MatchesSafe(kvp.Value, result.ExitCode, stdout, stderr))
                    return new ToolchainFaultClassification(
                        kvp.Value.Disposition, kvp.Value.FaultClass, kvp.Key);
            }

            return ToolchainFaultClassification.None;
        }
        catch (Exception)
        {
            return ToolchainFaultClassification.None;
        }
    }

    private static bool MatchesSafe(
        ToolchainFaultSignatureOptions signature, int exitCode, string stdout, string stderr)
    {
        try
        {
            return signature.Matches(exitCode, stdout, stderr);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Length <= MaxStreamChars ? value : value.Substring(0, MaxStreamChars);
    }
}

/// <summary>
/// Auditable record of one toolchain-fault classification: the matched
/// signature, the command, and the exit code.
/// </summary>
public sealed record ToolchainFaultRecord(
    DateTimeOffset At,
    string Command,
    int ExitCode,
    string? FaultClass,
    string? MatchedSignature,
    ToolchainFaultDisposition Disposition);

/// <summary>
/// Queryable store for toolchain-fault classification records. The frequency
/// of each fault class must be queryable — the present inability to count
/// occurrences is part of the defect — so this store exposes
/// <see cref="GetCountsByFaultClass"/> alongside the raw records.
/// </summary>
public interface IToolchainFaultRecordStore
{
    void Record(ToolchainFaultRecord record);

    IReadOnlyList<ToolchainFaultRecord> List(string? faultClass = null, int limit = 100);

    IReadOnlyDictionary<string, long> GetCountsByFaultClass();
}

/// <summary>
/// Bounded in-memory <see cref="IToolchainFaultRecordStore"/>. Keeps the most
/// recent records (oldest dropped past capacity) and per-fault-class counts.
/// Thread-safe.
/// </summary>
public sealed class InMemoryToolchainFaultRecordStore : IToolchainFaultRecordStore
{
    public const int DefaultCapacity = 10_000;

    private readonly int _capacity;
    private readonly Queue<ToolchainFaultRecord> _records = new();
    private readonly Dictionary<string, long> _countsByFaultClass = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public InMemoryToolchainFaultRecordStore(int capacity = DefaultCapacity)
    {
        _capacity = capacity < 1 ? DefaultCapacity : capacity;
    }

    public void Record(ToolchainFaultRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _records.Enqueue(record);
            while (_records.Count > _capacity)
                _records.Dequeue();
            var key = string.IsNullOrEmpty(record.FaultClass) ? "(unknown)" : record.FaultClass;
            _countsByFaultClass[key] = _countsByFaultClass.TryGetValue(key, out var count) ? count + 1 : 1;
        }
        CodeyBoxMeters.ToolchainFaults.Add(1,
            new KeyValuePair<string, object?>("fault_class", record.FaultClass ?? "(none)"),
            new KeyValuePair<string, object?>("signature", record.MatchedSignature ?? "(none)"),
            new KeyValuePair<string, object?>("disposition", record.Disposition.ToString()));
    }

    public IReadOnlyList<ToolchainFaultRecord> List(string? faultClass = null, int limit = 100)
    {
        var capped = limit < 1 ? 1 : Math.Min(limit, _capacity);
        lock (_gate)
        {
            IEnumerable<ToolchainFaultRecord> query = _records;
            if (!string.IsNullOrEmpty(faultClass))
                query = query.Where(r => string.Equals(r.FaultClass, faultClass, StringComparison.OrdinalIgnoreCase));
            return query.TakeLast(capped).ToArray();
        }
    }

    public IReadOnlyDictionary<string, long> GetCountsByFaultClass()
    {
        lock (_gate)
        {
            return new Dictionary<string, long>(_countsByFaultClass, StringComparer.OrdinalIgnoreCase);
        }
    }
}
