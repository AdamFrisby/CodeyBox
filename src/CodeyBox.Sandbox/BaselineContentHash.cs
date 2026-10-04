using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Sandbox;

/// <summary>
/// One host-staged executable as seen by the provider-neutral baseline
/// toolchain hash. Only content identity (plus install coordinates) joins the
/// hash — never host paths or timestamps — so two providers staging the same
/// bytes at the same guest path compute the same hash.
/// </summary>
public sealed record BaselineToolchainExecutable(
    string VmDestPath,
    IReadOnlyList<string> VmSymlinks,
    string? Label,
    string ContentSha256);

/// <summary>
/// Provider-neutral baseline toolchain inputs: the three definition sources
/// every baseline bake installs and verifies — provisioning shell commands,
/// staged host executables, and post-provisioning verification commands
/// (which include the host-composed plugin tool presence probes).
/// Provider-specific infrastructure (pools, bridges, flavors, base image ids)
/// is intentionally absent: those select WHERE a baseline runs, not WHAT
/// toolchain it carries, so they must not perturb cross-provider equivalence.
/// </summary>
public sealed record BaselineToolchainInputs(
    IReadOnlyList<string> Runcmd,
    IReadOnlyList<BaselineToolchainExecutable> Executables,
    IReadOnlyList<BaselineVerificationCommand> Verifications);

/// <summary>
/// Single content-hash computation shared by every baseline-baking provider.
/// Incus and OpenStack both funnel their toolchain inputs through here, so the
/// same logical toolchain yields the same hash wherever it bakes. Providers
/// must call this — never reimplement the canonicalization.
/// </summary>
public static class BaselineContentHash
{
    /// <summary>Full hash length in lowercase hexadecimal characters.</summary>
    public const int HashHexChars = 64;

    /// <summary>Short hash length embedded in image names and pins.</summary>
    public const int ShortHashChars = 12;

    /// <summary>Canonical document version. Bump when the hashed fields change.</summary>
    public const int CanonicalVersion = 1;

    /// <summary>
    /// Computes the 64-character lowercase hexadecimal SHA-256 over the
    /// canonical JSON of <paramref name="inputs"/>. Pure and deterministic:
    /// equal inputs always yield equal hashes, on any provider.
    /// </summary>
    public static string ComputeToolchainHash(BaselineToolchainInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var runcmd = RequireList(inputs.Runcmd, nameof(inputs.Runcmd));
        var executables = RequireList(inputs.Executables, nameof(inputs.Executables));
        var verifications = RequireList(inputs.Verifications, nameof(inputs.Verifications));

        foreach (var command in runcmd)
        {
            if (command is null)
                throw new ArgumentException("Baseline runcmd cannot contain null commands.", nameof(inputs));
        }

        for (var i = 0; i < executables.Count; i++)
        {
            var executable = executables[i]
                ?? throw new ArgumentException(
                    $"Baseline executable provision {i} cannot be null.", nameof(inputs));
            if (string.IsNullOrWhiteSpace(executable.VmDestPath))
                throw new ArgumentException(
                    $"Baseline executable provision {i} must name a guest destination path.", nameof(inputs));
            RequireList(executable.VmSymlinks, $"{nameof(inputs.Executables)}[{i}].VmSymlinks");
            ValidateContentFingerprint(executable.ContentSha256, i);
        }

        for (var i = 0; i < verifications.Count; i++)
        {
            var verification = verifications[i]
                ?? throw new ArgumentException(
                    $"Baseline verification command {i} cannot be null.", nameof(inputs));
            if (string.IsNullOrWhiteSpace(verification.Label))
                throw new ArgumentException(
                    $"Baseline verification command {i} must carry a label.", nameof(inputs));
            RequireList(verification.Argv, $"{nameof(inputs.Verifications)}[{i}].Argv");
        }

        var canonical = new
        {
            version = CanonicalVersion,
            runcmd = runcmd.ToArray(),
            executables = executables.Select(static executable => new
            {
                executable.VmDestPath,
                VmSymlinks = executable.VmSymlinks.ToArray(),
                executable.Label,
                executable.ContentSha256,
            }).ToArray(),
            verifications = verifications.Select(static verification => new
            {
                verification.Label,
                Argv = verification.Argv.ToArray(),
                verification.FailureHint,
            }).ToArray(),
        };
        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>Shortens a full hash to the <see cref="ShortHashChars"/> characters used in names and pins.</summary>
    public static string ToShortHash(string fullHash)
    {
        if (fullHash is null || fullHash.Length != HashHexChars || !IsLowerHex(fullHash))
            throw new ArgumentException(
                "A baseline content hash must be 64 lowercase hexadecimal characters.", nameof(fullHash));
        return fullHash[..ShortHashChars];
    }

    /// <summary>
    /// Streams one host file through SHA-256 without buffering it, enforcing
    /// the per-file and aggregate byte caps as bytes are read (the caps apply
    /// BEFORE buffering, so an oversized file fails fast instead of exhausting
    /// memory). Returns the <c>sha256:</c>-prefixed fingerprint shape every
    /// provider's hash input expects.
    /// </summary>
    public static string HashHostFile(
        string path,
        long maxFileBytes,
        long maxAggregateBytes,
        ref long aggregateBytes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Host source path must not be blank.", nameof(path));
        if (maxFileBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes), "The per-file cap must be positive.");
        if (maxAggregateBytes < maxFileBytes)
            throw new ArgumentOutOfRangeException(nameof(maxAggregateBytes), "The aggregate cap must cover one file.");
        if (aggregateBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(aggregateBytes), "The running aggregate must not be negative.");

        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var buffer = new byte[64 * 1024];
        var fileBytes = 0L;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            fileBytes += read;
            if (fileBytes > maxFileBytes)
                throw new InvalidOperationException(
                    $"Host file '{path}' exceeds the {maxFileBytes}-byte per-file provisioning cap.");
            if (aggregateBytes + fileBytes > maxAggregateBytes)
                throw new InvalidOperationException(
                    "Host executable provisions exceed the aggregate provisioning byte cap.");
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        aggregateBytes += fileBytes;
        return "sha256:" + Convert.ToHexStringLower(sha.Hash ?? throw new InvalidOperationException("SHA-256 produced no digest."));
    }

    internal static void ValidateContentFingerprint(string? fingerprint, int index)
    {
        if (fingerprint is null
            || fingerprint.Length != "sha256:".Length + HashHexChars
            || !fingerprint.StartsWith("sha256:", StringComparison.Ordinal)
            || !IsLowerHex(fingerprint.AsSpan("sha256:".Length)))
        {
            throw new ArgumentException(
                $"Executable fingerprint {index} must be 'sha256:' followed by 64 lowercase hexadecimal characters.",
                nameof(fingerprint));
        }
    }

    internal static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return value.Length > 0;
    }

    /// <summary>True when <paramref name="value"/> is non-empty lowercase hexadecimal. Total: never throws.</summary>
    public static bool IsLowerHex(string value)
    {
        if (value.Length == 0)
            return false;
        foreach (var c in value)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static IReadOnlyList<T> RequireList<T>(IReadOnlyList<T>? list, string name)
    {
        ArgumentNullException.ThrowIfNull(list, name);
        return list;
    }

    /// <summary>
    /// Computes the provenance fingerprint over admitted executable artifacts:
    /// SHA-256 over the sorted admitted identities (digest, verified
    /// publisher/issuer, policy digest). Providers combine this with the
    /// toolchain hash so verified artifact identities join the baseline cache
    /// fingerprint. Empty input yields the empty fingerprint (no provenance).
    /// </summary>
    public static string ComputeProvenanceFingerprint(IEnumerable<ArtifactProvenance.ArtifactProvenanceEvidence> evidences)
    {
        ArgumentNullException.ThrowIfNull(evidences);
        var identities = evidences
            .Where(static e => e is not null && e.IsAdmitted)
            .Select(static e => e.ToFingerprintIdentity())
            .OrderBy(static identity => identity, StringComparer.Ordinal)
            .ToArray();
        if (identities.Length == 0)
            return string.Empty;
        var canonical = string.Join("\n", identities) + "\n";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Combines a toolchain hash with a provenance fingerprint for baseline
    /// cache keys. A null or empty fingerprint returns the toolchain hash
    /// unchanged, so disabled-policy behavior is byte-identical; a present
    /// fingerprint binds the verified identities into the cache identity.
    /// </summary>
    public static string CombineToolchainHash(string toolchainHash, string? provenanceFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolchainHash);
        if (string.IsNullOrEmpty(provenanceFingerprint))
            return toolchainHash;
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(toolchainHash + ":" + provenanceFingerprint)));
    }
}

/// <summary>
/// Provider-scoped baseline pin format shared by every baseline-baking
/// provider: <c>{provider-kind}/tc-{12-hex-toolchain-hash}/{provider-ref}</c>
/// (for example <c>incus/tc-a1b2c3d4e5f6/cb-incus-baseline-work-headless-a1b2c3d4e5f6</c>).
///
/// <para>The scope records which provider baked the pin; the hash is the
/// <see cref="BaselineContentHash"/> of the toolchain, so any provider can
/// test equivalence against its own live hash and serve the pin from its
/// equivalent local image; the ref names the exact baking-provider resource
/// for drift-tolerant reuse (an existing pin keeps working after a
/// non-toolchain config edit, exactly like pre-scoping pins did).
/// Anything without exactly this shape is a legacy (pre-scoping) pin:
/// providers keep honoring their own legacy refs through their pre-existing
/// paths, and must never throw while merely PARSING one — parsing is total,
/// interpretation is per-provider.</para>
/// </summary>
public static class BaselinePin
{
    private const string HashMarker = "tc-";

    /// <summary>Maximum persisted pin length (bounded persistence, unbounded providers).</summary>
    public const int MaximumPinLength = 256;

    /// <summary>Maximum provider-ref characters carried inside a scoped pin.</summary>
    public const int MaximumRefLength = 200;

    /// <summary>Formats a provider-scoped pin. <paramref name="toolchainHash"/> accepts full or short form.</summary>
    public static string FormatScopedPin(string providerKind, string toolchainHash, string providerRef)
    {
        var kind = NormalizeProviderKind(providerKind);
        var shortHash = NormalizeHash(toolchainHash);
        var reference = NormalizeRef(providerRef);
        return $"{kind}/{HashMarker}{shortHash}/{reference}";
    }

    /// <summary>
    /// Parses a scoped pin into its provider kind, 12-character toolchain
    /// hash, and provider ref. Returns false — never throws — for legacy or
    /// malformed pins.
    /// </summary>
    public static bool TryParseScopedPin(
        string? pin, out string providerKind, out string toolchainHash, out string providerRef)
    {
        providerKind = string.Empty;
        toolchainHash = string.Empty;
        providerRef = string.Empty;
        if (string.IsNullOrWhiteSpace(pin) || pin.Length > MaximumPinLength)
            return false;
        var firstSlash = pin.IndexOf('/');
        if (firstSlash <= 0)
            return false;
        var kind = pin[..firstSlash];
        var remainder = pin[(firstSlash + 1)..];
        if (!IsValidProviderKind(kind))
            return false;
        if (!remainder.StartsWith(HashMarker, StringComparison.Ordinal))
            return false;
        var afterMarker = remainder[HashMarker.Length..];
        var secondSlash = afterMarker.IndexOf('/');
        if (secondSlash != BaselineContentHash.ShortHashChars)
            return false;
        var hash = afterMarker[..secondSlash];
        var reference = afterMarker[(secondSlash + 1)..];
        if (!BaselineContentHash.IsLowerHex(hash) || !IsValidRef(reference))
            return false;

        providerKind = kind;
        toolchainHash = hash;
        providerRef = reference;
        return true;
    }

    /// <summary>
    /// Extracts the toolchain hash from a scoped pin, or null for legacy pins.
    /// Total: never throws.
    /// </summary>
    public static string? TryExtractToolchainHash(string? pin) =>
        TryParseScopedPin(pin, out _, out var hash, out _) ? hash : null;

    /// <summary>True when <paramref name="pin"/> has scoped-pin shape (any provider). Total: never throws.</summary>
    public static bool IsScopedPin(string? pin) =>
        TryParseScopedPin(pin, out _, out _, out _);

    /// <summary>
    /// True when <paramref name="pin"/> is scoped to <paramref name="providerKind"/>
    /// by exact (ordinal) match. Never a substring test.
    /// </summary>
    public static bool IsScopedTo(string? pin, string providerKind)
    {
        if (!TryParseScopedPin(pin, out var kind, out _, out _))
            return false;
        try
        {
            return string.Equals(kind, NormalizeProviderKind(providerKind), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeRef(string providerRef)
    {
        if (!IsValidRef(providerRef))
            throw new ArgumentException(
                "A scoped baseline pin ref must be 1-200 characters with no control characters.",
                nameof(providerRef));
        return providerRef;
    }

    internal static bool IsValidRef(string? reference)
    {
        if (reference is null || reference.Length is < 1 or > MaximumRefLength)
            return false;
        if (string.IsNullOrWhiteSpace(reference))
            return false;
        foreach (var c in reference)
        {
            if (char.IsControl(c))
                return false;
        }
        return true;
    }

    private static string NormalizeProviderKind(string providerKind)
    {
        if (!IsValidProviderKind(providerKind))
            throw new ArgumentException(
                "A baseline provider kind must be 1-32 lowercase ASCII letters, digits, or hyphens.",
                nameof(providerKind));
        return providerKind;
    }

    internal static bool IsValidProviderKind(string? kind)
    {
        if (kind is null || kind.Length is < 1 or > 32)
            return false;
        foreach (var c in kind)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                return false;
        }
        return true;
    }

    private static string NormalizeHash(string toolchainHash)
    {
        if (toolchainHash is null)
            throw new ArgumentException("A baseline toolchain hash must not be null.", nameof(toolchainHash));
        if (toolchainHash.Length == BaselineContentHash.HashHexChars
            && BaselineContentHash.IsLowerHex(toolchainHash))
        {
            return toolchainHash[..BaselineContentHash.ShortHashChars];
        }
        if (toolchainHash.Length == BaselineContentHash.ShortHashChars
            && BaselineContentHash.IsLowerHex(toolchainHash))
        {
            return toolchainHash;
        }

        throw new ArgumentException(
            "A baseline toolchain hash must be 12 or 64 lowercase hexadecimal characters.",
            nameof(toolchainHash));
    }
}
