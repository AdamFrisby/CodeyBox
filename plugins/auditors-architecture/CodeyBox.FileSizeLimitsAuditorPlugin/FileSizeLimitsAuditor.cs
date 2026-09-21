using System.Globalization;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.FileSizeLimitsAuditorPlugin;

[CodeyBoxPlugin(
    id: "codeybox.file-size-limits",
    displayName: "CodeyBox: File Size Limits",
    minHostApiVersion: "1.0")]
public sealed class FileSizeLimitsAuditor : IAuditor, IPluginInitializer
{
    public const string PluginId = "codeybox.file-size-limits";
    public const string AuditorName = "codeybox:file-size-limits";
    private const string RootConfigSection = "CodeyBox:Auditors:FileSizeLimits";
    private const string HeadRef = "HEAD";

    private readonly IConfiguration? _configuration;
    private IConfigurationSection? _pluginScopedConfig;

    public FileSizeLimitsAuditor()
    {
    }

    public FileSizeLimitsAuditor(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string Name => AuditorName;
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        _pluginScopedConfig = context.ScopedConfig;
        context.Logger.LogInformation(
            "FileSizeLimitsAuditor initialized: pluginId={PluginId}",
            context.PluginId);
        return Task.CompletedTask;
    }

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        var options = ResolveOptions();
        var includeGlobs = options.IncludeGlobs.Select(GlobPattern.Create).ToArray();
        var excludeGlobs = options.ExcludeGlobs.Select(GlobPattern.Create).ToArray();
        if (includeGlobs.Length == 0)
            return new AuditResult(true, []);

        var filesResult = await GitAsync(
            sandbox,
            workingDirectory,
            ["ls-tree", "-r", "-z", "--name-only", HeadRef],
            ct);
        if (!filesResult.Success)
        {
            return new AuditResult(false,
            [
                new AuditFinding(
                    Name,
                    AuditSeverity.Error,
                    "failed to list repository files",
                    filesResult.Stderr),
            ],
            RawOutput: filesResult.Stderr);
        }

        var files = SplitNul(filesResult.Stdout)
            .Where(path => IsIncluded(path, includeGlobs, excludeGlobs))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var findings = new List<AuditFinding>();
        string? baseRef = null;
        var baseRefResolved = false;

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();

            var current = await TryReadMetricsAsync(sandbox, workingDirectory, HeadRef, path, ct);
            if (current is null)
                continue;

            var dimensions = EvaluateDimensions(current, options);
            if (dimensions.Count == 0)
                continue;

            var needsBase = options.GrandfatherMode == FileSizeLimitsGrandfatherMode.BlockGrowth
                && dimensions.Any(d => d.Tier == ThresholdTier.Block);
            FileMetrics? baseMetrics = null;
            if (needsBase)
            {
                if (!baseRefResolved)
                {
                    baseRef = await ResolveBaseRefAsync(sandbox, workingDirectory, context.BaseBranch, ct);
                    baseRefResolved = true;
                }

                if (baseRef is not null)
                    baseMetrics = await TryReadMetricsAsync(sandbox, workingDirectory, baseRef, path, ct);
            }

            var finding = BuildFinding(path, current, dimensions, baseMetrics, baseRef, options);
            if (finding is not null)
                findings.Add(finding);
        }

        return new AuditResult(
            Passed: findings.All(f => f.Severity < AuditSeverity.Error),
            Findings: findings);
    }

    private FileSizeLimitsAuditorOptions ResolveOptions()
    {
        if (_configuration is not null)
        {
            var rootSection = _configuration.GetSection(RootConfigSection);
            if (HasValues(rootSection))
                return FileSizeLimitsAuditorOptions.FromConfiguration(rootSection);
        }

        if (_pluginScopedConfig is not null && HasValues(_pluginScopedConfig))
            return FileSizeLimitsAuditorOptions.FromConfiguration(_pluginScopedConfig);

        return new FileSizeLimitsAuditorOptions();
    }

    private static bool HasValues(IConfigurationSection section)
        => section.Value is not null || section.GetChildren().Any();

    private static async Task<string?> ResolveBaseRefAsync(
        ISandbox sandbox,
        string workingDirectory,
        string baseBranch,
        CancellationToken ct)
    {
        foreach (var candidate in new[] { $"origin/{baseBranch}", baseBranch })
        {
            var result = await GitAsync(
                sandbox,
                workingDirectory,
                ["rev-parse", "--verify", $"{candidate}^{{commit}}"],
                ct);
            if (result.Success)
                return candidate;
        }

        return null;
    }

    private static async Task<FileMetrics?> TryReadMetricsAsync(
        ISandbox sandbox,
        string workingDirectory,
        string revision,
        string path,
        CancellationToken ct)
    {
        var spec = $"{revision}:{path}";
        var bytesResult = await GitAsync(
            sandbox,
            workingDirectory,
            ["cat-file", "-s", spec],
            ct);
        if (!bytesResult.Success)
            return null;

        if (!long.TryParse(bytesResult.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
            return null;

        var contentResult = await GitAsync(
            sandbox,
            workingDirectory,
            ["show", "--no-textconv", spec],
            ct);
        if (!contentResult.Success)
            return null;

        return new FileMetrics(bytes, CountLines(contentResult.Stdout));
    }

    private static IReadOnlyList<DimensionEvaluation> EvaluateDimensions(
        FileMetrics current,
        FileSizeLimitsAuditorOptions options)
    {
        var dimensions = new List<DimensionEvaluation>(capacity: 2);
        AddDimension(
            dimensions,
            DimensionKind.Lines,
            current.Lines,
            options.WarnFileLines,
            options.MaxFileLines,
            warnKey: "WarnFileLines",
            maxKey: "MaxFileLines");
        AddDimension(
            dimensions,
            DimensionKind.Bytes,
            current.Bytes,
            options.WarnFileBytes,
            options.MaxFileBytes,
            warnKey: "WarnFileBytes",
            maxKey: "MaxFileBytes");
        return dimensions;
    }

    private static void AddDimension(
        List<DimensionEvaluation> dimensions,
        DimensionKind kind,
        long actual,
        long warnThreshold,
        long blockThreshold,
        string warnKey,
        string maxKey)
    {
        if (blockThreshold > 0 && actual > blockThreshold)
        {
            dimensions.Add(new DimensionEvaluation(kind, ThresholdTier.Block, actual, blockThreshold, maxKey));
            return;
        }

        if (warnThreshold > 0 && actual > warnThreshold)
            dimensions.Add(new DimensionEvaluation(kind, ThresholdTier.Warn, actual, warnThreshold, warnKey));
    }

    private AuditFinding? BuildFinding(
        string path,
        FileMetrics current,
        IReadOnlyList<DimensionEvaluation> dimensions,
        FileMetrics? baseMetrics,
        string? baseRef,
        FileSizeLimitsAuditorOptions options)
    {
        var details = new List<string>(dimensions.Count);
        var hasBlockingDimension = false;

        foreach (var dimension in dimensions)
        {
            var dimensionSeverity = ResolveDimensionSeverity(dimension, current, baseMetrics, baseRef, options, out var reason);
            if (dimensionSeverity == AuditSeverity.Error)
                hasBlockingDimension = true;

            details.Add(
                $"{dimension.Label} {FormatNumber(dimension.Actual)} > {dimension.ConfigKey} {FormatNumber(dimension.Cap)} ({reason})");
        }

        if (details.Count == 0)
            return null;

        var severity = hasBlockingDimension ? AuditSeverity.Error : AuditSeverity.Warning;
        var title = severity == AuditSeverity.Error
            ? "source file exceeds blocking size limit"
            : "source file exceeds warning size limit";
        var description = "File exceeds CodeyBox:Auditors:FileSizeLimits: " + string.Join("; ", details) + ".";

        return new AuditFinding(
            AuditorName: Name,
            Severity: severity,
            Title: title,
            Description: description,
            Location: path);
    }

    private static AuditSeverity ResolveDimensionSeverity(
        DimensionEvaluation dimension,
        FileMetrics current,
        FileMetrics? baseMetrics,
        string? baseRef,
        FileSizeLimitsAuditorOptions options,
        out string reason)
    {
        if (dimension.Tier == ThresholdTier.Warn)
        {
            reason = "warning threshold exceeded";
            return AuditSeverity.Warning;
        }

        if (options.GrandfatherMode == FileSizeLimitsGrandfatherMode.Strict)
        {
            reason = "blocking threshold exceeded";
            return AuditSeverity.Error;
        }

        if (baseRef is null)
        {
            reason = "blocking threshold exceeded; base branch could not be resolved";
            return AuditSeverity.Error;
        }

        if (baseMetrics is null)
        {
            reason = "blocking threshold exceeded by a new file";
            return AuditSeverity.Error;
        }

        var baseValue = dimension.Kind == DimensionKind.Lines
            ? baseMetrics.Lines
            : baseMetrics.Bytes;
        var currentValue = dimension.Kind == DimensionKind.Lines
            ? current.Lines
            : current.Bytes;

        if (baseValue <= dimension.Cap)
        {
            reason = $"blocking threshold newly exceeded; base was {FormatNumber(baseValue)}";
            return AuditSeverity.Error;
        }

        if (currentValue > baseValue)
        {
            reason = $"blocking threshold exceeded and file grew from {FormatNumber(baseValue)}";
            return AuditSeverity.Error;
        }

        reason = $"grandfathered: already over cap on {baseRef} at {FormatNumber(baseValue)} and did not grow";
        return AuditSeverity.Warning;
    }

    private static bool IsIncluded(
        string path,
        IReadOnlyList<GlobPattern> includeGlobs,
        IReadOnlyList<GlobPattern> excludeGlobs)
    {
        var normalized = NormalizePath(path);
        return includeGlobs.Any(g => g.IsMatch(normalized))
            && !excludeGlobs.Any(g => g.IsMatch(normalized));
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static IReadOnlyList<string> SplitNul(string value)
        => value.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int CountLines(string content)
    {
        if (content.Length == 0)
            return 0;

        var lines = 0;
        foreach (var c in content)
        {
            if (c == '\n')
                lines++;
        }

        if (content[^1] != '\n')
            lines++;

        return lines;
    }

    private static string FormatNumber(long value)
        => value.ToString("N0", CultureInfo.InvariantCulture);

    private static Task<SandboxExecResult> GitAsync(
        ISandbox sandbox,
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct)
        => sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, .. args],
        }, ct);

    private sealed record FileMetrics(long Bytes, int Lines);

    private enum DimensionKind
    {
        Lines,
        Bytes,
    }

    private enum ThresholdTier
    {
        Warn,
        Block,
    }

    private sealed record DimensionEvaluation(
        DimensionKind Kind,
        ThresholdTier Tier,
        long Actual,
        long Cap,
        string ConfigKey)
    {
        public string Label => Kind == DimensionKind.Lines ? "lines" : "bytes";
    }

    private sealed class GlobPattern
    {
        private readonly IReadOnlyList<GlobToken> _tokens;

        private GlobPattern(IReadOnlyList<GlobToken> tokens)
        {
            _tokens = tokens;
        }

        public static GlobPattern Create(string pattern)
            => new(Parse(NormalizePath(pattern)));

        public bool IsMatch(string path)
        {
            var memo = new Dictionary<(int TokenIndex, int PathIndex), bool>();
            return Match(0, 0);

            bool Match(int tokenIndex, int pathIndex)
            {
                if (tokenIndex == _tokens.Count)
                    return pathIndex == path.Length;

                var key = (tokenIndex, pathIndex);
                if (memo.TryGetValue(key, out var cached))
                    return cached;

                var token = _tokens[tokenIndex];
                var matched = token.Kind switch
                {
                    GlobTokenKind.Literal => pathIndex < path.Length
                        && CharsEqual(token.Literal, path[pathIndex])
                        && Match(tokenIndex + 1, pathIndex + 1),
                    GlobTokenKind.SingleCharacter => pathIndex < path.Length
                        && path[pathIndex] != '/'
                        && Match(tokenIndex + 1, pathIndex + 1),
                    GlobTokenKind.SegmentWildcard => MatchSegmentWildcard(tokenIndex, pathIndex),
                    GlobTokenKind.PathWildcard => MatchPathWildcard(tokenIndex, pathIndex),
                    GlobTokenKind.DirectoryPrefixWildcard => MatchDirectoryPrefixWildcard(tokenIndex, pathIndex),
                    _ => false,
                };
                memo[key] = matched;
                return matched;
            }

            bool MatchSegmentWildcard(int tokenIndex, int pathIndex)
            {
                for (var i = pathIndex; ; i++)
                {
                    if (Match(tokenIndex + 1, i))
                        return true;

                    if (i >= path.Length || path[i] == '/')
                        return false;
                }
            }

            bool MatchPathWildcard(int tokenIndex, int pathIndex)
            {
                for (var i = pathIndex; i <= path.Length; i++)
                {
                    if (Match(tokenIndex + 1, i))
                        return true;
                }

                return false;
            }

            bool MatchDirectoryPrefixWildcard(int tokenIndex, int pathIndex)
            {
                if (Match(tokenIndex + 1, pathIndex))
                    return true;

                for (var i = pathIndex; i < path.Length; i++)
                {
                    if (path[i] == '/' && Match(tokenIndex + 1, i + 1))
                        return true;
                }

                return false;
            }
        }

        private static IReadOnlyList<GlobToken> Parse(string pattern)
        {
            var tokens = new List<GlobToken>(pattern.Length);
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == '*')
                {
                    var isDoubleStar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                    if (isDoubleStar)
                    {
                        i++;
                        if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                        {
                            i++;
                            tokens.Add(new GlobToken(GlobTokenKind.DirectoryPrefixWildcard));
                        }
                        else
                        {
                            tokens.Add(new GlobToken(GlobTokenKind.PathWildcard));
                        }
                    }
                    else
                    {
                        tokens.Add(new GlobToken(GlobTokenKind.SegmentWildcard));
                    }
                    continue;
                }

                tokens.Add(c == '?'
                    ? new GlobToken(GlobTokenKind.SingleCharacter)
                    : new GlobToken(GlobTokenKind.Literal, c));
            }

            return tokens;
        }

        private static bool CharsEqual(char left, char right)
            => char.ToUpperInvariant(left) == char.ToUpperInvariant(right);

        private readonly record struct GlobToken(GlobTokenKind Kind, char Literal = '\0');

        private enum GlobTokenKind
        {
            Literal,
            SingleCharacter,
            SegmentWildcard,
            PathWildcard,
            DirectoryPrefixWildcard,
        }
    }
}

public sealed record FileSizeLimitsAuditorOptions
{
    public const int DefaultWarnFileLines = 800;
    public const int DefaultMaxFileLines = 1500;
    public const long DefaultWarnFileBytes = 102400;
    public const long DefaultMaxFileBytes = 153600;

    public static readonly IReadOnlyList<string> DefaultIncludeGlobs = ["**/*.cs"];
    public static readonly IReadOnlyList<string> DefaultExcludeGlobs =
    [
        "**/bin/**",
        "**/obj/**",
        "**/*.generated.cs",
        "**/*.Designer.cs",
        "**/Migrations/**",
    ];

    public long WarnFileBytes { get; init; } = DefaultWarnFileBytes;
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;
    public int WarnFileLines { get; init; } = DefaultWarnFileLines;
    public int MaxFileLines { get; init; } = DefaultMaxFileLines;
    public IReadOnlyList<string> IncludeGlobs { get; init; } = DefaultIncludeGlobs;
    public IReadOnlyList<string> ExcludeGlobs { get; init; } = DefaultExcludeGlobs;
    public FileSizeLimitsGrandfatherMode GrandfatherMode { get; init; } = FileSizeLimitsGrandfatherMode.BlockGrowth;

    public static FileSizeLimitsAuditorOptions FromConfiguration(IConfigurationSection section)
    {
        var defaults = new FileSizeLimitsAuditorOptions();
        return defaults with
        {
            WarnFileBytes = ReadLong(section, defaults.WarnFileBytes, "WarnFileBytes", "WarnBytes"),
            MaxFileBytes = ReadLong(section, defaults.MaxFileBytes, "MaxFileBytes", "BlockFileBytes", "BlockBytes", "MaxBytes"),
            WarnFileLines = (int)ReadLong(section, defaults.WarnFileLines, "WarnFileLines", "WarnLines"),
            MaxFileLines = (int)ReadLong(section, defaults.MaxFileLines, "MaxFileLines", "BlockFileLines", "BlockLines", "MaxLines"),
            IncludeGlobs = ReadList(section, "IncludeGlobs", defaults.IncludeGlobs),
            ExcludeGlobs = ReadList(section, "ExcludeGlobs", defaults.ExcludeGlobs),
            GrandfatherMode = ReadGrandfatherMode(section, defaults.GrandfatherMode),
        };
    }

    private static long ReadLong(IConfigurationSection section, long fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = section[key];
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return fallback;
    }

    private static IReadOnlyList<string> ReadList(
        IConfigurationSection section,
        string key,
        IReadOnlyList<string> fallback)
    {
        var children = section.GetSection(key).GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToArray();
        if (children.Length > 0)
            return children;

        var scalar = section[key];
        if (!string.IsNullOrWhiteSpace(scalar))
        {
            return scalar.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray();
        }

        return fallback;
    }

    private static FileSizeLimitsGrandfatherMode ReadGrandfatherMode(
        IConfigurationSection section,
        FileSizeLimitsGrandfatherMode fallback)
    {
        var value = section["GrandfatherMode"] ?? section["Mode"];
        return value?.Trim().ToLowerInvariant() switch
        {
            "strict" => FileSizeLimitsGrandfatherMode.Strict,
            "block-growth" or "blockgrowth" or "growth" => FileSizeLimitsGrandfatherMode.BlockGrowth,
            _ => fallback,
        };
    }
}

public enum FileSizeLimitsGrandfatherMode
{
    BlockGrowth,
    Strict,
}
