using System.Text.RegularExpressions;
using CodeyBox.Audit.Llm;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Audit.Presets;

/// <summary>
/// Built-in audit-type presets. Cross-language: applicable regardless of
/// what the project is written in.
///
/// Mix of capabilities:
///   - <c>security</c>: tool-only (gitleaks, semgrep) + comprehensive LLM
///     review aligned to OWASP ASVS 5.0 + Top 10 + LLM-specific checks.
///   - <c>architecture</c>, <c>quality</c>, <c>completeness</c>: LLM-only.
///   - <c>cheating</c>: deterministic diff-pattern matcher + LLM
///     "did you take shortcuts?" reviewer.
///   - <c>tests</c>: deterministic no-op assertion patterns + LLM
///     "are these tests meaningful?" reviewer.
/// </summary>
internal static class AuditTypePresets
{
    public static void Register(PresetCatalog catalog)
    {
        catalog.RegisterAuditType("security", ctx =>
        [
            Shell("security:gitleaks", "gitleaks", "detect", "--source", ".", "--no-banner", "--no-color"),
            Shell("security:semgrep", "semgrep", "--config", "auto", "--error", "--quiet"),
            new LlmReviewAuditor(new LlmReviewAuditorOptions
            {
                Name = "security:llm-review",
                Agent = ctx.Agent,
                ReviewFocus = SecurityReviewFocus,
            }),
        ]);

        catalog.RegisterAuditType("architecture", ctx =>
        [
            new LlmReviewAuditor(new LlmReviewAuditorOptions
            {
                Name = "architecture:llm-review",
                Agent = ctx.Agent,
                ReviewFocus =
                    "- Loose-coupling violations: concrete types appearing in cross-module method signatures where an interface exists\n" +
                    "- New direct dependencies that should have gone through an existing abstraction\n" +
                    "- God objects / classes accumulating unrelated responsibilities\n" +
                    "- Layering violations (e.g. domain code referencing infrastructure)\n" +
                    "- Public APIs that leak internal types",
            }),
        ]);

        catalog.RegisterAuditType("quality", ctx =>
        [
            new LlmReviewAuditor(new LlmReviewAuditorOptions
            {
                Name = "quality:llm-review",
                Agent = ctx.Agent,
                ReviewFocus =
                    "- Dead code (unreachable branches, unused functions/imports)\n" +
                    "- Magic numbers and unexplained literal constants\n" +
                    "- Unclear or misleading names; abbreviations a new reader couldn't expand\n" +
                    "- Error handling at boundaries that swallows or rethrows incorrectly\n" +
                    "- Duplicated logic that should be a single helper\n" +
                    "- Comments that describe WHAT instead of WHY",
            }),
        ]);

        catalog.RegisterAuditType("completeness", ctx =>
        [
            new LlmReviewAuditor(new LlmReviewAuditorOptions
            {
                Name = "completeness:llm-review",
                Agent = ctx.Agent,
                ReviewFocus =
                    "- TODO / FIXME / XXX markers added in this change\n" +
                    "- New functionality without corresponding tests\n" +
                    "- Half-finished implementations (functions that return early, swallowed branches)\n" +
                    "- Public functions whose docstrings/comments describe behaviour the code doesn't implement\n" +
                    "- Test files that were renamed or deleted instead of fixed",
            }),
        ]);

        // Cheating: detect agent shortcuts. Two layers — a deterministic
        // diff-pattern auditor for the obvious markers, and an LLM reviewer
        // for the subtle cases (faked logic, hardcoded returns, etc.).
        catalog.RegisterAuditType("cheating", ctx =>
        [
            new DiffPatternAuditor(new DiffPatternAuditorOptions
            {
                Name = "cheating:suppression-patterns",
                Patterns = CheatingPatterns,
            }),
            new LlmReviewAuditor(new LlmReviewAuditorOptions
            {
                Name = "cheating:llm-review",
                Agent = ctx.Agent,
                ReviewFocus =
                    "Compare the diff against the original task. Look for shortcuts the agent took rather than fully solving the problem:\n" +
                    "- Stubbed or trivially-faked implementations (NotImplementedException, hardcoded returns where logic was requested)\n" +
                    "- Disabled compiler/linter/type-checker warnings instead of fixing the underlying issue\n" +
                    "- Overly broad exception catches that swallow errors\n" +
                    "- Skipped or removed failing tests rather than fixing them\n" +
                    "- Commented-out code that should be active\n" +
                    "- 'Mock' or 'temporary' implementations marked as such\n" +
                    "- Functions that return success without actually doing the work\n" +
                    "Any of these should be flagged as Error.",
            }),
        ]);

        // Tests: deterministic no-op assertion patterns + LLM meaningfulness
        // reviewer. Catches both the obvious bad-test patterns and the
        // subtle ones (test mirrors implementation; pure-mock tests).
        catalog.RegisterAuditType("tests", ctx =>
        [
            new DiffPatternAuditor(new DiffPatternAuditorOptions
            {
                Name = "tests:no-op-assertions",
                Patterns = NoOpTestPatterns,
            }),
            new LlmReviewAuditor(new LlmReviewAuditorOptions
            {
                Name = "tests:meaningfulness-review",
                Agent = ctx.Agent,
                ReviewFocus = TestsReviewFocus,
            }),
        ]);
    }

    private static IAuditor Shell(string name, params string[] argv)
        => new ShellCommandAuditor(new ShellCommandAuditorOptions { Name = name, Argv = argv });

    // --- Cheating patterns ---------------------------------------------------

    private static readonly IReadOnlyList<DiffPattern> CheatingPatterns =
    [
        // TypeScript / JavaScript
        Pat(@"@ts-ignore|@ts-nocheck|@ts-expect-error", "TypeScript type-check suppression"),
        Pat(@"eslint-disable(?:-next-line|-line)?", "ESLint rule disabled inline"),
        Pat(@"tslint:disable", "TSLint rule disabled inline"),

        // Python
        Pat(@"#\s*type:\s*ignore", "Python type-check suppression (# type: ignore)"),
        Pat(@"#\s*noqa", "Python lint suppression (# noqa)"),
        Pat(@"@pytest\.mark\.skip|@unittest\.skip", "Skipped Python test"),

        // C#
        Pat(@"#pragma\s+warning\s+disable", "C# warning suppression pragma"),
        Pat(@"\[SuppressMessage\(", "C# message suppression attribute"),

        // Rust
        Pat(@"#\[allow\(", "Rust lint suppression"),

        // Go
        Pat(@"//\s*nolint", "Go golangci-lint suppression"),

        // Skipped tests (cross-language JS/TS test frameworks)
        Pat(@"\b(?:it|describe|test)\.skip\s*\(|\bxit\s*\(|\bxdescribe\s*\(", "Skipped test (jest/mocha/vitest)"),

        // Stubbed implementations
        Pat(@"throw\s+new\s+NotImplementedException", "C# / Java stubbed implementation"),
        Pat(@"raise\s+NotImplementedError", "Python stubbed implementation"),
        Pat(@"\bunimplemented!\s*\(\s*\)|\btodo!\s*\(\s*\)", "Rust stub macro (unimplemented!/todo!)"),
        Pat(@"panic\(""(?:not implemented|TODO|unimplemented)""\)", "Go stub panic"),

        // TODO/FIXME with implementation intent (warning, not error — completeness preset is the strict one)
        Pat(@"TODO:\s*implement|FIXME:\s*implement", "TODO marker for missing implementation", AuditSeverity.Warning),
    ];

    // --- No-op test patterns -------------------------------------------------

    /// <summary>
    /// Patterns that catch deterministically-bad test assertions: ones that
    /// can never fail, or compare a value with itself. These are an
    /// indicator of "writing tests to make the suite green" rather than
    /// "writing tests to catch bugs."
    /// </summary>
    private static readonly IReadOnlyList<DiffPattern> NoOpTestPatterns =
    [
        // Python
        Pat(@"^\s*assert\s+True\s*$", "assert True (no-op assertion)"),
        Pat(@"^\s*assert\s+1\s*==\s*1\s*$", "assert 1 == 1 (trivially-true)"),
        Pat(@"^\s*assert\s+not\s+False\s*$", "assert not False (no-op)"),
        Pat(@"^\s*pass\s*#\s*test\b", "pass-only test body", AuditSeverity.Warning),

        // .NET (xUnit / NUnit / MSTest)
        Pat(@"\bAssert\.True\s*\(\s*true\s*[,)]", "Assert.True(true) (no-op)"),
        Pat(@"\bAssert\.IsTrue\s*\(\s*true\s*[,)]", "Assert.IsTrue(true) (no-op)"),
        Pat(@"\bAssert\.False\s*\(\s*false\s*[,)]", "Assert.False(false) (no-op)"),
        Pat(@"\bAssert\.Equal\s*\(\s*(\w+)\s*,\s*\1\s*\)", "Assert.Equal(x, x) (no-op)"),
        Pat(@"\bAssert\.AreEqual\s*\(\s*(\w+)\s*,\s*\1\s*\)", "Assert.AreEqual(x, x) (no-op)"),
        Pat(@"\bAssert\.That\s*\(\s*true\b", "Assert.That(true, ...) (no-op)"),

        // JavaScript / TypeScript (jest / mocha / vitest)
        Pat(@"\bexpect\s*\(\s*true\s*\)\s*\.\s*toBe\s*\(\s*true\s*\)", "expect(true).toBe(true) (no-op)"),
        Pat(@"\bexpect\s*\(\s*1\s*\)\s*\.\s*toBe\s*\(\s*1\s*\)", "expect(1).toBe(1) (no-op)"),
        Pat(@"\bexpect\s*\(\s*(\w+)\s*\)\s*\.\s*toBe\s*\(\s*\1\s*\)", "expect(x).toBe(x) (no-op)"),
        Pat(@"\bexpect\s*\(\s*(\w+)\s*\)\s*\.\s*toEqual\s*\(\s*\1\s*\)", "expect(x).toEqual(x) (no-op)"),

        // Go (testify / std testing)
        Pat(@"\bassert\.True\s*\(\s*t\s*,\s*true\s*[,)]", "assert.True(t, true) (no-op)"),
        Pat(@"\bassert\.Equal\s*\(\s*t\s*,\s*(\w+)\s*,\s*\1\s*\)", "assert.Equal(t, x, x) (no-op)"),

        // Rust (std assertions)
        Pat(@"^\s*assert!\s*\(\s*true\s*\)", "assert!(true) (no-op)"),
        Pat(@"^\s*assert_eq!\s*\(\s*(\w+)\s*,\s*\1\s*\)", "assert_eq!(x, x) (no-op)"),
    ];

    // --- Security review prompt ----------------------------------------------

    /// <summary>
    /// Comprehensive security review checklist for the LLM auditor, aligned
    /// to OWASP ASVS 5.0 chapters where they apply to code review, plus
    /// OWASP Top 10 (2021), CWE Top 25, and LLM-specific issues.
    ///
    /// Organised by category. Each line is a concrete pattern to look for,
    /// not a vague principle. The reviewer is expected to hit each category
    /// and emit findings keyed to specific lines in the diff.
    /// </summary>
    private const string SecurityReviewFocus = """
        You are performing a security code review aligned to OWASP ASVS 5.0,
        OWASP Top 10 (2021), the CWE Top 25, and current LLM/AI-specific
        attack patterns. Read every changed file fully; read calling sites
        of any new public function to understand the trust boundary. Cite
        path:line in the location field of each finding. Do not reproduce
        the diff in your output.

        Severity guidance:
          - Error: confirmed vulnerability with a clear exploitation path
            given typical inputs / config. Treat any of the patterns below
            as Error unless you can show why this specific use is safe.
          - Warning: suspicious pattern that requires human review.
          - Info: hardening recommendation or defence-in-depth suggestion.

        Walk through every category. Where the diff has nothing relevant,
        skip silently — do not produce noise findings.

        # 1. Injection (ASVS V1, V2; OWASP A03)
        - SQL: any user-influenced value reaching a SQL string without
          parameterised binding. Includes ORM 'raw' / 'execute' methods,
          string concatenation in stored-procedure callers, sprintf-style
          format into queries.
        - Command / shell: ProcessStartInfo / subprocess / child_process.exec
          / Runtime.exec / system / os.system / popen / IO.popen / `backticks`
          with shell=true OR with concatenated user input. Includes shell
          metacharacters reaching argv even in array form when the binary
          interprets them (find -exec, xargs).
        - LDAP / NoSQL / XPath: user input concatenated into search filters
          or queries. Mongo `$where` accepting strings, etree.find with user
          xpath, search filter strings.
        - Template / SSTI: user input concatenated into Jinja, Handlebars,
          Razor, ERB, Liquid template strings (rather than passed as
          parameters).
        - Header injection: user input in HTTP response headers (Set-Cookie,
          Location, Content-Disposition) without CRLF stripping.
        - Code execution: eval, exec, Function(), ScriptEngine.eval,
          Reflection.Emit, Assembly.Load(byte[]) on user-influenced data.

        # 2. Output encoding / XSS (ASVS V1; OWASP A03)
        - innerHTML / outerHTML / dangerouslySetInnerHTML / @Html.Raw /
          document.write / document.writeln / setHTML with non-static content.
        - DOM XSS sinks: location.hash, document.referrer, postMessage data,
          window.name reaching innerHTML / src / href / onclick.
        - Reflected XSS: query / form params written into an HTML response
          without contextual encoding.
        - Mixed-context encoding: HTML-encoding inside an attribute that
          allows JS (onclick, href="javascript:").

        # 3. Validation and business logic (ASVS V2; OWASP A04)
        - Negative quantities, integer overflow, sign flips on monetary or
          count fields. Off-by-one on slice/range bounds.
        - TOCTOU: check-then-act without a lock or atomic operation
          (stat-then-open, exists-then-create, balance-check-then-debit).
        - Replay: state-changing operations missing a nonce / idempotency
          key where one is plausibly required.
        - Time-of-action: deletes, refunds, status changes that don't
          revalidate ownership at the moment of action.

        # 4. API / web service (ASVS V4; OWASP A04)
        - HTTP method choice: state-changing handlers responding to GET.
        - Missing or relaxed CORS (Access-Control-Allow-Origin: *) combined
          with credentialed requests.
        - Mass assignment: binding the entire request body to a model that
          includes server-controlled fields (role, isAdmin, ownerId,
          createdAt, balance).
        - Missing pagination / max-page-size on list endpoints (DoS).

        # 5. File handling (ASVS V5; OWASP A04)
        - Path traversal: any user-influenced segment reaching a filesystem
          path without canonicalisation AND containment-under-base check.
          Includes Path.Combine where one piece is user input.
        - Unrestricted upload: missing extension allowlist + MIME sniff +
          size limit + storing under a path the user can later request.
        - Symlink races: writing through a path without O_NOFOLLOW or
          equivalent in directories the user can influence.
        - Insecure deserialisation: pickle.loads, java ObjectInputStream,
          .NET BinaryFormatter / NetDataContractSerializer / SoapFormatter,
          YAML.unsafe_load / yaml.load(stream), Marshal.load, PHP unserialize
          on untrusted input.
        - XML / XXE: XmlReader / DocumentBuilder / SAXParser / etree.parse
          / lxml without explicitly disabling DTDs and external entities.
          XmlResolver = null in .NET; setFeature("disallow-doctype-decl") in
          Java; resolve_entities=False in lxml.

        # 6. Authentication (ASVS V6; OWASP A07)
        - New endpoints lacking authentication when surrounding endpoints
          require it.
        - Weak password storage: MD5, SHA-1, plain SHA-256/SHA-512 (no KDF),
          missing per-user salt, work factors below today's recommendations
          (bcrypt < 12, argon2id memory < 64 MiB, scrypt N < 2^14).
        - Predictable session IDs (Random instead of cryptographic RNG).
        - Auth cookies missing HttpOnly / Secure / SameSite.
        - Hardcoded test/master tokens or backdoor users left in code.
        - Account enumeration via timing or differing error messages on
          login / reset.

        # 7. Sessions / Self-contained tokens (ASVS V7, V9; OWASP A07)
        - JWT with 'none' algorithm accepted.
        - JWT signature not verified, or library autoselects 'alg' from
          the token (algorithm-confusion).
        - Symmetric JWT key reused across services; HS256 with a low-entropy
          shared secret.
        - Session fixation (id not rotated on auth state change).
        - Long-lived bearer tokens stored in localStorage where httpOnly
          cookies were viable.

        # 8. Authorization (ASVS V8; OWASP A01)
        - IDOR: a route accepts an id and acts on the corresponding object
          without verifying caller ownership / permission.
        - Vertical privilege escalation: privileged endpoint with no role /
          policy check.
        - Horizontal: routes that allow user A to operate on user B's data.
        - Path-based bypass: /admin/Foo vs /Admin/Foo, trailing slash, URL
          encoding tricks slipping past middleware.
        - Forced browsing: hidden admin pages reachable by direct URL.

        # 9. OAuth / OIDC (ASVS V10)
        - Implicit flow used for new clients (deprecated).
        - Public clients without PKCE.
        - redirect_uri allowed without exact-match comparison (substring,
          startsWith).
        - id_token consumed without iss / aud / exp / nonce checks.

        # 10. Cryptography (ASVS V11; OWASP A02)
        - MD5 / SHA-1 used for anything beyond non-security checksums.
        - DES / 3DES / RC4 / Blowfish / ECB-mode AES.
        - Hardcoded keys, IVs, salts.
        - Missing AEAD where confidentiality + integrity is needed
          (raw AES-CBC without HMAC, raw stream ciphers).
        - Key/IV reuse with stream ciphers or GCM.
        - Insufficient key length (RSA < 2048, ECC < 256, AES < 128).
        - Custom / hand-rolled crypto (rolling your own KDF, your own MAC).
        - Insecure random: Random / Math.random / rand() seeded from time
          for security-relevant material.

        # 11. Secure communication (ASVS V12; OWASP A02)
        - TLS verification disabled: verify=False (requests),
          ServerCertificateCustomValidationCallback returning true,
          NODE_TLS_REJECT_UNAUTHORIZED=0, InsecureSkipVerify in Go.
        - Plain HTTP for sensitive content.
        - TLS < 1.2 explicitly enabled or weak cipher suites configured.
        - Certificate pinning weakened / removed without compensating
          control.
        - Disabled hostname verification.

        # 12. Configuration (ASVS V13; OWASP A05)
        - Debug / development mode enabled in shipping config (DEBUG=True,
          ASPNETCORE_ENVIRONMENT=Development at startup).
        - Verbose error pages / stack traces returned to clients.
        - CORS Access-Control-Allow-Origin: * with Allow-Credentials, or
          newly added wildcard allowances on credentialed routes.
        - Missing security response headers introduced by this change:
          HSTS, CSP, X-Content-Type-Options, Referrer-Policy,
          Permissions-Policy.
        - Default credentials in code or seed data.
        - Permissive cookie scope (Domain= a parent of necessary).
        - Sample / demo configs deployed alongside production.

        # 13. Data protection (ASVS V14; OWASP A02)
        - Hardcoded secrets in source: AWS keys (AKIA...), Slack tokens,
          GitHub PATs (ghp_..., github_pat_...), Stripe (sk_live_...),
          OpenAI (sk-...), private keys (-----BEGIN ... PRIVATE KEY-----).
        - High-entropy strings assigned to identifiers like *_KEY, *_SECRET,
          *_TOKEN, *_PASSWORD.
        - Secrets reaching logs, metrics, telemetry, or error responses.
        - PII (emails, names, IPs, geolocation) sent to third parties not
          previously receiving it.
        - PII stored without minimisation / retention.

        # 14. SSRF (CWE-918; OWASP A10)
        - User-controlled URL passed to an HTTP client without an allowlist.
        - Especially: cloud metadata endpoints (169.254.169.254,
          fd00:ec2::254, metadata.google.internal), localhost / loopback,
          internal IP ranges (10/8, 172.16/12, 192.168/16, ::1).
        - DNS rebinding-resistant validation: resolve once and use the IP,
          not the hostname.
        - Redirect-following: HTTP client following redirects without
          re-validating the target.

        # 15. Resource exhaustion / DoS (ASVS V11; OWASP A04)
        - Unbounded loops or recursion driven by user input (counts,
          depths, sizes).
        - Regex DoS: nested quantifiers like (a+)+, alternation with
          overlapping prefixes, .* before a critical delimiter, on user
          input.
        - Compression bombs: zip / gzip / xz / brotli without ratio cap.
        - XML / JSON depth bombs: parsers without max-depth limit.
        - Missing request body / query / header size limits on new endpoints.
        - Missing rate limiting on expensive operations (login, search,
          file processing, AI calls).
        - Unbounded concurrency on outbound calls (no connection pool cap).

        # 16. Logging and error handling (ASVS V16; OWASP A09)
        - Stack traces returned to clients in error responses.
        - Sensitive data in logs: passwords, tokens, PII, auth cookies,
          session IDs, full request bodies for auth endpoints.
        - Log injection: user-supplied strings with newlines unescaped going
          into structured logs.
        - Generic catch-all swallowing exceptions (catch (Exception) {} /
          except: pass / catch (Throwable) {}).
        - Missing audit log for security-relevant events: auth changes,
          permission grants, admin actions, deletes.
        - Logging at user-controlled levels (a header / param choosing the
          level).

        # 17. Memory safety (for unsafe / native / FFI)
        - Manual buffer arithmetic in unsafe blocks / unsafe pointers /
          fixed pointers.
        - Integer overflow before allocation, then alloc + write
          (classic heap-overflow primitive).
        - Use-after-free / use-after-dispose on disposable resources.
        - Pinned pointers escaping a fixed scope.
        - Marshalling that doesn't validate length (e.g. Marshal.Copy with
          attacker-controlled length).

        # 18. Race conditions and concurrency (ASVS V11)
        - Shared mutable state without synchronization.
        - check-then-act on resources external to the process (filesystem,
          DB, cache).
        - Double-checked locking that's broken in this language's memory
          model.
        - Missing transactions around multi-step business invariants.

        # 19. Dependencies (ASVS V14)
        - New dependencies pinned at suspicious versions (typosquats,
          packages that don't appear in well-known registries).
        - Transitive dependencies bringing in known-vulnerable libs (only
          if you can identify a specific CVE from the name+version).

        # 20. LLM / AI specific
        - User input concatenated into agent prompts where the agent has
          tool access (filesystem, shell, network). This is a prompt-
          injection vector.
        - Stored injection: user-saved content (DB, file, ticket comment)
          later read by an LLM that has tools — even if the user couldn't
          directly call the LLM.
        - LLM-controlled values reaching exec / shell / file / network ops
          without an allowlist or human approval step.
        - Insecure use of system prompts (treating them as confidential
          when the model can leak them; or trusting them when they were
          mixed with user content).
        - Memory / context exfiltration: tool calls that read previous
          turns and send them externally.

        # 21. Business logic
        - Negative quantities / reversed-sign math on financial flows.
        - Toggle / feature-flag bypass: flag check before re-fetch but
          action uses the stale state.
        - Exposed administrative actions reachable by id manipulation
          even if the UI doesn't link to them.
        - Time-window abuse: actions reversible past their natural window
          (refunds, password resets) without a check.
        """;

    // --- Tests review prompt -------------------------------------------------

    private const string TestsReviewFocus = """
        You are reviewing whether the test changes in this diff give
        ADEQUATE and MEANINGFUL coverage of the production-code changes.
        Read both halves: the new/changed production code AND the test
        changes. Your goal is to flag tests that exist on paper but would
        not catch a plausible bug.

        Severity guidance:
          - Error: a code path with no test, or a new test that's
            impossibility-mirror / pure-mock / no-assertion / trivially-true.
          - Warning: happy-path-only on branchy code, edge cases missing,
            failure paths uncovered.
          - Info: suggestions to strengthen the suite.

        # Adequacy — flag missing tests as Error
        - Each NEW public function / class / endpoint / branch in this
          diff should have at least one test that exercises it.
        - Each NEW error path (throw, return error, validation rejection)
          should have at least one test that triggers it.
        - Each NEW config option / feature flag should have tests for both
          on and off branches when the flag changes behaviour.
        - Tests that were removed without a replacement covering the same
          path are Error, regardless of why.

        # Meaningfulness — bad patterns to flag as Error
        - **Implementation-mirroring**: the test asserts the same expression
          the implementation computes. Example: production has
            `def add(a,b): return a + b`,
          test asserts `add(1,2) == 1+2`. The test is logically equivalent
          to the impl and would not detect any algebraic error.
        - **Pure-mock tests**: the test sets up mocks, calls the SUT, and
          only asserts against the mock invocations. The assertions
          confirm the SUT's call pattern but not its real effect. Mocks
          are fine for isolating dependencies; the test must additionally
          assert a real outcome (return value, observable state change,
          published event content).
        - **No-assertion tests**: the test runs code and exits without
          asserting anything. The "test" only catches throws.
        - **Trivially-true assertions**: Assert.True(true), assert 1 == 1,
          expect(x).toBe(x), Assert.Equal(x, x). Already caught by the
          deterministic auditor; flag any the deterministic check missed.
        - **Tautology asserting on construction**: a test that creates a
          new instance and asserts a property holds the value just passed
          to its constructor. This tests the framework, not the code.
        - **Coverage-padding**: a test whose only purpose is to exercise
          a line so coverage tooling reports green; no behaviour is
          checked.

        # Meaningfulness — good patterns to confirm exist
        - **Integration tests** that exercise multiple modules together,
          where each must work for the test to pass.
        - **Failure-mode tests**: timeout, network error, malformed input,
          exhausted resources, partial failure / retry.
        - **Edge cases**: empty / null / boundary / max / min / unicode /
          concurrent / very-large / very-small.
        - **Property tests** for parsing, validation, and stateful logic
          when feasible.
        - **Round-trip tests** for serialization, encoding, encryption.
        - **Regression tests** for any bug fix in this change.

        # Meaningfulness — heuristic
        For each test, ask: "if I introduced a plausible bug X in the
        implementation, would this test catch it?" If the answer for every
        plausible X is no, the test is not meaningful.

        Examples of plausible bugs the tests should catch:
          - Off-by-one
          - Inverted condition (== vs !=, < vs <=)
          - Forgotten null / empty check
          - Wrong order of operations
          - Wrong field used (id vs userId)
          - Forgotten failure-path handling

        # Skipped / disabled tests
        - Any new @Skip / it.skip / [Fact(Skip=...)] / @pytest.mark.skip /
          #[ignore] without a clearly documented reason → Error.
        - Tests removed entirely → Error if they covered a still-extant
          code path (cross-reference with the production diff).

        # Test organisation (Info-level)
        - Test names that don't describe the behaviour under test.
        - Multiple unrelated assertions in a single test.
        - Setup that obscures the test (heavy fixtures for simple checks).

        Cite the test file:line you're flagging in 'location'. Where the
        problem is "no test exists for this prod change," cite the
        production file:line where the missing coverage is.
        """;

    private static DiffPattern Pat(string regex, string description, AuditSeverity severity = AuditSeverity.Error) => new()
    {
        Regex = new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        Description = description,
        Severity = severity,
    };
}
