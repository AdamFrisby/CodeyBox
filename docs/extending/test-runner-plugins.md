# Test-runner plugins

The merge gate's test step is an `ITestRunnerAuditor` (`CodeyBox.Core`), not a
bespoke host service. The bundled `dotnet test` runner ships as a plugin
package — `plugins/CodeyBox.DotnetTestRunnerPlugin/` — so `pytest`, `go test`,
and `cargo test` can be peer `ITestRunnerAuditor` plugins without touching
`CodeyBox.Core`. `TestFramework` already declares all four members.

This is a packaging seam, not a behavioural one: the default host still
registers the dotnet runner explicitly (same byte-identical command, same
soundness tests), with or without any allowlist entry.

## Contents

- [`DotnetTestAuditor`](#dotnettestauditor) — the bundled implementation
- [`DotnetTestRunner`](#dotnettestrunner) — the attributed plugin entry
- [`PytestTestRunner`](#pytesttestrunner) — the reference stub
- [Writing a peer runner](#writing-a-peer-runner)
- [Bundling caveat](#bundling-caveat)

## `DotnetTestAuditor`

Lives in the plugin package (`CodeyBox.DotnetTestRunnerPlugin` namespace).
Owns the full `dotnet test` invocation — base command, test-selection
`--filter`, `--blame-hang` args — carries its own result classifier, and
delegates each run to `ShellCommandAuditor` so tool-presence handling stays
identical to the generic shell path. With an all-tests selection and default
options the command is byte-identical to `["dotnet", "test", "--no-build"]`.

The host default-registers one instance (`csharp:test-pass`, build-test-gate
role) in `Program.cs`, and the `csharp` language preset builds per-project
instances with the same hot-reloadable run options. The VSTest escaping guard
(`VstestFilterEscaping`) and the output parser (`DotnetTestOutputParser`) stay
in `CodeyBox.Audit.Shell` as the single shared copy both the plugin and the
shell attribution path reuse.

## `DotnetTestRunner`

The `[CodeyBoxPlugin]` entry (`id: "codeybox.dotnet-test-runner"`). A thin,
parameterless-constructible wrapper over the canonical `csharp:test-pass`
configuration — parameterless because the plugin loader registers entry types
without constructor arguments.

Run options are static per load: `BlameHangTimeout` / `AuditorIdleTimeout`
under `CodeyBox:Plugins:codeybox.dotnet-test-runner:` are read once in
`InitializeAsync`. Invalid values log a warning and keep
`TestRunOptions.Default`. The host's default registration (live
`Func<TestRunOptions>`) remains the hot-reloadable path.

## `PytestTestRunner`

A reference-only stub (`id: "codeybox.pytest-test-runner"`) proving a second
framework needs no Core change: a real `TestSuiteDescriptor`
(`pytest --collect-only -q`) and a real `BuildInvocation` shape (bare `pytest`
for the whole suite, `pytest -k <or-joined>` when narrowed). `RunAsync`
deliberately throws `NotSupportedException` instead of returning a fabricated
pass, so the stub can never green a gate it did not run. It is not referenced
by any catalog or DI registration. A production pytest runner would replace the
throw with a sandboxed `pytest` invocation plus a pytest output classifier.

## Writing a peer runner

1. Reference `CodeyBox.Core` + `CodeyBox.PluginSdk` only (never the
   orchestrator or API). The pytest stub is the template: it needs nothing
   else.
2. Implement `ITestRunnerAuditor`: a stable `Name`, the `TestSuiteDescriptor`
   (framework + enumeration argv the selector reasons about), `BuildInvocation`
   (apply the narrowed `TestSelection` through runner-native filter syntax —
   never raw passthrough of untrusted baseline names), and a `ResultClassifier`
   that distinguishes genuine test failures from an unrunnable environment.
3. Decorate with `[CodeyBoxPlugin]` and a new `TestFramework` member only if
   the framework is not already declared. `Pytest`, `GoTest`, and `CargoTest`
   are pre-declared.
4. Keep the runner out of the default panel until the host gains multi-runner
   selection: the single-`ITestRunnerAuditor` default registration assumes one
   canonical runner.

## Bundling caveat

The dotnet entry is bundled AND default-registered. Do not add
`codeybox.dotnet-test-runner` to the allowlist of a host that already
default-registers it — the discovered copy would join the audit panel as a
second test gate alongside the default one. The plugin id exists so downstream
hosts can load this package as an external plugin instead of referencing it.
