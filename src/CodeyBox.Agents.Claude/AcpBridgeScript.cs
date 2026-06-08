namespace CodeyBox.Agents.Claude;

/// <summary>
/// In-sandbox Node.js bridge that turns CodeyBox into an ACP-speaking "IDE"
/// for an in-sandbox <c>claude --ide</c> subprocess.
///
/// <para>Why this exists. The Agent Client Protocol is JSON-RPC 2.0 over a
/// connection-oriented transport; <c>claude --ide</c> discovers its peer via
/// the lockfile mechanism (<c>~/.claude/ide/&lt;port&gt;.lock</c>) and
/// connects to a WebSocket the "IDE" hosts. Our orchestrator runs on the
/// HOST and cannot expose a WebSocket directly visible to the in-sandbox
/// claude in a provider-portable way (process / bubblewrap / multipass each
/// have different network reachability). The pragmatic answer is a small
/// in-sandbox bridge: it hosts the WebSocket inside the sandbox (so claude
/// can connect via the lockfile), writes the lockfile, spawns
/// <c>claude --ide</c>, and pipes JSON-RPC frames between the in-sandbox
/// WebSocket and its own STDIO — which is the pipe we get from
/// <c>sandbox.ExecAsync</c>. All ACP traffic travels host ↔ bridge stdio ↔
/// in-VM WebSocket ↔ claude, with no extra network configuration.</para>
///
/// <para>Turn shape. Because <c>sandbox.ExecAsync</c> hands the bridge a
/// single stdin string, each TURN is exactly one bridge subprocess: the
/// host frames the queued envelopes (hello + initialize + session/new or
/// session/load + session/prompt) and writes them as stdin lines; the
/// bridge plays them in order, watches incoming ACP responses, and exits
/// cleanly the moment a <c>stopReason</c> response or a JSON-RPC error
/// arrives. Continuity across turns is the captured ACP session id, just
/// as <c>--resume</c> is for the print transport. Restarting claude per
/// turn does NOT lose cache warmth — ACP <c>session/load</c> reattaches to
/// the same logical session.</para>
///
/// <para>Wire format on the bridge's stdio: line-delimited JSON, one
/// envelope per line. Envelopes wrap the actual ACP JSON-RPC payload so
/// out-of-band lifecycle events ("claude exited", "lockfile written") do
/// not collide with the RPC stream.</para>
///
/// <para>Permission and question handling is configured at start via the
/// CodeyBox header: <c>{"type":"hello","autoApprovePermissions":true,
/// "autoAnswerQuestions":true}</c>. The bridge honours these flags by
/// auto-replying to <c>session/request_permission</c> RPCs and by emitting a
/// CodeyBox question event + a default answer when the agent asks anything
/// that would block — matching the existing <c>&lt;codeybox-question&gt;</c>
/// async convention so a headless turn never hangs on a human.</para>
/// </summary>
internal static class AcpBridgeScript
{
    /// <summary>
    /// Path inside the sandbox where the orchestrator materialises the
    /// bridge script before launching it. Lives under
    /// <c>~/.codeybox</c> rather than <c>.codeybox/</c> in the workspace so
    /// it survives between turns even when the workspace is a fresh checkout.
    /// </summary>
    public const string BridgeScriptPath = "$HOME/.codeybox/claude-acp-bridge.cjs";

    /// <summary>
    /// Maximum bridge wall-clock per turn. Hard cap on the bridge subprocess
    /// inside the sandbox so a wedged claude / wedged WebSocket cannot pin
    /// the worker forever — the bridge auto-exits, the worker observes the
    /// failure, and the configured fallback path (print transport) picks up
    /// the next turn. Conservatively long.
    /// </summary>
    public const int TurnTimeoutSeconds = 900;

    /// <summary>
    /// Verbatim bridge script. Node.js, self-contained (no npm install).
    /// Hosts an ACP WebSocket server on a random local port, writes the
    /// claude IDE lockfile, spawns <c>claude --ide</c>, and bridges
    /// JSON-RPC frames between the WebSocket and stdio.
    ///
    /// <para>Limited to the standard Node.js library so the script runs
    /// against the Node bundled with the claude install — no external deps
    /// to keep installed inside the baseline image.</para>
    /// </summary>
    public static readonly string Source =
        "#!/usr/bin/env node\n" +
        "'use strict';\n" +
        "// CodeyBox ACP bridge — see AcpBridgeScript.cs for protocol notes.\n" +
        "const fs = require('fs');\n" +
        "const path = require('path');\n" +
        "const os = require('os');\n" +
        "const http = require('http');\n" +
        "const crypto = require('crypto');\n" +
        "const { spawn } = require('child_process');\n" +
        "\n" +
        "function emit(env) {\n" +
        "  try { process.stdout.write(JSON.stringify(env) + '\\n'); } catch (_) {}\n" +
        "}\n" +
        "function fatal(msg, detail) {\n" +
        "  emit({ type: 'fatal', message: msg, detail: detail && String(detail) });\n" +
        "  shutdown(2);\n" +
        "}\n" +
        "\n" +
        "let config = { autoApprovePermissions: true, autoAnswerQuestions: true,\n" +
        "  claudeBinary: 'claude', claudeArgs: [],\n" +
        "  workingDirectory: process.cwd(), claudeEnv: {}, lockDir: null,\n" +
        "  turnTimeoutSeconds: " + TurnTimeoutSeconds + " };\n" +
        "let pending = [];\n" +
        "let server = null, port = 0, lockPath = null, authToken = null;\n" +
        "let claudeProc = null, claudeSocket = null, peerReady = false;\n" +
        "let shutdownStarted = false, turnDeadlineTimer = null;\n" +
        "\n" +
        "// Minimal RFC6455 server (no external dep): handshake + text frames only.\n" +
        "function wsAccept(key) {\n" +
        "  return crypto.createHash('sha1')\n" +
        "    .update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');\n" +
        "}\n" +
        "function sendFrame(sock, payload) {\n" +
        "  const data = Buffer.from(payload, 'utf8');\n" +
        "  const len = data.length;\n" +
        "  let header;\n" +
        "  if (len < 126) { header = Buffer.from([0x81, len]); }\n" +
        "  else if (len < 65536) {\n" +
        "    header = Buffer.alloc(4);\n" +
        "    header[0] = 0x81; header[1] = 126;\n" +
        "    header.writeUInt16BE(len, 2);\n" +
        "  } else {\n" +
        "    header = Buffer.alloc(10);\n" +
        "    header[0] = 0x81; header[1] = 127;\n" +
        "    header.writeBigUInt64BE(BigInt(len), 2);\n" +
        "  }\n" +
        "  try { sock.write(Buffer.concat([header, data])); } catch (_) {}\n" +
        "}\n" +
        "function bindSocket(sock) {\n" +
        "  claudeSocket = sock; peerReady = true;\n" +
        "  let buf = Buffer.alloc(0);\n" +
        "  sock.on('data', (chunk) => {\n" +
        "    buf = Buffer.concat([buf, chunk]);\n" +
        "    while (true) {\n" +
        "      if (buf.length < 2) return;\n" +
        "      const b1 = buf[0]; const b2 = buf[1];\n" +
        "      const op = b1 & 0x0f; const masked = (b2 & 0x80) !== 0;\n" +
        "      let len = b2 & 0x7f; let offset = 2;\n" +
        "      if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); offset = 4; }\n" +
        "      else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); offset = 10; }\n" +
        "      let maskStart = -1;\n" +
        "      if (masked) { if (buf.length < offset + 4) return; maskStart = offset; offset += 4; }\n" +
        "      if (buf.length < offset + len) return;\n" +
        "      let payload = buf.slice(offset, offset + len);\n" +
        "      if (masked) {\n" +
        "        const mask = buf.slice(maskStart, maskStart + 4);\n" +
        "        const unmasked = Buffer.alloc(len);\n" +
        "        for (let i = 0; i < len; i++) unmasked[i] = payload[i] ^ mask[i % 4];\n" +
        "        payload = unmasked;\n" +
        "      }\n" +
        "      buf = buf.slice(offset + len);\n" +
        "      if (op === 0x8) { try { sock.end(); } catch (_) {} return; }\n" +
        "      if (op === 0x1 || op === 0x2) onFrame(payload.toString('utf8'));\n" +
        "    }\n" +
        "  });\n" +
        "  sock.on('error', () => {});\n" +
        "  sock.on('close', () => { claudeSocket = null; peerReady = false;\n" +
        "    emit({ type: 'peer_closed' }); maybeFinish(); });\n" +
        "  emit({ type: 'peer_connected' });\n" +
        "  drainPending();\n" +
        "}\n" +
        "function onFrame(text) {\n" +
        "  let msg = null;\n" +
        "  try { msg = JSON.parse(text); } catch (_) { return; }\n" +
        "  if (msg && typeof msg === 'object' && msg.method) {\n" +
        "    if (config.autoApprovePermissions &&\n" +
        "        (msg.method === 'session/request_permission' || msg.method === 'permission/request')) {\n" +
        "      const id = msg.id;\n" +
        "      if (id !== undefined) {\n" +
        "        sendFrame(claudeSocket, JSON.stringify({\n" +
        "          jsonrpc: '2.0', id,\n" +
        "          result: { outcome: { outcome: 'selected', optionId: 'allow_once' } }\n" +
        "        }));\n" +
        "      }\n" +
        "      emit({ type: 'permission_auto_granted', method: msg.method });\n" +
        "      return;\n" +
        "    }\n" +
        "    if (config.autoAnswerQuestions &&\n" +
        "        (msg.method === 'session/request_input' || msg.method === 'input/request')) {\n" +
        "      const id = msg.id;\n" +
        "      if (id !== undefined) {\n" +
        "        sendFrame(claudeSocket, JSON.stringify({\n" +
        "          jsonrpc: '2.0', id,\n" +
        "          result: { value: '<codeybox-question>: agent asked a blocking question; default applied, continuing.' }\n" +
        "        }));\n" +
        "      }\n" +
        "      emit({ type: 'question_auto_answered', method: msg.method });\n" +
        "      return;\n" +
        "    }\n" +
        "  }\n" +
        "  emit({ type: 'acp_recv', payload: msg });\n" +
        "  if (msg && typeof msg === 'object') {\n" +
        "    if (msg.error) { emit({ type: 'turn_error', error: msg.error }); shutdown(0); return; }\n" +
        "    if (msg.result && (msg.result.stopReason || msg.result.stop_reason)) {\n" +
        "      emit({ type: 'turn_complete', stopReason: msg.result.stopReason || msg.result.stop_reason });\n" +
        "      shutdown(0); return;\n" +
        "    }\n" +
        "  }\n" +
        "}\n" +
        "function drainPending() {\n" +
        "  while (peerReady && pending.length > 0) {\n" +
        "    const next = pending.shift();\n" +
        "    sendFrame(claudeSocket, JSON.stringify(next));\n" +
        "    emit({ type: 'acp_sent', id: next.id, method: next.method });\n" +
        "  }\n" +
        "}\n" +
        "function maybeFinish() {\n" +
        "  if (!shutdownStarted && claudeProc && claudeProc.exitCode !== null && !peerReady) {\n" +
        "    shutdown(0);\n" +
        "  }\n" +
        "}\n" +
        "\n" +
        "function startServer(cb) {\n" +
        "  const httpServer = http.createServer((req, res) => { res.statusCode = 426; res.end(); });\n" +
        "  httpServer.on('upgrade', (req, sock) => {\n" +
        "    const authHeader = (req.headers['x-claude-code-ide-authorization'] ||\n" +
        "                       req.headers['authorization'] || '').toString();\n" +
        "    if (authToken && authHeader !== authToken && !authHeader.endsWith(authToken)) {\n" +
        "      sock.end('HTTP/1.1 401 Unauthorized\\r\\n\\r\\n');\n" +
        "      return;\n" +
        "    }\n" +
        "    const key = req.headers['sec-websocket-key'];\n" +
        "    if (!key) { sock.end('HTTP/1.1 400 Bad Request\\r\\n\\r\\n'); return; }\n" +
        "    const accept = wsAccept(key);\n" +
        "    sock.write('HTTP/1.1 101 Switching Protocols\\r\\n' +\n" +
        "               'Upgrade: websocket\\r\\n' +\n" +
        "               'Connection: Upgrade\\r\\n' +\n" +
        "               'Sec-WebSocket-Accept: ' + accept + '\\r\\n\\r\\n');\n" +
        "    bindSocket(sock);\n" +
        "  });\n" +
        "  httpServer.listen(0, '127.0.0.1', () => {\n" +
        "    const addr = httpServer.address();\n" +
        "    port = addr.port; server = httpServer; cb();\n" +
        "  });\n" +
        "  httpServer.on('error', (e) => fatal('http_listen_failed', e && e.message));\n" +
        "}\n" +
        "\n" +
        "function writeLockfile(cb) {\n" +
        "  const baseDir = config.lockDir || path.join(os.homedir(), '.claude', 'ide');\n" +
        "  try { fs.mkdirSync(baseDir, { recursive: true, mode: 0o700 }); } catch (e) {\n" +
        "    return fatal('lockdir_create_failed', e && e.message);\n" +
        "  }\n" +
        "  authToken = crypto.randomBytes(24).toString('hex');\n" +
        "  lockPath = path.join(baseDir, port + '.lock');\n" +
        "  const lockBody = JSON.stringify({\n" +
        "    pid: process.pid,\n" +
        "    workspaceFolders: [config.workingDirectory],\n" +
        "    ideName: 'CodeyBox',\n" +
        "    transport: 'ws',\n" +
        "    runningInWindows: false,\n" +
        "    authToken,\n" +
        "    url: 'ws://127.0.0.1:' + port,\n" +
        "  });\n" +
        "  try {\n" +
        "    const fd = fs.openSync(lockPath, 'w', 0o600);\n" +
        "    fs.writeSync(fd, lockBody); fs.closeSync(fd);\n" +
        "  } catch (e) { return fatal('lockfile_write_failed', e && e.message); }\n" +
        "  emit({ type: 'ready', port, lockPath, workspaceFolders: [config.workingDirectory] });\n" +
        "  cb();\n" +
        "}\n" +
        "\n" +
        "function spawnClaude() {\n" +
        "  const args = ['--ide'].concat(config.claudeArgs);\n" +
        "  let env = Object.assign({}, process.env, config.claudeEnv);\n" +
        "  try {\n" +
        "    claudeProc = spawn(config.claudeBinary, args, {\n" +
        "      cwd: config.workingDirectory, env, stdio: ['ignore', 'pipe', 'pipe'],\n" +
        "    });\n" +
        "  } catch (e) { return fatal('claude_spawn_failed', e && e.message); }\n" +
        "  claudeProc.stdout.on('data', (d) =>\n" +
        "    emit({ type: 'claude_stdout', text: d.toString('utf8') }));\n" +
        "  claudeProc.stderr.on('data', (d) =>\n" +
        "    emit({ type: 'claude_stderr', text: d.toString('utf8') }));\n" +
        "  claudeProc.on('exit', (code, signal) => {\n" +
        "    emit({ type: 'claude_exit', code, signal: signal && String(signal) });\n" +
        "    maybeFinish();\n" +
        "  });\n" +
        "  claudeProc.on('error', (e) =>\n" +
        "    emit({ type: 'claude_error', message: e && e.message }));\n" +
        "}\n" +
        "\n" +
        "function shutdown(code) {\n" +
        "  if (shutdownStarted) return; shutdownStarted = true;\n" +
        "  if (turnDeadlineTimer) { clearTimeout(turnDeadlineTimer); turnDeadlineTimer = null; }\n" +
        "  try { if (claudeProc && claudeProc.exitCode === null) claudeProc.kill('SIGTERM'); } catch (_) {}\n" +
        "  try { if (server) server.close(); } catch (_) {}\n" +
        "  try { if (lockPath) fs.unlinkSync(lockPath); } catch (_) {}\n" +
        "  setTimeout(() => process.exit(code || 0), 100);\n" +
        "}\n" +
        "process.on('SIGTERM', () => shutdown(0));\n" +
        "process.on('SIGINT', () => shutdown(0));\n" +
        "\n" +
        "let stdinBuf = '';\n" +
        "process.stdin.on('data', (d) => {\n" +
        "  stdinBuf += d.toString('utf8');\n" +
        "  while (true) {\n" +
        "    const nl = stdinBuf.indexOf('\\n');\n" +
        "    if (nl < 0) return;\n" +
        "    const line = stdinBuf.slice(0, nl).trim();\n" +
        "    stdinBuf = stdinBuf.slice(nl + 1);\n" +
        "    if (!line) continue;\n" +
        "    let msg = null;\n" +
        "    try { msg = JSON.parse(line); } catch (_) { continue; }\n" +
        "    if (!msg || typeof msg !== 'object') continue;\n" +
        "    if (msg.type === 'hello') {\n" +
        "      config = Object.assign(config, msg);\n" +
        "      turnDeadlineTimer = setTimeout(\n" +
        "        () => { emit({ type: 'turn_timeout' }); shutdown(0); },\n" +
        "        Math.max(10, config.turnTimeoutSeconds) * 1000);\n" +
        "      startServer(() => writeLockfile(() => spawnClaude()));\n" +
        "    } else if (msg.type === 'acp_send') {\n" +
        "      pending.push(msg.payload);\n" +
        "      drainPending();\n" +
        "    } else if (msg.type === 'shutdown') {\n" +
        "      shutdown(0);\n" +
        "    }\n" +
        "  }\n" +
        "});\n" +
        "process.stdin.on('end', () => {});\n" +
        "emit({ type: 'bridge_started', pid: process.pid });\n";
}
