using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// One agent-specific transient signature owned by a single agent's detector
/// (e.g. devin's <c>protocol error (unimplemented)</c> envelope). Evaluated
/// before the shared agent-neutral sets. The pattern must name a multi-token
/// provider diagnostic, never a bare word or number.
/// </summary>
public sealed record AgentTransientSignature(Regex Pattern, ProviderTransientKind Kind, string Signature);

/// <summary>
/// Single source of truth for provider-transient evaluation order shared by
/// every per-agent <see cref="IAgentQuotaFailureDetector"/>: agent-specific
/// signatures first, then the shared model-capacity set, then truncation,
/// then transport/upstream, then operator-configured extras from
/// <see cref="ProviderTransientSignatureStore"/>. Detection is by compiled
/// regular expression over the raw streams — never by loose substring — and
/// never throws.
/// </summary>
public static class ProviderTransientDetectorCore
{
    public static ProviderTransientDetection? Detect(
        string agentValue,
        string? stderr,
        string? stdout,
        string? summary,
        IReadOnlyList<AgentTransientSignature>? agentSpecific = null)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout) && string.IsNullOrEmpty(summary))
            return null;

        try
        {
            if (agentSpecific is not null)
            {
                foreach (var entry in agentSpecific)
                {
                    if (entry is null)
                        continue;
                    if (ProviderTransientMatcher.IsMatch(entry.Pattern, stderr)
                        || ProviderTransientMatcher.IsMatch(entry.Pattern, stdout)
                        || ProviderTransientMatcher.IsMatch(entry.Pattern, summary))
                        return new ProviderTransientDetection(entry.Kind, entry.Signature, DetailFor(entry.Kind));
                }
            }

            foreach (var text in (string?[])[stderr, stdout, summary])
            {
                var capacity = ProviderTransientModelSignatures.MatchFirst(text);
                if (capacity is not null)
                    return new ProviderTransientDetection(
                        ProviderTransientKind.ModelCapacity,
                        capacity,
                        DetailFor(ProviderTransientKind.ModelCapacity));

                var truncation = ProviderTransientTruncationSignatures.MatchFirst(text);
                if (truncation is not null)
                    return new ProviderTransientDetection(
                        ProviderTransientKind.OutputTruncation,
                        truncation,
                        DetailFor(ProviderTransientKind.OutputTruncation));

                var transport = ProviderTransientTransportSignatures.MatchFirst(text);
                if (transport is not null)
                    return new ProviderTransientDetection(
                        ProviderTransientKind.InfraTransport,
                        transport,
                        DetailFor(ProviderTransientKind.InfraTransport));
            }

            foreach (var extra in ProviderTransientSignatureStore.GetAgentSignatures(agentValue))
            {
                if (ProviderTransientMatcher.IsMatch(extra.Pattern, stderr)
                    || ProviderTransientMatcher.IsMatch(extra.Pattern, stdout)
                    || ProviderTransientMatcher.IsMatch(extra.Pattern, summary))
                    return new ProviderTransientDetection(extra.Kind, extra.Source, DetailFor(extra.Kind));
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string DetailFor(ProviderTransientKind kind) => kind switch
    {
        ProviderTransientKind.ModelCapacity =>
            "provider reported model capacity exhaustion; retry the same agent and model with backoff, never a different model",
        ProviderTransientKind.OutputTruncation =>
            "model hit its output-token limit; resume the same session with a bounded continue nudge",
        _ =>
            "transport/upstream blip after the CLI's own retries; retry the turn, resuming the session where supported",
    };
}
