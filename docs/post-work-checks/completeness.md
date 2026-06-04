# Post-work check: Completeness

After your functional work is done, check your diff against these. An automated
completeness reviewer applies the same criteria. Fix real issues without expanding the
original task's scope.

- **TODO / FIXME / XXX markers** added in this change.
- **New functionality without corresponding tests.**
- **Half-finished implementations** (functions that return early, swallowed branches).
- **Public functions whose docstrings/comments describe behaviour the code doesn't
  implement.**
- **Test files that were renamed or deleted instead of fixed.**

Tests which cannot be run in this environment are not part of the criteria.
