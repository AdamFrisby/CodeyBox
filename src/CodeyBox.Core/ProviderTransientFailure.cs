using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Provider-side transient failure families that must park for bounded
/// transient retry on the SAME agent and SAME model instead of failing the
/// work item terminally (which would increment
/// <see cref="WorkItem.TerminalFailureCount"/> and, for quota-shaped paths,
/// risk a model swap).
/// </summary>
public enum ProviderTransientKind
{
    /// <summary>
    /// The serving model reports it has no capacity (or is overloaded):
    /// back off with jitter and retry the same agent and model. A different
    /// model id may bill a different tier, so the retry must never switch.
    /// </summary>
    ModelCapacity = 0,

    /// <summary>
    /// The model hit its output-token limit mid-turn: resume the same
    /// session with a bounded <c>continue</c> nudge, not a fresh turn.
    /// </summary>
    OutputTruncation = 1,

    /// <summary>
    /// Transport / upstream blip after the CLI's own retries (websocket
    /// close, connection reset, upstream 5xx / invalid JSON): retry the
    /// turn, resuming the session where the agent supports it.
    /// </summary>
    InfraTransport = 2,
}

/// <summary>
/// One matched provider-transient signature. <see cref="MatchedSignature"/>
/// is the detector-owned signature label (or the operator-configured pattern
/// that matched) — never raw agent output, which may carry secrets.
/// </summary>
public sealed record ProviderTransientDetection(
    ProviderTransientKind Kind,
    string MatchedSignature,
    string? Detail = null);

/// <summary>
/// One operator-configured transient signature: a .NET regular expression
/// (<see cref="Pattern"/>) and the family to report on match. Bound from
/// <c>CodeyBox:ProviderTransientSignatures:&lt;agent-kind&gt;</c>. Patterns
/// are matched with <see cref="RegexOptions.IgnoreCase"/> and
/// <see cref="RegexOptions.Singleline"/>; they must name a multi-token
/// provider diagnostic (never a bare word or number) so model prose quoting
/// a single term cannot match.
/// </summary>
public sealed record ProviderTransientSignature(string Pattern, ProviderTransientKind Kind);

/// <summary>
/// Classifies provider-side transient failures for one agent. Implemented by
/// the agent's existing <see cref="IAgentQuotaFailureDetector"/> so the
/// per-provider failure seam stays in one place.
/// </summary>
public interface IProviderTransientClassifier
{
    /// <summary>
    /// Returns the matched transient family for this agent, or null when the
    /// output carries no recognised transient signature. Must never throw.
    /// </summary>
    ProviderTransientDetection? DetectTransient(
        AgentKind agent,
        string? stderr,
        string? stdout,
        string? summary = null);
}

/// <summary>
/// Exact-match helper for provider-transient signatures. Matching is always
/// by compiled regular expression over the raw stream — never by loose
/// substring — and every evaluation is time-boxed so a hostile pattern or a
/// huge buffer degrades to "no match" instead of stalling dispatch. Never
/// throws.
/// </summary>
public static class ProviderTransientMatcher
{
    /// <summary>Upper bound for a single signature evaluation.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Longest stream still scanned; longer inputs are truncated first.</summary>
    public const int MaxScannedChars = 64 * 1024;

    public static bool IsMatch(Regex pattern, string? text)
    {
        if (pattern is null || string.IsNullOrEmpty(text))
            return false;
        try
        {
            var bounded = text.Length > MaxScannedChars ? text[^MaxScannedChars..] : text;
            return pattern.IsMatch(bounded);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static Regex? TryCompile(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;
        try
        {
            return new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                MatchTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Hot-reloadable per-agent operator transient signatures. Built-in exact
/// defaults ship inside each agent's detector; entries here are APPENDED at
/// detect time (operator shapes observed in production before a code release
/// can land). Populated from
/// <c>CodeyBox:ProviderTransientSignatures:&lt;agent-kind&gt;</c> at startup
/// and refreshed on config reload. Never throws; invalid entries are rejected
/// at <see cref="SetAgentSignatures"/> time, never during detection.
/// </summary>
public static class ProviderTransientSignatureStore
{
    /// <summary>Maximum operator signatures retained per agent.</summary>
    public const int MaxSignaturesPerAgent = 32;

    /// <summary>Longest accepted operator pattern (bounds compile + match cost).</summary>
    public const int MaxSignatureChars = 500;

    private static readonly ConcurrentDictionary<string, ProviderTransientCompiledSignature[]> _byAgent =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Atomically replaces the operator signature set for
    /// <paramref name="agent"/>. Entries beyond
    /// <see cref="MaxSignaturesPerAgent"/> are dropped; entries that are
    /// empty, over <see cref="MaxSignatureChars"/>, or do not compile are
    /// rejected. Throws <see cref="ArgumentException"/> when nothing usable
    /// remains only if <paramref name="signatures"/> itself was non-empty —
    /// callers use this to fail config validation loudly; detection itself
    /// never observes the failure.
    /// </summary>
    public static void SetAgentSignatures(
        string agent,
        IEnumerable<ProviderTransientSignature>? signatures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        var compiled = Compile(agent, signatures);
        if (compiled.Length == 0)
            _byAgent.TryRemove(agent, out _);
        else
            _byAgent[agent] = compiled;
    }

    /// <summary>Live operator signatures for <paramref name="agent"/> (empty when none).</summary>
    public static IReadOnlyList<ProviderTransientCompiledSignature> GetAgentSignatures(string agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
            return [];
        return _byAgent.TryGetValue(agent, out var entries) ? entries : [];
    }

    /// <summary>
    /// Replaces the whole operator-signature table from configuration (hot
    /// reload). Agents absent from <paramref name="byAgent"/> lose their
    /// operator entries; entries that are empty, overlong, or do not compile
    /// are rejected. Throws <see cref="ArgumentException"/> identifying the
    /// first offending agent so config validation fails loudly; detection
    /// itself never observes the failure. Never leaves a partial table: the
    /// live store is swapped only after every agent compiles cleanly.
    /// </summary>
    public static void SyncAgents(IReadOnlyDictionary<string, IEnumerable<ProviderTransientSignature>?>? byAgent)
    {
        if (byAgent is null || byAgent.Count == 0)
        {
            _byAgent.Clear();
            return;
        }

        var staged = new Dictionary<string, ProviderTransientCompiledSignature[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (agent, signatures) in byAgent)
        {
            if (string.IsNullOrWhiteSpace(agent))
                throw new ArgumentException(
                    "ProviderTransientSignatures: agent key must not be empty.",
                    nameof(byAgent));
            var compiled = Compile(agent, signatures);
            if (compiled.Length > 0)
                staged[agent] = compiled;
        }

        _byAgent.Clear();
        foreach (var (agent, compiled) in staged)
            _byAgent[agent] = compiled;
    }

    internal static ProviderTransientCompiledSignature[] Compile(
        string agent,
        IEnumerable<ProviderTransientSignature>? signatures)
    {
        if (signatures is null)
            return [];
        var compiled = new List<ProviderTransientCompiledSignature>();
        foreach (var entry in signatures)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Pattern))
                continue;
            if (entry.Pattern.Length > MaxSignatureChars)
                throw new ArgumentException(
                    $"ProviderTransientSignatures:{agent}: pattern exceeds {MaxSignatureChars} chars.",
                    nameof(signatures));
            if (compiled.Count >= MaxSignaturesPerAgent)
                break;
            var regex = ProviderTransientMatcher.TryCompile(entry.Pattern);
            if (regex is null)
                throw new ArgumentException(
                    $"ProviderTransientSignatures:{agent}: pattern does not compile as a .NET regular expression: '{entry.Pattern}'.",
                    nameof(signatures));
            compiled.Add(new ProviderTransientCompiledSignature(regex, entry.Kind, entry.Pattern));
        }

        return [.. compiled];
    }
}

/// <summary>One compiled operator signature (see <see cref="ProviderTransientSignature"/>).</summary>
public sealed record ProviderTransientCompiledSignature(
    Regex Pattern,
    ProviderTransientKind Kind,
    string Source);
