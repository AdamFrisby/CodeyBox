# Post-work check: Architecture

After your functional work is done, check your diff against these. An automated
architecture reviewer applies the same criteria. Fix real issues without expanding the
original task's scope.

- **Loose-coupling violations**: concrete types appearing in cross-module method
  signatures where an interface already exists.
- **New direct dependencies** that should have gone through an existing abstraction.
- **God objects / classes** accumulating unrelated responsibilities.
- **Layering violations** (e.g. domain code referencing infrastructure).
- **Public APIs that leak internal types.**
