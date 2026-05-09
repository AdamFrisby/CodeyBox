using System.Text.RegularExpressions;
using CodeyBox.Audit.Llm;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Audit.Presets;

/// <summary>
/// Built-in audit-type presets. Cross-language: applicable regardless of
/// what the project is written in.
///
/// Mix of capabilities:
///   - <c>security</c>: tool-only (gitleaks, semgrep) + comprehensive LLM
///     review aligned to OWASP ASVS 5.0 + Top 10 + LLM-specific checks.
///   - <c>architecture</c>, <c>quality</c>, <c>completeness</c>: LLM-only.
///   - <c>cheating</c>: deterministic diff-pattern matcher + LLM
///     "did you take shortcuts?" reviewer.
///   - <c>tests</c>: deterministic no-op assertion patterns + LLM
///     "are these tests meaningful?" reviewer.
/// </summary>
internal static class AuditTypePresets
{
    public static void Register(
        PresetCatalog catalog,
        IReadOnlyDictionary<string, AuditTypePresetDefinition> auditTypes,
        string frameTemplate)
    {
        foreach (var (id, definition) in auditTypes)
        {
            var captured = definition;
            catalog.RegisterAuditType(id, ctx => BuildAuditType(captured, frameTemplate, ctx));
        }
    }

    private static IReadOnlyList<IAuditor> BuildAuditType(
        AuditTypePresetDefinition definition,
        string frameTemplate,
        PresetContext ctx)
        => definition.Id.ToLowerInvariant() switch
        {
            "security" =>
            [
                Shell("security:gitleaks", "gitleaks", "detect", "--source", ".", "--no-banner", "--no-color"),
                Shell("security:semgrep", "semgrep", "--config", "auto", "--error", "--quiet"),
                Llm(definition, frameTemplate, ctx),
            ],
            "cheating" =>
            [
                new DiffPatternAuditor(new DiffPatternAuditorOptions
                {
                    Name = "cheating:suppression-patterns",
                    Patterns = CheatingPatterns,
                }),
                Llm(definition, frameTemplate, ctx),
            ],
            "tests" =>
            [
                new DiffPatternAuditor(new DiffPatternAuditorOptions
                {
                    Name = "tests:no-op-assertions",
                    Patterns = NoOpTestPatterns,
                }),
                Llm(definition, frameTemplate, ctx),
            ],
            _ => [Llm(definition, frameTemplate, ctx)],
        };

    private static IAuditor Llm(AuditTypePresetDefinition definition, string frameTemplate, PresetContext ctx)
        => new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = definition.LlmAuditorName ?? $"{definition.Id}:llm-review",
            Agent = ctx.Agent,
            ReviewFocus = definition.ReviewFocus,
            FrameTemplate = frameTemplate,
        });

    private static IAuditor Shell(string name, params string[] argv)
        => new ShellCommandAuditor(new ShellCommandAuditorOptions { Name = name, Argv = argv });

    // --- Cheating patterns ---------------------------------------------------

    private static readonly IReadOnlyList<DiffPattern> CheatingPatterns =
    [
        // TypeScript / JavaScript
        Pat(@"@ts-ignore|@ts-nocheck|@ts-expect-error", "TypeScript type-check suppression"),
        Pat(@"eslint-disable(?:-next-line|-line)?", "ESLint rule disabled inline"),
        Pat(@"tslint:disable", "TSLint rule disabled inline"),

        // Python
        Pat(@"#\s*type:\s*ignore", "Python type-check suppression (# type: ignore)"),
        Pat(@"#\s*noqa", "Python lint suppression (# noqa)"),
        Pat(@"@pytest\.mark\.skip|@unittest\.skip", "Skipped Python test"),

        // C#
        Pat(@"#pragma\s+warning\s+disable", "C# warning suppression pragma"),
        Pat(@"\[SuppressMessage\(", "C# message suppression attribute"),

        // Rust
        Pat(@"#\[allow\(", "Rust lint suppression"),

        // Go
        Pat(@"//\s*nolint", "Go golangci-lint suppression"),

        // Skipped tests (cross-language JS/TS test frameworks)
        Pat(@"\b(?:it|describe|test)\.skip\s*\(|\bxit\s*\(|\bxdescribe\s*\(", "Skipped test (jest/mocha/vitest)"),

        // Stubbed implementations
        Pat(@"throw\s+new\s+NotImplementedException", "C# / Java stubbed implementation"),
        Pat(@"raise\s+NotImplementedError", "Python stubbed implementation"),
        Pat(@"\bunimplemented!\s*\(\s*\)|\btodo!\s*\(\s*\)", "Rust stub macro (unimplemented!/todo!)"),
        Pat(@"panic\(""(?:not implemented|TODO|unimplemented)""\)", "Go stub panic"),

        // TODO/FIXME with implementation intent (warning, not error — completeness preset is the strict one)
        Pat(@"TODO:\s*implement|FIXME:\s*implement", "TODO marker for missing implementation", AuditSeverity.Warning),
    ];

    // --- No-op test patterns -------------------------------------------------

    /// <summary>
    /// Patterns that catch deterministically-bad test assertions: ones that
    /// can never fail, or compare a value with itself. These are an
    /// indicator of "writing tests to make the suite green" rather than
    /// "writing tests to catch bugs."
    /// </summary>
    private static readonly IReadOnlyList<DiffPattern> NoOpTestPatterns =
    [
        // Python
        Pat(@"^\s*assert\s+True\s*$", "assert True (no-op assertion)"),
        Pat(@"^\s*assert\s+1\s*==\s*1\s*$", "assert 1 == 1 (trivially-true)"),
        Pat(@"^\s*assert\s+not\s+False\s*$", "assert not False (no-op)"),
        Pat(@"^\s*pass\s*#\s*test\b", "pass-only test body", AuditSeverity.Warning),

        // .NET (xUnit / NUnit / MSTest)
        Pat(@"\bAssert\.True\s*\(\s*true\s*[,)]", "Assert.True(true) (no-op)"),
        Pat(@"\bAssert\.IsTrue\s*\(\s*true\s*[,)]", "Assert.IsTrue(true) (no-op)"),
        Pat(@"\bAssert\.False\s*\(\s*false\s*[,)]", "Assert.False(false) (no-op)"),
        Pat(@"\bAssert\.Equal\s*\(\s*(\w+)\s*,\s*\1\s*\)", "Assert.Equal(x, x) (no-op)"),
        Pat(@"\bAssert\.AreEqual\s*\(\s*(\w+)\s*,\s*\1\s*\)", "Assert.AreEqual(x, x) (no-op)"),
        Pat(@"\bAssert\.That\s*\(\s*true\b", "Assert.That(true, ...) (no-op)"),

        // JavaScript / TypeScript (jest / mocha / vitest)
        Pat(@"\bexpect\s*\(\s*true\s*\)\s*\.\s*toBe\s*\(\s*true\s*\)", "expect(true).toBe(true) (no-op)"),
        Pat(@"\bexpect\s*\(\s*1\s*\)\s*\.\s*toBe\s*\(\s*1\s*\)", "expect(1).toBe(1) (no-op)"),
        Pat(@"\bexpect\s*\(\s*(\w+)\s*\)\s*\.\s*toBe\s*\(\s*\1\s*\)", "expect(x).toBe(x) (no-op)"),
        Pat(@"\bexpect\s*\(\s*(\w+)\s*\)\s*\.\s*toEqual\s*\(\s*\1\s*\)", "expect(x).toEqual(x) (no-op)"),

        // Go (testify / std testing)
        Pat(@"\bassert\.True\s*\(\s*t\s*,\s*true\s*[,)]", "assert.True(t, true) (no-op)"),
        Pat(@"\bassert\.Equal\s*\(\s*t\s*,\s*(\w+)\s*,\s*\1\s*\)", "assert.Equal(t, x, x) (no-op)"),

        // Rust (std assertions)
        Pat(@"^\s*assert!\s*\(\s*true\s*\)", "assert!(true) (no-op)"),
        Pat(@"^\s*assert_eq!\s*\(\s*(\w+)\s*,\s*\1\s*\)", "assert_eq!(x, x) (no-op)"),
    ];

    private static DiffPattern Pat(string regex, string description, AuditSeverity severity = AuditSeverity.Error) => new()
    {
        Regex = new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        Description = description,
        Severity = severity,
    };
}
