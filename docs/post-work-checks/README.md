# Post-work self-checks

After you finish the functional work for a task **and the build passes**, review your
changes against the checklists in this folder before you declare done. An automated
reviewer applies these same criteria after your phase — addressing them now means
fewer rework cycles and faster merges.

**How to use these**

- Apply them **only after** your functional work compiles and works. Do not read or act
  on them before that — they describe review criteria, not the task, and must not
  reshape what you were asked to build.
- **Do not change, reduce, or expand the original work request's requirements** to
  satisfy a checklist. If a checklist item conflicts with the task as written, leave the
  task as written.
- Fix genuine issues your diff introduces. If a checklist item doesn't apply to your
  change, skip it — these are review lenses, not a to-do list to pad.

**Dimensions** (one file each):

- `architecture.md` — coupling, layering, dependency hygiene
- `completeness.md` — unfinished work, missing tests, stale docs
- `quality.md` — dead code, naming, error handling, duplication
- `security.md` — vulnerabilities (injection, authz, crypto, secrets, SSRF, …)
- `tests.md` — test adequacy and meaningfulness
