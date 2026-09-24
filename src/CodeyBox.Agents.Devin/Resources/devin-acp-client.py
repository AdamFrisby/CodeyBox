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
2 for any protocol, spawn, or turn-level failure. The CLI's own stderr is
inherited, so `Error: ...` diagnostics still reach the exec stderr untouched.
"""

from __future__ import annotations

import argparse
import json
import os
import signal
import subprocess
import sys

ENVELOPE_TYPE = "devin.acp"
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


def emit(event, **fields):
    envelope = {"type": ENVELOPE_TYPE, "event": event}
    envelope.update(fields)
    sys.stdout.write(json.dumps(envelope) + "\n")
    sys.stdout.flush()


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
        self.next_id = 0
        self.pending = {}

    def request(self, stage, method, params):
        self.next_id += 1
        request_id = self.next_id
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
            final_text_chars = handle_notification(frame, final_text_parts, final_text_chars)
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
            client.request("session/new", "session/new",
                           {"cwd": cwd, "mcpServers": []})
        elif stage == "session/new":
            session_id = result.get("sessionId")
            if not isinstance(session_id, str) or not session_id:
                emit("fatal", stage="session/new",
                     message="session/new response did not carry a sessionId")
                return EXIT_TURN_FAILED
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
                 usage=result.get("usage"),
                 finalText="".join(final_text_parts) or None)
            return 0
        else:
            emit("protocol_error", message="response for unhandled stage", stage=stage)


def send_prompt(client, session_id, prompt):
    client.request("prompt", "session/prompt", {
        "sessionId": session_id,
        "prompt": [{"type": "text", "text": prompt}],
    })
    emit("prompt_sent", promptBytes=len(prompt.encode("utf-8")))


def handle_notification(frame, final_text_parts, final_text_chars):
    method = frame.get("method")
    params = frame.get("params")
    if method == "session/update" and isinstance(params, dict):
        update = params.get("update")
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
    parser.add_argument("--prompt-file", required=True)
    parser.add_argument("--model", default=None,
                        help="Passed verbatim to `devin acp --model`; never a fallback")
    parser.add_argument("--mode", default=None,
                        help="ACP session mode applied via session/set_mode before the prompt")
    return parser.parse_args(argv)


def read_prompt(path):
    if os.path.getsize(path) > MAX_PROMPT_BYTES:
        raise ValueError("prompt file exceeds %d bytes" % MAX_PROMPT_BYTES)
    with open(path, "r", encoding="utf-8") as handle:
        return handle.read()


def main(argv=None):
    args = parse_args(argv)

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
            stderr=None,  # inherit: CLI `Error: ...` lines reach exec stderr untouched
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
