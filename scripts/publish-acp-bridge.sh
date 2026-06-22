#!/usr/bin/env sh
# Build the ACP bridge as a self-contained, statically-linked NativeAOT
# binary, verify it can execute in a Multipass VM, and refresh the embedded
# resource in CodeyBox.Agents.Claude.
#
# Operator prerequisites on the build host:
#   - .NET 10 SDK
#   - musl-tools (for the musl static linker the ILCompiler invokes)
#   - clang (NativeAOT passes --target=x86_64-linux-musl, which GCC rejects)
#   - lld (the project sets LinkerFlavor=lld)
#   - multipass (for the required in-VM runtime verification)
#
# Usage:
#   scripts/publish-acp-bridge.sh
#   CODEYBOX_ACP_BRIDGE_VERIFY_VM=cb-baseline-abc123 scripts/publish-acp-bridge.sh
#
# The verification VM MUST be an already-baked CodeyBox baseline supplied in
# CODEYBOX_ACP_BRIDGE_VERIFY_VM. Compile-only CI may pass
# --skip-multipass-verify, but release artifacts must not use that escape hatch.
set -eu

SKIP_VM_VERIFY=0
for arg in "$@"; do
    case "$arg" in
        --skip-multipass-verify)
            SKIP_VM_VERIFY=1
            ;;
        *)
            echo "Usage: scripts/publish-acp-bridge.sh [--skip-multipass-verify]" >&2
            exit 64
            ;;
    esac
done

if [ "${CODEYBOX_ACP_BRIDGE_SKIP_VM_VERIFY:-0}" = "1" ]; then
    SKIP_VM_VERIFY=1
fi

cd "$(dirname "$0")/.."

BRIDGE_PROJECT="src/CodeyBox.Agents.Claude.AcpBridge/CodeyBox.Agents.Claude.AcpBridge.csproj"
RESOURCE_DIR="src/CodeyBox.Agents.Claude/Resources"
RESOURCE_NAME="acp-bridge"
RESOURCE_PATH="$RESOURCE_DIR/$RESOURCE_NAME"
TMP_RESOURCE="$RESOURCE_DIR/.$RESOURCE_NAME.$$.new"
RID="linux-musl-x64"

cleanup()
{
    if [ -n "$TMP_RESOURCE" ]; then
        rm -f "$TMP_RESOURCE"
    fi
}
trap cleanup EXIT INT TERM

shell_quote()
{
    # POSIX single-quote escaping for values written to the VM-side verifier
    # env file. The file is sourced by /bin/sh inside the temporary verifier
    # directory, so credentials never need to ride on the multipass argv.
    printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"
}

write_env_assignment()
{
    printf '%s=' "$1"
    shell_quote "$2"
    printf '\n'
}

mkdir -p "$RESOURCE_DIR"

# Remove the old ignored resource before publishing. If dotnet publish or any
# verification step fails, the parent project can only fall back to the tracked
# placeholder; it cannot silently embed stale bytes from an earlier successful
# run.
rm -f "$RESOURCE_PATH" "$TMP_RESOURCE"

dotnet publish "$BRIDGE_PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishAot=true \
    -p:StaticExecutable=true

PUBLISH_DIR="src/CodeyBox.Agents.Claude.AcpBridge/bin/Release/net10.0/$RID/publish"
SOURCE_BIN="$PUBLISH_DIR/CodeyBox.Agents.Claude.AcpBridge"

if [ ! -f "$SOURCE_BIN" ]; then
    echo "ERROR: published binary not found at $SOURCE_BIN" >&2
    exit 1
fi

cp "$SOURCE_BIN" "$TMP_RESOURCE"
chmod 755 "$TMP_RESOURCE"

echo "Candidate bridge binary:"
ls -la "$TMP_RESOURCE"
file "$TMP_RESOURCE" 2>/dev/null || true

# Sanity-check the static-link claim: the binary must NOT advertise a dynamic
# interpreter. ldd on a static-PIE ELF prints "not a dynamic executable" or
# "statically linked". A dynamically-linked output is a HARD FAILURE: the
# bridge ships into a Multipass sandbox whose glibc version is not guaranteed
# to match the build host.
if command -v ldd >/dev/null 2>&1; then
    LDD_OUT="$(ldd "$TMP_RESOURCE" 2>&1 || true)"
    echo "ldd: $LDD_OUT"
    case "$LDD_OUT" in
        *"not a dynamic executable"*|*"statically linked"*) ;;
        *)
            echo "ERROR: published binary appears dynamically linked — check StaticExecutable and musl-tools." >&2
            exit 1
            ;;
    esac
fi

if [ "$SKIP_VM_VERIFY" = "1" ]; then
    echo "WARNING: skipping required Multipass runtime verification by explicit request." >&2
else
    if ! command -v multipass >/dev/null 2>&1; then
        echo "ERROR: multipass is required to verify the ACP bridge inside the sandbox image." >&2
        echo "       Set CODEYBOX_ACP_BRIDGE_VERIFY_VM to a baked CodeyBox baseline VM, or install Multipass." >&2
        exit 1
    fi

    VERIFY_VM="${CODEYBOX_ACP_BRIDGE_VERIFY_VM:-}"
    if [ -z "$VERIFY_VM" ]; then
        echo "ERROR: CODEYBOX_ACP_BRIDGE_VERIFY_VM must name an already-baked CodeyBox sandbox baseline VM." >&2
        echo "       The bridge verifier must run on the same image operators will clone; it no longer falls back to vanilla Ubuntu." >&2
        exit 1
    fi

    echo "Using CodeyBox Multipass verification VM $VERIFY_VM..."
    multipass start "$VERIFY_VM" >/dev/null 2>&1 || true

    REMOTE_DIR="$(multipass exec "$VERIFY_VM" -- mktemp -d /tmp/codeybox-acp-bridge-verify.XXXXXX)"
    REMOTE="$REMOTE_DIR/acp-bridge"
    REMOTE_VERIFY="$REMOTE_DIR/verify-acp-bridge.py"
    REMOTE_ENV="$REMOTE_DIR/claude-env.sh"
    multipass transfer "$TMP_RESOURCE" "$VERIFY_VM:$REMOTE"
    multipass exec "$VERIFY_VM" -- chmod 700 "$REMOTE"
    multipass exec "$VERIFY_VM" -- sh -c "cat > '$REMOTE_VERIFY'" <<'PY'
#!/usr/bin/env python3
import json
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time

SUCCESS_MARKER = "ACP bridge end-to-end verification passed"

# Keep the shared prefix large and stable across the two turns so the provider
# has something meaningful to write on turn 1 and read via session/load on turn
# 2. The exact reply text is irrelevant; the verifier reads the usage buckets.
CACHE_PROMPT_PREFIX = (
    "CodeyBox ACP bridge cache verification context. "
    "This paragraph is intentionally repetitive and stable across turns. "
) * 1200


def fail(message):
    raise AssertionError(message)


def require_claude_binary():
    claude = shutil.which("claude")
    if not claude:
        fail("real claude binary not found on verification VM PATH")

    try:
        version = subprocess.run(
            [claude, "--version"],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=20,
        )
    except Exception as ex:
        fail("failed to execute real claude --version: %s" % ex)
    if version.returncode != 0:
        fail("real claude --version exited %d; stdout=%s stderr=%s" %
             (version.returncode, version.stdout.strip(), version.stderr.strip()))
    return claude


def prepare_claude_auth_files():
    oauth_json = os.environ.get("CODEYBOX_CLAUDE_OAUTH_JSON")
    if not oauth_json:
        return
    claude_dir = os.path.expanduser("~/.claude")
    os.makedirs(claude_dir, mode=0o700, exist_ok=True)
    credentials_path = os.path.join(claude_dir, ".credentials.json")
    with open(credentials_path, "w", encoding="utf-8") as handle:
        handle.write(oauth_json)
    os.chmod(credentials_path, 0o600)


def build_claude_env():
    env = {}
    for key in ("ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "API_TIMEOUT_MS"):
        value = os.environ.get(key)
        if value:
            env[key] = value
    return env


def reader_thread(pipe, output, seen):
    try:
        for line in iter(pipe.readline, ""):
            text = line.rstrip("\n")
            output.append(text)
            try:
                seen.put(json.loads(text))
            except json.JSONDecodeError:
                pass
    finally:
        try:
            pipe.close()
        except Exception:
            pass


def wait_for_type(seen, kind, timeout, lines):
    deadline = time.time() + timeout
    while time.time() < deadline:
        remaining = max(0.01, deadline - time.time())
        try:
            envelope = seen.get(timeout=remaining)
        except queue.Empty:
            break
        if envelope.get("type") == kind:
            return envelope
    fail("timed out waiting for %s; bridge stdout was:\n%s" % (kind, "\n".join(lines)))


def write_envelope(proc, envelope):
    proc.stdin.write(json.dumps(envelope, separators=(",", ":")) + "\n")
    proc.stdin.flush()


def iter_json_envelopes(lines):
    for line in lines:
        if not line.startswith("{"):
            continue
        try:
            yield json.loads(line)
        except json.JSONDecodeError:
            continue


def extract_session_id(lines):
    for env in iter_json_envelopes(lines):
        if env.get("type") != "acp_recv":
            continue
        payload = env.get("payload") or {}
        result = payload.get("result") or {}
        session_id = result.get("sessionId") or result.get("session_id")
        if isinstance(session_id, str) and session_id:
            return session_id
    return None


def extract_usage(lines):
    usage = {
        "input_tokens": 0,
        "output_tokens": 0,
        "cache_read_input_tokens": 0,
        "cache_creation_input_tokens": 0,
    }
    for env in iter_json_envelopes(lines):
        if env.get("type") != "acp_recv":
            continue
        payload = env.get("payload") or {}
        candidates = []
        result = payload.get("result")
        if isinstance(result, dict):
            candidates.append(result.get("usage"))
        params = payload.get("params")
        if isinstance(params, dict):
            update = params.get("update")
            if isinstance(update, dict):
                candidates.append(update.get("usage"))
        for candidate in candidates:
            if not isinstance(candidate, dict):
                continue
            for key in usage:
                value = candidate.get(key)
                if isinstance(value, int):
                    usage[key] += value
    return usage


def sent_methods(lines):
    methods = []
    for env in iter_json_envelopes(lines):
        if env.get("type") == "acp_sent":
            methods.append(env.get("method"))
    return methods


def run_turn(bridge_path, claude_path, session_method, session_id, prompt):
    with tempfile.TemporaryDirectory(prefix="cb-acp-verify-") as tmp:
        work_dir = os.path.join(tmp, "work")
        lock_dir = os.path.join(tmp, "locks")
        os.mkdir(work_dir)
        os.mkdir(lock_dir)

        proc = subprocess.Popen(
            [bridge_path],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
            cwd=work_dir,
        )
        stdout_lines = []
        stderr_lines = []
        seen = queue.Queue()
        stdout_reader = threading.Thread(
            target=reader_thread, args=(proc.stdout, stdout_lines, seen), daemon=True)
        stderr_reader = threading.Thread(
            target=lambda: stderr_lines.extend(line.rstrip("\n") for line in proc.stderr), daemon=True)
        stdout_reader.start()
        stderr_reader.start()

        try:
            turn_timeout = int(os.environ.get("CODEYBOX_ACP_BRIDGE_VERIFY_TURN_TIMEOUT_SECONDS", "240"))
            claude_args = ["--dangerously-skip-permissions"]
            model = os.environ.get("CODEYBOX_ACP_BRIDGE_VERIFY_MODEL")
            if model:
                claude_args.extend(["--model", model])
            write_envelope(proc, {
                "type": "hello",
                "claudeBinary": claude_path,
                "claudeArgs": claude_args,
                "workingDirectory": work_dir,
                "lockDir": lock_dir,
                "claudeEnv": build_claude_env(),
                "turnTimeoutSeconds": turn_timeout,
            })
            ready = wait_for_type(seen, "ready", 15, stdout_lines)
            wait_for_type(seen, "peer_connected", 15, stdout_lines)

            session_params = {"cwd": work_dir}
            if session_method == "session/new":
                session_params["mcpServers"] = []
            elif session_method == "session/load":
                if not session_id:
                    fail("session/load verifier turn requires a session id")
                session_params["sessionId"] = session_id
            else:
                fail("unexpected session method: " + session_method)

            frames = [
                {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}},
                {"jsonrpc": "2.0", "id": 2, "method": session_method, "params": session_params},
                {"jsonrpc": "2.0", "id": 3, "method": "session/prompt", "params": {"prompt": [{"type": "text", "text": prompt}]}},
            ]
            for payload in frames:
                write_envelope(proc, {"type": "acp_send", "payload": payload})

            complete = wait_for_type(seen, "turn_complete", turn_timeout + 30, stdout_lines)
            if complete.get("stopReason") != "end_turn":
                fail("turn_complete stopReason drifted: " + json.dumps(complete))

            try:
                proc.stdin.close()
            except Exception:
                pass
            try:
                code = proc.wait(timeout=20)
            except subprocess.TimeoutExpired:
                proc.kill()
                fail("bridge did not exit after turn_complete")
            if code != 0:
                fail("bridge exited %d; stdout:\n%s\nstderr:\n%s" %
                     (code, "\n".join(stdout_lines), "\n".join(stderr_lines)))

            lock_path = ready["lockPath"]
            if os.path.exists(lock_path):
                fail("bridge left IDE lockfile behind: " + lock_path)

            observed_sent_methods = sent_methods(stdout_lines)
            for expected in ("initialize", session_method, "session/prompt"):
                if expected not in observed_sent_methods:
                    fail("bridge did not emit acp_sent for %s; saw %s" % (expected, observed_sent_methods))

            observed_session_id = extract_session_id(stdout_lines)
            if not observed_session_id:
                fail("bridge did not surface an ACP session id during %s; stdout:\n%s" %
                     (session_method, "\n".join(stdout_lines)))
            return {
                "session_id": observed_session_id,
                "usage": extract_usage(stdout_lines),
                "stdout": stdout_lines,
                "stderr": stderr_lines,
            }
        finally:
            if proc.poll() is None:
                proc.kill()


def main():
    if len(sys.argv) != 2:
        fail("usage: verify-acp-bridge.py /path/to/acp-bridge")
    bridge_path = sys.argv[1]
    claude_path = require_claude_binary()
    prepare_claude_auth_files()
    first = run_turn(
        bridge_path,
        claude_path,
        "session/new",
        None,
        CACHE_PROMPT_PREFIX + "\nTurn 1: reply briefly with codeybox-acp-verify.",
    )
    second = run_turn(
        bridge_path,
        claude_path,
        "session/load",
        first["session_id"],
        CACHE_PROMPT_PREFIX + "\nTurn 2: reply briefly with codeybox-acp-verify.",
    )

    first_usage = first["usage"]
    second_usage = second["usage"]
    if first_usage["cache_creation_input_tokens"] <= 0:
        fail("cold ACP turn did not report cache_creation_input_tokens > 0; usage=%s" % first_usage)
    if second_usage["cache_read_input_tokens"] <= 0:
        fail("session/load ACP turn did not report cache_read_input_tokens > 0; usage=%s" % second_usage)
    if (second_usage["cache_creation_input_tokens"] > 0
            and second_usage["cache_creation_input_tokens"] >= first_usage["cache_creation_input_tokens"]):
        fail("session/load appears to rebuild the cache instead of reading it; first=%s second=%s" %
             (first_usage, second_usage))

    print(SUCCESS_MARKER + ": real claude --ide lockfile discovery, session/new, session/load, and cache_read continuity.")


if __name__ == "__main__":
    main()
PY
    multipass exec "$VERIFY_VM" -- chmod 700 "$REMOTE_VERIFY"
    if [ -z "${ANTHROPIC_API_KEY:-}" ] \
        && [ -z "${CODEYBOX_CLAUDE_API_KEY:-}" ] \
        && [ -z "${CLAUDE_CODE_OAUTH_TOKEN:-}" ] \
        && [ -z "${CODEYBOX_CLAUDE_OAUTH_JSON:-}" ]; then
        echo "ERROR: ACP bridge VM verification requires a Claude credential in host env." >&2
        echo "       Set CODEYBOX_CLAUDE_API_KEY (mapped to ANTHROPIC_API_KEY in the VM), ANTHROPIC_API_KEY, CLAUDE_CODE_OAUTH_TOKEN, or CODEYBOX_CLAUDE_OAUTH_JSON." >&2
        multipass exec "$VERIFY_VM" -- rm -rf "$REMOTE_DIR" >/dev/null 2>&1 || true
        exit 1
    fi
    {
        if [ -n "${ANTHROPIC_API_KEY:-}" ]; then
            write_env_assignment "ANTHROPIC_API_KEY" "$ANTHROPIC_API_KEY"
        elif [ -n "${CODEYBOX_CLAUDE_API_KEY:-}" ]; then
            write_env_assignment "ANTHROPIC_API_KEY" "$CODEYBOX_CLAUDE_API_KEY"
        fi
        if [ -n "${CLAUDE_CODE_OAUTH_TOKEN:-}" ]; then
            write_env_assignment "CLAUDE_CODE_OAUTH_TOKEN" "$CLAUDE_CODE_OAUTH_TOKEN"
        fi
        if [ -n "${CODEYBOX_CLAUDE_OAUTH_JSON:-}" ]; then
            write_env_assignment "CODEYBOX_CLAUDE_OAUTH_JSON" "$CODEYBOX_CLAUDE_OAUTH_JSON"
        fi
    } | multipass exec "$VERIFY_VM" -- sh -c "umask 077; cat > '$REMOTE_ENV'"
    VERIFY_OUT="$(multipass exec "$VERIFY_VM" -- sh -c ". '$REMOTE_ENV'; export ANTHROPIC_API_KEY CLAUDE_CODE_OAUTH_TOKEN CODEYBOX_CLAUDE_OAUTH_JSON; python3 '$REMOTE_VERIFY' '$REMOTE'" 2>&1)" || {
        echo "ERROR: ACP bridge end-to-end verification failed inside Multipass VM $VERIFY_VM:" >&2
        echo "$VERIFY_OUT" >&2
        multipass exec "$VERIFY_VM" -- rm -rf "$REMOTE_DIR" >/dev/null 2>&1 || true
        exit 1
    }
    multipass exec "$VERIFY_VM" -- rm -rf "$REMOTE_DIR" >/dev/null 2>&1 || true
    case "$VERIFY_OUT" in
        *"ACP bridge end-to-end verification passed"*) ;;
        *)
            echo "ERROR: ACP bridge verifier did not report success:" >&2
            echo "$VERIFY_OUT" >&2
            exit 1
            ;;
    esac
    echo "$VERIFY_OUT"
    echo "Multipass ACP verification passed on $VERIFY_VM."
fi

mv "$TMP_RESOURCE" "$RESOURCE_PATH"
chmod 644 "$RESOURCE_PATH"
TMP_RESOURCE=""

echo "Embedded resource refreshed:"
ls -la "$RESOURCE_PATH"
