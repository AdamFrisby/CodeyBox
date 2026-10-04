using System.Security.Cryptography;
using System.Text;

namespace CodeyBox.Sandbox.ArtifactProvenance;

/// <summary>Privately staged plugin bundle bytes: the immutable inputs to verification.</summary>
public sealed record StagedPluginBundle
{
    /// <summary>Private staged copy of the primary assembly.</summary>
    public required string StagedPrimaryPath { get; init; }

    /// <summary>Hex digest of the staged primary bytes.</summary>
    public required string PrimaryDigestHex { get; init; }

    /// <summary>Bundle digest over the primary plus executable dependencies, or null for single files.</summary>
    public required string? BundleDigestHex { get; init; }

    /// <summary>Staged executable dependencies (file name + digest).</summary>
    public required IReadOnlyList<StagedDependency> Dependencies { get; init; }

    /// <summary>Directory holding the source artifact (sidecar lookup scope).</summary>
    public required string SourceDirectory { get; init; }

    /// <summary>Primary file name (sidecar basename).</summary>
    public required string PrimaryFileName { get; init; }

    /// <summary>Adapts the bundle to the verifier input shape.</summary>
    public StagedArtifact ToStagedArtifact() => new()
    {
        StagedPrimaryPath = StagedPrimaryPath,
        PrimaryDigestHex = PrimaryDigestHex,
        BundleDigestHex = BundleDigestHex,
        Dependencies = Dependencies,
        SourceDirectory = SourceDirectory,
        PrimaryFileName = PrimaryFileName,
    };
}

/// <summary>
/// Private staging: sources are copied once into a 0700 directory (0600
/// files) without following symlinks, hashed while copying with the byte cap
/// enforced during the copy, and re-hashable at consumption. Path traversal
/// in bundle entries is rejected; symlink sources are refused.
/// </summary>
public static class ArtifactStaging
{
    private const int CopyBufferBytes = 64 * 1024;

    /// <summary>Creates a private staging root reserved to this service instance.</summary>
    public static string CreatePrivateRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsLinux())
        {
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(root, mode);
        }
        return root;
    }

    /// <summary>Stages a plugin bundle: primary plus co-located <c>*.dll</c> dependencies.</summary>
    public static StagedPluginBundle StagePluginBundle(
        string assemblyPath,
        string stageRoot,
        long maxArtifactBytes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageRoot);
        if (maxArtifactBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxArtifactBytes));

        var fullSource = Path.GetFullPath(assemblyPath);
        if (IsLink(fullSource, isDirectory: false))
            throw new ArtifactBlockedException(
                "Plugin bundle source is a symlink; refusing to stage.",
                ProvenanceOutcome.InfrastructureFailure);
        var sourceDirectory = Path.GetDirectoryName(fullSource)
            ?? throw new ArtifactBlockedException("Plugin bundle path has no directory.", ProvenanceOutcome.InfrastructureFailure);
        if (IsLink(sourceDirectory, isDirectory: true))
            throw new ArtifactBlockedException(
                "Plugin bundle directory is a symlink; refusing to stage.",
                ProvenanceOutcome.InfrastructureFailure);
        var primaryName = Path.GetFileName(fullSource);
        if (string.IsNullOrEmpty(primaryName))
            throw new ArtifactBlockedException("Plugin bundle path has no file name.", ProvenanceOutcome.InfrastructureFailure);

        var bundleDir = Path.Combine(stageRoot, "bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundleDir);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(bundleDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var stagedPrimary = Path.Combine(bundleDir, primaryName);
        var primaryDigest = CopyHashNoFollow(fullSource, stagedPrimary, maxArtifactBytes, ct);

        var dependencies = new List<StagedDependency>();
        string[] siblings;
        try
        {
            siblings = Directory.GetFiles(sourceDirectory, "*.dll");
        }
        catch (IOException ex)
        {
            throw new ArtifactBlockedException($"Cannot enumerate plugin bundle directory: {ex.GetType().Name}.", ProvenanceOutcome.InfrastructureFailure);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ArtifactBlockedException($"Cannot enumerate plugin bundle directory: {ex.GetType().Name}.", ProvenanceOutcome.InfrastructureFailure);
        }
        if (siblings.Length > ArtifactAdmissionService.MaxBundleFiles + 1)
        {
            throw new ArtifactBlockedException(
                $"Plugin bundle exceeds {ArtifactAdmissionService.MaxBundleFiles} files.",
                ProvenanceOutcome.InfrastructureFailure);
        }
        foreach (var sibling in siblings.OrderBy(static s => s, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(sibling, fullSource, StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Path.GetFileName(sibling);
            if (!IsSafeBundleName(name))
            {
                throw new ArtifactBlockedException(
                    $"Plugin bundle entry '{name}' is not a plain file name; refusing to stage.",
                    ProvenanceOutcome.InfrastructureFailure);
            }
            if (IsLink(sibling, isDirectory: false))
            {
                throw new ArtifactBlockedException(
                    $"Plugin bundle dependency '{name}' is a symlink; refusing to stage.",
                    ProvenanceOutcome.InfrastructureFailure);
            }
            var staged = Path.Combine(bundleDir, name);
            var digest = CopyHashNoFollow(sibling, staged, maxArtifactBytes, ct);
            dependencies.Add(new StagedDependency(name, digest));
        }

        string? bundleDigest = null;
        if (dependencies.Count > 0)
            bundleDigest = ComputeBundleDigest(primaryName, primaryDigest, dependencies);

        return new StagedPluginBundle
        {
            StagedPrimaryPath = stagedPrimary,
            PrimaryDigestHex = primaryDigest,
            BundleDigestHex = bundleDigest,
            Dependencies = dependencies,
            SourceDirectory = sourceDirectory,
            PrimaryFileName = primaryName,
        };
    }

    /// <summary>Computes the bundle digest over the sorted manifest (primary + dependencies).</summary>
    public static string ComputeBundleDigest(
        string primaryName,
        string primaryDigestHex,
        IReadOnlyList<StagedDependency> dependencies)
    {
        var lines = new List<string>(dependencies.Count + 1) { $"{primaryName}:{primaryDigestHex}" };
        foreach (var dependency in dependencies.OrderBy(static d => d.FileName, StringComparer.Ordinal))
            lines.Add($"{dependency.FileName}:{dependency.DigestHex}");
        var manifest = string.Join("\n", lines) + "\n";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)));
    }

    /// <summary>Stages a single tool executable with no dependency scope.</summary>
    public static StagedArtifact StageSingleFile(
        string sourcePath,
        string stageRoot,
        long maxArtifactBytes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageRoot);
        if (maxArtifactBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxArtifactBytes));

        var fullSource = Path.GetFullPath(sourcePath);
        if (IsLink(fullSource, isDirectory: false))
            throw new ArtifactBlockedException(
                "Tool artifact source is a symlink; refusing to stage.",
                ProvenanceOutcome.InfrastructureFailure);
        var sourceDirectory = Path.GetDirectoryName(fullSource)
            ?? throw new ArtifactBlockedException("Tool artifact path has no directory.", ProvenanceOutcome.InfrastructureFailure);
        var fileName = Path.GetFileName(fullSource);
        if (string.IsNullOrEmpty(fileName))
            throw new ArtifactBlockedException("Tool artifact path has no file name.", ProvenanceOutcome.InfrastructureFailure);

        var singleDir = Path.Combine(stageRoot, "tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(singleDir);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(singleDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var staged = Path.Combine(singleDir, fileName);
        var digest = CopyHashNoFollow(fullSource, staged, maxArtifactBytes, ct);
        return new StagedArtifact
        {
            StagedPrimaryPath = staged,
            PrimaryDigestHex = digest,
            BundleDigestHex = null,
            Dependencies = [],
            SourceDirectory = sourceDirectory,
            PrimaryFileName = fileName,
        };
    }

    /// <summary>Hashes an already-staged file without following symlinks, enforcing the cap.</summary>
    public static string HashStagedFile(string path, long maxBytes, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (IsLink(path, isDirectory: false))
            throw new ArtifactBlockedException("Staged file is a symlink; refusing to verify.", ProvenanceOutcome.CryptographicallyInvalid);
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[CopyBufferBytes];
            var total = 0L;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                total += read;
                if (total > maxBytes)
                    throw new ArtifactBlockedException("Staged file exceeds the artifact byte cap.", ProvenanceOutcome.InfrastructureFailure);
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (ArtifactBlockedException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new ArtifactBlockedException($"Cannot read staged file: {ex.GetType().Name}.", ProvenanceOutcome.InfrastructureFailure);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ArtifactBlockedException($"Cannot read staged file: {ex.GetType().Name}.", ProvenanceOutcome.InfrastructureFailure);
        }
    }

    private static string CopyHashNoFollow(string source, string destination, long maxBytes, CancellationToken ct)
    {
        try
        {
            using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[CopyBufferBytes];
            var total = 0L;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                total += read;
                if (total > maxBytes)
                    throw new ArtifactBlockedException("Artifact exceeds the artifact byte cap.", ProvenanceOutcome.InfrastructureFailure);
                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }
            output.Flush(flushToDisk: true);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (ArtifactBlockedException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new ArtifactBlockedException($"Cannot stage artifact: {ex.GetType().Name}.", ProvenanceOutcome.InfrastructureFailure);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ArtifactBlockedException($"Cannot stage artifact: {ex.GetType().Name}.", ProvenanceOutcome.InfrastructureFailure);
        }
    }

    private static bool IsSafeBundleName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 256)
            return false;
        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Any(c => c == '/' || c == '\\' || char.IsControl(c)))
            return false;
        if (name is "." or ".." || name.Contains("..", StringComparison.Ordinal))
            return false;
        return true;
    }

    private static bool IsLink(string path, bool isDirectory)
    {
        try
        {
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            return info.LinkTarget is not null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
