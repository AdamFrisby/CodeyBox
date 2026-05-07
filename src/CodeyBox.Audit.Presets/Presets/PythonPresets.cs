namespace CodeyBox.Audit.Presets.Presets;

internal static class PythonPresets
{
    public static void Register(PresetCatalog catalog)
        => catalog.RegisterLanguage("python", _ =>
        [
            LanguagePresetHelpers.Shell(
                "python",
                "pyproject.toml/setup.py/setup.cfg/requirements.txt",
                LanguagePresetHelpers.PythonMarkerScript,
                "python:format-check",
                "ruff", "format", "--check", "."),
            LanguagePresetHelpers.ShellScript(
                "python",
                "pyproject.toml/setup.py/setup.cfg/requirements.txt",
                LanguagePresetHelpers.PythonMarkerScript,
                "python:typecheck",
                "if command -v mypy >/dev/null 2>&1; then exec mypy .; fi; if command -v pyright >/dev/null 2>&1; then exec pyright --workdir .; fi; echo 'mypy or pyright: not found in sandbox' >&2; exit 127",
                "mypy or pyright",
                treatExit127AsMissingTool: true),
            LanguagePresetHelpers.Shell(
                "python",
                "pyproject.toml/setup.py/setup.cfg/requirements.txt",
                LanguagePresetHelpers.PythonMarkerScript,
                "python:test-pass",
                "pytest"),
        ]);
}
