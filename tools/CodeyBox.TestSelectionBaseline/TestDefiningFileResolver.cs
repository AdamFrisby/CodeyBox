using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using CodeyBox.Core;

namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// Method → source-document index for one built test assembly, read from the
/// portable PDB in a single pass. Built once per test project, then queried
/// per listed test — a 16k-test suite must not rescan the PDB 16k times.
/// Sequence-point documents are normalised with
/// <see cref="TestSelectionCoverageMap.ToRepositoryRelative"/> at index build.
/// </summary>
public sealed class TestAssemblyIndex
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<MethodDocument>> EmptyMap =
        new Dictionary<string, IReadOnlyList<MethodDocument>>(StringComparer.Ordinal);

    public static readonly TestAssemblyIndex Empty = new(EmptyMap);

    private readonly IReadOnlyDictionary<string, IReadOnlyList<MethodDocument>> _byName;

    private TestAssemblyIndex(IReadOnlyDictionary<string, IReadOnlyList<MethodDocument>> byName)
        => _byName = byName;

    /// <summary>
    /// The repository-relative file defining <paramref name="testName"/>, or
    /// "" when the name cannot be resolved (unlisted method, missing document).
    /// </summary>
    public string Resolve(string testName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testName);
        var methodName = TestDefiningFileResolver.MethodName(testName);
        var typeSuffix = TestDefiningFileResolver.TypeName(testName);
        if (methodName.Length == 0 || typeSuffix.Length == 0)
            return "";
        if (!_byName.TryGetValue(methodName, out var candidates))
            return "";
        foreach (var candidate in candidates)
        {
            if (TestDefiningFileResolver.TypeMatches(candidate.TypeName, typeSuffix))
                return candidate.Document;
        }

        return "";
    }

    internal static TestAssemblyIndex Create(
        IReadOnlyDictionary<string, IReadOnlyList<MethodDocument>> byName)
        => new(byName);
}

/// <summary>One method's declaring type and first sequence-point document.</summary>
internal sealed record MethodDocument(string TypeName, string Document);

/// <summary>
/// Resolves a listed test's defining source file from the portable PDB next
/// to the test assembly. <see cref="LoadIndex"/> performs the (expensive)
/// one-time PDB scan; <see cref="TestAssemblyIndex.Resolve"/> is a pure
/// lookup over the result.
/// </summary>
public static class TestDefiningFileResolver
{
    public const int MaxPdbBytes = 64 * 1024 * 1024;
    public const int MaxPeBytes = 256 * 1024 * 1024;

    /// <summary>
    /// Builds the method→document index for <paramref name="assemblyPath"/>.
    /// Returns <see cref="TestAssemblyIndex.Empty"/> when the assembly is
    /// missing, has no metadata, or has no readable portable PDB — every test
    /// in it then resolves to "" (a defining-file miss, never an error).
    /// </summary>
    public static TestAssemblyIndex LoadIndex(string assemblyPath, string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        var fullAssembly = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullAssembly))
            return TestAssemblyIndex.Empty;
        var peInfo = new FileInfo(fullAssembly);
        if (peInfo.Length > MaxPeBytes)
        {
            throw new TestSelectionBaselineProduceException(
                $"Test assembly '{fullAssembly}' exceeds the {MaxPeBytes}-byte cap.");
        }

        try
        {
            return BuildIndex(fullAssembly, repoRoot);
        }
        catch (BadImageFormatException ex)
        {
            throw new TestSelectionBaselineProduceException(
                $"Test assembly '{fullAssembly}' is not a readable managed PE.", ex);
        }
    }

    private static TestAssemblyIndex BuildIndex(string fullAssembly, string repoRoot)
    {
        using var peStream = File.OpenRead(fullAssembly);
        using var peReader = new PEReader(peStream);
        if (!peReader.HasMetadata)
            return TestAssemblyIndex.Empty;

        var metadata = peReader.GetMetadataReader();
        MetadataReader? pdbReader = null;
        MetadataReaderProvider? pdbProvider = null;
        try
        {
            try
            {
                if (!TryGetPdbReader(peReader, fullAssembly, out pdbReader, out pdbProvider))
                    return TestAssemblyIndex.Empty;
            }
            catch (InvalidOperationException)
            {
                return TestAssemblyIndex.Empty;
            }

            var byName = new Dictionary<string, List<MethodDocument>>(StringComparer.Ordinal);
            var documents = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var handle in metadata.MethodDefinitions)
            {
                var debugHandle = handle.ToDebugInformationHandle();
                if (debugHandle.IsNil)
                    continue;
                var debug = pdbReader.GetMethodDebugInformation(debugHandle);
                string? documentPath = null;
                foreach (var point in debug.GetSequencePoints())
                {
                    if (point.IsHidden || point.Document.IsNil || point.StartLine <= 0)
                        continue;
                    var document = pdbReader.GetDocument(point.Document);
                    documentPath = pdbReader.GetString(document.Name);
                    break;
                }

                if (string.IsNullOrWhiteSpace(documentPath))
                    continue;
                if (!documents.TryGetValue(documentPath, out var relative))
                {
                    relative = TestSelectionCoverageMap.ToRepositoryRelative(documentPath, repoRoot);
                    documents[documentPath] = relative;
                }

                var method = metadata.GetMethodDefinition(handle);
                var declaring = metadata.GetTypeDefinition(method.GetDeclaringType());
                var fullType = GetFullTypeName(metadata, declaring);
                var name = metadata.GetString(method.Name);
                if (!byName.TryGetValue(name, out var list))
                    byName[name] = list = [];
                list.Add(new MethodDocument(fullType, relative));
            }

            var frozen = new Dictionary<string, IReadOnlyList<MethodDocument>>(StringComparer.Ordinal);
            foreach (var (name, list) in byName)
                frozen[name] = list;
            return TestAssemblyIndex.Create(frozen);
        }
        finally
        {
            pdbProvider?.Dispose();
        }
    }

    public static async Task<string?> ResolveTargetPathAsync(
        string repoRoot,
        string projectRelative,
        TestSelectionProducerOptions options,
        IHostCommandRunner runner,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRelative);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);

        var result = await HostCommandRun.CappedAsync(
            runner,
            [
                options.DotnetExecutable, "msbuild", projectRelative,
                "-nologo", "-v:q", "-getProperty:TargetPath",
            ],
            repoRoot,
            options.MaxCommandStdoutChars,
            options.MaxCommandStdoutChars,
            options.CommandTimeout,
            $"dotnet msbuild -getProperty:TargetPath {projectRelative}",
            ct).ConfigureAwait(false);
        if (!result.Success)
            return FindBuiltTestAssembly(repoRoot, projectRelative);

        foreach (var raw in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!raw.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && !raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var full = Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(repoRoot, raw));
            if (!HostPathPolicy.IsWithinDirectory(full, repoRoot))
            {
                throw new TestSelectionBaselineProduceException(
                    $"TargetPath '{raw}' for '{projectRelative}' escapes the repository.");
            }

            if (File.Exists(full))
                return full;
        }

        return FindBuiltTestAssembly(repoRoot, projectRelative);
    }

    private static string? FindBuiltTestAssembly(string repoRoot, string projectRelative)
    {
        var projectDir = Path.GetDirectoryName(Path.Combine(repoRoot, projectRelative));
        if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir))
            return null;
        var name = Path.GetFileNameWithoutExtension(projectRelative) + ".dll";
        foreach (var file in Directory.EnumerateFiles(projectDir, name, SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            if (!HostPathPolicy.IsWithinDirectory(full, repoRoot))
                continue;
            var relative = Path.GetRelativePath(projectDir, full);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return full;
            }
        }

        return null;
    }

    internal static string MethodName(string testName)
    {
        var withoutArgs = StripArguments(testName);
        var lastDot = withoutArgs.LastIndexOf('.');
        return lastDot < 0 ? withoutArgs : withoutArgs[(lastDot + 1)..];
    }

    internal static string TypeName(string testName)
    {
        var withoutArgs = StripArguments(testName);
        var lastDot = withoutArgs.LastIndexOf('.');
        return lastDot < 0 ? "" : withoutArgs[..lastDot];
    }

    private static string StripArguments(string testName)
    {
        var paren = testName.IndexOf('(');
        return paren < 0 ? testName : testName[..paren];
    }

    internal static bool TypeMatches(string metadataType, string listedType)
    {
        var left = metadataType.Replace('+', '.');
        var right = listedType.Replace('+', '.');
        return string.Equals(left, right, StringComparison.Ordinal)
            || left.EndsWith("." + right, StringComparison.Ordinal)
            || right.EndsWith("." + left, StringComparison.Ordinal);
    }

    private static string GetFullTypeName(MetadataReader metadata, TypeDefinition type)
    {
        var name = metadata.GetString(type.Name);
        var ns = metadata.GetString(type.Namespace);
        if (type.IsNested)
        {
            var declaring = metadata.GetTypeDefinition(type.GetDeclaringType());
            return GetFullTypeName(metadata, declaring) + "+" + name;
        }

        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static bool TryGetPdbReader(
        PEReader peReader,
        string assemblyPath,
        out MetadataReader pdbReader,
        out MetadataReaderProvider? provider)
    {
        foreach (var entry in peReader.ReadDebugDirectory())
        {
            if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
            {
                provider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                pdbReader = provider.GetMetadataReader();
                return true;
            }
        }

        var sidecar = Path.ChangeExtension(assemblyPath, ".pdb");
        if (File.Exists(sidecar))
        {
            var info = new FileInfo(sidecar);
            if (info.Length > MaxPdbBytes)
            {
                throw new TestSelectionBaselineProduceException(
                    $"PDB '{sidecar}' exceeds the {MaxPdbBytes}-byte cap.");
            }

            var stream = File.OpenRead(sidecar);
            provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            pdbReader = provider.GetMetadataReader();
            return true;
        }

        provider = null;
        pdbReader = null!;
        return false;
    }
}
