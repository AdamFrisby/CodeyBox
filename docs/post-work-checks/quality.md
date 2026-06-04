# Post-work check: Quality

After your functional work is done, check your diff against these. An automated quality
reviewer applies the same criteria. Fix real issues without expanding the original
task's scope.

- **Dead code** (unreachable branches, unused functions/imports).
- **Magic numbers** and unexplained literal constants.
- **Unclear or misleading names**; abbreviations a new reader couldn't expand.
- **Error handling at boundaries** that swallows or rethrows incorrectly.
- **Duplicated logic** that should be a single helper.
- **Comments that describe WHAT instead of WHY.**

Tests which cannot be run in this environment are not part of the criteria.
