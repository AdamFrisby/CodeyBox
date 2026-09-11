using CodeyBox.Core;

namespace CodeyBox.AdminSeed;

/// <summary>
/// Scripted outcome the <see cref="SeededFakeAgentRunner"/> produces for one
/// invocation. The success path stages a deterministic file change; every
/// other path exercises a distinct pipeline branch (quota-park, auth,
/// normal failure, empty diff) without any LLM, VM, or network call.
/// </summary>
public enum SeededFakeBehavior
{
    Success = 0,
    QuotaPark = 1,
    AuthFailure = 2,
    NormalFailure = 3,
    EmptyDiff = 4,
}

/// <summary>
/// Pure behavior selection for the seeded fake runner. Marker-driven when
/// the prompt names a branch explicitly, hash-driven otherwise, so seeded
/// runs are reproducible for a fixed (seed, prompt) pair.
/// </summary>
public static class SeededFakeBehaviorSelector
{
    public const string QuotaMarker = "[seeded-fake:quota]";
    public const string AuthMarker = "[seeded-fake:auth]";
    public const string FailMarker = "[seeded-fake:fail]";
    public const string EmptyMarker = "[seeded-fake:empty]";

    public static SeededFakeBehavior Select(string prompt, SeededFakeAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);

        if (prompt.Contains(QuotaMarker, StringComparison.OrdinalIgnoreCase))
            return SeededFakeBehavior.QuotaPark;
        if (prompt.Contains(AuthMarker, StringComparison.OrdinalIgnoreCase))
            return SeededFakeBehavior.AuthFailure;
        if (prompt.Contains(FailMarker, StringComparison.OrdinalIgnoreCase))
            return SeededFakeBehavior.NormalFailure;
        if (prompt.Contains(EmptyMarker, StringComparison.OrdinalIgnoreCase))
            return SeededFakeBehavior.EmptyDiff;

        var successBuckets = Math.Clamp(options.DefaultSuccessBuckets, 0, 100);
        var bucket = (int)(Fnv1a32($"{options.Seed}:{prompt}") % 100);
        return bucket < successBuckets ? SeededFakeBehavior.Success : SeededFakeBehavior.NormalFailure;
    }

    internal static uint Fnv1a32(string text)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= prime;
        }
        return hash;
    }
}
