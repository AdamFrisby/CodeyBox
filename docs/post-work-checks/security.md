# Post-work check: Security

After your functional work is done, review your changes for the issues below — aligned to
OWASP ASVS 5.0, OWASP Top 10 (2021), the CWE Top 25, and LLM/AI-specific attack patterns.
An automated security reviewer applies the same criteria. Read every changed file fully;
read calling sites of any new public function to understand the trust boundary. Fix real
issues without expanding the original task's scope. Where the diff has nothing relevant to
a category, skip it.

## 1. Injection
- **SQL**: any user-influenced value reaching a query without parameterised binding
  (incl. ORM `raw`/`execute`, string concat in stored-proc callers, sprintf into queries).
- **Command / shell**: `ProcessStartInfo` / `subprocess` / `child_process.exec` /
  `Runtime.exec` / `system` / `os.system` / `popen` / backticks with `shell=true` or
  concatenated user input; shell metacharacters reaching argv (find -exec, xargs).
- **LDAP / NoSQL / XPath**: user input concatenated into filters/queries (Mongo `$where`
  strings, etree.find with user xpath).
- **Template / SSTI**: user input concatenated into Jinja/Handlebars/Razor/ERB/Liquid
  rather than passed as parameters.
- **Header injection**: user input in response headers without CRLF stripping.
- **Code execution**: `eval`, `exec`, `Function()`, `Assembly.Load(byte[])` on
  user-influenced data.

## 2. Output encoding / XSS
- `innerHTML` / `dangerouslySetInnerHTML` / `@Html.Raw` / `document.write` with
  non-static content.
- DOM XSS sinks: `location.hash`, `document.referrer`, `postMessage` data, `window.name`
  reaching `innerHTML` / `src` / `href` / `onclick`.
- Reflected XSS: params written into an HTML response without contextual encoding.

## 3. Validation & business logic
- Negative quantities, integer overflow, sign flips on monetary/count fields;
  off-by-one on slice/range bounds.
- TOCTOU: check-then-act without a lock/atomic op.
- Replay: state-changing ops missing a nonce / idempotency key where one is plausibly
  required.
- Time-of-action: deletes/refunds/status changes that don't revalidate ownership.

## 4. API / web service
- State-changing handlers responding to GET.
- Missing/relaxed CORS (`*`) combined with credentialed requests.
- Mass assignment: binding the whole request body to a model with server-controlled
  fields (role, isAdmin, ownerId, balance).
- Missing pagination / max-page-size on list endpoints (DoS).

## 5. File handling
- Path traversal: user-influenced segment reaching a filesystem path without
  canonicalisation AND containment-under-base (incl. `Path.Combine` with user input).
- Unrestricted upload: missing extension allowlist + MIME sniff + size limit.
- Insecure deserialisation: `pickle.loads`, java `ObjectInputStream`, .NET
  `BinaryFormatter`, `yaml.load`, PHP `unserialize` on untrusted input.
- XML / XXE: parsers without DTDs and external entities explicitly disabled.

## 6. Authentication
- New endpoints lacking auth when surrounding endpoints require it.
- Weak password storage: MD5/SHA-1/plain SHA-2 (no KDF), missing salt, work factors below
  today's recommendations (bcrypt < 12, argon2id memory < 64 MiB).
- Predictable session IDs (`Random` instead of cryptographic RNG).
- Auth cookies missing HttpOnly / Secure / SameSite.
- Hardcoded test/master tokens or backdoor users.

## 7. Sessions / tokens
- JWT with `none` algorithm accepted; signature not verified; alg autoselected
  (algorithm-confusion).
- Session fixation (id not rotated on auth state change).
- Long-lived bearer tokens in localStorage where httpOnly cookies were viable.

## 8. Authorization
- IDOR: a route accepts an id and acts on the object without verifying caller ownership.
- Vertical/horizontal privilege escalation; missing role/policy check on privileged
  endpoints.
- Path-based bypass (case, trailing slash, URL-encoding tricks).

## 9. OAuth / OIDC
- Implicit flow for new clients; public clients without PKCE.
- `redirect_uri` matched without exact comparison.
- `id_token` consumed without iss/aud/exp/nonce checks.

## 10. Cryptography
- MD5/SHA-1 for security; DES/3DES/RC4/ECB-mode AES.
- Hardcoded keys/IVs/salts; key/IV reuse.
- Missing AEAD where confidentiality + integrity is needed.
- Insufficient key length (RSA < 2048, ECC < 256, AES < 128).
- **Custom / hand-rolled crypto** — use established libraries.
- Insecure random (`Random`/`Math.random`/`rand()`) for security material.

## 11. Secure communication
- TLS verification disabled (`verify=False`, callback returning true,
  `NODE_TLS_REJECT_UNAUTHORIZED=0`, Go `InsecureSkipVerify`).
- Plain HTTP for sensitive content; TLS < 1.2; disabled hostname verification.

## 12. Configuration
- Debug/dev mode in shipping config; verbose error pages / stack traces to clients.
- CORS `*` with `Allow-Credentials`; default credentials in code or seed data.
- Missing security headers introduced by this change (HSTS, CSP, X-Content-Type-Options).

## 13. Data protection
- **Hardcoded secrets**: AWS keys (`AKIA…`), Slack/GitHub/Stripe/OpenAI tokens, private
  keys.
- High-entropy strings assigned to `*_KEY` / `*_SECRET` / `*_TOKEN` / `*_PASSWORD`.
- Secrets reaching logs/metrics/telemetry/error responses.
- PII sent to third parties not previously receiving it, or stored without minimisation.

## 14. SSRF
- User-controlled URL passed to an HTTP client without an allowlist.
- Cloud metadata endpoints (`169.254.169.254`, `metadata.google.internal`),
  localhost/loopback, internal ranges.
- Resolve once and use the IP (DNS-rebinding-resistant); re-validate on redirect.

## 15. Resource exhaustion / DoS
- Unbounded loops/recursion driven by user input.
- Regex DoS (nested quantifiers, `.*` before a delimiter) on user input.
- Compression / depth bombs without limits.
- Missing request/body/query size limits or rate limiting on expensive operations.

## 16. Logging & error handling
- Stack traces / sensitive data (passwords, tokens, PII, session IDs) in logs or
  responses.
- Log injection (unescaped newlines in user strings).
- Catch-all swallowing exceptions; missing audit log for security-relevant events.

## 17. Memory safety (unsafe / native / FFI)
- Manual buffer arithmetic in unsafe blocks; integer overflow before allocation.
- Use-after-free / use-after-dispose; marshalling without length validation.

## 18. Race conditions & concurrency
- Shared mutable state without synchronization; check-then-act on external resources.
- Missing transactions around multi-step business invariants.

## 19. Dependencies
- New dependencies at suspicious versions (typosquats); transitive known-vulnerable libs
  (only if you can identify a specific CVE).

## 20. LLM / AI specific
- User input concatenated into agent prompts where the agent has tool access
  (prompt-injection).
- Stored injection: user-saved content later read by a tool-enabled LLM.
- LLM-controlled values reaching exec/shell/file/network ops without an allowlist or human
  approval.

## 21. Business logic
- Reversed-sign math on financial flows; feature-flag bypass using stale state.
- Administrative actions reachable by id manipulation; actions reversible past their
  natural window without a check.
