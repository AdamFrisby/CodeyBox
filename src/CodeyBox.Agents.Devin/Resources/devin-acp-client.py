#!/usr/bin/env python3
"""CodeyBox ACP client shim for the Devin CLI.

Drives `devin acp` (Agent Client Protocol server over stdio, verified against
devin 3000.11.1) for one non-interactive turn: initialize -> session/new ->
session/set_mode (when --mode is given) -> session/prompt, then exits.

Every ACP frame the agent emits is folded into a single-line JSON envelope on
stdout so the host-side agent-stream file keeps advancing while the agent
works — `devin -p` only printed at exit, which starved the worker-progress
watchdog on long turns. Envelope shape:

    {"type":"devin.acp","event":"<event>", ...}

Events: session_started, prompt_sent, session_update, notification,
permission_auto_granted, permission_auto_cancelled, unhandled_request,
mode_set, turn_complete, turn_error, fatal, protocol_error.

Exit status: 0 only after a session/prompt response carrying a stopReason;
2 for any protocol, spawn, or turn-level failure.

Stderr is NOT inherited into the exec channel: fd 2 is retargeted to a pipe
drained by a relay thread that re-emits every line on stdout as a
{"type":"codeybox.stderr","text":...} envelope. Envelope provenance must be
established at the emission point — the in-VM exec wrapper merges the
command's stderr into its stdout whenever the invocation log tee is active
(CODEYBOX_AGENT_LOG_FILE), so a tool subprocess printing a forged devin.acp
line to stderr would otherwise arrive host-side as a bare, claimable
envelope and falsify the run's recorded outcome/diagnostics. Wrapped at the
source, a merged or injected stream can never yield a bare devin.acp line
sourced from stderr; `Error: ...` diagnostics still surface as envelope
text for the host-side diagnoser.

What the envelopes do NOT prove: a same-uid (root-capable) process inside
the sandbox can still write the exec stdout pipe through /proc/<pid>/fd,
and a tool subprocess that inherits the agent's fd 1 writes onto this
client's wire pipe. The wire pipe is narrowed — JSON-RPC request ids are
unguessable (a forged response can never name a pending one),
session-scoped frames must carry the negotiated sessionId, and usage bags
are re-emitted as flat number maps, not verbatim objects — but the
exec-pipe leg has no in-VM fix. Consumers must treat envelope payloads
(usage counters, terminal event, finalText) as agent-influenceable
telemetry, never authoritative accounting.
"""

from __future__ import annotations

import argparse
import json
import os
import secrets
import signal
import subprocess
import sys
import threading

ENVELOPE_TYPE = "devin.acp"
# Mirror of the host stream parser's stderr-envelope tag
# (CliAgentRunnerBase.StderrEnvelopeType). Duplicated because this shim
# ships as a self-contained script.
STDERR_ENVELOPE_TYPE = "codeybox.stderr"
# Mirror of the host StderrEnvelopeForwarder bound: a runaway stderr writer
# must not grow the relay's pending buffer without bound.
STDERR_MAX_LINE_CHARS = 64 * 1024
STDERR_TRUNCATION_MARKER = "[...stderr line truncated]"
ACP_PROTOCOL_VERSION = 1
CLIENT_NAME = "codeybox"
CLIENT_VERSION = "1"

# Frames observed from devin 3000.11.1 are KB-sized. A single line past this
# bound means the peer is no longer speaking the protocol we drive; fail
# closed instead of buffering unbounded output.
MAX_FRAME_BYTES = 16 * 1024 * 1024
# Rework prompts exceed MAX_ARG_STRLEN (128 KiB) but stay far below this; a
# larger prompt fails loudly rather than silently truncating mid-prompt.
MAX_PROMPT_BYTES = 32 * 1024 * 1024
# agent_message_chunk text is accumulated for the turn_complete envelope;
# beyond this cap the tail is dropped with a marker so a runaway stream
# cannot balloon the captured file.
MAX_FINAL_TEXT_CHARS = 256 * 1024
FINAL_TEXT_TRUNCATION_MARKER = "[...final text truncated]"

EXIT_TURN_FAILED = 2


_stdout_lock = threading.Lock()


def _write_stdout_line(line):
    # The relay thread and emit() share fd 1; serialise so a stderr envelope
    # can never interleave mid-line with a devin.acp envelope.
    with _stdout_lock:
        sys.stdout.write(line + "\n")
        sys.stdout.flush()


def emit(event, **fields):
    envelope = {"type": ENVELOPE_TYPE, "event": event}
    envelope.update(fields)
    _write_stdout_line(json.dumps(envelope))


def _emit_stderr_line(raw):
    text = raw.decode("utf-8", errors="replace").rstrip("\r")
    _write_stdout_line(json.dumps({"type": STDERR_ENVELOPE_TYPE, "text": text}))


def _stderr_relay_loop(read_fd):
    """Drain the stderr pipe and re-emit each line on stdout as a
    codeybox.stderr envelope. Keeps draining on envelope-write failure so a
    dead stdout can never block writers on a full pipe; bounded per line so
    a runaway writer cannot grow the buffer without limit."""
    pending = bytearray()
    overflowed = False

    def flush_line(raw):
        if len(raw) > STDERR_MAX_LINE_CHARS:
            raw = raw[:STDERR_MAX_LINE_CHARS] + STDERR_TRUNCATION_MARKER.encode("utf-8")
        try:
            _emit_stderr_line(raw)
        except Exception:
            pass

    while True:
        try:
            chunk = os.read(read_fd, 65536)
        except OSError:
            break
        if not chunk:
            break
        pending.extend(chunk)
        while True:
            newline = pending.find(b"\n")
            if newline < 0:
                break
            line = bytes(pending[:newline])
            del pending[:newline + 1]
            if overflowed:
                # Tail of a line whose truncated prefix was already emitted.
                overflowed = False
                continue
            flush_line(line)
        if len(pending) > STDERR_MAX_LINE_CHARS:
            # Enforce the bound even mid-overflow — a writer emitting a
            # never-ending line must not grow the buffer without limit.
            if not overflowed:
                flush_line(bytes(pending))
                overflowed = True
            pending.clear()
    if pending and not overflowed:
        flush_line(bytes(pending))


def install_stderr_envelope_relay():
    """Retarget fd 2 to a pipe drained by a daemon relay thread that
    re-emits every line on stdout as a codeybox.stderr envelope.

    Provenance is established at the emission point, inside the sandbox,
    rather than relying on the transport to keep stderr off the claimable
    stdout stream: the exec wrapper merges the command's stderr into its
    stdout whenever the invocation log tee is active, so without this relay
    a tool subprocess could print a bare forged devin.acp envelope to
    stderr and have it claimed as genuine shim output host-side. The CLI
    and every tool subprocess inherit fd 2, so all of their stderr passes
    through the relay — including this shim's own tracebacks.
    """
    read_fd, write_fd = os.pipe()
    os.dup2(write_fd, 2)
    os.close(write_fd)
    thread = threading.Thread(
        target=_stderr_relay_loop, args=(read_fd,), daemon=True,
        name="stderr-envelope-relay")
    thread.start()
    return thread


class FrameTooLargeError(Exception):
    pass


def read_frame(stream):
    """Return one newline-terminated frame, or None at EOF.

    readline(size) returns at most `size` bytes, so an overlong frame
    surfaces as a buffer that does not end in a newline at the cap rather
    than growing host memory without bound.
    """
    line = stream.readline(MAX_FRAME_BYTES + 1)
    if not line:
        return None
    if len(line) > MAX_FRAME_BYTES:
        raise FrameTooLargeError()
    return line


class AcpClient:
    """Synchronous JSON-RPC 2.0 driver over the child's stdio pipes."""

    def __init__(self, proc):
        self.proc = proc
        self.pending = {}
        # Negotiated session id, set when session/new answers. Session-scoped
        # frames for any other id are dropped — the agent's sessionId is a
        # server-chosen opaque string a wire-pipe writer cannot guess.
        self.session_id = None

    def request(self, stage, method, params):
        # Unguessable ids: a process that inherited the agent's stdout fd (a
        # tool subprocess of `devin acp`) writes onto the pipe this client
        # reads, and could otherwise answer a predictable sequential id with
        # a forged turn result that the shim would stamp into a genuine
        # terminal envelope.
        request_id = "cb-" + secrets.token_hex(16)
        self.pending[request_id] = stage
        self._write({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})

    def respond_result(self, request_id, result):
        self._write({"jsonrpc": "2.0", "id": request_id, "result": result})

    def respond_error(self, request_id, code, message):
        self._write({"jsonrpc": "2.0", "id": request_id, "error": {"code": code, "message": message}})

    def _write(self, obj):
        self.proc.stdin.write(json.dumps(obj).encode("utf-8") + b"\n")
        self.proc.stdin.flush()


def pick_permission_option(options):
    """Choose the most durable allow option, matching `dangerous` semantics.

    ACP option kinds are allow_always / allow_once / reject_*; fall back to
    any option whose kind or id mentions allow, then to the first option —
    never to a reject when an allow exists.
    """
    for preferred in ("allow_always", "allow_once"):
        for option in options:
            if isinstance(option, dict) and option.get("kind") == preferred:
                return option.get("optionId")
    for option in options:
        if not isinstance(option, dict):
            continue
        if "allow" in str(option.get("kind", "")) or "allow" in str(option.get("optionId", "")):
            return option.get("optionId")
    for option in options:
        if isinstance(option, dict) and option.get("optionId") is not None:
            return option.get("optionId")
    return None


def truncated_append(parts, total, text):
    if total >= MAX_FINAL_TEXT_CHARS:
        return total
    remaining = MAX_FINAL_TEXT_CHARS - total
    parts.append(text[:remaining])
    total += len(text[:remaining])
    if len(text) > remaining:
        parts.append(FINAL_TEXT_TRUNCATION_MARKER)
    return total


def run_turn(client, prompt, cwd, mode):
    session_id = None
    final_text_parts = []
    final_text_chars = 0

    client.request("initialize", "initialize", {
        "protocolVersion": ACP_PROTOCOL_VERSION,
        "clientCapabilities": {
            "fs": {"readTextFile": False, "writeTextFile": False},
            "terminal": False,
        },
        "clientInfo": {"name": CLIENT_NAME, "version": CLIENT_VERSION},
    })

    while True:
        raw = read_frame(client.proc.stdout)
        if raw is None:
            emit("fatal", stage="turn",
                 message="devin acp closed its stdout before the turn completed")
            return EXIT_TURN_FAILED

        try:
            frame = json.loads(raw)
        except ValueError:
            emit("protocol_error", message="non-JSON frame from devin acp")
            continue
        if not isinstance(frame, dict):
            emit("protocol_error", message="non-object frame from devin acp")
            continue

        if "method" in frame and "id" in frame:
            handle_agent_request(client, frame)
            continue

        if "method" in frame:
            final_text_chars = handle_notification(client, frame, final_text_parts, final_text_chars)
            continue

        if "id" not in frame:
            emit("protocol_error", message="frame with neither method nor id from devin acp")
            continue

        stage = client.pending.pop(frame["id"], None)
        if stage is None:
            emit("protocol_error", message="response for unknown request id",
                 requestId=frame["id"])
            continue

        if "error" in frame:
            error = frame["error"] if isinstance(frame["error"], dict) else {}
            message = error.get("message", "unknown error")
            if stage == "prompt":
                emit("turn_error", code=error.get("code"), message=message)
            else:
                emit("fatal", stage=stage, code=error.get("code"), message=message)
            return EXIT_TURN_FAILED

        result = frame.get("result")
        if not isinstance(result, dict):
            # The pending request was already popped and no follow-up is
            # sent, so continuing here would deadlock the turn until the
            # agent closes stdout — fail fast instead.
            emit("fatal", stage=stage,
                 message="response for %s did not carry an object result" % stage)
            return EXIT_TURN_FAILED

        if stage == "initialize":
            # The negotiated version must be the one this client drives; a
            # peer speaking anything else would only fail later, in a less
            # diagnosable stage.
            if result.get("protocolVersion") != ACP_PROTOCOL_VERSION:
                emit("fatal", stage="initialize",
                     message="devin acp spoke protocolVersion %r; expected %d"
                             % (result.get("protocolVersion"), ACP_PROTOCOL_VERSION))
                return EXIT_TURN_FAILED
            client.request("session/new", "session/new",
                           {"cwd": cwd, "mcpServers": []})
        elif stage == "session/new":
            session_id = result.get("sessionId")
            if not isinstance(session_id, str) or not session_id:
                emit("fatal", stage="session/new",
                     message="session/new response did not carry a sessionId")
                return EXIT_TURN_FAILED
            client.session_id = session_id
            emit("session_started", sessionId=session_id)
            if mode:
                client.request("session/set_mode", "session/set_mode",
                               {"sessionId": session_id, "modeId": mode})
            else:
                send_prompt(client, session_id, prompt)
        elif stage == "session/set_mode":
            emit("mode_set", modeId=mode)
            send_prompt(client, session_id, prompt)
        elif stage == "prompt":
            stop_reason = result.get("stopReason")
            if not isinstance(stop_reason, str) or not stop_reason:
                # Exit 0 is contracted to mean a completed turn; a result
                # without a stopReason is a protocol violation, never a
                # silent success.
                emit("fatal", stage="session/prompt",
                     message="session/prompt result did not carry a stopReason")
                return EXIT_TURN_FAILED
            emit("turn_complete",
                 stopReason=stop_reason,
                 usage=numeric_map(result.get("usage")),
                 finalText="".join(final_text_parts) or None)
            return 0
        else:
            emit("protocol_error", message="response for unhandled stage", stage=stage)
            # The pending request was already popped and no follow-up is
            # sent, so continuing here would deadlock the turn until the
            # agent closes stdout — fail fast instead.
            return EXIT_TURN_FAILED


def send_prompt(client, session_id, prompt):
    client.request("prompt", "session/prompt", {
        "sessionId": session_id,
        "prompt": [{"type": "text", "text": prompt}],
    })
    emit("prompt_sent", promptBytes=len(prompt.encode("utf-8")))


def numeric_map(value):
    """Bound a peer-supplied usage/_meta bag to a flat string->number map
    before it is stamped into a claimable envelope field. The wire pipe is
    writable by anything that inherited the agent's stdout fd, so nested
    objects are never re-emitted verbatim. Host consumers treat these
    counters as diagnostics-only telemetry — nothing folds them into usage
    accounting."""
    if not isinstance(value, dict):
        return None
    return {
        key: entry
        for key, entry in value.items()
        if isinstance(key, str) and isinstance(entry, (int, float))
        and not isinstance(entry, bool)
    }


def sanitise_update(update):
    """Re-emit a session/update payload with its claimable surface bounded:
    _meta is reduced to a flat number map so a wire-pipe writer cannot
    smuggle nested objects into a claimable envelope field. Every envelope
    field is display metadata — the host never folds _meta counters into
    usage accounting."""
    if not isinstance(update, dict):
        return update
    meta = update.get("_meta")
    if not isinstance(meta, dict):
        return update
    bounded = dict(update)
    bounded["_meta"] = numeric_map(meta)
    return bounded


def handle_notification(client, frame, final_text_parts, final_text_chars):
    method = frame.get("method")
    params = frame.get("params")
    if method == "session/update" and isinstance(params, dict):
        if params.get("sessionId") != client.session_id:
            # The wire pipe is writable by anything that inherited the
            # agent's stdout fd; session frames must name the negotiated
            # session id or they are not the peer's to emit.
            emit("protocol_error",
                 message="session/update for a session id this client did not open")
            return final_text_chars
        update = sanitise_update(params.get("update"))
        emit("session_update", sessionId=params.get("sessionId"), update=update)
        if isinstance(update, dict) and update.get("sessionUpdate") == "agent_message_chunk":
            content = update.get("content")
            if isinstance(content, dict) and isinstance(content.get("text"), str):
                return truncated_append(final_text_parts, final_text_chars, content["text"])
        return final_text_chars
    emit("notification", method=method, params=params)
    return final_text_chars


def handle_agent_request(client, frame):
    request_id = frame["id"]
    method = frame.get("method")
    params = frame.get("params") if isinstance(frame.get("params"), dict) else {}

    if method == "session/request_permission":
        if params.get("sessionId") != client.session_id:
            # Answer (so a genuinely-misshaped peer is not left waiting on
            # a response it needs) but never honour the request: a frame
            # for a session this client did not open is not the peer's.
            client.respond_error(request_id, -32602,
                                 "sessionId does not match the negotiated session")
            emit("protocol_error",
                 message="session/request_permission for a session id this client did not open")
            return
        options = params.get("options")
        options = options if isinstance(options, list) else []
        tool_call = params.get("toolCall") if isinstance(params.get("toolCall"), dict) else {}
        option_id = pick_permission_option(options)
        if option_id is None:
            client.respond_result(request_id, {"outcome": {"outcome": "cancelled"}})
            emit("permission_auto_cancelled", toolCallId=tool_call.get("toolCallId"))
        else:
            client.respond_result(
                request_id,
                {"outcome": {"outcome": "selected", "optionId": option_id}})
            emit("permission_auto_granted",
                 optionId=option_id, toolCallId=tool_call.get("toolCallId"))
        return

    client.respond_error(request_id, -32601,
                         "codeybox shim does not implement client method %s" % method)
    emit("unhandled_request", method=method)


def install_signal_handlers(proc):
    def handler(signum, _frame):
        try:
            proc.kill()
        except OSError:
            pass
        raise SystemExit(128 + signum)

    for signum in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
        signal.signal(signum, handler)


def parse_args(argv):
    parser = argparse.ArgumentParser(description="CodeyBox devin acp client shim")
    parser.add_argument("--binary", required=True)
    parser.add_argument("--cwd", default=None,
                        help="Session cwd; defaults to the shim's working directory")
    parser.add_argument("--prompt-file", required=True,
                        help="'-' reads the prompt tail from descriptor 0 "
                             "(the dispatch's framed stdin)")
    parser.add_argument("--model", default=None,
                        help="Passed verbatim to `devin acp --model`; never a fallback")
    parser.add_argument("--mode", default=None,
                        help="ACP session mode applied via session/set_mode before the prompt")
    return parser.parse_args(argv)


def read_prompt(path):
    if path == "-":
        # The prompt tail of the dispatch's stdin frame. Read the
        # already-open descriptor 0 — re-opening the exec wrapper's stdin
        # pipe by name (/dev/stdin) fails EACCES because the pipe was
        # created before the sandbox-user drop.
        #
        # Provenance bound: fd 0 is an anonymous pipe a same-uid in-VM peer
        # can append to via /proc/<pid>/fd/0 reopened O_WRONLY, so the
        # prompt bytes read here are agent-influenceable — never treat the
        # channel as operator-authentic. read() blocks until every write
        # end closes, so a held write end can also stall this read (an
        # already-bounded DoS: the dispatch carries the phase timeout).
        data = sys.stdin.buffer.read(MAX_PROMPT_BYTES + 1)
    else:
        if os.path.getsize(path) > MAX_PROMPT_BYTES:
            raise ValueError("prompt file exceeds %d bytes" % MAX_PROMPT_BYTES)
        with open(path, "rb") as handle:
            data = handle.read(MAX_PROMPT_BYTES + 1)
    if len(data) > MAX_PROMPT_BYTES:
        raise ValueError("prompt exceeds %d bytes" % MAX_PROMPT_BYTES)
    return data.decode("utf-8")


def main(argv=None):
    args = parse_args(argv)

    # Everything the CLI and its tool subprocesses write to fd 2 from here on
    # is folded into codeybox.stderr envelopes on stdout — a bare stderr line
    # can never reach the claimable envelope stream.
    stderr_relay = install_stderr_envelope_relay()
    try:
        return run(args)
    finally:
        # Best-effort drain: once the CLI and any grandchildren close their
        # copies of the pipe's write end the relay sees EOF; a lingering
        # holder just loses the un-emitted tail after the grace window.
        stderr_relay.join(timeout=2.0)


def run(args):
    try:
        prompt = read_prompt(args.prompt_file)
    except (OSError, ValueError) as exc:
        emit("fatal", stage="prompt", message="cannot read prompt file: %s" % exc)
        return EXIT_TURN_FAILED

    cwd = args.cwd or os.getcwd()

    env = dict(os.environ)
    # DEVIN_REFUSAL_FALLBACK switches to OTHER Devin models (paid) when the
    # provider refuses a request, and DEVIN_MODEL silently picks the session
    # model when no --model flag is passed. Never allow ambient config to
    # opt a dispatch into a model the operator did not configure.
    env.pop("DEVIN_REFUSAL_FALLBACK", None)
    env.pop("DEVIN_MODEL", None)

    spawn_argv = [args.binary, "acp"]
    if args.model:
        spawn_argv += ["--model", args.model]

    try:
        proc = subprocess.Popen(
            spawn_argv,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=None,  # inherits fd 2, which the relay retargeted to the envelope pipe
            cwd=cwd,
            env=env)
    except OSError as exc:
        emit("fatal", stage="spawn", message="cannot start devin acp: %s" % exc)
        return EXIT_TURN_FAILED

    install_signal_handlers(proc)
    try:
        return run_turn(AcpClient(proc), prompt, cwd, args.mode)
    except BrokenPipeError:
        emit("fatal", stage="turn",
             message="devin acp closed its stdin before the turn completed")
        return EXIT_TURN_FAILED
    except FrameTooLargeError:
        emit("fatal", stage="turn",
             message="devin acp emitted a frame larger than %d bytes" % MAX_FRAME_BYTES)
        return EXIT_TURN_FAILED
    finally:
        if proc.poll() is None:
            try:
                proc.stdin.close()
            except OSError:
                pass
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                try:
                    proc.kill()
                except OSError:
                    pass
        proc.wait()


if __name__ == "__main__":
    sys.exit(main())
