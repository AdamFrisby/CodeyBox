# Post-work check: Test adequacy & meaningfulness

After your functional work is done, check that your tests give **adequate and meaningful**
coverage of the production changes. An automated reviewer applies the same criteria. A
test that exists on paper but wouldn't catch a plausible bug is not enough. Add or
strengthen tests without changing the original task's requirements.

## Adequacy — make sure these exist

- Each **new public function / class / endpoint / branch** has at least one test that
  exercises it.
- Each **new error path** (throw, return error, validation rejection) has a test that
  triggers it.
- Each **new config option / feature flag** has tests for both on and off branches when
  the flag changes behaviour.
- Tests removed without a replacement covering the same path are a gap — restore the
  coverage.

## Meaningfulness — avoid these weak patterns

- **Implementation-mirroring**: the test asserts the same expression the implementation
  computes (e.g. impl `return a + b`, test asserts `add(1,2) == 1+2`) — it can't detect an
  algebraic error.
- **Pure-mock tests**: only asserting against mock invocations, never a real outcome
  (return value, observable state change, published event content). Mocks isolate
  dependencies; still assert a real effect.
- **No-assertion tests**: running code and exiting without asserting anything.
- **Trivially-true assertions**: `Assert.True(true)`, `assert 1 == 1`,
  `expect(x).toBe(x)`, `Assert.Equal(x, x)`.
- **Construction tautologies**: asserting a property equals the value just passed to the
  constructor — tests the framework, not your code.
- **Coverage-padding**: a test whose only purpose is to turn a line green.

## Meaningfulness — prefer these

- **Integration tests** exercising multiple modules together.
- **Failure-mode tests**: timeout, network error, malformed input, exhausted resources,
  partial failure / retry.
- **Edge cases**: empty / null / boundary / max / min / unicode / concurrent / very-large.
- **Property tests** for parsing, validation, stateful logic.
- **Round-trip tests** for serialization, encoding, encryption.
- **Regression tests** for any bug fixed in this change.

## Heuristic

For each test, ask: *"if I introduced a plausible bug (off-by-one, inverted condition,
forgotten null check, wrong field) would this test catch it?"* If the answer for every
plausible bug is no, the test isn't meaningful.

Any new skip/disable marker without a clearly documented reason is a gap. Tests which
cannot be run in this environment are not part of the criteria.
