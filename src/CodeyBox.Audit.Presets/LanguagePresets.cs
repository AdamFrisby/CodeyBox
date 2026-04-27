using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Audit.Presets;

/// <summary>
/// Built-in language presets. Each registers a bundle of
/// <see cref="ShellCommandAuditor"/>s wrapping the standard tooling for
/// that language. Operators with non-standard tool layouts (e.g. ruff in a
/// venv, cargo via rustup) can override entries by registering after this
/// runs or by writing custom auditors.
///
/// Capability: all language presets are <see cref="AuditCapabilities.None"/>
/// — they don't need agent credentials, only the standard binaries available
/// inside the sandbox image.
/// </summary>
internal static class LanguagePresets
{
    public static void Register(PresetCatalog catalog)
    {
        // Python — ruff handles lint + format checks; pyright for types;
        // bandit for security. Operators using flake8 / black / mypy can
        // override individual entries.
        catalog.RegisterLanguage("python", _ =>
        [
            Shell("python:ruff-check", "ruff", "check", "."),
            Shell("python:ruff-format-check", "ruff", "format", "--check", "."),
            Shell("python:pyright", "pyright"),
            Shell("python:bandit", "bandit", "-r", ".", "-q"),
        ]);

        // TypeScript / JavaScript — eslint + tsc + prettier in --check mode.
        // Use npx so the project's own pinned versions win over any global.
        catalog.RegisterLanguage("typescript", _ =>
        [
            Shell("ts:eslint", "npx", "--no-install", "eslint", "."),
            Shell("ts:tsc-noemit", "npx", "--no-install", "tsc", "--noEmit"),
            Shell("ts:prettier-check", "npx", "--no-install", "prettier", "--check", "."),
        ]);
        catalog.RegisterLanguage("javascript", _ =>
        [
            Shell("js:eslint", "npx", "--no-install", "eslint", "."),
            Shell("js:prettier-check", "npx", "--no-install", "prettier", "--check", "."),
        ]);

        // Go — golangci-lint covers most concerns; "go vet" for the basics
        // even when golangci-lint is not configured.
        catalog.RegisterLanguage("go", _ =>
        [
            Shell("go:golangci-lint", "golangci-lint", "run", "./..."),
            Shell("go:vet", "go", "vet", "./..."),
        ]);

        // Rust — clippy with -D warnings (clippy-as-error) and rustfmt
        // in --check mode for formatting.
        catalog.RegisterLanguage("rust", _ =>
        [
            Shell("rust:clippy", "cargo", "clippy", "--all-targets", "--", "-D", "warnings"),
            Shell("rust:fmt-check", "cargo", "fmt", "--", "--check"),
        ]);

        // C# — dotnet format catches style; building with WaE catches
        // analyzer + compiler issues.
        catalog.RegisterLanguage("csharp", _ =>
        [
            Shell("csharp:format-check", "dotnet", "format", "--verify-no-changes"),
            Shell("csharp:build-WaE", "dotnet", "build", "--no-incremental", "/warnaserror"),
        ]);

        // Ruby — rubocop for style/lint, brakeman for security.
        catalog.RegisterLanguage("ruby", _ =>
        [
            Shell("ruby:rubocop", "rubocop", "--no-color"),
            Shell("ruby:brakeman", "brakeman", "-q", "--no-pager"),
        ]);

        // Shell — shellcheck across tracked .sh files. Uses git ls-files
        // so files in .gitignore aren't surfaced.
        catalog.RegisterLanguage("shell", _ =>
        [
            new ShellCommandAuditor(new ShellCommandAuditorOptions
            {
                Name = "shell:shellcheck",
                Argv = ["sh", "-c", "git ls-files '*.sh' | xargs -r shellcheck"],
            }),
        ]);
    }

    private static IAuditor Shell(string name, params string[] argv)
        => new ShellCommandAuditor(new ShellCommandAuditorOptions { Name = name, Argv = argv });
}
