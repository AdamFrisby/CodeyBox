using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents;

/// <summary>
/// Shared scaffolding for agent runners that drive a one-shot CLI binary
/// inside the sandbox. Subclasses describe how to invoke their CLI; this base
/// handles credential staging and result wrapping uniformly.
/// </summary>
public abstract class CliAgentRunnerBase : IPreemptibleAgentRunner, IResumableAgentRunner
{
    public abstract AgentKind Kind { get; }

    /// <summary>
    /// Build the argv to execute inside the sandbox for a given prompt. The
    /// prompt may be passed via argv, stdin, or a file; subclasses choose.
    /// </summary>
    protected abstract AgentInvocation BuildInvocation(string prompt, AgentCredential? credential, string? modelId = null, string? reasoningMode = null);

    /// <summary>
    /// CLI state paths under HOME whose contents are useful for graceful
    /// preemption. The default preempt hook captures only these allowlisted
    /// relative paths, with size/type/path validation, into the checkpointed
    /// scratchpad archive.
    /// </summary>
    protected virtual IReadOnlyList<string> ScratchpadHomeDirectories => [];

    /// <summary>
    /// Pattern used only after scratchpad capture to ask the running CLI to stop.
    /// </summary>
    protected virtual string PreemptProcessPattern => Kind.Value;

    /// <summary>
    /// Build the invocation used after a checkpoint restore. The default restores
    /// CLI state and reruns the normal one-shot command; CLIs with a native resume
    /// mode can override this to add the relevant flag.
    /// </summary>
    protected virtual AgentInvocation BuildResumeInvocation(
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null)
        => BuildInvocation(prompt, credential, modelId, reasoningMode);

    /// <summary>
    /// Gives subclasses a chance to materialise non-argv CLI prerequisites
    /// immediately before invoking the binary. Returning a result short-circuits
    /// the run with that failure.
    /// </summary>
    protected virtual Task<AgentResult?> PrepareSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct)
        => Task.FromResult<AgentResult?>(null);

    public virtual async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        // The credential env is set on the container at boot via SandboxSpec.Environment
        // so secrets don't land on per-exec argv. We deliberately do NOT merge
        // credential.EnvironmentVariables into the per-exec ExtraEnvironment.
        var preparation = await PrepareSandboxAsync(sandbox, workingDirectory, credential, resume: null, ct);
        if (preparation is not null)
            return preparation;

        var invocation = BuildInvocation(prompt, credential, modelId, reasoningMode);
        var exec = new SandboxExec
        {
            Argv = invocation.Argv,
            WorkingDirectory = workingDirectory,
            ExtraEnvironment = invocation.ExtraEnvironment,
            Stdin = invocation.Stdin,
            StdoutChunkCallback = stdoutChunkCallback,
        };

        var result = await sandbox.ExecAsync(exec, ct);
        return new AgentResult(
            Success: result.Success,
            Summary: result.Success ? "ok" : $"agent exited {result.ExitCode}",
            Stdout: result.Stdout,
            Stderr: result.Stderr);
    }

    public virtual async Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        await RestoreScratchpadAsync(sandbox, workingDirectory, resume, ct);

        var preparation = await PrepareSandboxAsync(sandbox, workingDirectory, credential, resume, ct);
        if (preparation is not null)
            return preparation;

        var invocation = BuildResumeInvocation(prompt, credential, resume, modelId, reasoningMode);
        var exec = new SandboxExec
        {
            Argv = invocation.Argv,
            WorkingDirectory = workingDirectory,
            ExtraEnvironment = invocation.ExtraEnvironment,
            Stdin = invocation.Stdin,
            StdoutChunkCallback = stdoutChunkCallback,
        };

        var result = await sandbox.ExecAsync(exec, ct);
        return new AgentResult(
            Success: result.Success,
            Summary: result.Success ? "ok" : $"agent exited {result.ExitCode}",
            Stdout: result.Stdout,
            Stderr: result.Stderr);
    }

    public virtual async Task RequestPreemptAsync(ISandbox sandbox, string workingDirectory, CancellationToken ct = default)
    {
        var dirs = string.Join(" ", ScratchpadHomeDirectories.Select(ShellQuote));
        var pattern = PreemptProcessPattern;
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash", "-c",
                $$"""
                set -euo pipefail
                mkdir -p .codeybox
                scratch_tmp="$(mktemp -d .codeybox/preempt-scratchpad.XXXXXX)"
                manifest="$scratch_tmp/manifest.txt"
                manifest_tsv="$scratch_tmp/manifest.tsv"
                printf '%s\n' "Preempt requested at $(date -u +%FT%TZ)." > "$manifest"
                : > "$manifest_tsv"
                max_file_bytes=2097152
                max_total_bytes=26214400
                max_entries=2000
                max_depth=16
                captured=0
                total_bytes=0
                entries=0

                valid_rel() {
                  local rel="$1"
                  [ -n "$rel" ] || return 1
                  [[ "$rel" != /* ]] || return 1
                  [[ "$rel" != *$'\t'* && "$rel" != *$'\n'* ]] || return 1
                  IFS=/ read -r -a parts <<< "$rel"
                  [ "${#parts[@]}" -le "$max_depth" ] || return 1
                  for part in "${parts[@]}"; do
                    [ -n "$part" ] || return 1
                    [ "$part" != "." ] || return 1
                    [ "$part" != ".." ] || return 1
                    [ "$part" != ".git" ] || return 1
                  done
                }

                record_entry() {
                  local kind="$1" scope="$2" rel="$3"
                  printf '%s\t%s\t%s\n' "$kind" "$scope" "$rel" >> "$manifest_tsv"
                }

                record_parent_dirs() {
                  local scope="$1" rel="$2"
                  local dir
                  dir="$(dirname "$rel")"
                  while [ "$dir" != "." ] && [ "$dir" != "/" ]; do
                    valid_rel "$dir" || return 0
                    mkdir -p "$scratch_tmp/$scope/$dir"
                    record_entry dir "$scope" "$dir"
                    dir="$(dirname "$dir")"
                  done
                }

                capture_path() {
                  local scope="$1" base="$2" rel="$3"
                  rel="${rel#/}"
                  valid_rel "$rel" || {
                    printf '%s\n' "skipped $scope/$rel: invalid scratchpad path" >> "$manifest"
                    return 0
                  }

                  local src="$base/$rel"
                  [ -e "$src" ] || return 0
                  if [ -L "$src" ]; then
                    printf '%s\n' "skipped $scope/$rel: scratchpad root is a symlink" >> "$manifest"
                    return 0
                  fi
                  if [ ! -d "$src" ] && [ ! -f "$src" ]; then
                    printf '%s\n' "skipped $scope/$rel: scratchpad root is not a regular file or directory" >> "$manifest"
                    return 0
                  fi

                  printf '%s\n' "capturing $scope/$rel" >> "$manifest"
                  while IFS= read -r -d '' path; do
                    local sub=""
                    if [ "$path" != "$src" ]; then
                      sub="${path#"$src"/}"
                    fi
                    local dest_rel="$rel"
                    [ -z "$sub" ] || dest_rel="$rel/$sub"
                    valid_rel "$dest_rel" || {
                      printf '%s\n' "skipped $scope/$dest_rel: invalid nested scratchpad path" >> "$manifest"
                      continue
                    }
                    entries=$((entries + 1))
                    if [ "$entries" -gt "$max_entries" ]; then
                      printf '%s\n' "stopped capturing $scope/$rel: entry limit exceeded" >> "$manifest"
                      break
                    fi

                    local dest="$scratch_tmp/$scope/$dest_rel"
                    if [ -d "$path" ]; then
                      mkdir -p "$dest"
                      record_entry dir "$scope" "$dest_rel"
                      captured=1
                      continue
                    fi

                    if [ ! -f "$path" ]; then
                      printf '%s\n' "skipped $scope/$dest_rel: unsupported file type" >> "$manifest"
                      continue
                    fi

                    local size
                    size="$(wc -c < "$path")"
                    if [ "$size" -gt "$max_file_bytes" ]; then
                      printf '%s\n' "skipped $scope/$dest_rel: file exceeds per-file limit" >> "$manifest"
                      continue
                    fi
                    if [ $((total_bytes + size)) -gt "$max_total_bytes" ]; then
                      printf '%s\n' "skipped $scope/$dest_rel: archive byte limit reached" >> "$manifest"
                      continue
                    fi
                    mkdir -p "$(dirname "$dest")"
                    record_parent_dirs "$scope" "$dest_rel"
                    cp -p "$path" "$dest"
                    total_bytes=$((total_bytes + size))
                    record_entry file "$scope" "$dest_rel"
                    captured=1
                  done < <(find -P "$src" -xdev \( -type f -o -type d \) -print0)
                }

                for rel in {{dirs}} ""; do
                  [ -n "$rel" ] || continue
                  capture_path home "$HOME" "$rel"
                  if [ -e "$PWD/$rel" ] \
                     && { [ ! -e "$HOME/$rel" ] || [ "$(readlink -f "$PWD/$rel")" != "$(readlink -f "$HOME/$rel")" ]; }; then
                    capture_path work "$PWD" "$rel"
                  fi
                done
                if [ "$captured" -eq 0 ]; then
                  printf '%s\n' "No known CLI scratchpad directory existed at preempt time." >> "$manifest"
                fi
                tar -czf .codeybox/preempt-scratchpad.tgz -C "$scratch_tmp" .
                cp "$manifest" .codeybox/preempt-scratchpad.md
                rm -rf "$scratch_tmp"
                for pid in $(pgrep -f "$1" 2>/dev/null || true); do
                  [ "$pid" = "$$" ] && continue
                  kill -TERM "$pid" 2>/dev/null || true
                done
                """,
                "codeybox-preempt",
                pattern,
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        if (!result.Success)
            throw new InvalidOperationException($"agent preempt signal failed (exit {result.ExitCode}): {result.Stderr}");
    }

    private async Task RestoreScratchpadAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentResumeContext resume,
        CancellationToken ct)
    {
        var argv = new List<string>
        {
            "bash", "-c",
            """
            set -euo pipefail
            archive="$1"
            shift
            [ -f "$archive" ] || exit 0
            max_archive_bytes=33554432
            archive_bytes="$(wc -c < "$archive")"
            [ "$archive_bytes" -le "$max_archive_bytes" ] || {
              echo "scratchpad archive exceeds restore limit" >&2
              exit 10
            }

            scratch_tmp="$(mktemp -d .codeybox/resume-scratchpad.XXXXXX)"
            cleanup() { rm -rf "$scratch_tmp"; }
            trap cleanup EXIT

            manifest="$scratch_tmp/manifest.tsv"
            if ! tar -xOzf "$archive" ./manifest.tsv > "$manifest" 2>/dev/null \
               && ! tar -xOzf "$archive" manifest.tsv > "$manifest" 2>/dev/null; then
              # Legacy checkpoints did not carry restorable scratchpad state.
              exit 0
            fi

            max_entries=2000
            max_depth=16
            valid_rel() {
              local rel="$1"
              [ -n "$rel" ] || return 1
              [[ "$rel" != /* ]] || return 1
              [[ "$rel" != *$'\t'* && "$rel" != *$'\n'* ]] || return 1
              IFS=/ read -r -a parts <<< "$rel"
              [ "${#parts[@]}" -le "$max_depth" ] || return 1
              for part in "${parts[@]}"; do
                [ -n "$part" ] || return 1
                [ "$part" != "." ] || return 1
                [ "$part" != ".." ] || return 1
                [ "$part" != ".git" ] || return 1
              done
            }

            allowed_roots=()
            for root in "$@"; do
              root="${root#/}"
              valid_rel "$root" || continue
              allowed_roots+=("$root")
            done

            is_allowed_entry() {
              local kind="$1"
              local rel="$2"
              local root
              for root in "${allowed_roots[@]}"; do
                if [ "$rel" = "$root" ] || [[ "$rel" == "$root/"* ]]; then
                  return 0
                fi
                if [ "$kind" = "dir" ] && [[ "$root" == "$rel/"* ]]; then
                  return 0
                fi
              done
              return 1
            }

            allowed="$scratch_tmp/allowed.txt"
            {
              printf '%s\n' "." "./" "manifest.tsv" "./manifest.tsv" "manifest.txt" "./manifest.txt" "home" "./home" "home/" "./home/" "work" "./work" "work/" "./work/"
              while IFS=$'\t' read -r kind scope rel; do
                [ "$kind" = "file" ] || [ "$kind" = "dir" ] || exit 11
                [ "$scope" = "home" ] || [ "$scope" = "work" ] || exit 11
                valid_rel "$rel" || exit 11
                is_allowed_entry "$kind" "$rel" || exit 11
                printf '%s/%s\n' "$scope" "$rel"
                printf './%s/%s\n' "$scope" "$rel"
                if [ "$kind" = "dir" ]; then
                  printf '%s/%s/\n' "$scope" "$rel"
                  printf './%s/%s/\n' "$scope" "$rel"
                fi
              done < "$manifest"
            } > "$allowed"

            members="$scratch_tmp/members.txt"
            tar -tzf "$archive" > "$members"
            entry_count=0
            while IFS= read -r member; do
              [ -n "$member" ] || continue
              entry_count=$((entry_count + 1))
              [ "$entry_count" -le "$max_entries" ] || {
                echo "scratchpad archive entry limit exceeded" >&2
                exit 12
              }
              normalized="${member#./}"
              [[ "$normalized" != /* && "$normalized" != *"/../"* && "$normalized" != "../"* ]] || {
                echo "scratchpad archive contains unsafe path: $member" >&2
                exit 12
              }
              if ! grep -Fx -- "$member" "$allowed" >/dev/null \
                 && ! grep -Fx -- "$normalized" "$allowed" >/dev/null; then
                echo "scratchpad archive contains unmanifested path: $member" >&2
                exit 12
              fi
            done < "$members"

            tar -xzf "$archive" -C "$scratch_tmp"
            if find -P "$scratch_tmp" \( -type l -o \( ! -type f ! -type d \) \) -print -quit | grep -q .; then
              echo "scratchpad archive contains unsupported file type" >&2
              exit 13
            fi

            while IFS=$'\t' read -r kind scope rel; do
              valid_rel "$rel" || exit 11
              is_allowed_entry "$kind" "$rel" || exit 11
              src="$scratch_tmp/$scope/$rel"
              [ -e "$src" ] || continue
              case "$scope" in
                home) dest_base="$HOME" ;;
                work) dest_base="." ;;
                *) exit 11 ;;
              esac
              dest="$dest_base/$rel"
              if [ "$kind" = "dir" ]; then
                mkdir -p "$dest"
              elif [ "$kind" = "file" ]; then
                [ -f "$src" ] || exit 13
                mkdir -p "$(dirname "$dest")"
                cp -p "$src" "$dest"
              else
                exit 11
              fi
            done < "$manifest"
            """,
            "codeybox-resume",
            resume.ScratchpadArchivePath,
        };
        argv.AddRange(ScratchpadHomeDirectories);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = argv,
            WorkingDirectory = workingDirectory,
        }, ct);
        if (!result.Success)
            throw new InvalidOperationException($"agent scratchpad restore failed (exit {result.ExitCode}): {result.Stderr}");
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    protected sealed record AgentInvocation(
        IReadOnlyList<string> Argv,
        IReadOnlyDictionary<string, string>? ExtraEnvironment = null,
        string? Stdin = null);
}
