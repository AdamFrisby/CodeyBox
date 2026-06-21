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
    multipass transfer "$TMP_RESOURCE" "$VERIFY_VM:$REMOTE"
    multipass exec "$VERIFY_VM" -- chmod 700 "$REMOTE"
    multipass exec "$VERIFY_VM" -- sh -c "cat > '$REMOTE_VERIFY'" <<'PY'
#!/usr/bin/env python3
import base64
import hashlib
import json
import os
import queue
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time


def fail(message):
    raise AssertionError(message)


def read_exact(sock, count):
    chunks = []
    remaining = count
    while remaining:
        chunk = sock.recv(remaining)
        if not chunk:
            raise RuntimeError("socket closed while reading WebSocket frame")
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


FAKE_CLAUDE = r'''#!/usr/bin/env python3
import base64
import hashlib
import json
import os
import socket
import struct
import sys
import time


def read_exact(sock, count):
    chunks = []
    remaining = count
    while remaining:
        chunk = sock.recv(remaining)
        if not chunk:
            raise RuntimeError("socket closed while reading WebSocket frame")
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def recv_ws(sock):
    first = read_exact(sock, 2)
    length = first[1] & 0x7f
    masked = (first[1] & 0x80) != 0
    if length == 126:
        length = struct.unpack("!H", read_exact(sock, 2))[0]
    elif length == 127:
        length = struct.unpack("!Q", read_exact(sock, 8))[0]
    mask = read_exact(sock, 4) if masked else b""
    payload = bytearray(read_exact(sock, length))
    if masked:
        for i in range(length):
            payload[i] ^= mask[i % 4]
    return payload.decode("utf-8")


def send_ws(sock, payload):
    raw = json.dumps(payload, separators=(",", ":")).encode("utf-8")
    mask = os.urandom(4)
    if len(raw) < 126:
        header = bytes([0x81, 0x80 | len(raw)])
    elif len(raw) < 65536:
        header = bytes([0x81, 0x80 | 126]) + struct.pack("!H", len(raw))
    else:
        header = bytes([0x81, 0x80 | 127]) + struct.pack("!Q", len(raw))
    masked = bytes(raw[i] ^ mask[i % 4] for i in range(len(raw)))
    sock.sendall(header + mask + masked)


def main():
    if len(sys.argv) < 2 or sys.argv[1] != "--ide":
        raise SystemExit("expected bridge to spawn claude with --ide")

    lock_dir = os.environ["CODEYBOX_ACP_VERIFY_LOCK_DIR"]
    marker_path = os.environ["CODEYBOX_ACP_VERIFY_MARKER"]
    lock_path = None
    deadline = time.time() + 15
    while time.time() < deadline:
        candidates = [os.path.join(lock_dir, name) for name in os.listdir(lock_dir)] if os.path.isdir(lock_dir) else []
        candidates = [path for path in candidates if path.endswith(".lock")]
        if candidates:
            lock_path = candidates[0]
            break
        time.sleep(0.05)
    if lock_path is None:
        raise SystemExit("bridge did not write an IDE lockfile")

    with open(lock_path, "r", encoding="utf-8") as handle:
        lockfile = json.load(handle)
    url = lockfile["url"]
    if not url.startswith("ws://127.0.0.1:"):
        raise SystemExit("unexpected lockfile URL: " + url)
    port = int(url.rsplit(":", 1)[1])
    token = lockfile["authToken"]

    sock = socket.create_connection(("127.0.0.1", port), timeout=10)
    sock.settimeout(10)
    key = base64.b64encode(os.urandom(16)).decode("ascii")
    request = (
        "GET / HTTP/1.1\r\n"
        "Host: 127.0.0.1:%d\r\n"
        "Upgrade: websocket\r\n"
        "Connection: Upgrade\r\n"
        "Sec-WebSocket-Key: %s\r\n"
        "Sec-WebSocket-Version: 13\r\n"
        "x-claude-code-ide-authorization: %s\r\n\r\n"
    ) % (port, key, token)
    sock.sendall(request.encode("ascii"))
    response = b""
    while b"\r\n\r\n" not in response:
        response += sock.recv(4096)
    if b"101 Switching Protocols" not in response:
        raise SystemExit("WebSocket upgrade failed: " + response.decode("ascii", "replace"))

    methods = []
    while True:
        incoming = json.loads(recv_ws(sock))
        method = incoming.get("method")
        methods.append(method)
        request_id = incoming.get("id")
        if request_id is None:
            continue

        if method == "initialize":
            send_ws(sock, {"jsonrpc": "2.0", "id": request_id, "result": {"protocolVersion": 1, "capabilities": {}}})
        elif method == "session/new":
            send_ws(sock, {"jsonrpc": "2.0", "id": request_id, "result": {"sessionId": "verify-session"}})
        elif method == "session/load":
            send_ws(sock, {"jsonrpc": "2.0", "id": request_id, "result": {"sessionId": "verify-session"}})
        elif method == "session/prompt":
            with open(marker_path, "w", encoding="utf-8") as handle:
                json.dump({"argv": sys.argv[1:], "methods": methods, "lockPath": lock_path}, handle)
            send_ws(sock, {"jsonrpc": "2.0", "id": request_id, "result": {"stopReason": "end_turn"}})
            time.sleep(0.1)
            return
        else:
            raise SystemExit("unexpected ACP method from bridge: " + str(method))


if __name__ == "__main__":
    main()
'''


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


def run_turn(bridge_path, session_method):
    with tempfile.TemporaryDirectory(prefix="cb-acp-verify-") as tmp:
        work_dir = os.path.join(tmp, "work")
        lock_dir = os.path.join(tmp, "locks")
        os.mkdir(work_dir)
        os.mkdir(lock_dir)
        marker = os.path.join(tmp, "fake-claude-marker.json")
        fake_claude = os.path.join(tmp, "claude")
        with open(fake_claude, "w", encoding="utf-8") as handle:
            handle.write(FAKE_CLAUDE)
        os.chmod(fake_claude, 0o700)

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
            write_envelope(proc, {
                "type": "hello",
                "claudeBinary": fake_claude,
                "workingDirectory": work_dir,
                "lockDir": lock_dir,
                "turnTimeoutSeconds": 30,
                "claudeEnv": {
                    "CODEYBOX_ACP_VERIFY_LOCK_DIR": lock_dir,
                    "CODEYBOX_ACP_VERIFY_MARKER": marker,
                },
            })
            ready = wait_for_type(seen, "ready", 15, stdout_lines)
            wait_for_type(seen, "peer_connected", 15, stdout_lines)

            frames = [
                {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}},
                {"jsonrpc": "2.0", "id": 2, "method": session_method, "params": {"sessionId": "verify-session"}},
                {"jsonrpc": "2.0", "id": 3, "method": "session/prompt", "params": {"prompt": [{"type": "text", "text": "verify"}]}},
            ]
            for payload in frames:
                write_envelope(proc, {"type": "acp_send", "payload": payload})

            complete = wait_for_type(seen, "turn_complete", 20, stdout_lines)
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

            if not os.path.exists(marker):
                fail("fake claude did not record a completed ACP exchange; stdout:\n%s" % "\n".join(stdout_lines))
            with open(marker, "r", encoding="utf-8") as handle:
                marker_doc = json.load(handle)
            if marker_doc["argv"][0] != "--ide":
                fail("bridge did not spawn claude with --ide: " + json.dumps(marker_doc["argv"]))
            for expected in ("initialize", session_method, "session/prompt"):
                if expected not in marker_doc["methods"]:
                    fail("fake claude did not receive %s; saw %s" % (expected, marker_doc["methods"]))

            lock_path = ready["lockPath"]
            if os.path.exists(lock_path):
                fail("bridge left IDE lockfile behind: " + lock_path)

            sent_methods = [
                env.get("method") for env in
                (json.loads(line) for line in stdout_lines if line.startswith("{"))
                if env.get("type") == "acp_sent"
            ]
            for expected in ("initialize", session_method, "session/prompt"):
                if expected not in sent_methods:
                    fail("bridge did not emit acp_sent for %s; saw %s" % (expected, sent_methods))
        finally:
            if proc.poll() is None:
                proc.kill()


def main():
    if len(sys.argv) != 2:
        fail("usage: verify-acp-bridge.py /path/to/acp-bridge")
    bridge_path = sys.argv[1]
    with open(bridge_path, "rb") as handle:
        magic = handle.read(4)
    if magic != b"\x7fELF":
        fail("published bridge is not an ELF binary")

    run_turn(bridge_path, "session/new")
    run_turn(bridge_path, "session/load")
    print("ACP bridge end-to-end verification passed: lockfile, WebSocket, claude --ide spawn, session/new, and session/load.")


if __name__ == "__main__":
    main()
PY
    multipass exec "$VERIFY_VM" -- chmod 700 "$REMOTE_VERIFY"
    VERIFY_OUT="$(multipass exec "$VERIFY_VM" -- python3 "$REMOTE_VERIFY" "$REMOTE" 2>&1)" || {
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
