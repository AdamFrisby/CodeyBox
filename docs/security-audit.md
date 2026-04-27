# Security audit — initial pass

This is a complete-codebase review of CodeyBox as of the commit that
introduced the Kata + crun-vm providers, REST auth, live-cancel, and
integration tests. The audit was performed by the same author who wrote the
code; see "Limitations" at the end.

Each finding lists:
- **Where**: file paths.
- **Severity**: HIGH / MEDIUM / LOW / INFO.
- **Status**: FIXED in this audit / MITIGATED by design / ACCEPTED with caveats.
- **Why**: the reasoning.

## Threat model assumptions used during the review

* Agent output is adversarial-by-default (prompt injection from repo
  contents, fetched URLs, etc.).
* Operator running the orchestrator binary is trusted; the host kernel is
  trusted; the host's git binary is trusted.
* API callers are partially trusted: authenticated, but may submit
  malicious repo URLs / branch names / prompts.
* Multi-tenant within a single CodeyBox instance is **out of scope**.
  Single-tenant operator deployments only. Multi-tenancy would need
  per-tenant credential providers, repo namespacing, and authn/authz on
  the API beyond a single bearer token.

## Findings

### A — Cross-work-item bare-repo exposure  &nbsp; *MEDIUM, FIXED*

**Where:** `src/CodeyBox.Git/LocalGitHost.cs::GetSandboxAccess`

The original implementation bind-mounted the *root* bare-repos directory
(`_opts.RootDirectory`) into every sandbox at `/repos`. Work item A's
sandbox could therefore read or write work item B's bare repo — so a
prompt-injected agent in A could corrupt or steal the output of any
concurrent work item B by directly editing files under
`/repos/<B-id>.git/`.

**Fix:** mount only the per-item bare repo directory at `/repo` (singular).
The mount source is `GetRepoPath(repositoryId)`. Other items' bare repos
are no longer visible inside the sandbox.

**Regression test:** `PipelineIntegrationTests.TwoWorkItems_DoNotShareBareRepoVisibility`
asserts that the mount source is the per-item path, not the bare-repos
root, and that another item's repo path is *not* contained in the mount
source.

### B — `workBranch == baseBranch` bypass of merge-phase containment  &nbsp; *HIGH, FIXED*

**Where:** API ingress and `PipelineRunner.RunAsync`

The work phase runs an agent and pushes its commits to `workBranch`. The
merge phase runs in a *separate* sandbox with no agent credentials and
merges `workBranch` into `baseBranch`. The two-sandbox split is the core
defence: a malicious agent can write code, but the same compromised
context cannot push that code straight to the integration branch.

If a caller sets `workBranch` equal to `baseBranch` (e.g. both `main`),
the work-phase push lands directly on `main` in the host bare repo, and
the merge phase becomes a no-op fast-forward. The merge sandbox's
containment is bypassed.

**Fix:** rejected at two layers.
1. API: `WorkItemEndpoints.CreateAsync` returns 400 when both branches
   are explicitly equal.
2. Pipeline: `PipelineRunner.RunAsync` throws *after* default-branch
   resolution (so the case where `BaseBranch` was null and the resolved
   default equals an explicit `WorkBranch` is also caught).

**Regression test:** `PipelineIntegrationTests.WorkBranchEqualsBaseBranch_FailsBeforeSandbox`.

### C — GitHub PAT visible on argv via token-in-URL  &nbsp; *HIGH, FIXED*

**Where:** `src/CodeyBox.Upstream.GitHub/GitHubUpstreamRemote.cs`

The original implementation built a push URL of the form
`https://x-access-token:<PAT>@github.com/owner/repo.git` and passed it to
`git push`. Since `ProcessStartInfo.ArgumentList` puts arguments on argv,
the token was visible in `/proc/<pid>/cmdline`, `ps`, audit logs, and any
process accounting subsystem on the host.

**Fix:** new `CodeyBox.Git/GitCredentialHelper.cs` builds an ephemeral
askpass script in a 0700 tmp dir with a 0700 script file. The script
reads the password from the env var `CODEYBOX_GIT_PASS`; the env is
inherited only by the git child process. The push URL no longer contains
the token. The askpass scope is `IDisposable` and removes the tmp dir on
disposal, regardless of push outcome.

**Defence in depth:** the catch block still scrubs the literal token
substring from any error message before returning it, in case some
unforeseen code path causes git to echo the env value.

**Test:** `GitCredentialHelperTests` — execute-the-script-and-assert-output,
plus dispose-cleans-up coverage.

### D — Sandbox env vars on per-exec argv  &nbsp; *HIGH, FIXED*

**Where:** `src/CodeyBox.Agents/CliAgentRunnerBase.cs` and the planned
Podman impl.

Earlier code merged `AgentCredential.EnvironmentVariables` into per-exec
`ExtraEnvironment`. With the eventual `podman exec -e KEY=VALUE` impl,
that VALUE would be visible on argv. Even with the dev `ProcessSandbox`
the values were copied into a `ProcessStartInfo` env block — fine on its
own, but the abstraction encouraged the wrong shape for the production
path.

**Fix:** `PipelineRunner.BuildSandboxSpec` now puts credential env on
`SandboxSpec.Environment` (set on the *container* at boot via
`--env-file`, never on argv). `CliAgentRunnerBase.RunAsync` no longer
merges credentials into per-exec env. The `PodmanDriver` writes a
0600 env-file in a 0700 tmp dir and passes only `--env-file <path>` on
argv.

### E — `AgentCredential.Files` declared but never written  &nbsp; *MEDIUM, FIXED*

**Where:** `src/CodeyBox.Core/ICredentialProvider.cs` and the work phase.

The credential bundle declared a `Files` map intended for agents whose
auth lives in a config file (e.g. `~/.config/foo/auth.json`). The
pipeline never actually wrote those files to the sandbox tmpfs — silent
no-op. A future credential provider returning a non-empty `Files`
dictionary would have failed silently, leaving the agent unauthenticated
or, worse, silently authenticated against the wrong identity.

**Fix:** `PipelineRunner.RunWorkPhaseAsync` now materialises files via
`sandbox.ExecAsync` with `Argv=["sh","-c","umask 077 && cat > \"$0\"",
fullPath]` and `Stdin=contents`. The contents pass through a pipe, never
on argv. Filenames are validated by `SanitiseCredentialFileName` to
forbid `..` traversal and strip leading `/`.

### F — git-clone option-injection via `RepositoryUrl`  &nbsp; *HIGH, FIXED*

**Where:** `src/CodeyBox.Git/LocalGitHost.cs::EnsureRepositoryAsync`
(host-side seeding from upstream).

`git clone <url> <dest>` treats `<url>` as an option when it begins with
`-`. A malicious `RepositoryUrl` of `--upload-pack=evil-cmd` would let
git execute attacker-supplied commands at clone time. This is a known
class of bug (see CVE-2017-1000117 and friends).

**Fix:** new `CodeyBox.Core/Validation.cs` enforces a strict
URL-or-path regex (rejects leading `-`, control chars, anything that
isn't a recognised git URL form). The clone invocation also uses
`git clone --bare -- <url> <dest>` — the `--` separator is git's own
defence and stops further options from being parsed even if the URL is
crafted.

`PushToUpstreamAsync` validates both the URL and the branch name; `git
push` doesn't accept a `--` separator before the repository, so URL
validation alone is the gate there.

**Tests:** `ValidationTests` (option-injection URLs and leading-dash
branch names rejected; valid forms accepted).

### G — Branch-name option-injection  &nbsp; *MEDIUM, FIXED*

**Where:** any code path that runs `git checkout -B <branch>`,
`git push origin <branch>:<branch>`, etc.

A branch name beginning with `-` would be parsed as an option by some
git subcommands. A regex that accepted "interesting" names like
`-d` (delete) or `--all` would be a problem.

**Fix:** `Validation.ValidateBranchName` enforces a conservative
ASCII-alnum + `._/-` regex with leading-non-dash, no `..`, no `.lock`.
Applied at the API and inside `LocalGitHost.PushToUpstreamAsync`.

### H — Unbounded request body / WorkItem field sizes  &nbsp; *LOW, FIXED*

**Where:** `src/CodeyBox.Api/WorkItemEndpoints.cs`

ASP.NET Core's default request body limit is 30 MB. With auth on, only
authenticated callers can hit this. But a 30 MB title or prompt is
absurd and would bloat the SQLite store.

**Fix:** `Title` capped at 200 chars, `Prompt` capped at 64 KB. Returned
as 400 with a clear error message.

### I — Default network bind  &nbsp; *LOW, FIXED*

**Where:** `src/CodeyBox.Api/Program.cs`

ASP.NET Core in non-Development environments binds to whatever
`ASPNETCORE_URLS` says, which on some hosting setups defaults to all
interfaces. With auth on, the risk is bounded; without it (the
"DangerouslyDisableAuth" path) it's a wide-open RCE surface.

**Fix:** if no URL is configured by the operator (`ASPNETCORE_URLS`,
`urls`, or `Kestrel:Endpoints:Default:Url`), default to
`http://127.0.0.1:5000`. Operators putting a TLS-terminating reverse
proxy in front will set the URL explicitly.

### J — `DangerouslyDisableAuth` footgun  &nbsp; *MEDIUM, MITIGATED*

**Where:** `src/CodeyBox.Api/ApiKeyAuth.cs`

The opt-out config key for auth is named `DangerouslyDisableAuth`
explicitly so reading the config is jarring. The startup path requires
either a 32+ char `CODEYBOX_API_KEY` *or* the explicit opt-out — the
default is to refuse to start without an API key. This is the safer
posture (fail-closed) but the existence of the opt-out is itself a risk.

**Status:** Accepted with caveat. The opt-out exists for the dev loop
where the operator is on localhost and doesn't want to set an env var.
In combination with finding I (loopback-default bind), the blast radius
is "anything reaching loopback can hit the API."

### K — Token in process env inherits to child processes  &nbsp; *LOW, ACCEPTED*

**Where:** any subprocess spawned by the orchestrator (host-side git
push, podman invocations).

The orchestrator process holds env vars like `CODEYBOX_CLAUDE_API_KEY`
and `CODEYBOX_GITHUB_TOKEN`. Subprocesses inherit the entire env by
default. The git binary doesn't normally read those names, but if it
were maliciously replaced or if some debug log emitted env, the values
could leak.

**Status:** Accepted. Mitigation would be to scrub the env before each
subprocess spawn; not done because the host trust assumption already
includes the binaries we run.

### L — `GitGenericUpstreamRemote` does not scrub error messages  &nbsp; *LOW, ACCEPTED*

**Where:** `src/CodeyBox.Upstream/GitGenericUpstreamRemote.cs`

The GitHub variant scrubs its token from any error returned to the
orchestrator. The generic variant does not (it doesn't know what's a
token vs. URL). If an operator passes a tokenized URL via
`GitGenericUpstreamOptions.UpstreamUrl`, that URL could end up in a log
line through the error path.

**Status:** Accepted with documented caveat. Operators using the generic
upstream should use askpass-style auth (configured via the host git
config) rather than tokenized URLs. Documented in
`docs/security.md`.

### M — Disk and wall-clock resource limits unenforced by Podman driver  &nbsp; *LOW, ACCEPTED*

**Where:** `src/CodeyBox.Sandbox/PodmanDriver.cs`

`SandboxResourceLimits.MemoryBytes` and `CpuCount` are honoured via
`--memory` and `--cpus`. `DiskBytes` is not — limiting overlayfs
quota requires host filesystem support (XFS/btrfs quotas, or
`--storage-opt size=N` with overlay-on-XFS). `WallClock` is enforced
orchestrator-side via `CancellationTokenSource.CancelAfter`, which kills
the sandbox via Dispose, but the *intermediate* commands inside the
sandbox don't get a SIGTERM grace period.

**Status:** Accepted with caveat. Documented in
`docs/sandbox-providers.md`. A high-stakes deployment should configure
host quotas.

### N — ProcessSandbox does not enforce filesystem isolation  &nbsp; *INFO, BY DESIGN*

**Where:** `src/CodeyBox.Sandbox.Process/ProcessSandboxProvider.cs`

The Process provider runs commands as the host user. Any path the agent
references that *isn't* under a known sandbox mount point is left
unmodified — `cat /etc/passwd` runs against the real `/etc/passwd`. The
provider is repeatedly labelled UNSAFE and is intended only for
developing the orchestrator pipeline.

**Status:** By design. The README and security docs say "do not use in
production." Production deployments must select Kata or crun-vm.

### O — Network egress allowlist is documented, not enforced by driver  &nbsp; *MEDIUM, MITIGATED*

**Where:** `src/CodeyBox.Sandbox/PodmanDriver.cs`

When the spec lists `AllowedHosts`, the driver attaches the container to
the operator-configured CNI network (`codeybox-egress`). The driver
does NOT add nftables rules to actually constrain egress to the
allowlist — that requires root and host firewall changes. A startup log
line warns when the policy isn't enforceable from inside the container.

**Status:** Mitigated by documentation. The host setup checklist in
`docs/sandbox-providers.md` includes an nftables snippet that drops
egress except to the allowlist. Without that host config, allowed-hosts
becomes "any host" — operators must follow the checklist.

### P — InMemoryPullRequestService has unbounded growth  &nbsp; *LOW, ACCEPTED*

**Where:** `src/CodeyBox.Git/InMemoryPullRequestService.cs`

PRs accumulate in a ConcurrentDictionary forever. With auth on, only
authenticated callers can submit work; the dict grows linearly with
work-item count.

**Status:** Accepted; document recommendation to use a database-backed
PR service for long-running deployments. Not a security issue, an
operations one.

### Q — SQLite store concurrency  &nbsp; *INFO, ACCEPTED*

**Where:** `src/CodeyBox.Orchestrator/SqliteWorkItemStore.cs`

A single `SqliteConnection` is shared across all callers, with a
`SemaphoreSlim` serialising writes. Reads are not synchronised but
SQLite is safe for concurrent reads on one connection in default
journal mode, and our tests cover the main read patterns.

**Status:** Accepted. For deployments needing real concurrency on the
write path, swap `IWorkItemStore` for a Postgres-backed implementation —
the interface was designed for this.

### R — Replay duplicates agent work  &nbsp; *LOW, ACCEPTED, DOCUMENTED*

**Where:** `src/CodeyBox.Orchestrator/OrchestratorService.cs::ReplayPendingAsync`

If the orchestrator crashes after the agent has produced commits but
before the work-phase push, the next start re-runs the agent, which may
produce additional commits stacked on top. Idempotency at the *phase*
boundary is ensured (push of an already-pushed commit is a no-op) but
not at the *agent* boundary.

**Status:** Accepted. Documented in `docs/operations.md`.

### S — Token presence check uses non-constant time for early exit  &nbsp; *INFO, ACCEPTED*

**Where:** `src/CodeyBox.Api/ApiKeyAuth.cs`

`TryExtractBearer` returns false fast when the `Authorization` header is
missing — not constant time. This only reveals "header missing", which
is observable to any attacker via the same response, so no information
is leaked. The constant-time compare protects the actual token bytes.

**Status:** Accepted.

### T — Logged error messages may include sandbox stderr  &nbsp; *LOW, ACCEPTED*

**Where:** `src/CodeyBox.Orchestrator/PipelineRunner.cs::Run`

Failures throw with the failed argv and the full sandbox stderr. The
sandbox runs git inside an isolated environment with no upstream creds;
the merge phase has no agent creds. The risk that stderr contains a
secret is low. The work-phase agent has its API key in env; if the
agent itself fatally errors and dumps env, the stderr could include it.

**Status:** Accepted. Operators concerned about this can replace the
`PipelineRunner` (it's not behind an interface today, but pulling it
behind one is a small refactor).

## Things specifically reviewed and fine

* SQLite calls — all parameterised. `Bind` only uses `AddWithValue`.
* `LibGit2Sharp.Repository.Init(path, isBare: true)` — operates on a
  path under `_opts.RootDirectory`, not user input.
* `WorkItem` and `WorkItemId` — Guid-based; SQL injection not possible
  from id values.
* `AgentRegistry` — pure dictionary lookup keyed on `AgentKind` value;
  no eval, no reflection.
* `SandboxConventions.WorkDir` and `CredentialsDir` — fixed strings, not
  user-influenced.
* Cancellation tokens — `CancellationRegistry` linked to the host's
  ApplicationStopping. Disposed registrations remove themselves; root
  cancel propagates. Tested.
* `AskPassScope` — script + dir cleaned up on dispose; perms set to
  0700/0600 where supported.
* Docker/Podman image — driver does not pull; operator-managed.
  Documented requirement to pin by digest.

## Limitations of this audit

1. **Same author.** I wrote the code and the audit. An independent
   reviewer would catch things I'm blind to.
2. **No runtime testing of Kata or crun-vm.** Code reviewed only. The
   host setup required to run them is documented but I have no way to
   verify the resulting microVM enforces what we expect (e.g. `--read-only`
   actually preventing writes outside tmpfs mounts, the egress policy
   actually being applied at the network namespace).
3. **No fuzz testing.** Validation rules are tested with hand-picked
   adversarial inputs, not generative fuzzing.
4. **Dependencies not audited.** `LibGit2Sharp`, `Microsoft.Data.Sqlite`,
   xUnit, and the .NET 10 base libraries are trusted as-is.

## Recommended next steps

1. **Run on a real Kata host.** Verify the PodmanDriver actually
   provisions Firecracker microVMs as expected, and that `--read-only`
   plus tmpfs mounts give the agent a writable `/work` only.
2. **Configure host nftables** for the egress policy (sample snippet in
   `docs/sandbox-providers.md`). Re-run the integration suite against
   a host with the rules in place; confirm allowlisted destinations
   reach and others don't.
3. **Add a fuzz test** for `Validation.ValidateRepositoryUrl` and
   `ValidateBranchName`. Aim for a corpus including the known git
   option-injection vectors.
4. **Independent review** of the API surface and the git workflow.
   A second pair of eyes on `WorkItemEndpoints` and `LocalGitHost` is
   the highest-value review left.
5. **Threat-model multi-tenancy.** If you ever want multiple users on
   one orchestrator, design the separation explicitly: per-tenant
   `IGitHost`, per-tenant `ICredentialProvider`, per-tenant API
   namespacing, per-tenant SQLite (or row-level scoping).
